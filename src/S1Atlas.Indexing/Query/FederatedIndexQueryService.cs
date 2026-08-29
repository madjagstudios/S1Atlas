using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Query;

public sealed class FederatedIndexQueryService
{
    private const int MaxSourceNeighborhoodLimit = 50;
    private const string CallSiteCompletenessNotice = TargetRelationshipQueryNotices.CallSites;
    private readonly IIndexRepository _repository;
    private readonly SymbolResolver _symbolResolver;
    private readonly IndexQueryService _game;
    private readonly ReferenceModQueryService _reference;

    public FederatedIndexQueryService(IIndexRepository repository, string? dataRoot = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        _symbolResolver = new SymbolResolver(repository);
        _game = new IndexQueryService(repository, dataRoot);
        _reference = new ReferenceModQueryService(repository, dataRoot);
    }

    public async Task<SymbolSearchResult> SearchAsync(
        string query,
        IndexQueryOptions options,
        CancellationToken cancellationToken,
        SymbolKind? kind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ValidateOptions(options);
        if (options.Limit <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Scope == IndexQueryScope.Game)
            return await _game.SearchAsync(query, options with { Scope = IndexQueryScope.Game }, cancellationToken, kind);
        if (options.Scope == IndexQueryScope.Reference)
            return await _reference.SearchAsync(query, options with { Scope = IndexQueryScope.Reference }, cancellationToken, kind);

        var selection = await _reference.GetSelectionForFederationAsync(options, cancellationToken);
        if (selection is null)
            return new SymbolSearchResult(0, 0, [], SymbolResolutionStatus.NoCompletedIndex);

        var game = await _game.SearchInIndexAsync(
            selection.GameRun,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            query,
            int.MaxValue,
            kind,
            cancellationToken);
        var reference = await _reference.SearchAsync(query, options with { Scope = IndexQueryScope.Reference, Limit = int.MaxValue }, cancellationToken, kind);
        var results = MergeSymbols(game.Results.Concat(reference.Results), query, options.Limit);
        SymbolResolutionStatus? status = results.Length > 0
            ? null
            : game.ResolutionStatus == SymbolResolutionStatus.NoCompletedIndex && reference.ResolutionStatus == SymbolResolutionStatus.NoCompletedIndex
                ? SymbolResolutionStatus.NoCompletedIndex
                : SymbolResolutionStatus.NotFound;
        return new SymbolSearchResult(game.TotalCount + reference.TotalCount, results.Length, results, status);
    }

    public async Task<SymbolResolutionResult> ResolveAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken,
        string? referenceIndexId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateOptions(options);
        if (options.Scope == IndexQueryScope.Game)
            return await _gameResolution(selector, options, cancellationToken);
        if (options.Scope == IndexQueryScope.Reference)
            return await _reference.ResolveAsync(selector, options, cancellationToken, referenceIndexId);

        var selection = await _reference.GetSelectionForFederationAsync(options, referenceIndexId, cancellationToken);
        if (selection is null)
            return new SymbolResolutionResult(SymbolResolutionStatus.NoCompletedIndex, null, []);

        var game = await _game.ResolveInIndexAsync(
            selection.GameRun,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            selector,
            cancellationToken);
        var reference = await ResolveReferenceInSelectionAsync(selection, selector, cancellationToken);
        var candidates = MergeSymbols(
            (game.Status == SymbolResolutionStatus.Ambiguous ? game.Candidates : game.Symbol is null ? [] : [game.Symbol])
                .Concat(reference.Status == SymbolResolutionStatus.Ambiguous ? reference.Candidates : reference.Symbol is null ? [] : [reference.Symbol]),
            selector,
            int.MaxValue);
        var hasAmbiguity = game.Status == SymbolResolutionStatus.Ambiguous || reference.Status == SymbolResolutionStatus.Ambiguous || candidates.Length > 1;
        if (hasAmbiguity)
            return new SymbolResolutionResult(SymbolResolutionStatus.Ambiguous, null, candidates);
        if (candidates.Length == 1)
            return new SymbolResolutionResult(SymbolResolutionStatus.Resolved, candidates[0], []);
        return new SymbolResolutionResult(
            game.Status == SymbolResolutionStatus.NoCompletedIndex && reference.Status == SymbolResolutionStatus.NoCompletedIndex
                ? SymbolResolutionStatus.NoCompletedIndex
                : SymbolResolutionStatus.NotFound,
            null,
            []);
    }

    public async Task<SourceSnippetResolutionResult> SourceAsync(
        string selector,
        IndexQueryOptions options,
        int context,
        CancellationToken cancellationToken,
        bool fullType = false,
        int relatedLimit = 10,
        string? referenceIndexId = null)
    {
        ValidateOptions(options);
        ValidateSourceRelatedLimit(relatedLimit);
        var selection = options.Scope == IndexQueryScope.Game
            ? null
            : await _reference.GetSelectionForFederationAsync(options, referenceIndexId, cancellationToken);
        if (options.Scope != IndexQueryScope.Game && selection is null)
            return new SourceSnippetResolutionResult(
                new SymbolResolutionResult(SymbolResolutionStatus.NoCompletedIndex, null, []),
                null);

        var resolution = await ResolveAsync(selector, options, cancellationToken, referenceIndexId);
        if (resolution.Status != SymbolResolutionStatus.Resolved || resolution.Symbol is null)
            return new SourceSnippetResolutionResult(resolution, null);
        return resolution.Symbol.Origin == "reference"
            ? await _reference.SourceAsync(selector, options with { Scope = IndexQueryScope.Reference }, context, cancellationToken, fullType, relatedLimit, referenceIndexId)
            : selection is null
                ? await _game.SourceAsync(selector, GameOptions(options, options.Limit), context, cancellationToken, fullType, relatedLimit)
                : await _game.SourceInIndexAsync(
                    selection.GameRun,
                    CodebaseKind.ScheduleI,
                    CodeChannel.Installed,
                    selector,
                    context,
                    cancellationToken,
                    fullType,
                    relatedLimit);
    }

    public Task<RelationshipQuerySetResult> RefsAsync(string selector, IndexQueryOptions options, CancellationToken cancellationToken) =>
        RelationshipsAsync(selector, options, RelationshipKind.Refs, cancellationToken);

    public Task<RelationshipQuerySetResult> CallersAsync(string selector, IndexQueryOptions options, CancellationToken cancellationToken) =>
        RelationshipsAsync(selector, options, RelationshipKind.Callers, cancellationToken);

    public Task<RelationshipQuerySetResult> CalleesAsync(string selector, IndexQueryOptions options, CancellationToken cancellationToken) =>
        RelationshipsAsync(selector, options, RelationshipKind.Callees, cancellationToken);

    public async Task<CallSiteQueryResult> CallSitesAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken,
        string? referenceIndexId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateOptions(options);
        ValidateLimit(options.Limit);

        if (options.Scope == IndexQueryScope.Game)
            return await _game.CallSitesAsync(selector, GameOptions(options, options.Limit), cancellationToken);

        var selection = await _reference.GetSelectionForFederationAsync(options, referenceIndexId, cancellationToken);
        if (selection is null)
            return new CallSiteQueryResult(new RelationshipQueryPageResult(0, 0, []), CallSiteCompletenessNotice);

        if (options.Scope == IndexQueryScope.Reference)
            return await ReferenceCallSitesAsync(selector, selection, options.Limit, cancellationToken);

        var targetQuery = await ResolveCallSiteTargetQueryAsync(
            selection.GameRun,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            selector,
            cancellationToken);
        var game = await _game.CallSitesInIndexAsync(
            selection.GameRun,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            selector,
            options.Limit,
            cancellationToken);
        var reference = await ReferenceCallSitesAsync(
            selector,
            selection,
            options.Limit,
            cancellationToken,
            targetQuery);
        return new CallSiteQueryResult(
            MergeRelationshipPages(game.Page, reference.Page, options.Limit),
            CallSiteCompletenessNotice);
    }

    public async Task<FieldReferenceQueryResult> FieldReferencesAsync(
        string selector,
        IndexQueryOptions options,
        FieldReferenceFilter filter,
        CancellationToken cancellationToken,
        string? referenceIndexId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateOptions(options);
        ValidateLimit(options.Limit);

        if (options.Scope == IndexQueryScope.Game)
            return await _game.FieldReferencesAsync(selector, GameOptions(options, options.Limit), filter, cancellationToken);

        var selection = await _reference.GetSelectionForFederationAsync(options, referenceIndexId, cancellationToken);
        if (selection is null)
            return new FieldReferenceQueryResult(
                new SymbolResolutionResult(SymbolResolutionStatus.NoCompletedIndex, null, []),
                new RelationshipQueryPageResult(0, 0, []));

        var resolution = options.Scope == IndexQueryScope.Reference
            ? await ResolveReferenceInSelectionAsync(selection, selector, cancellationToken)
            : await ResolveAsync(selector, options, cancellationToken, referenceIndexId);
        if (resolution.Status != SymbolResolutionStatus.Resolved || resolution.Symbol is null)
            return new FieldReferenceQueryResult(resolution, new RelationshipQueryPageResult(0, 0, []));

        if (resolution.Symbol.Origin == "reference")
            return await ReferenceFieldReferencesAsync(selection, resolution, filter, options.Limit, cancellationToken);

        var game = await _game.FieldReferencesInIndexAsync(
            selection.GameRun,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            selector,
            options.Limit,
            filter,
            cancellationToken);
        var reference = await ReferenceFieldReferencesForTargetSymbolAsync(
            selection,
            resolution.Symbol.SymbolId,
            filter,
            options.Limit,
            cancellationToken);
        return new FieldReferenceQueryResult(
            resolution,
            MergeRelationshipPages(game.Page, reference, options.Limit));
    }

    private async Task<RelationshipQuerySetResult> RelationshipsAsync(
        string selector,
        IndexQueryOptions options,
        RelationshipKind kind,
        CancellationToken cancellationToken)
    {
        var selection = options.Scope == IndexQueryScope.All
            ? await _reference.GetSelectionForFederationAsync(options, cancellationToken)
            : null;
        if (options.Scope == IndexQueryScope.All && selection is null)
            return new RelationshipQuerySetResult(
                new SymbolResolutionResult(SymbolResolutionStatus.NoCompletedIndex, null, []),
                [],
                null,
                kind == RelationshipKind.Callers,
                "no completed reference collection");

        var resolution = await ResolveAsync(selector, options, cancellationToken);
        if (resolution.Status != SymbolResolutionStatus.Resolved || resolution.Symbol is null)
            return new RelationshipQuerySetResult(resolution, [], null, kind == RelationshipKind.Callers, string.Empty);

        if (resolution.Symbol.Origin == "reference")
            return await ReferenceRelationshipsAsync(selector, options, kind, cancellationToken);

        var game = await GameRelationshipsAsync(selector, options, kind, cancellationToken, selection?.GameRun);
        if (options.Scope != IndexQueryScope.All || string.IsNullOrWhiteSpace(options.ReferenceCollection))
            return game;
        var reference = await ReferenceRelationshipsAsync(selector, options, kind, cancellationToken);
        return MergeRelationships(resolution, game, reference, kind);
    }

    private Task<RelationshipQuerySetResult> GameRelationshipsAsync(
        string selector,
        IndexQueryOptions options,
        RelationshipKind kind,
        CancellationToken cancellationToken,
        IndexRunRecord? pinnedRun = null) =>
        pinnedRun is null
            ? kind switch
            {
                RelationshipKind.Refs => _game.RefsAsync(selector, GameOptions(options, options.Limit), cancellationToken),
                RelationshipKind.Callers => _game.CallersAsync(selector, GameOptions(options, options.Limit), cancellationToken),
                _ => _game.CalleesAsync(selector, GameOptions(options, options.Limit), cancellationToken)
            }
            : kind switch
            {
                RelationshipKind.Refs => _game.RefsInIndexAsync(pinnedRun, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, options.Limit, cancellationToken),
                RelationshipKind.Callers => _game.CallersInIndexAsync(pinnedRun, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, options.Limit, cancellationToken),
                _ => _game.CalleesInIndexAsync(pinnedRun, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, options.Limit, cancellationToken)
            };

    private Task<RelationshipQuerySetResult> ReferenceRelationshipsAsync(string selector, IndexQueryOptions options, RelationshipKind kind, CancellationToken cancellationToken) =>
        kind switch
        {
            RelationshipKind.Refs => _reference.RefsAsync(selector, options, cancellationToken),
            RelationshipKind.Callers => _reference.CallersAsync(selector, options, cancellationToken),
            _ => _reference.CalleesAsync(selector, options, cancellationToken)
        };

    private async Task<CallSiteQueryResult> ReferenceCallSitesAsync(
        string selector,
        ReferenceModQueryService.IndexSelection selection,
        int limit,
        CancellationToken cancellationToken,
        CallSiteTargetQuery? targetQuery = null)
    {
        var query = targetQuery ?? await ResolveCallSiteTargetQueryAsync(
            selection.Run,
            CodebaseKind.ReferenceMod,
            CodeChannel.Installed,
            selector,
            cancellationToken);
        var totalCount = await Repository.CountCompletedRelationshipsByTargetTextAsync(
            selection.Run.IndexId,
            query.TargetText,
            query.MatchMode,
            "Calls",
            cancellationToken);
        if (totalCount == 0)
            return new CallSiteQueryResult(new RelationshipQueryPageResult(0, 0, []), CallSiteCompletenessNotice);

        var edges = await Repository.GetCompletedRelationshipsByTargetTextAsync(
            selection.Run.IndexId,
            query.TargetText,
            query.MatchMode,
            "Calls",
            limit,
            cancellationToken);
        var page = await MapReferenceRelationshipPageAsync(
            selection,
            edges.Select(edge => (edge, "Incoming")).ToArray(),
            totalCount,
            includeGameEndpoints: true,
            cancellationToken);
        return new CallSiteQueryResult(page, CallSiteCompletenessNotice);
    }

    private async Task<FieldReferenceQueryResult> ReferenceFieldReferencesAsync(
        ReferenceModQueryService.IndexSelection selection,
        SymbolResolutionResult resolution,
        FieldReferenceFilter filter,
        int limit,
        CancellationToken cancellationToken)
    {
        var page = await ReferenceFieldReferencesForTargetSymbolAsync(
            selection,
            resolution.Symbol!.SymbolId,
            filter,
            limit,
            cancellationToken);
        return new FieldReferenceQueryResult(resolution, page);
    }

    private async Task<RelationshipQueryPageResult> ReferenceFieldReferencesForTargetSymbolAsync(
        ReferenceModQueryService.IndexSelection selection,
        string targetSymbolId,
        FieldReferenceFilter filter,
        int limit,
        CancellationToken cancellationToken)
    {
        var totalCount = 0;
        var edges = new List<IndexRelationshipRecord>();
        foreach (var kind in FieldRelationshipKinds(filter))
        {
            totalCount += await Repository.CountCompletedRelationshipsByTargetSymbolIdAsync(
                selection.Run.IndexId,
                targetSymbolId,
                kind,
                cancellationToken);
            edges.AddRange(await Repository.GetCompletedRelationshipsByTargetSymbolIdAsync(
                selection.Run.IndexId,
                targetSymbolId,
                kind,
                limit,
                cancellationToken));
        }

        return await MapReferenceRelationshipPageAsync(
            selection,
            edges.Select(edge => (edge, "Incoming"))
                .OrderBy(item => item.edge.RelationshipId, StringComparer.Ordinal)
                .Take(limit)
                .ToArray(),
            totalCount,
            includeGameEndpoints: true,
            cancellationToken);
    }

    private static RelationshipQuerySetResult MergeRelationships(
        SymbolResolutionResult resolution,
        RelationshipQuerySetResult game,
        RelationshipQuerySetResult reference,
        RelationshipKind kind)
    {
        var relationships = game.Relationships
            .Concat(reference.Relationships)
            .GroupBy(edge => (
                Origin: edge.Source.Origin ?? string.Empty,
                ModId: edge.Source.ReferenceModId ?? string.Empty,
                SymbolId: edge.Source.SymbolId ?? string.Empty,
                RelationshipId: edge.RelationshipId,
                Direction: edge.Direction))
            .Select(group => group.First())
            .OrderBy(edge => edge.RelationshipId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Source.Origin, StringComparer.Ordinal)
            .ThenBy(edge => edge.Source.ReferenceModId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Target.SymbolId, StringComparer.Ordinal)
            .ToArray();
        return new RelationshipQuerySetResult(
            resolution,
            relationships,
            game.BodyRecoveryStatus ?? reference.BodyRecoveryStatus,
            kind == RelationshipKind.Callers,
            game.CompletenessNotice + reference.CompletenessNotice);
    }

    private async Task<RelationshipQueryPageResult> MapReferenceRelationshipPageAsync(
        ReferenceModQueryService.IndexSelection selection,
        IReadOnlyList<(IndexRelationshipRecord Edge, string Direction)> selectedEdges,
        int totalCount,
        bool includeGameEndpoints,
        CancellationToken cancellationToken)
    {
        var ordered = selectedEdges
            .OrderBy(item => item.Edge.RelationshipId, StringComparer.Ordinal)
            .ThenBy(item => item.Direction, StringComparer.Ordinal)
            .ToArray();
        var ids = ordered
            .SelectMany(item => new[] { item.Edge.SourceSymbolId, item.Edge.TargetSymbolId })
            .Where(id => id is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var referenceSymbols = await Repository.GetCompletedSymbolsByIdsAsync(selection.Run.IndexId, ids, cancellationToken);
        var gameSymbols = includeGameEndpoints
            ? await Repository.GetCompletedSymbolsByIdsAsync(selection.Context.GameIndexId, ids, cancellationToken)
            : [];
        var referenceById = referenceSymbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        var gameById = gameSymbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        var relationships = ordered
            .Select(item => new RelationshipQueryResult(
                item.Edge.RelationshipId,
                item.Edge.Kind,
                item.Edge.Evidence,
                item.Direction,
                MapReferenceEndpoint(item.Edge.SourceSymbolId, null, referenceById, gameById, selection),
                MapReferenceEndpoint(item.Edge.TargetSymbolId, item.Edge.TargetText, referenceById, gameById, selection)))
            .ToArray();
        return new RelationshipQueryPageResult(totalCount, relationships.Length, relationships);
    }

    private static SymbolQueryResult[] MergeSymbols(IEnumerable<SymbolQueryResult> candidates, string query, int limit) =>
        candidates
            .GroupBy(candidate => (candidate.Origin ?? string.Empty, candidate.ReferenceModId ?? string.Empty, candidate.SymbolId))
            .Select(group => group.First())
            .OrderBy(candidate => Rank(candidate, query))
            .ThenBy(candidate => candidate.Origin, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ReferenceModId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.QualifiedName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Signature, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.SymbolId, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();

    private static RelationshipQueryPageResult MergeRelationshipPages(
        RelationshipQueryPageResult first,
        RelationshipQueryPageResult second,
        int limit)
    {
        var relationships = first.Relationships
            .Concat(second.Relationships)
            .GroupBy(edge => (
                edge.RelationshipId,
                edge.Direction,
                SourceOrigin: edge.Source.Origin ?? string.Empty,
                SourceModId: edge.Source.ReferenceModId ?? string.Empty,
                SourceSymbolId: edge.Source.SymbolId ?? string.Empty,
                TargetOrigin: edge.Target.Origin ?? string.Empty,
                TargetSymbolId: edge.Target.SymbolId ?? string.Empty,
                TargetRawText: edge.Target.RawText ?? string.Empty))
            .Select(group => group.First())
            .OrderBy(edge => edge.RelationshipId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Direction, StringComparer.Ordinal)
            .ThenBy(edge => edge.Source.Origin, StringComparer.Ordinal)
            .ThenBy(edge => edge.Source.ReferenceModId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Source.SymbolId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Target.Origin, StringComparer.Ordinal)
            .ThenBy(edge => edge.Target.SymbolId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Target.RawText, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        return new RelationshipQueryPageResult(first.TotalCount + second.TotalCount, relationships.Length, relationships);
    }

    private static int Rank(SymbolQueryResult result, string query)
    {
        if (string.Equals(result.QualifiedName, query, StringComparison.OrdinalIgnoreCase) || string.Equals(result.Signature, query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (result.QualifiedName.EndsWith("." + query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (result.QualifiedName.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (result.QualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (result.Signature.Contains(query, StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }

    private static IndexQueryOptions GameOptions(IndexQueryOptions options, int limit) =>
        options with { Codebase = CodebaseKind.ScheduleI, Scope = IndexQueryScope.Game, ReferenceCollection = null, Limit = limit };

    private static void ValidateOptions(IndexQueryOptions options)
    {
        if (options.Scope == IndexQueryScope.Game && !string.IsNullOrWhiteSpace(options.ReferenceCollection))
            throw new ArgumentException("ReferenceCollection is valid only for All or Reference scope.", nameof(options));
    }

    private static void ValidateLimit(int limit)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "The query result limit must be positive.");
    }

    private static void ValidateSourceRelatedLimit(int relatedLimit)
    {
        if (relatedLimit < 0 || relatedLimit > MaxSourceNeighborhoodLimit)
            throw new ArgumentOutOfRangeException(nameof(relatedLimit), $"The source neighborhood limit must be between 0 and {MaxSourceNeighborhoodLimit}.");
    }

    private Task<SymbolResolutionResult> _gameResolution(string selector, IndexQueryOptions options, CancellationToken cancellationToken) =>
        _game.ResolveAsync(selector, GameOptions(options, options.Limit), cancellationToken);

    private async Task<SymbolResolutionResult> ResolveReferenceInSelectionAsync(
        ReferenceModQueryService.IndexSelection selection,
        string selector,
        CancellationToken cancellationToken)
    {
        var result = await SymbolResolver.ResolveAsync(
            selection.Run.IndexId,
            selector,
            CodebaseKind.ReferenceMod,
            CodeChannel.Installed,
            cancellationToken);
        return new SymbolResolutionResult(
            result.Status,
            result.Symbol is null ? null : DecorateReferenceQuerySymbol(selection, result.Symbol),
            result.Candidates.Select(candidate => DecorateReferenceQuerySymbol(selection, candidate)).ToArray());
    }

    private Task<CallSiteTargetQuery> ResolveCallSiteTargetQueryAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        CancellationToken cancellationToken) =>
        CallSiteSelectors.ResolveTargetQueryAsync(SymbolResolver, run, codebase, channel, selector, cancellationToken);

    private RelationshipEndpointQueryResult MapReferenceEndpoint(
        string? symbolId,
        string? rawText,
        IReadOnlyDictionary<string, IndexSymbolRecord> referenceById,
        IReadOnlyDictionary<string, IndexSymbolRecord> gameById,
        ReferenceModQueryService.IndexSelection selection)
    {
        if (symbolId is not null && referenceById.TryGetValue(symbolId, out var reference))
        {
            var symbol = DecorateReferenceSymbol(selection, reference);
            return new RelationshipEndpointQueryResult(
                symbol.SymbolId,
                symbol.QualifiedName,
                symbol.Signature,
                null,
                true,
                symbol.Origin,
                symbol.Collection,
                symbol.ReferenceModId,
                symbol.DisplayName,
                symbol.Version,
                symbol.License,
                symbol.RelativePath,
                symbol.Sha256);
        }

        if (symbolId is not null && gameById.TryGetValue(symbolId, out var game))
        {
            var symbol = SymbolResolver.ToQueryResult(selection.Context.GameIndexId, CodebaseKind.ScheduleI, CodeChannel.Installed, game, "game");
            return new RelationshipEndpointQueryResult(
                symbol.SymbolId,
                symbol.QualifiedName,
                symbol.Signature,
                null,
                true,
                symbol.Origin,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        return new RelationshipEndpointQueryResult(symbolId, null, null, rawText, false);
    }

    private SymbolQueryResult DecorateReferenceSymbol(
        ReferenceModQueryService.IndexSelection selection,
        IndexSymbolRecord symbol) =>
        DecorateReferenceQuerySymbol(
            selection,
            SymbolResolver.ToQueryResult(selection.Run.IndexId, CodebaseKind.ReferenceMod, CodeChannel.Installed, symbol, "reference", selection.Collection));

    private SymbolQueryResult DecorateReferenceQuerySymbol(
        ReferenceModQueryService.IndexSelection selection,
        SymbolQueryResult result)
    {
        var mod = selection.Mods.FirstOrDefault(candidate => candidate.SymbolIds.Contains(result.SymbolId, StringComparer.Ordinal));
        var source = SourceProvenance(selection, result.SymbolId);
        return result with
        {
            Origin = "reference",
            Collection = selection.Collection,
            ReferenceModId = mod?.ModId,
            DisplayName = mod?.DisplayName,
            Version = mod?.Version,
            License = mod?.License,
            RelativePath = source.RelativePath,
            Sha256 = source.Sha256
        };
    }

    private static (string? RelativePath, string? Sha256) SourceProvenance(
        ReferenceModQueryService.IndexSelection selection,
        string symbolId)
    {
        var locations = selection.SourceLocations
            .Where(location => string.Equals(location.SymbolId, symbolId, StringComparison.Ordinal))
            .ToArray();
        if (locations.Length != 1)
            return (null, null);

        var sourceFile = selection.SourceFiles.SingleOrDefault(file =>
            string.Equals(file.SourceFileId, locations[0].SourceFileId, StringComparison.Ordinal));
        return sourceFile is null ? (null, null) : (sourceFile.RelativePath, sourceFile.Sha256);
    }

    private static IReadOnlyList<string> FieldRelationshipKinds(FieldReferenceFilter filter) =>
        CallSiteSelectors.FieldRelationshipKinds(filter);

    private IIndexRepository Repository => _repository;

    private SymbolResolver SymbolResolver => _symbolResolver;

    private enum RelationshipKind { Refs, Callers, Callees }
}
