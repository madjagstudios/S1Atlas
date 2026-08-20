using S1Atlas.Application.Authority;
using S1Atlas.Application.Composition;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Docs.Identity;
using S1Atlas.Docs.Content;
using S1Atlas.Docs.Source;

namespace S1Atlas.Docs.Generation;

public sealed class PortalModelBuilder
{
    private const int PageSize = 512;

    public async Task<PortalSiteModel> BuildAsync(
        AtlasReadOnlyServices services,
        DocsGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(request);

        var authority = await services.AuthorityResolver.ResolveAsync(request.RequestedBuildId, cancellationToken);
        if (authority.Status != InstalledBuildAuthorityStatus.Resolved ||
            authority.IndexRun is null ||
            authority.ResolvedBuildId is null ||
            authority.ExtractionId is null)
        {
            throw new InvalidOperationException(authority.Message ?? "The requested Schedule I build has no verified completed authority.");
        }

        var scheduleSnapshot = await services.Repository.GetCodeSnapshotAsync(authority.IndexRun.SnapshotId, cancellationToken)
            ?? throw new InvalidDataException("The resolved Schedule I index snapshot is missing.");
        var indexes = new List<PortalIndexModel>
        {
            await MaterializeIndexAsync(
                services,
                authority.IndexRun,
                CodebaseKind.ScheduleI,
                CodeChannel.Installed,
                authority.ResolvedBuildId,
                authority.ExtractionId,
                isVerifiedAuthority: true,
                scheduleSnapshot.SourceIdentity,
                cancellationToken)
        };

        foreach (var codebase in new[] { CodebaseKind.S1Api, CodebaseKind.S1MApi })
            foreach (var channel in Enum.GetValues<CodeChannel>())
            {
                var selection = await services.IndexQueryService.GetLatestCompletedIndexSelectionAsync(codebase, channel, cancellationToken);
                if (selection is null) continue;
                indexes.Add(await MaterializeIndexAsync(
                    services,
                    selection.Run,
                    codebase,
                    channel,
                    buildId: null,
                    extractionId: null,
                    isVerifiedAuthority: false,
                    selection.Snapshot.SourceIdentity,
                    cancellationToken));
            }

        var history = await services.InstalledBuildHistoryQueryService.GetHistoryAsync(cancellationToken);
        var diffs = new List<PortalDiffModel>();
        foreach (var pair in history.AdjacentPairs)
        {
            if (pair.Before.Authority?.IndexId is null || pair.After.Authority?.IndexId is null) continue;
            var result = await services.BuildDiffService.DiffAsync(
                pair.Before.Authority.IndexId,
                pair.After.Authority.IndexId,
                CodebaseKind.ScheduleI.ToString(),
                CodeChannel.Installed.ToString(),
                kindFilter: null,
                cancellationToken);
            diffs.Add(new PortalDiffModel(
                pair.Before.Build.BuildId,
                pair.After.Build.BuildId,
                result,
                $"diffs/{pair.Before.Build.BuildId}--{pair.After.Build.BuildId}.html"));
        }

        var entries = history.Entries
            .Select(entry => new PortalBuildEntry(
                entry.Build,
                entry.Status,
                entry.IsNavigable,
                entry.IsNavigable ? $"builds/{entry.Build.BuildId}.html" : null))
            .ToArray();
        var scheduleSymbols = indexes
            .Where(index => index.Codebase == CodebaseKind.ScheduleI && index.Channel == CodeChannel.Installed)
            .SelectMany(index => index.Namespaces)
            .SelectMany(namespaceModel => namespaceModel.Symbols)
            .Where(symbol => symbol.Kind is SymbolKind.Type or SymbolKind.Method or SymbolKind.Constructor)
            .OrderBy(symbol => symbol.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
        var symbolHistories = new List<PortalSymbolHistoryModel>(scheduleSymbols.Length);
        var slugService = new PortalSlugService();
        foreach (var symbol in scheduleSymbols)
        {
            var occurrences = await services.InstalledBuildHistoryQueryService.GetSymbolOccurrencesAsync(
                symbol.CanonicalKey,
                history.Entries,
                cancellationToken);
            var slug = slugService.Create(symbol.CanonicalKey);
            symbolHistories.Add(new PortalSymbolHistoryModel(
                symbol.CanonicalKey,
                symbol.QualifiedName,
                $"history/schedule-i/symbols/{slug.HashPrefix}/{slug.FileStem}.html",
                occurrences));
        }
        var currentEnvironment = await services.Repository.GetCurrentSnapshotAsync(cancellationToken);
        var environment = currentEnvironment is not null &&
                          string.Equals(currentEnvironment.Build.BuildId, authority.ResolvedBuildId, StringComparison.Ordinal)
            ? new PortalEnvironmentModel(currentEnvironment, $"environment/{authority.ResolvedBuildId}.html")
            : null;
        var statuses = indexes
            .Where(index => !index.IsVerifiedAuthority)
            .Select(index => new PortalStatus(
                $"{index.Codebase}:{index.Channel}",
                "latest completed index",
                false,
                index.SourceIdentity + "; index " + index.IndexId))
            .ToArray();

        return new PortalSiteModel(
            authority.ResolvedBuildId,
            indexes,
            new PortalBuildHistoryModel(entries, diffs),
            environment,
            diffs,
            statuses,
            symbolHistories);
    }

    private static async Task<PortalIndexModel> MaterializeIndexAsync(
        AtlasReadOnlyServices services,
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string? buildId,
        string? extractionId,
        bool isVerifiedAuthority,
        string sourceIdentity,
        CancellationToken cancellationToken)
    {
        var page = new IndexPageRequest(0, PageSize);
        var all = new List<IndexedSymbolQueryResult>();
        do
        {
            var result = await services.IndexQueryService.ListSymbolsInIndexAsync(run, codebase, channel, page, cancellationToken);
            all.AddRange(result.Results);
            if (!result.HasMore) break;
            page = new IndexPageRequest(page.Offset + page.Limit, page.Limit);
        } while (true);

        var slugService = new PortalSlugService();
        var materialized = all.Select(symbol => ToPortalSymbol(symbol, codebase, channel, slugService)).ToArray();
        var typesByCanonical = materialized
            .Where(symbol => symbol.Kind == SymbolKind.Type)
            .ToDictionary(symbol => symbol.CanonicalKey, StringComparer.Ordinal);
        materialized = materialized.Select(symbol =>
        {
            if (symbol.Kind is SymbolKind.Type or SymbolKind.Method or SymbolKind.Constructor) return symbol;
            var owner = symbol.CanonicalKey.Split(':', 4).LastOrDefault()?.Split("::", StringSplitOptions.None)[0];
            var type = typesByCanonical.Values.FirstOrDefault(candidate => candidate.CanonicalKey.EndsWith(":Type:" + owner, StringComparison.Ordinal));
            return type is null ? symbol : symbol with { PagePath = type.PagePath, Anchor = slugService.MemberAnchor(symbol.CanonicalKey) };
        }).ToArray();
        var symbolsById = materialized.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        var namespaceResult = await services.IndexQueryService.ListNamespacesInIndexAsync(
            run, codebase, channel, cancellationToken);
        var symbolsByNamespace = all
            .GroupBy(symbol => CanonicalSymbolKeyParser.NamespaceFrom(symbol.CanonicalKey), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var namespaceNames = namespaceResult.Namespaces.ToList();
        if (symbolsByNamespace.ContainsKey(string.Empty)) namespaceNames.Add(string.Empty);
        var namespaces = namespaceNames
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new PortalNamespaceModel(
                name,
                symbolsByNamespace.GetValueOrDefault(name, [])
                    .OrderBy(symbol => symbol.CanonicalKey, StringComparer.Ordinal)
                    .ThenBy(symbol => symbol.Kind, StringComparer.Ordinal)
                    .ThenBy(symbol => symbol.SymbolId, StringComparer.Ordinal)
                    .Select(symbol => symbolsById[symbol.SymbolId])
                    .ToArray(),
                symbolsByNamespace.GetValueOrDefault(name, []).Length))
            .ToArray();
        var index = new PortalIndexModel(run, codebase, channel, run.IndexId, sourceIdentity, buildId, extractionId, isVerifiedAuthority, namespaces, all.Count);
        var sourceReader = new PortalSourceReader(services.IndexQueryService);
        var contextBuilder = new DerivedContextBuilder();
        var enrichedNamespaces = new List<PortalNamespaceModel>(index.Namespaces.Count);
        foreach (var portalNamespace in index.Namespaces)
        {
            var enrichedSymbols = new List<PortalSymbolModel>(portalNamespace.Symbols.Count);
            foreach (var symbol in portalNamespace.Symbols)
            {
                if (symbol.Kind is not (SymbolKind.Type or SymbolKind.Method or SymbolKind.Constructor))
                {
                    enrichedSymbols.Add(symbol);
                    continue;
                }
                var evidence = await services.IndexQueryService.GetRelationshipEvidenceInIndexAsync(
                    run, codebase, channel, symbol.SymbolId, cancellationToken);
                var source = await sourceReader.ReadAsync(index, symbol, cancellationToken);
                var relationships = new PortalRelationshipEvidenceModel(
                    evidence.References, evidence.ReferenceTotal,
                    evidence.Callers, evidence.CallerTotal,
                    evidence.Callees, evidence.CalleeTotal,
                    evidence.CallerCompletenessNotice, evidence.CalleeCompletenessNotice);
                var context = contextBuilder.Build(symbol, relationships, source, new PortalLinkResolver());
                enrichedSymbols.Add(symbol with { Evidence = new PortalSymbolEvidenceModel(relationships, source, context) });
            }
            enrichedNamespaces.Add(portalNamespace with { Symbols = enrichedSymbols });
        }
        return index with { Namespaces = enrichedNamespaces };
    }

    private static PortalSymbolModel ToPortalSymbol(IndexedSymbolQueryResult symbol, CodebaseKind codebase, CodeChannel channel, PortalSlugService slugService)
    {
        var kind = Enum.TryParse<SymbolKind>(symbol.Kind, ignoreCase: false, out var parsed) ? parsed : SymbolKind.Type;
        var slug = slugService.Create(symbol.CanonicalKey);
        var page = $"code/{codebase.ToString().ToLowerInvariant()}/{channel.ToString().ToLowerInvariant()}/symbols/{slug.HashPrefix}/{slug.FileStem}.html";
        return new PortalSymbolModel(symbol.IndexId, codebase, channel, symbol.SymbolId, symbol.CanonicalKey, kind, symbol.QualifiedName, symbol.Signature, symbol.IsBestEffort, symbol.BodyRecoveryStatus, page, slugService.MemberAnchor(symbol.CanonicalKey));
    }

}
