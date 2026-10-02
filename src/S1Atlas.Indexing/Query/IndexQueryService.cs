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
        SymbolKind? kind = null,
        bool includeGenerated = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (options.Limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "The query result limit must be positive.");

        var totalCount = 0;
        var hiddenCount = 0;
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
                cancellationToken,
                includeGenerated);
            totalCount += result.TotalCount;
            if (GeneratedSearchNotice.TryParseHiddenCount(result.SearchNotice, out var channelHidden))
                hiddenCount += channelHidden;
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
            completedIndexCount == 0 ? SymbolResolutionStatus.NoCompletedIndex : null,
            GeneratedSearchNotice.ForHidden(hiddenCount));
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
        CancellationToken cancellationToken,
        bool includeGenerated = false) =>
        SearchInRunAsync(run, codebase, channel, query, limit, kind, cancellationToken, includeGenerated);

    internal const string SearchIndexFallbackNotice =
        "search index not built; run any s1atlas write command, e.g. `s1atlas index`, to upgrade";

    private static string CombineNotices(string first, string? second) =>
        second is null ? first : first + "; " + second;

    // Serve-only ranked search. Uses the FTS trigram index when the atlas has
    // been migrated; otherwise falls back to the LIKE scan with a visible
    // notice. CLI and MCP keep SearchInIndexAsync unchanged.
    public async Task<SymbolSearchResult> SearchRankedInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string query,
        int limit,
        SymbolKind? kind,
        CancellationToken cancellationToken,
        bool includeGenerated = false)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "The query result limit must be positive.");

        var kindName = kind?.ToString();
        if (!await _repository.SupportsSymbolSearchIndexAsync(cancellationToken))
        {
            var fallbackTotal = await _repository.CountCompletedSymbolMatchesAsync(
                run.IndexId, query, cancellationToken, kindName, includeGenerated);
            var fallbackHidden = await GeneratedSearchNotice.ForHiddenAsync(
                () => _repository.CountCompletedSymbolMatchesAsync(
                    run.IndexId, query, cancellationToken, kindName, includeGenerated: true),
                fallbackTotal,
                includeGenerated);
            if (fallbackTotal == 0)
                return new SymbolSearchResult(0, 0, [], null, CombineNotices(SearchIndexFallbackNotice, fallbackHidden));

            var fallbackSymbols = await _repository.SearchCompletedSymbolsAsync(
                run.IndexId, query, limit, cancellationToken, kindName, includeGenerated);
            return new SymbolSearchResult(
                fallbackTotal,
                fallbackSymbols.Count,
                fallbackSymbols
                    .Select(symbol => SymbolResolver.ToQueryResult(run.IndexId, codebase, channel, symbol, SymbolResolver.OriginFor(codebase)))
                    .ToArray(),
                null,
                CombineNotices(SearchIndexFallbackNotice, fallbackHidden));
        }

        var totalCount = await _repository.CountRankedSymbolMatchesAsync(run.IndexId, query, cancellationToken, kindName, includeGenerated);
        var notice = await GeneratedSearchNotice.ForHiddenAsync(
            () => _repository.CountRankedSymbolMatchesAsync(
                run.IndexId, query, cancellationToken, kindName, includeGenerated: true),
            totalCount,
            includeGenerated);
        if (totalCount == 0)
            return new SymbolSearchResult(0, 0, [], null, notice);

        var symbols = await _repository.SearchRankedSymbolsAsync(
            run.IndexId, query, limit, cancellationToken, kindName, includeGenerated);
        var results = symbols
            .Select(symbol => SymbolResolver.ToQueryResult(run.IndexId, codebase, channel, symbol, SymbolResolver.OriginFor(codebase)))
            .ToArray();
        return new SymbolSearchResult(totalCount, results.Length, results, null, notice);
    }

    public async Task<SymbolResolutionResult> ResolveInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        CancellationToken cancellationToken,
        IReadOnlySet<SymbolKind>? kinds = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        return (await ResolveInRunAsync(run, codebase, channel, selector, cancellationToken, kinds)).Resolution;
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
        CancellationToken cancellationToken,
        bool includeGenerated = false)
    {
        return (await SearchAsync(query, options, cancellationToken, kind, includeGenerated)).Results;
    }

    public async Task<IReadOnlyList<SymbolQueryResult>> FindInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string query,
        SymbolKind kind,
        int limit,
        CancellationToken cancellationToken,
        bool includeGenerated = false)
    {
        return (await SearchInRunAsync(run, codebase, channel, query, limit, kind, cancellationToken, includeGenerated)).Results;
    }

    public Task<RelationshipQuerySetResult> RefsAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken,
        bool includeGenerated = false) =>
        RelationshipSetAsync(selector, options, RelationshipQueryMode.Refs, cancellationToken, includeGenerated: includeGenerated);

    public Task<RelationshipQuerySetResult> RefsInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        CancellationToken cancellationToken,
        bool includeGenerated = false) =>
        RelationshipSetInRunAsync(run, codebase, channel, selector, limit, RelationshipQueryMode.Refs, cancellationToken, includeGenerated: includeGenerated);

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
        CancellationToken cancellationToken,
        bool exact = false,
        bool includeGenerated = false,
        bool includeDelegates = false) =>
        RelationshipSetAsync(selector, options, RelationshipQueryMode.Callers, cancellationToken, exact, includeGenerated, includeDelegates);

    public Task<RelationshipQuerySetResult> CallersInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        CancellationToken cancellationToken,
        bool exact = false,
        bool includeGenerated = false,
        bool includeDelegates = false) =>
        RelationshipSetInRunAsync(run, codebase, channel, selector, limit, RelationshipQueryMode.Callers, cancellationToken, exact, includeGenerated, includeDelegates);

    public Task<RelationshipQuerySetResult> CalleesAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken,
        bool includeGenerated = false,
        bool includeDelegates = false) =>
        RelationshipSetAsync(selector, options, RelationshipQueryMode.Callees, cancellationToken, includeGenerated: includeGenerated, includeDelegates: includeDelegates);

    public Task<RelationshipQuerySetResult> CalleesInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        CancellationToken cancellationToken,
        bool includeGenerated = false,
        bool includeDelegates = false) =>
        RelationshipSetInRunAsync(run, codebase, channel, selector, limit, RelationshipQueryMode.Callees, cancellationToken, includeGenerated: includeGenerated, includeDelegates: includeDelegates);

    public Task<HierarchyQueryResult> OverridesAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken) =>
        HierarchyAcrossChannelsAsync(selector, options, HierarchyQueryMode.Overrides, HierarchyTraversal.FullChainDepth, 0, cancellationToken);

    public Task<HierarchyQueryResult> OverriddenByAsync(
        string selector,
        IndexQueryOptions options,
        int depth,
        CancellationToken cancellationToken) =>
        HierarchyAcrossChannelsAsync(selector, options, HierarchyQueryMode.OverriddenBy, depth, 0, cancellationToken);

    public Task<HierarchyQueryResult> DerivedAsync(
        string selector,
        IndexQueryOptions options,
        int depth,
        int offset,
        CancellationToken cancellationToken) =>
        HierarchyAcrossChannelsAsync(selector, options, HierarchyQueryMode.Derived, depth, offset, cancellationToken);

    public Task<HierarchyQueryResult> OverridesInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        CancellationToken cancellationToken) =>
        HierarchyInRunAsync(run, codebase, channel, selector, limit, HierarchyQueryMode.Overrides, HierarchyTraversal.FullChainDepth, 0, cancellationToken);

    public Task<HierarchyQueryResult> OverriddenByInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        int depth,
        CancellationToken cancellationToken) =>
        HierarchyInRunAsync(run, codebase, channel, selector, limit, HierarchyQueryMode.OverriddenBy, depth, 0, cancellationToken);

    public Task<HierarchyQueryResult> DerivedInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        int depth,
        int offset,
        CancellationToken cancellationToken) =>
        HierarchyInRunAsync(run, codebase, channel, selector, limit, HierarchyQueryMode.Derived, depth, offset, cancellationToken);

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
        var totalCount = 0;
        foreach (var kind in CallSiteKinds.Names)
        {
            totalCount += await _repository.CountCompletedRelationshipsByTargetTextAsync(
                run.IndexId,
                targetQuery.TargetText,
                targetQuery.MatchMode,
                kind,
                cancellationToken);
        }

        if (totalCount == 0)
        {
            return new CallSiteQueryResult(
                new RelationshipQueryPageResult(0, 0, []),
                CallSiteCompletenessNotice);
        }

        var fetched = new List<IndexRelationshipRecord>();
        foreach (var kind in CallSiteKinds.Names)
        {
            fetched.AddRange(await _repository.GetCompletedRelationshipsByTargetTextAsync(
                run.IndexId,
                targetQuery.TargetText,
                targetQuery.MatchMode,
                kind,
                limit,
                cancellationToken));
        }

        var edges = CallSiteKinds.MergeAndTake(fetched, limit);
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
        CancellationToken cancellationToken,
        bool includeGenerated = false) =>
        FieldReferencesAcrossChannelsAsync(selector, options, filter, cancellationToken, includeGenerated);

    public Task<FieldReferenceQueryResult> FieldReferencesInIndexAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        FieldReferenceFilter filter,
        CancellationToken cancellationToken,
        bool includeGenerated = false) =>
        FieldReferencesInRunAsync(run, codebase, channel, selector, limit, filter, cancellationToken, includeGenerated);

    private async Task<RelationshipQuerySetResult> RelationshipSetAsync(
        string selector,
        IndexQueryOptions options,
        RelationshipQueryMode mode,
        CancellationToken cancellationToken,
        bool exact = false,
        bool includeGenerated = false,
        bool includeDelegates = false)
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
            cancellationToken,
            exact,
            includeGenerated,
            includeDelegates);
    }

    private async Task<FieldReferenceQueryResult> FieldReferencesAcrossChannelsAsync(
        string selector,
        IndexQueryOptions options,
        FieldReferenceFilter filter,
        CancellationToken cancellationToken,
        bool includeGenerated = false)
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
            cancellationToken,
            includeGenerated);
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
        CancellationToken cancellationToken,
        bool includeGenerated = false)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "The query result limit must be positive.");

        var kindName = kind?.ToString();
        var totalCount = await _repository.CountCompletedSymbolMatchesAsync(run.IndexId, query, cancellationToken, kindName, includeGenerated);
        var notice = await GeneratedSearchNotice.ForHiddenAsync(
            () => _repository.CountCompletedSymbolMatchesAsync(
                run.IndexId, query, cancellationToken, kindName, includeGenerated: true),
            totalCount,
            includeGenerated);
        if (totalCount == 0)
            return new SymbolSearchResult(0, 0, [], null, notice);

        var symbols = await _repository.SearchCompletedSymbolsAsync(
            run.IndexId,
            query,
            limit,
            cancellationToken,
            kindName,
            includeGenerated);
        var results = symbols
            .Select(symbol => SymbolResolver.ToQueryResult(run.IndexId, codebase, channel, symbol, SymbolResolver.OriginFor(codebase)))
            .ToArray();
        return new SymbolSearchResult(totalCount, results.Length, results, null, notice);
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
        CancellationToken cancellationToken,
        bool exact = false,
        bool includeGenerated = false,
        bool includeDelegates = false)
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
            cancellationToken,
            exact,
            includeGenerated,
            includeDelegates);
    }

    private async Task<RelationshipQuerySetResult> RelationshipSetFromSelectedAsync(
        SelectedSymbol selected,
        RelationshipQueryMode mode,
        int limit,
        CancellationToken cancellationToken,
        bool exact = false,
        bool includeGenerated = false,
        bool includeDelegates = false)
    {
        var symbolRecord = await _repository.GetCompletedSymbolByIdAsync(
            selected.Run.IndexId,
            selected.Symbol.SymbolId,
            cancellationToken)
            ?? throw new InvalidDataException("The resolved symbol disappeared from the completed index.");
        BodyRecoveryStatus? bodyRecoveryStatus = IsCallable(symbolRecord.Kind)
            ? symbolRecord.BodyRecoveryStatus ?? BodyRecoveryStatus.Unknown
            : null;
        if (mode == RelationshipQueryMode.Callers && !exact)
            return await ExpandedCallersFromSelectedAsync(selected, bodyRecoveryStatus, limit, cancellationToken, includeGenerated, includeDelegates);
        var selectedEdges = await GetSelectedRelationshipEdgesAsync(selected, mode, limit, cancellationToken, includeDelegates);
        var relationships = (await MapRelationshipPageAsync(
            selected.Run,
            selectedEdges.Relationships,
            selectedEdges.TotalCount,
            selected.Symbol.Origin,
            cancellationToken,
            includeGenerated,
            LabelContextFor(mode))).Relationships;

        var notice = mode == RelationshipQueryMode.Refs
            ? string.Empty
            : CompletenessNotice(bodyRecoveryStatus, mode == RelationshipQueryMode.Callers);
        return new RelationshipQuerySetResult(
            new SymbolResolutionResult(SymbolResolutionStatus.Resolved, selected.Symbol, []),
            relationships,
            bodyRecoveryStatus,
            mode == RelationshipQueryMode.Callers,
            notice,
            selectedEdges.TotalCount,
            ExactCount: mode == RelationshipQueryMode.Callers ? selectedEdges.TotalCount : null,
            DerivedCount: mode == RelationshipQueryMode.Callers ? 0 : null);
    }

    private async Task<RelationshipQuerySetResult> ExpandedCallersFromSelectedAsync(
        SelectedSymbol selected,
        BodyRecoveryStatus? bodyRecoveryStatus,
        int limit,
        CancellationToken cancellationToken,
        bool includeGenerated = false,
        bool includeDelegates = false)
    {
        var incoming = await _repository.GetCompletedRelationshipsByTargetSymbolIdAsync(
            selected.Run.IndexId,
            selected.Symbol.SymbolId,
            cancellationToken);
        var exact = await MapRelationshipEdgesAsync(
            selected.Run,
            incoming
                .Where(edge => IsCallLike(edge.Kind) || IsIncludedDelegate(edge, includeDelegates))
                .Select(edge => (edge, "Incoming"))
                .ToArray(),
            selected.Symbol.Origin,
            cancellationToken,
            includeGenerated,
            RelationshipLabelContext.Callers);
        var derivedEdges = await DispatchExpansion.CollectDerivedAsync(
            _repository,
            selected.Run.IndexId,
            selected.Run.IndexId,
            selected.Symbol.SymbolId,
            cancellationToken);
        var derived = await MapRelationshipEdgesAsync(
            selected.Run,
            derivedEdges.Select(item => (item.Edge, "Incoming")).ToArray(),
            selected.Symbol.Origin,
            cancellationToken,
            includeGenerated);
        // Routes join by relationship id so the flagging stays correct no
        // matter what order the mapper emits.
        var routesById = derivedEdges.ToDictionary(
            item => item.Edge.RelationshipId, item => item.Routes, StringComparer.Ordinal);
        var flagged = derived
            .Select(row => row with { IsDerived = true, Routes = routesById[row.RelationshipId] })
            .ToArray();
        var page = DispatchExpansion.MergeAndTake(exact, flagged, limit);
        return new RelationshipQuerySetResult(
            new SymbolResolutionResult(SymbolResolutionStatus.Resolved, selected.Symbol, []),
            page.Relationships,
            bodyRecoveryStatus,
            true,
            CompletenessNotice(bodyRecoveryStatus, true),
            page.ExactCount + page.DerivedCount,
            page.ExactCount,
            page.DerivedCount);
    }

    private async Task<HierarchyQueryResult> HierarchyInRunAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        HierarchyQueryMode mode,
        int depth,
        int offset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateQueryLimit(limit, nameof(limit));
        HierarchyTraversal.ValidateDepth(depth);
        HierarchyTraversal.ValidateOffset(offset);

        var selection = await ResolveInRunAsync(run, codebase, channel, selector, cancellationToken);
        if (selection.Resolution.Status != SymbolResolutionStatus.Resolved || selection.Selected is null)
            return new HierarchyQueryResult(selection.Resolution, [], 0, 0);

        return await HierarchyFromSelectedAsync(selection.Selected.Value, limit, mode, depth, offset, cancellationToken);
    }

    private async Task<HierarchyQueryResult> HierarchyAcrossChannelsAsync(
        string selector,
        IndexQueryOptions options,
        HierarchyQueryMode mode,
        int depth,
        int offset,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateQueryLimit(options.Limit, nameof(options));
        HierarchyTraversal.ValidateDepth(depth);
        HierarchyTraversal.ValidateOffset(offset);

        var selection = await ResolveAcrossChannelsAsync(selector, options, cancellationToken);
        if (selection.Resolution.Status != SymbolResolutionStatus.Resolved || selection.Selected is null)
            return new HierarchyQueryResult(selection.Resolution, [], 0, 0);

        return await HierarchyFromSelectedAsync(selection.Selected.Value, options.Limit, mode, depth, offset, cancellationToken);
    }

    private async Task<HierarchyQueryResult> HierarchyFromSelectedAsync(
        SelectedSymbol selected,
        int limit,
        HierarchyQueryMode mode,
        int depth,
        int offset,
        CancellationToken cancellationToken)
    {
        var (kinds, incoming, direction) = HierarchyTraversal.Plan(mode);
        var collected = await HierarchyTraversal.CollectAsync(
            _repository,
            selected.Run.IndexId,
            selected.Symbol.SymbolId,
            kinds,
            incoming,
            depth,
            cancellationToken);
        var mapped = await MapRelationshipEdgesAsync(
            selected.Run,
            collected.Select(item => (item.Edge, direction)).ToArray(),
            selected.Symbol.Origin,
            cancellationToken);
        var ordered = HierarchyTraversal.Order(
            mapped.Zip(collected, (edge, item) => new HierarchyNodeQueryResult(edge, item.Depth, item.Depth == 1)).ToArray(),
            incoming);
        var page = ordered.Skip(offset).Take(limit).ToArray();
        return new HierarchyQueryResult(
            new SymbolResolutionResult(SymbolResolutionStatus.Resolved, selected.Symbol, []),
            page,
            ordered.Count,
            page.Length);
    }

    private async Task<FieldReferenceQueryResult> FieldReferencesInRunAsync(
        IndexRunRecord run,
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        FieldReferenceFilter filter,
        CancellationToken cancellationToken,
        bool includeGenerated = false)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateQueryLimit(limit, nameof(limit));

        var selection = await ResolveInRunAsync(run, codebase, channel, selector, cancellationToken);
        if (selection.Resolution.Status != SymbolResolutionStatus.Resolved || selection.Selected is null)
            return new FieldReferenceQueryResult(selection.Resolution, new RelationshipQueryPageResult(0, 0, []));

        return await FieldReferencesFromSelectedAsync(selection.Selected.Value, limit, filter, cancellationToken, includeGenerated);
    }

    private async Task<FieldReferenceQueryResult> FieldReferencesFromSelectedAsync(
        SelectedSymbol selected,
        int limit,
        FieldReferenceFilter filter,
        CancellationToken cancellationToken,
        bool includeGenerated = false)
    {
        if (!includeGenerated)
        {
            var target = await _repository.GetCompletedSymbolByIdAsync(
                selected.Run.IndexId,
                selected.Symbol.SymbolId,
                cancellationToken);
            if (target is not null && target.IsGenerated)
                return new FieldReferenceQueryResult(
                    new SymbolResolutionResult(SymbolResolutionStatus.Resolved, selected.Symbol, []),
                    new RelationshipQueryPageResult(0, 0, []));
        }

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

        var droppedMetadata = edges.RemoveAll(IsMetadataAddressTaken);
        totalCount = Math.Max(0, totalCount - droppedMetadata);

        var page = await MapRelationshipPageAsync(
            selected.Run,
            edges.Select(edge => (edge, "Incoming"))
                .OrderBy(item => item.edge.RelationshipId, StringComparer.Ordinal)
                .Take(limit)
                .ToArray(),
            totalCount,
            selected.Symbol.Origin,
            cancellationToken,
            includeGenerated,
            LabelContextFor(filter));
        return new FieldReferenceQueryResult(
            new SymbolResolutionResult(SymbolResolutionStatus.Resolved, selected.Symbol, []),
            page);
    }

    private async Task<SelectedRelationshipEdges> GetSelectedRelationshipEdgesAsync(
        SelectedSymbol selected,
        RelationshipQueryMode mode,
        int limit,
        CancellationToken cancellationToken,
        bool includeDelegates = false)
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
                .Where(edge => IsCallLike(edge.Kind) || IsIncludedDelegate(edge, includeDelegates))
                .Select(edge => (edge, "Incoming"))
                .OrderBy(item => item.edge.RelationshipId, StringComparer.Ordinal)
                .ToArray();
        }
        else
        {
            var outgoing = await _repository.GetCompletedRelationshipsBySourceSymbolIdAsync(selected.Run.IndexId, selected.Symbol.SymbolId, cancellationToken);
            allEdges = outgoing
                .Where(edge => IsCallLike(edge.Kind) || IsIncludedDelegate(edge, includeDelegates))
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
        CancellationToken cancellationToken,
        bool includeGenerated = false,
        RelationshipLabelContext labelContext = RelationshipLabelContext.Refs)
    {
        var endpointIds = selectedEdges
            .SelectMany(item => new[] { item.Edge.SourceSymbolId, item.Edge.TargetSymbolId, item.Edge.GeneratedSourceSymbolId })
            .Where(id => id is not null)
            .Select(id => id!)
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
                Endpoint(
                    includeGenerated && item.Edge.GeneratedSourceSymbolId is not null
                        ? item.Edge.GeneratedSourceSymbolId
                        : item.Edge.SourceSymbolId,
                    null,
                    byId,
                    origin),
                Endpoint(item.Edge.TargetSymbolId, item.Edge.TargetText, byId, origin),
                GeneratedDetail: GeneratedBodyResolver.VisibleDetail(item.Edge.GeneratedDetail, includeGenerated),
                Label: RelationshipLabels.ForRelationship(item.Edge.Kind, item.Edge.Evidence, labelContext)))
            .ToArray();
    }

    private async Task<RelationshipQueryPageResult> MapRelationshipPageAsync(
        IndexRunRecord run,
        IReadOnlyList<(IndexRelationshipRecord Edge, string Direction)> selectedEdges,
        int totalCount,
        string? origin,
        CancellationToken cancellationToken,
        bool includeGenerated = false,
        RelationshipLabelContext labelContext = RelationshipLabelContext.Refs)
    {
        var ordered = selectedEdges
            .OrderBy(item => item.Edge.RelationshipId, StringComparer.Ordinal)
            .ThenBy(item => item.Direction, StringComparer.Ordinal)
            .ToArray();
        var relationships = await MapRelationshipEdgesAsync(run, ordered, origin, cancellationToken, includeGenerated, labelContext);
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
        var suggestions = new List<SymbolQueryResult>();
        var sides = new List<(SymbolResolutionResult Result, int Contributed)>();
        var completedIndexCount = 0;
        foreach (var channel in Channels(options))
        {
            var run = await _repository.GetLatestCompletedIndexAsync(options.Codebase, channel, null, cancellationToken);
            if (run is null) continue;
            completedIndexCount++;

            var selection = await ResolveInRunAsync(run, options.Codebase, channel, selector, cancellationToken);
            var resolution = selection.Resolution;
            suggestions.AddRange(resolution.Suggestions);
            if (resolution.Status == SymbolResolutionStatus.Ambiguous)
            {
                ambiguous.AddRange(resolution.Candidates);
                sides.Add((resolution, resolution.Candidates.Count));
                continue;
            }
            if (resolution.Status == SymbolResolutionStatus.Resolved && selection.Selected is not null)
            {
                resolved.Add(selection.Selected.Value);
                sides.Add((resolution, 1));
            }
            else
            {
                sides.Add((resolution, 0));
            }
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
                new SymbolResolutionResult(
                    SymbolResolutionStatus.Ambiguous,
                    null,
                    candidates,
                    TotalCandidateCount: ResolutionMerge.CombineTotals(sides.ToArray())),
                null);
        }

        if (completedIndexCount == 0)
            return new ChannelSelection(
                new SymbolResolutionResult(SymbolResolutionStatus.NoCompletedIndex, null, []),
                null);

        if (resolved.Count == 0)
            return new ChannelSelection(
                new SymbolResolutionResult(
                    SymbolResolutionStatus.NotFound,
                    null,
                    [],
                    suggestions.Take(ResolutionMerge.MaxSuggestions).ToArray()),
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
        CancellationToken cancellationToken,
        IReadOnlySet<SymbolKind>? kinds = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var resolution = await _symbolResolver.ResolveAsync(
            run.IndexId,
            selector,
            codebase,
            channel,
            cancellationToken,
            kinds);
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
        string.Equals(kind, "CallsVirtual", StringComparison.Ordinal) ||
        string.Equals(kind, "Constructs", StringComparison.Ordinal);

    private static bool IsIncludedDelegate(IndexRelationshipRecord edge, bool includeDelegates) =>
        includeDelegates &&
        string.Equals(edge.Kind, nameof(RelationshipKind.ReferencesMethod), StringComparison.Ordinal) &&
        !string.Equals(edge.Evidence, nameof(RelationshipEvidence.Metadata), StringComparison.Ordinal);

    private static RelationshipLabelContext LabelContextFor(RelationshipQueryMode mode) => mode switch
    {
        RelationshipQueryMode.Callers => RelationshipLabelContext.Callers,
        RelationshipQueryMode.Callees => RelationshipLabelContext.Callees,
        _ => RelationshipLabelContext.Refs
    };

    private static RelationshipLabelContext LabelContextFor(FieldReferenceFilter filter) => filter switch
    {
        FieldReferenceFilter.Readers => RelationshipLabelContext.Readers,
        FieldReferenceFilter.Writers => RelationshipLabelContext.Writers,
        _ => RelationshipLabelContext.All
    };

    private static bool IsMetadataAddressTaken(IndexRelationshipRecord edge) =>
        string.Equals(edge.Kind, nameof(RelationshipKind.TakesFieldAddress), StringComparison.Ordinal) &&
        string.Equals(edge.Evidence, nameof(RelationshipEvidence.Metadata), StringComparison.Ordinal);

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
