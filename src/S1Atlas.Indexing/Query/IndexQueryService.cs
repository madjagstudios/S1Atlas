using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Query;

public sealed class IndexQueryService
{
    private const string CallSiteCompletenessNotice = "Call-site results are evidence of recovered IL references and do not prove runtime behavior or execution order.";
    private const int MaxSourceNeighborhoodLimit = 50;
    private readonly IIndexRepository _repository;
    private readonly string? _dataRoot;
    private readonly SymbolResolver _symbolResolver;
    private readonly SourceSnippetReader _sourceSnippetReader = new();

    public IndexQueryService(IIndexRepository repository)
        : this(repository, null)
    {
    }

    public IndexQueryService(IIndexRepository repository, string? dataRoot)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _dataRoot = dataRoot is null ? null : Path.GetFullPath(dataRoot);
        _symbolResolver = new SymbolResolver(_repository);
    }

    public async Task<SymbolQueryResult?> GetExactSymbolAsync(string indexId, string symbolId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId); ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        var run = await _repository.GetCompletedIndexAsync(indexId, cancellationToken);
        if (run is null) return null;
        var snapshot = await _repository.GetCodeSnapshotAsync(run.SnapshotId, cancellationToken);
        var symbol = await _repository.GetCompletedSymbolByIdAsync(indexId, symbolId, cancellationToken);
        return snapshot is null || symbol is null ? null : SymbolResolver.ToQueryResult(indexId, snapshot.Codebase, snapshot.Channel, symbol, SymbolResolver.OriginFor(snapshot.Codebase));
    }

    public async Task<IReadOnlyList<SymbolQueryResult>> GetCanonicalSymbolsInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string canonicalKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalKey);
        var symbols = await _repository.GetCompletedSymbolByCanonicalKeyAsync(run.IndexId, canonicalKey, cancellationToken);
        return symbols
            .Select(symbol => SymbolResolver.ToQueryResult(run.IndexId, codebase, channel, symbol, SymbolResolver.OriginFor(codebase)))
            .ToArray();
    }

    public async Task<SymbolSearchResult> SearchAsync(
        string query,
        IndexQueryOptions options,
        CancellationToken cancellationToken,
        SymbolKind? kind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (options.Limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "The query result limit must be positive.");

        var totalCount = 0;
        var completedIndexCount = 0;
        var candidates = new List<SymbolQueryResult>();
        foreach (var channel in Channels(options))
        {
            var run = await _repository.GetLatestCompletedIndexAsync(options.Codebase, channel, null, cancellationToken);
            if (run is null) continue;
            completedIndexCount++;
            var result = await SearchInRunAsync(
                run,
                options.Codebase,
                channel,
                query,
                options.Limit,
                kind,
                cancellationToken);
            totalCount += result.TotalCount;
            candidates.AddRange(result.Results);
        }

        var results = candidates
            .OrderBy(result => Rank(result, query))
            .ThenBy(result => result.QualifiedName, StringComparer.Ordinal)
            .ThenBy(result => result.Signature, StringComparer.Ordinal)
            .ThenBy(result => result.Channel, StringComparer.Ordinal)
            .ThenBy(result => result.SymbolId, StringComparer.Ordinal)
            .Take(options.Limit)
            .ToArray();
        return new SymbolSearchResult(
            totalCount,
            results.Length,
            results,
            completedIndexCount == 0 ? SymbolResolutionStatus.NoCompletedIndex : null);
    }

    public async Task<SymbolResolutionResult> ResolveAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        return (await ResolveAcrossChannelsAsync(selector, options, cancellationToken)).Resolution;
    }

    public async Task<CallableSurfaceResolutionResult> GetCallableSurfaceAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        if (options.Codebase != CodebaseKind.ScheduleI || options.Channel != CodeChannel.Installed || options.AllChannels)
            throw new ArgumentException("Callable surface is available only for the installed Schedule I index.", nameof(options));

        var run = await _repository.GetLatestCompletedIndexAsync(
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            null,
            cancellationToken);
        return run is null
            ? new CallableSurfaceResolutionResult(
                new SymbolResolutionResult(SymbolResolutionStatus.NoCompletedIndex, null, []),
                null)
            : await GetCallableSurfaceInIndexAsync(
                run,
                CodebaseKind.ScheduleI,
                CodeChannel.Installed,
                selector,
                cancellationToken);
    }

    public async Task<CallableSurfaceResolutionResult> GetCallableSurfaceInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var resolution = await _symbolResolver.ResolveAsync(run.IndexId, selector, codebase, channel, cancellationToken);
        if (resolution.Status != SymbolResolutionStatus.Resolved || resolution.Symbol is null)
            return new CallableSurfaceResolutionResult(resolution, null);

        var symbol = await _repository.GetCompletedSymbolByIdAsync(run.IndexId, resolution.Symbol.SymbolId, cancellationToken);
        if (symbol is null)
            return new CallableSurfaceResolutionResult(resolution, null);

        var records = await _repository.GetCompletedCallableSurfaceByGameSymbolIdAsync(
            run.IndexId,
            symbol.SymbolId,
            cancellationToken);
        if (records.Count > 1)
        {
            return new CallableSurfaceResolutionResult(
                resolution,
                ToCallableSurfaceQueryResult(
                    run.IndexId,
                    codebase,
                    channel,
                    CreateAmbiguousCallableSurface(run, symbol)));
        }

        var record = records.Count == 1
            ? records[0]
            : CreateLegacyCallableSurface(run, symbol);
        return new CallableSurfaceResolutionResult(
            resolution,
            ToCallableSurfaceQueryResult(run.IndexId, codebase, channel, record));
    }

    public Task<SymbolSearchResult> SearchInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string query,
        int limit,
        SymbolKind? kind,
        CancellationToken cancellationToken) =>
        SearchInRunAsync(run, codebase, channel, query, limit, kind, cancellationToken);

    public async Task<SymbolResolutionResult> ResolveInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        return (await ResolveInRunAsync(run, codebase, channel, selector, cancellationToken)).Resolution;
    }

    public async Task<IndexedSymbolPageResult> ListSymbolsInIndexAsync(
        IndexRunRecord run, CodebaseKind codebase, CodeChannel channel,
        IndexPageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(page);
        var total = await _repository.CountCompletedSymbolsAsync(run.IndexId, cancellationToken);
        var records = await _repository.GetCompletedSymbolPageAsync(run.IndexId, page.Offset, page.Limit, cancellationToken);
        var results = records.Select(symbol => new IndexedSymbolQueryResult(
            run.IndexId, codebase.ToString(), channel.ToString(), symbol.SymbolId,
            symbol.CanonicalKey, symbol.Kind, symbol.QualifiedName, symbol.Signature,
            symbol.IsBestEffort, symbol.BodyRecoveryStatus)).ToArray();
        return new IndexedSymbolPageResult(total, results, page.Offset + results.Length < total);
    }

    public async Task<NamespaceQueryResult> ListNamespacesInIndexAsync(
        IndexRunRecord run, CodebaseKind codebase, CodeChannel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        var total = await _repository.CountCompletedSymbolsAsync(run.IndexId, cancellationToken);
        const int pageSize = 512;
        for (var offset = 0; offset < total; offset += pageSize)
        {
            var records = await _repository.GetCompletedSymbolPageAsync(run.IndexId, offset, pageSize, cancellationToken);
            foreach (var symbol in records)
            {
                var namespaceName = CanonicalSymbolKeyParser.NamespaceFrom(symbol.CanonicalKey);
                if (!string.IsNullOrEmpty(namespaceName)) namespaces.Add(namespaceName);
            }
            if (records.Count == 0) break;
        }
        var ordered = namespaces.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        return new NamespaceQueryResult(ordered.Length, ordered);
    }

    public async Task<IndexSelectionQueryResult?> GetLatestCompletedIndexSelectionAsync(
        CodebaseKind codebase, CodeChannel channel, CancellationToken cancellationToken)
    {
        var run = await _repository.GetLatestCompletedIndexAsync(codebase, channel, null, cancellationToken);
        if (run is null) return null;
        var snapshot = await _repository.GetCodeSnapshotAsync(run.SnapshotId, cancellationToken);
        return snapshot is null || snapshot.Codebase != codebase || snapshot.Channel != channel
            ? null : new IndexSelectionQueryResult(run, snapshot);
    }

    public async Task<RelationshipEvidenceQueryResult> GetRelationshipEvidenceInIndexAsync(
        IndexRunRecord run, CodebaseKind codebase, CodeChannel channel, string symbolId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        var symbol = await _repository.GetCompletedSymbolByIdAsync(run.IndexId, symbolId, cancellationToken);
        if (symbol is null)
            return new RelationshipEvidenceQueryResult([], 0, [], 0, [], 0, "symbol not found in this index", "symbol not found in this index");
        var selected = new SelectedSymbol(
            channel,
            run,
            SymbolResolver.ToQueryResult(run.IndexId, codebase, channel, symbol, SymbolResolver.OriginFor(codebase)));
        var refs = await GetSelectedRelationshipEdgesAsync(selected, RelationshipQueryMode.Refs, int.MaxValue, cancellationToken);
        var callers = await GetSelectedRelationshipEdgesAsync(selected, RelationshipQueryMode.Callers, int.MaxValue, cancellationToken);
        var callees = await GetSelectedRelationshipEdgesAsync(selected, RelationshipQueryMode.Callees, int.MaxValue, cancellationToken);
        var mapped = await MapRelationshipEdgesAsync(
            run,
            refs.Relationships.Concat(callers.Relationships).Concat(callees.Relationships).DistinctBy(item => (item.Edge.RelationshipId, item.Direction)).ToArray(),
            SymbolResolver.OriginFor(codebase),
            cancellationToken);
        var referenceKeys = refs.Relationships.Select(item => (item.Edge.RelationshipId, item.Direction)).ToHashSet();
        var callerKeys = callers.Relationships.Select(item => (item.Edge.RelationshipId, item.Direction)).ToHashSet();
        var calleeKeys = callees.Relationships.Select(item => (item.Edge.RelationshipId, item.Direction)).ToHashSet();
        var bodyStatus = IsCallable(symbol.Kind) ? symbol.BodyRecoveryStatus ?? BodyRecoveryStatus.Unknown : (BodyRecoveryStatus?)null;
        return new RelationshipEvidenceQueryResult(
            mapped.Where(item => referenceKeys.Contains((item.RelationshipId, item.Direction))).Take(128).ToArray(), refs.TotalCount,
            mapped.Where(item => callerKeys.Contains((item.RelationshipId, item.Direction))).Take(128).ToArray(), callers.TotalCount,
            mapped.Where(item => calleeKeys.Contains((item.RelationshipId, item.Direction))).Take(128).ToArray(), callees.TotalCount,
            CompletenessNotice(bodyStatus, callers: true), CompletenessNotice(bodyStatus, callers: false));
    }

    public async Task<RelationshipEvidenceQueryResult> GetRelationshipEvidenceInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string symbolId,
        int relatedLimit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        if (relatedLimit <= 0 || relatedLimit > MaxSourceNeighborhoodLimit)
            throw new ArgumentOutOfRangeException(nameof(relatedLimit), $"The source neighborhood limit must be between 1 and {MaxSourceNeighborhoodLimit}.");

        var symbol = await _repository.GetCompletedSymbolByIdAsync(run.IndexId, symbolId, cancellationToken);
        if (symbol is null)
        {
            return new RelationshipEvidenceQueryResult(
                [],
                0,
                [],
                0,
                [],
                0,
                "symbol not found in this index",
                "symbol not found in this index");
        }

        var selected = new SelectedSymbol(
            channel,
            run,
            SymbolResolver.ToQueryResult(run.IndexId, codebase, channel, symbol, SymbolResolver.OriginFor(codebase)));
        var callers = await GetSelectedRelationshipEdgesAsync(selected, RelationshipQueryMode.Callers, int.MaxValue, cancellationToken);
        var callees = await GetSelectedRelationshipEdgesAsync(selected, RelationshipQueryMode.Callees, int.MaxValue, cancellationToken);
        var mapped = await MapRelationshipEdgesAsync(
            run,
            callers.Relationships.Concat(callees.Relationships).DistinctBy(item => (item.Edge.RelationshipId, item.Direction)).ToArray(),
            SymbolResolver.OriginFor(codebase),
            cancellationToken);
        var callerKeys = callers.Relationships.Select(item => (item.Edge.RelationshipId, item.Direction)).ToHashSet();
        var calleeKeys = callees.Relationships.Select(item => (item.Edge.RelationshipId, item.Direction)).ToHashSet();
        var bodyStatus = IsCallable(symbol.Kind) ? symbol.BodyRecoveryStatus ?? BodyRecoveryStatus.Unknown : (BodyRecoveryStatus?)null;

        return new RelationshipEvidenceQueryResult(
            [],
            0,
            mapped.Where(item => callerKeys.Contains((item.RelationshipId, item.Direction))).Take(relatedLimit).ToArray(),
            callers.TotalCount,
            mapped.Where(item => calleeKeys.Contains((item.RelationshipId, item.Direction))).Take(relatedLimit).ToArray(),
            callees.TotalCount,
            CompletenessNotice(bodyStatus, callers: true),
            CompletenessNotice(bodyStatus, callers: false));
    }

    public async Task<IReadOnlyList<SymbolQueryResult>> FindAsync(
        string query,
        SymbolKind kind,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        return (await SearchAsync(query, options, cancellationToken, kind)).Results;
    }

    public async Task<IReadOnlyList<SymbolQueryResult>> FindInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string query,
        SymbolKind kind,
        int limit,
        CancellationToken cancellationToken)
    {
        return (await SearchInRunAsync(run, codebase, channel, query, limit, kind, cancellationToken)).Results;
    }

    public Task<RelationshipQuerySetResult> RefsAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken) =>
        RelationshipSetAsync(selector, options, RelationshipQueryMode.Refs, cancellationToken);

    public Task<RelationshipQuerySetResult> RefsInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        CancellationToken cancellationToken) =>
        RelationshipSetInRunAsync(run, codebase, channel, selector, limit, RelationshipQueryMode.Refs, cancellationToken);

    public async Task<RelationshipQuerySetResult> RelatedTypesInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        IReadOnlySet<string> relationshipKinds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relationshipKinds);
        ValidateQueryLimit(limit, nameof(limit));
        var all = await RelationshipSetInRunAsync(
            run,
            codebase,
            channel,
            selector,
            int.MaxValue,
            RelationshipQueryMode.Refs,
            cancellationToken);
        if (all.Resolution.Status != SymbolResolutionStatus.Resolved)
            return all;

        var filtered = all.Relationships
            .Where(relationship => relationshipKinds.Contains(relationship.Kind))
            .ToArray();
        return all with
        {
            Relationships = filtered.Take(limit).ToArray(),
            TotalCount = filtered.Length
        };
    }

    public Task<RelationshipQuerySetResult> CallersAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken) =>
        RelationshipSetAsync(selector, options, RelationshipQueryMode.Callers, cancellationToken);

    public Task<RelationshipQuerySetResult> CallersInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        CancellationToken cancellationToken) =>
        RelationshipSetInRunAsync(run, codebase, channel, selector, limit, RelationshipQueryMode.Callers, cancellationToken);

    public Task<RelationshipQuerySetResult> CalleesAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken) =>
        RelationshipSetAsync(selector, options, RelationshipQueryMode.Callees, cancellationToken);

    public Task<RelationshipQuerySetResult> CalleesInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        CancellationToken cancellationToken) =>
        RelationshipSetInRunAsync(run, codebase, channel, selector, limit, RelationshipQueryMode.Callees, cancellationToken);

    public async Task<CallSiteQueryResult> CallSitesAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateQueryLimit(options.Limit, nameof(options));

        var totalCount = 0;
        var candidates = new List<RelationshipQueryResult>();
        foreach (var channel in Channels(options))
        {
            var run = await _repository.GetLatestCompletedIndexAsync(options.Codebase, channel, null, cancellationToken);
            if (run is null) continue;

            var page = await CallSitesInIndexAsync(run, options.Codebase, channel, selector, options.Limit, cancellationToken);
            totalCount += page.TotalCount;
            candidates.AddRange(page.Relationships);
        }

        var relationships = candidates
            .OrderBy(edge => edge.RelationshipId, StringComparer.Ordinal)
            .Take(options.Limit)
            .ToArray();
        return new CallSiteQueryResult(
            new RelationshipQueryPageResult(totalCount, relationships.Length, relationships),
            CallSiteCompletenessNotice);
    }

    public async Task<CallSiteQueryResult> CallSitesInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateQueryLimit(limit, nameof(limit));

        var targetQuery = await ResolveCallSiteTargetQueryAsync(run, codebase, channel, selector, cancellationToken);
        var totalCount = await _repository.CountCompletedRelationshipsByTargetTextAsync(
            run.IndexId,
            targetQuery.TargetText,
            targetQuery.MatchMode,
            "Calls",
            cancellationToken);
        if (totalCount == 0)
        {
            return new CallSiteQueryResult(
                new RelationshipQueryPageResult(0, 0, []),
                CallSiteCompletenessNotice);
        }

        var edges = await _repository.GetCompletedRelationshipsByTargetTextAsync(
            run.IndexId,
            targetQuery.TargetText,
            targetQuery.MatchMode,
            "Calls",
            limit,
            cancellationToken);
        var relationships = await MapRelationshipPageAsync(
            run,
            edges.Select(edge => (edge, "Incoming")).ToArray(),
            totalCount,
            SymbolResolver.OriginFor(codebase),
            cancellationToken);
        return new CallSiteQueryResult(relationships, CallSiteCompletenessNotice);
    }

    public Task<FieldReferenceQueryResult> FieldReferencesAsync(
        string selector,
        IndexQueryOptions options,
        FieldReferenceFilter filter,
        CancellationToken cancellationToken) =>
        FieldReferencesAcrossChannelsAsync(selector, options, filter, cancellationToken);

    public Task<FieldReferenceQueryResult> FieldReferencesInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        FieldReferenceFilter filter,
        CancellationToken cancellationToken) =>
        FieldReferencesInRunAsync(run, codebase, channel, selector, limit, filter, cancellationToken);

    private async Task<RelationshipQuerySetResult> RelationshipSetAsync(
        string selector,
        IndexQueryOptions options,
        RelationshipQueryMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var selection = await ResolveAcrossChannelsAsync(selector, options, cancellationToken);
        if (selection.Resolution.Status != SymbolResolutionStatus.Resolved || selection.Selected is null)
        {
            return new RelationshipQuerySetResult(
                selection.Resolution,
                [],
                null,
                mode == RelationshipQueryMode.Callers,
                string.Empty);
        }

        return await RelationshipSetFromSelectedAsync(
            selection.Selected.Value,
            mode,
            int.MaxValue,
            cancellationToken);
    }

    private async Task<FieldReferenceQueryResult> FieldReferencesAcrossChannelsAsync(
        string selector,
        IndexQueryOptions options,
        FieldReferenceFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateQueryLimit(options.Limit, nameof(options));

        var selection = await ResolveAcrossChannelsAsync(selector, options, cancellationToken);
        if (selection.Resolution.Status != SymbolResolutionStatus.Resolved || selection.Selected is null)
            return new FieldReferenceQueryResult(selection.Resolution, new RelationshipQueryPageResult(0, 0, []));

        return await FieldReferencesFromSelectedAsync(
            selection.Selected.Value,
            options.Limit,
            filter,
            cancellationToken);
    }

    public async Task<SourceSnippetResolutionResult> SourceInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int context,
        CancellationToken cancellationToken,
        bool fullType = false,
        int relatedLimit = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        if (context < 0)
            throw new ArgumentOutOfRangeException(nameof(context), "Source context cannot be negative.");
        ValidateSourceRelatedLimit(relatedLimit);
        if (_dataRoot is null)
            throw new InvalidOperationException("The Atlas data root is required for integrity-checked source queries.");

        var selection = await ResolveInRunAsync(run, codebase, channel, selector, cancellationToken);
        if (selection.Resolution.Status != SymbolResolutionStatus.Resolved || selection.Selected is null)
            return new SourceSnippetResolutionResult(selection.Resolution, null);
        return await SourceFromSelectedAsync(
            selection.Selected.Value,
            codebase,
            context,
            fullType,
            relatedLimit,
            cancellationToken);
    }

    public async Task<SourceSnippetResolutionResult> SourceAsync(
        string selector,
        IndexQueryOptions options,
        int context,
        CancellationToken cancellationToken,
        bool fullType = false,
        int relatedLimit = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        if (context < 0)
            throw new ArgumentOutOfRangeException(nameof(context), "Source context cannot be negative.");
        ValidateSourceRelatedLimit(relatedLimit);
        if (_dataRoot is null)
            throw new InvalidOperationException("The Atlas data root is required for integrity-checked source queries.");

        var selection = await ResolveAcrossChannelsAsync(selector, options, cancellationToken);
        if (selection.Resolution.Status != SymbolResolutionStatus.Resolved || selection.Selected is null)
            return new SourceSnippetResolutionResult(selection.Resolution, null);
        return await SourceFromSelectedAsync(
            selection.Selected.Value,
            options.Codebase,
            context,
            fullType,
            relatedLimit,
            cancellationToken);
    }

    private async Task<SymbolSearchResult> SearchInRunAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string query,
        int limit,
        SymbolKind? kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "The query result limit must be positive.");

        var kindName = kind?.ToString();
        var totalCount = await _repository.CountCompletedSymbolMatchesAsync(run.IndexId, query, cancellationToken, kindName);
        if (totalCount == 0)
            return new SymbolSearchResult(0, 0, [], null);

        var symbols = await _repository.SearchCompletedSymbolsAsync(
            run.IndexId,
            query,
            limit,
            cancellationToken,
            kindName);
        var results = symbols
            .Select(symbol => SymbolResolver.ToQueryResult(run.IndexId, codebase, channel, symbol, SymbolResolver.OriginFor(codebase)))
            .ToArray();
        return new SymbolSearchResult(totalCount, results.Length, results, null);
    }

    private static IndexCallableSurfaceRecord CreateLegacyCallableSurface(IndexRunRecord run, IndexSymbolRecord symbol) =>
        new(
            "legacy-" + symbol.SymbolId,
            run.IndexId,
            run.SnapshotId,
            symbol.SymbolId,
            symbol.CanonicalKey,
            "Assembly-CSharp.dll",
            null,
            null,
            symbol.IsPublic ? CallableSurfaceKind.DirectGameMember : CallableSurfaceKind.NonPublicWrapper,
            false,
            symbol.IsPublic ? CallableSurfaceStatus.Resolved : CallableSurfaceStatus.Unavailable,
            InteropInputTrust.LocalOnly,
            symbol.IsPublic
                ? "public game member is directly callable; no interop input was indexed"
                : "no callable-surface mapping was retained by this legacy index");

    private static IndexCallableSurfaceRecord CreateAmbiguousCallableSurface(IndexRunRecord run, IndexSymbolRecord symbol) =>
        new(
            "ambiguous-" + symbol.SymbolId,
            run.IndexId,
            run.SnapshotId,
            symbol.SymbolId,
            symbol.CanonicalKey,
            "Assembly-CSharp.dll",
            null,
            null,
            CallableSurfaceKind.NonPublicWrapper,
            false,
            CallableSurfaceStatus.Ambiguous,
            InteropInputTrust.LocalOnly,
            "multiple callable-surface mappings were retained for this game member");

    private static CallableSurfaceQueryResult ToCallableSurfaceQueryResult(
        string indexId,
        CodebaseKind codebase,
        CodeChannel channel,
        IndexCallableSurfaceRecord record) =>
        new(
            indexId,
            codebase.ToString(),
            channel.ToString(),
            record.GameSymbolId,
            record.GameCanonicalKey,
            record.Kind.ToString(),
            record.Status.ToString(),
            record.RequiresReflection,
            record.InteropAssemblyName,
            record.InteropInputSha256,
            record.InteropSignature,
            record.InteropInputTrust.ToString(),
            record.Evidence);

    private async Task<RelationshipQuerySetResult> RelationshipSetInRunAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        RelationshipQueryMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var selection = await ResolveInRunAsync(run, codebase, channel, selector, cancellationToken);
        if (selection.Resolution.Status != SymbolResolutionStatus.Resolved || selection.Selected is null)
        {
            return new RelationshipQuerySetResult(
                selection.Resolution,
                [],
                null,
                mode == RelationshipQueryMode.Callers,
                string.Empty);
        }

        return await RelationshipSetFromSelectedAsync(
            selection.Selected.Value,
            mode,
            limit,
            cancellationToken);
    }

    private async Task<RelationshipQuerySetResult> RelationshipSetFromSelectedAsync(
        SelectedSymbol selected,
        RelationshipQueryMode mode,
        int limit,
        CancellationToken cancellationToken)
    {
        var symbolRecord = await _repository.GetCompletedSymbolByIdAsync(
            selected.Run.IndexId,
            selected.Symbol.SymbolId,
            cancellationToken)
            ?? throw new InvalidDataException("The resolved symbol disappeared from the completed index.");
        BodyRecoveryStatus? bodyRecoveryStatus = IsCallable(symbolRecord.Kind)
            ? symbolRecord.BodyRecoveryStatus ?? BodyRecoveryStatus.Unknown
            : null;
        var selectedEdges = await GetSelectedRelationshipEdgesAsync(selected, mode, limit, cancellationToken);
        var relationships = (await MapRelationshipPageAsync(
            selected.Run,
            selectedEdges.Relationships,
            selectedEdges.TotalCount,
            selected.Symbol.Origin,
            cancellationToken)).Relationships;

        var notice = mode == RelationshipQueryMode.Refs
            ? string.Empty
            : CompletenessNotice(bodyRecoveryStatus, mode == RelationshipQueryMode.Callers);
        return new RelationshipQuerySetResult(
            new SymbolResolutionResult(SymbolResolutionStatus.Resolved, selected.Symbol, []),
            relationships,
            bodyRecoveryStatus,
            mode == RelationshipQueryMode.Callers,
            notice,
            selectedEdges.TotalCount);
    }

    private async Task<FieldReferenceQueryResult> FieldReferencesInRunAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        FieldReferenceFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateQueryLimit(limit, nameof(limit));

        var selection = await ResolveInRunAsync(run, codebase, channel, selector, cancellationToken);
        if (selection.Resolution.Status != SymbolResolutionStatus.Resolved || selection.Selected is null)
            return new FieldReferenceQueryResult(selection.Resolution, new RelationshipQueryPageResult(0, 0, []));

        return await FieldReferencesFromSelectedAsync(selection.Selected.Value, limit, filter, cancellationToken);
    }

    private async Task<FieldReferenceQueryResult> FieldReferencesFromSelectedAsync(
        SelectedSymbol selected,
        int limit,
        FieldReferenceFilter filter,
        CancellationToken cancellationToken)
    {
        var kinds = FieldRelationshipKinds(filter);
        var totalCount = 0;
        var edges = new List<IndexRelationshipRecord>();
        foreach (var kind in kinds)
        {
            totalCount += await _repository.CountCompletedRelationshipsByTargetSymbolIdAsync(
                selected.Run.IndexId,
                selected.Symbol.SymbolId,
                kind,
                cancellationToken);
            edges.AddRange(await _repository.GetCompletedRelationshipsByTargetSymbolIdAsync(
                selected.Run.IndexId,
                selected.Symbol.SymbolId,
                kind,
                limit,
                cancellationToken));
        }

        var page = await MapRelationshipPageAsync(
            selected.Run,
            edges.Select(edge => (edge, "Incoming"))
                .OrderBy(item => item.edge.RelationshipId, StringComparer.Ordinal)
                .Take(limit)
                .ToArray(),
            totalCount,
            selected.Symbol.Origin,
            cancellationToken);
        return new FieldReferenceQueryResult(
            new SymbolResolutionResult(SymbolResolutionStatus.Resolved, selected.Symbol, []),
            page);
    }

    private async Task<SelectedRelationshipEdges> GetSelectedRelationshipEdgesAsync(
        SelectedSymbol selected,
        RelationshipQueryMode mode,
        int limit,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<(IndexRelationshipRecord Edge, string Direction)> allEdges;
        if (mode == RelationshipQueryMode.Refs)
        {
            var outgoing = await _repository.GetCompletedRelationshipsBySourceSymbolIdAsync(selected.Run.IndexId, selected.Symbol.SymbolId, cancellationToken);
            var incoming = await _repository.GetCompletedRelationshipsByTargetSymbolIdAsync(selected.Run.IndexId, selected.Symbol.SymbolId, cancellationToken);
            allEdges = outgoing
                .Select(edge => (edge, "Outgoing"))
                .Concat(incoming.Select(edge => (edge, "Incoming")))
                .GroupBy(item => item.edge.RelationshipId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.edge.RelationshipId, StringComparer.Ordinal)
                .ToArray();
        }
        else if (mode == RelationshipQueryMode.Callers)
        {
            var incoming = await _repository.GetCompletedRelationshipsByTargetSymbolIdAsync(selected.Run.IndexId, selected.Symbol.SymbolId, cancellationToken);
            allEdges = incoming
                .Where(edge => IsCallLike(edge.Kind))
                .Select(edge => (edge, "Incoming"))
                .OrderBy(item => item.edge.RelationshipId, StringComparer.Ordinal)
                .ToArray();
        }
        else
        {
            var outgoing = await _repository.GetCompletedRelationshipsBySourceSymbolIdAsync(selected.Run.IndexId, selected.Symbol.SymbolId, cancellationToken);
            allEdges = outgoing
                .Where(edge => IsCallLike(edge.Kind))
                .Select(edge => (edge, "Outgoing"))
                .OrderBy(item => item.edge.RelationshipId, StringComparer.Ordinal)
                .ToArray();
        }

        return new SelectedRelationshipEdges(allEdges.Take(limit).ToArray(), allEdges.Count);
    }

    private async Task<IReadOnlyList<RelationshipQueryResult>> MapRelationshipEdgesAsync(
        IndexRunRecord run,
        IReadOnlyList<(IndexRelationshipRecord Edge, string Direction)> selectedEdges,
        string? origin,
        CancellationToken cancellationToken)
    {
        var endpointIds = selectedEdges
            .SelectMany(item => item.Edge.TargetSymbolId is null
                ? new[] { item.Edge.SourceSymbolId }
                : new[] { item.Edge.SourceSymbolId, item.Edge.TargetSymbolId })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var endpointSymbols = await _repository.GetCompletedSymbolsByIdsAsync(run.IndexId, endpointIds, cancellationToken);
        var byId = endpointSymbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        return selectedEdges
            .Select(item => new RelationshipQueryResult(
                item.Edge.RelationshipId,
                item.Edge.Kind,
                item.Edge.Evidence,
                item.Direction,
                Endpoint(item.Edge.SourceSymbolId, null, byId, origin),
                Endpoint(item.Edge.TargetSymbolId, item.Edge.TargetText, byId, origin)))
            .ToArray();
    }

    private async Task<RelationshipQueryPageResult> MapRelationshipPageAsync(
        IndexRunRecord run,
        IReadOnlyList<(IndexRelationshipRecord Edge, string Direction)> selectedEdges,
        int totalCount,
        string? origin,
        CancellationToken cancellationToken)
    {
        var ordered = selectedEdges
            .OrderBy(item => item.Edge.RelationshipId, StringComparer.Ordinal)
            .ThenBy(item => item.Direction, StringComparer.Ordinal)
            .ToArray();
        var relationships = await MapRelationshipEdgesAsync(run, ordered, origin, cancellationToken);
        return new RelationshipQueryPageResult(totalCount, relationships.Count, relationships);
    }

    private Task<CallSiteTargetQuery> ResolveCallSiteTargetQueryAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        CancellationToken cancellationToken) =>
        CallSiteSelectors.ResolveTargetQueryAsync(_symbolResolver, run, codebase, channel, selector, cancellationToken);

    private async Task<SourceSnippetResolutionResult> SourceFromSelectedAsync(
        SelectedSymbol selected,
        CodebaseKind codebase,
        int context,
        bool fullType,
        int relatedLimit,
        CancellationToken cancellationToken)
    {
        if (_dataRoot is null)
            throw new InvalidOperationException("The Atlas data root is required for integrity-checked source queries.");

        var symbolRecord = await _repository.GetCompletedSymbolByIdAsync(
            selected.Run.IndexId,
            selected.Symbol.SymbolId,
            cancellationToken)
            ?? throw new InvalidDataException("The resolved symbol disappeared from the completed index.");
        if (fullType && !string.Equals(selected.Symbol.Kind, SymbolKind.Type.ToString(), StringComparison.Ordinal))
        {
            var containingType = await ResolveContainingTypeAsync(selected, symbolRecord, codebase, cancellationToken);
            if (containingType is null)
            {
                return new SourceSnippetResolutionResult(
                    new SymbolResolutionResult(SymbolResolutionStatus.NotFound, null, []),
                    null);
            }

            selected = containingType.Value;
            symbolRecord = await _repository.GetCompletedSymbolByIdAsync(
                selected.Run.IndexId,
                selected.Symbol.SymbolId,
                cancellationToken)
                ?? throw new InvalidDataException("The containing type disappeared from the completed index.");
            relatedLimit = 0;
        }
        var locations = await _repository.GetCompletedSourceLocationsAsync(selected.Run.IndexId, cancellationToken);
        var matchingLocations = locations
            .Where(location => string.Equals(location.SymbolId, selected.Symbol.SymbolId, StringComparison.Ordinal))
            .OrderBy(location => location.StartLine)
            .ThenBy(location => location.StartColumn)
            .ToArray();
        if (matchingLocations.Length == 0)
            return new SourceSnippetResolutionResult(
                new SymbolResolutionResult(SymbolResolutionStatus.Resolved, selected.Symbol, []),
                null);
        if (matchingLocations.Length > 1)
            throw new InvalidDataException("The completed index contains multiple source locations for the selected symbol.");

        var locationRecord = matchingLocations[0];
        var files = await _repository.GetCompletedSourceFilesAsync(selected.Run.IndexId, cancellationToken);
        var sourceFile = files.SingleOrDefault(file =>
            string.Equals(file.SourceFileId, locationRecord.SourceFileId, StringComparison.Ordinal));
        if (sourceFile is null)
            throw new InvalidDataException("The selected symbol source location references a missing source file record.");

        var indexRoot = ResolveIndexRoot(_dataRoot, codebase, selected.Channel, selected.Run.IndexId);
        var sourcePath = ResolveContainedSourcePath(indexRoot, sourceFile.RelativePath);
        var read = await _sourceSnippetReader.ReadAsync(
            sourcePath,
            sourceFile.Sha256,
            locationRecord,
            context,
            cancellationToken);
        var selectedSpan = context == 0
            ? read.Text
            : (await _sourceSnippetReader.ReadAsync(
                sourcePath,
                sourceFile.Sha256,
                locationRecord,
                0,
                cancellationToken)).Text;
        var location = new SourceLocationQueryResult(
            locationRecord.SymbolId,
            locationRecord.StartLine,
            locationRecord.StartColumn,
            locationRecord.EndLine,
            locationRecord.EndColumn);
        BodyRecoveryStatus? bodyRecoveryStatus = IsCallable(symbolRecord.Kind)
            ? symbolRecord.BodyRecoveryStatus ?? BodyRecoveryStatus.Unknown
            : null;
        var runtimeVerification = RuntimeVerificationClassifier.Classify(selectedSpan, selected.Symbol.Signature);
        RelationshipEvidenceQueryResult? neighborhood = null;
        string? neighborhoodNotice = null;
        if (!fullType && relatedLimit > 0 && IsCallable(symbolRecord.Kind))
        {
            try
            {
                neighborhood = await GetRelationshipEvidenceInIndexAsync(
                    selected.Run,
                    codebase,
                    selected.Channel,
                    selected.Symbol.SymbolId,
                    relatedLimit,
                    cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                neighborhoodNotice = "Relationship neighborhood evidence was unavailable.";
            }
        }
        var snippet = new SourceSnippetQueryResult(
            selected.Symbol,
            selected.Run.IndexId,
            sourceFile.RelativePath,
            sourceFile.Sha256,
            sourceFile.ByteCount,
            location,
            read.ContextBefore,
            read.ContextAfter,
            read.Text,
            bodyRecoveryStatus,
            codebase + ":" + selected.Channel + ":generated",
            SymbolResolver.OriginFor(codebase),
            RuntimeVerification: runtimeVerification,
            Neighborhood: neighborhood,
            NeighborhoodNotice: neighborhoodNotice);
        return new SourceSnippetResolutionResult(
            new SymbolResolutionResult(SymbolResolutionStatus.Resolved, selected.Symbol, []),
            snippet);
    }

    private async Task<SelectedSymbol?> ResolveContainingTypeAsync(
        SelectedSymbol selected,
        IndexSymbolRecord? symbolRecord,
        CodebaseKind codebase,
        CancellationToken cancellationToken)
    {
        if (symbolRecord is null)
            return null;

        var canonicalParts = symbolRecord.CanonicalKey.Split(':', 4, StringSplitOptions.None);
        if (canonicalParts.Length < 4)
            return null;

        var memberKey = canonicalParts[3];
        var memberSeparator = memberKey.IndexOf("::", StringComparison.Ordinal);
        if (memberSeparator <= 0)
            return null;

        var typeCanonicalKey = $"{canonicalParts[0]}:{canonicalParts[1]}:{SymbolKind.Type}:{memberKey[..memberSeparator]}";
        var resolution = await _symbolResolver.ResolveAsync(
            selected.Run.IndexId,
            typeCanonicalKey,
            codebase,
            selected.Channel,
            cancellationToken);
        return resolution.Status == SymbolResolutionStatus.Resolved && resolution.Symbol is not null
            ? new SelectedSymbol(selected.Channel, selected.Run, resolution.Symbol)
            : null;
    }

    private async Task<ChannelSelection> ResolveAcrossChannelsAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken)
    {
        var resolved = new List<SelectedSymbol>();
        var ambiguous = new List<SymbolQueryResult>();
        var completedIndexCount = 0;
        foreach (var channel in Channels(options))
        {
            var run = await _repository.GetLatestCompletedIndexAsync(options.Codebase, channel, null, cancellationToken);
            if (run is null) continue;
            completedIndexCount++;

            var selection = await ResolveInRunAsync(run, options.Codebase, channel, selector, cancellationToken);
            var resolution = selection.Resolution;
            if (resolution.Status == SymbolResolutionStatus.Ambiguous)
            {
                ambiguous.AddRange(resolution.Candidates);
                continue;
            }
            if (resolution.Status == SymbolResolutionStatus.Resolved && selection.Selected is not null)
                resolved.Add(selection.Selected.Value);
        }

        if (ambiguous.Count > 0 || resolved.Count > 1)
        {
            var candidates = ambiguous
                .Concat(resolved.Select(item => item.Symbol))
                .GroupBy(candidate => candidate.SymbolId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(candidate => candidate.QualifiedName, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Signature, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Channel, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.SymbolId, StringComparer.Ordinal)
                .ToArray();
            return new ChannelSelection(
                new SymbolResolutionResult(SymbolResolutionStatus.Ambiguous, null, candidates),
                null);
        }

        if (completedIndexCount == 0)
            return new ChannelSelection(
                new SymbolResolutionResult(SymbolResolutionStatus.NoCompletedIndex, null, []),
                null);

        if (resolved.Count == 0)
            return new ChannelSelection(
                new SymbolResolutionResult(SymbolResolutionStatus.NotFound, null, []),
                null);

        return new ChannelSelection(
            new SymbolResolutionResult(SymbolResolutionStatus.Resolved, resolved[0].Symbol, []),
            resolved[0]);
    }

    private async Task<ChannelSelection> ResolveInRunAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var resolution = await _symbolResolver.ResolveAsync(
            run.IndexId,
            selector,
            codebase,
            channel,
            cancellationToken);
        if (resolution.Status == SymbolResolutionStatus.Resolved && resolution.Symbol is not null)
            return new ChannelSelection(resolution, new SelectedSymbol(channel, run, resolution.Symbol));
        return new ChannelSelection(resolution, null);
    }

    private static RelationshipEndpointQueryResult Endpoint(
        string? symbolId,
        string? rawText,
        IReadOnlyDictionary<string, IndexSymbolRecord> byId,
        string? origin)
    {
        if (symbolId is not null && byId.TryGetValue(symbolId, out var symbol))
            return new RelationshipEndpointQueryResult(
                symbol.SymbolId,
                symbol.QualifiedName,
                symbol.Signature,
                null,
                true,
                origin);

        return new RelationshipEndpointQueryResult(
            symbolId,
            null,
            null,
            rawText,
            false);
    }

    private static bool IsCallLike(string kind) =>
        string.Equals(kind, "Calls", StringComparison.Ordinal) ||
        string.Equals(kind, "Constructs", StringComparison.Ordinal);

    private static IReadOnlyList<string> FieldRelationshipKinds(FieldReferenceFilter filter) =>
        CallSiteSelectors.FieldRelationshipKinds(filter);

    private static void ValidateQueryLimit(int limit, string parameterName)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "The query result limit must be positive.");
    }

    private static void ValidateSourceRelatedLimit(int relatedLimit)
    {
        if (relatedLimit < 0 || relatedLimit > MaxSourceNeighborhoodLimit)
            throw new ArgumentOutOfRangeException(
                nameof(relatedLimit),
                $"The source neighborhood limit must be between 0 and {MaxSourceNeighborhoodLimit}.");
    }

    private static string CompletenessNotice(BodyRecoveryStatus? status, bool callers)
    {
        var bodyNotice = status switch
        {
            BodyRecoveryStatus.Recovered => "Atlas has affirmative recovered-body evidence.",
            BodyRecoveryStatus.NoBodyByDesign => "No implementation body is expected for this declaration.",
            BodyRecoveryStatus.StubOrUnavailable => "The body is stubbed or unavailable; zero call results are not definitive.",
            BodyRecoveryStatus.Unknown => "Body recovery is unknown; zero call results are not definitive.",
            null => "Call completeness is not applicable to a non-callable symbol.",
            _ => "Body recovery status is unrecognized; zero call results are not definitive."
        };
        return callers
            ? bodyNotice + " Incoming callers are limited to call sites whose target resolved to the selected symbol."
            : bodyNotice;
    }

    private static string ResolveIndexRoot(
        string dataRoot,
        CodebaseKind codebase,
        CodeChannel channel,
        string indexId)
    {
        var candidates = new List<string>();
        foreach (var candidate in EnumerateIndexRoots(dataRoot, codebase, channel, indexId))
        {
            if (Directory.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) == 0)
                candidates.Add(Path.GetFullPath(candidate));
        }

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new FileNotFoundException("The completed Atlas index source root was not found."),
            _ => throw new InvalidDataException("Multiple Atlas-owned source roots matched the completed index identity.")
        };
    }

    private static IEnumerable<string> EnumerateIndexRoots(
        string dataRoot,
        CodebaseKind codebase,
        CodeChannel channel,
        string indexId)
    {
        if (codebase == CodebaseKind.ScheduleI && channel == CodeChannel.Installed)
        {
            var buildsRoot = Path.Combine(dataRoot, "builds");
            if (!Directory.Exists(buildsRoot)) yield break;
            foreach (var buildRoot in Directory.EnumerateDirectories(buildsRoot))
            {
                if ((File.GetAttributes(buildRoot) & FileAttributes.ReparsePoint) != 0) continue;
                yield return Path.Combine(buildRoot, "indexes", indexId);
            }
            yield break;
        }

        if (codebase is not (CodebaseKind.S1Api or CodebaseKind.S1MApi))
            throw new NotSupportedException("Integrity-checked source path resolution is not available for this codebase/channel.");
        var segment = codebase == CodebaseKind.S1Api ? "s1api" : "s1mapi";
        var root = channel == CodeChannel.Installed
            ? Path.Combine(dataRoot, "installed", segment)
            : channel is CodeChannel.Release or CodeChannel.Preview
                ? Path.Combine(dataRoot, "upstream", segment, "commits")
                : throw new NotSupportedException("Integrity-checked source path resolution is not available for this codebase/channel.");
        if (!Directory.Exists(root)) yield break;
        foreach (var child in Directory.EnumerateDirectories(root))
            yield return Path.Combine(child, "indexes", indexId);
    }

    private static string ResolveContainedSourcePath(string indexRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Split(['/', '\\'], StringSplitOptions.None).Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("The indexed source path is not a safe relative path.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(indexRoot));
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("The indexed source path escaped its Atlas-owned index root.");
        return fullPath;
    }

    private static bool IsCallable(string kind) =>
        string.Equals(kind, SymbolKind.Method.ToString(), StringComparison.Ordinal) ||
        string.Equals(kind, SymbolKind.Constructor.ToString(), StringComparison.Ordinal);

    private static IReadOnlyList<CodeChannel> Channels(IndexQueryOptions options)
    {
        if (!options.AllChannels) return [options.Channel ?? CodeChannel.Installed];
        if (options.Codebase == CodebaseKind.ScheduleI) return [CodeChannel.Installed];
        return [CodeChannel.Installed, CodeChannel.Release, CodeChannel.Preview];
    }

    private static int Rank(SymbolQueryResult result, string query)
    {
        if (string.Equals(result.QualifiedName, query, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.Signature, query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (result.QualifiedName.EndsWith("." + query, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (result.QualifiedName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (result.QualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 3;
        if (result.Signature.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 4;
        return 5;
    }

    private enum RelationshipQueryMode
    {
        Refs,
        Callers,
        Callees
    }

    private readonly record struct SelectedSymbol(
        CodeChannel Channel,
        IndexRunRecord Run,
        SymbolQueryResult Symbol);

    private readonly record struct SelectedRelationshipEdges(
        IReadOnlyList<(IndexRelationshipRecord Edge, string Direction)> Relationships,
        int TotalCount);

    private readonly record struct ChannelSelection(
        SymbolResolutionResult Resolution,
        SelectedSymbol? Selected);
}
