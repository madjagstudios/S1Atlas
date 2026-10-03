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
        SymbolKind? kind = null,
        bool includeGenerated = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ValidateOptions(options);
        if (options.Limit <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Scope == IndexQueryScope.Game)
            return await _game.SearchAsync(query, options with { Scope = IndexQueryScope.Game }, cancellationToken, kind, includeGenerated);
        if (options.Scope == IndexQueryScope.Reference)
            return await _reference.SearchAsync(query, options with { Scope = IndexQueryScope.Reference }, cancellationToken, kind, includeGenerated);

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
            cancellationToken,
            includeGenerated);
        var reference = await _reference.SearchAsync(query, options with { Scope = IndexQueryScope.Reference, Limit = int.MaxValue }, cancellationToken, kind, includeGenerated);
        var results = MergeSymbols(game.Results.Concat(reference.Results), query, options.Limit);
        SymbolResolutionStatus? status = results.Length > 0
            ? null
            : game.ResolutionStatus == SymbolResolutionStatus.NoCompletedIndex && reference.ResolutionStatus == SymbolResolutionStatus.NoCompletedIndex
                ? SymbolResolutionStatus.NoCompletedIndex
                : SymbolResolutionStatus.NotFound;
        var hiddenCount = 0;
        if (GeneratedSearchNotice.TryParseHiddenCount(game.SearchNotice, out var gameHidden))
            hiddenCount += gameHidden;
        if (GeneratedSearchNotice.TryParseHiddenCount(reference.SearchNotice, out var referenceHidden))
            hiddenCount += referenceHidden;
        return new SymbolSearchResult(
            game.TotalCount + reference.TotalCount,
            results.Length,
            results,
            status,
            GeneratedSearchNotice.ForHidden(hiddenCount));
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
            return new SymbolResolutionResult(
                SymbolResolutionStatus.Ambiguous,
                null,
                candidates,
                TotalCandidateCount: ResolutionMerge.CombineTotals(
                    (game, game.Status == SymbolResolutionStatus.Ambiguous ? game.Candidates.Count : game.Symbol is null ? 0 : 1),
                    (reference, reference.Status == SymbolResolutionStatus.Ambiguous ? reference.Candidates.Count : reference.Symbol is null ? 0 : 1)));
        if (candidates.Length == 1)
            return new SymbolResolutionResult(SymbolResolutionStatus.Resolved, candidates[0], []);
        var mergedStatus = game.Status == SymbolResolutionStatus.NoCompletedIndex && reference.Status == SymbolResolutionStatus.NoCompletedIndex
            ? SymbolResolutionStatus.NoCompletedIndex
            : SymbolResolutionStatus.NotFound;
        return new SymbolResolutionResult(
            mergedStatus,
            null,
            [],
            mergedStatus == SymbolResolutionStatus.NotFound
                ? game.Suggestions.Concat(reference.Suggestions).Take(ResolutionMerge.MaxSuggestions).ToArray()
                : []);
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

    public Task<RelationshipQuerySetResult> RefsAsync(string selector, IndexQueryOptions options, CancellationToken cancellationToken, bool includeGenerated = false) =>
        RelationshipsAsync(selector, options, RelationshipKind.Refs, cancellationToken, includeGenerated: includeGenerated);

    public Task<RelationshipQuerySetResult> CallersAsync(string selector, IndexQueryOptions options, CancellationToken cancellationToken, bool exact = false, bool includeGenerated = false, bool includeDelegates = false) =>
        RelationshipsAsync(selector, options, RelationshipKind.Callers, cancellationToken, exact, includeGenerated, includeDelegates);

    public Task<RelationshipQuerySetResult> CalleesAsync(string selector, IndexQueryOptions options, CancellationToken cancellationToken, bool includeGenerated = false, bool includeDelegates = false) =>
        RelationshipsAsync(selector, options, RelationshipKind.Callees, cancellationToken, includeGenerated: includeGenerated, includeDelegates: includeDelegates);

    public Task<RelationshipQuerySetResult> PatchesAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken,
        bool includeGenerated = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateOptions(options);
        ValidateLimit(options.Limit);
        if (options.Scope == IndexQueryScope.Game)
            return _game.PatchesAsync(selector, GameOptions(options, options.Limit), cancellationToken);
        return _reference.PatchedByAsync(selector, options, cancellationToken, includeGenerated);
    }

    public Task<HierarchyQueryResult> OverridesAsync(
        string selector,
        IndexQueryOptions options,
        CancellationToken cancellationToken,
        string? referenceIndexId = null,
        int offset = 0) =>
        HierarchyAsync(selector, options, HierarchyQueryMode.Overrides, HierarchyTraversal.FullChainDepth, offset, cancellationToken, referenceIndexId);

    public Task<HierarchyQueryResult> OverriddenByAsync(
        string selector,
        IndexQueryOptions options,
        int depth,
        CancellationToken cancellationToken,
        string? referenceIndexId = null,
        int offset = 0) =>
        HierarchyAsync(selector, options, HierarchyQueryMode.OverriddenBy, depth, offset, cancellationToken, referenceIndexId);

    public Task<HierarchyQueryResult> DerivedAsync(
        string selector,
        IndexQueryOptions options,
        int depth,
        int offset,
        CancellationToken cancellationToken,
        string? referenceIndexId = null) =>
        HierarchyAsync(selector, options, HierarchyQueryMode.Derived, depth, offset, cancellationToken, referenceIndexId);

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
            return await ReferenceCallSitesAsync(selector, selection, options.Limit, cancellationToken, offset: options.Offset);

        var targetQuery = await ResolveCallSiteTargetQueryAsync(
            selection.GameRun,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            selector,
            cancellationToken);
        // Each side returns its first span rows; the merge applies the
        // offset and each side reports whether it holds more rows. The
        // per-side queries add the single +1 probe row themselves.
        var span = IndexPaging.PageSpan(options.Offset, options.Limit);
        var game = await _game.CallSitesInIndexAsync(
            selection.GameRun,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            selector,
            span,
            cancellationToken);
        var reference = await ReferenceCallSitesAsync(
            selector,
            selection,
            span,
            cancellationToken,
            targetQuery);
        var (page, hasMore) = MergeRelationshipPages(game.Page, reference.Page, options.Limit, options.Offset);
        return new CallSiteQueryResult(page, CallSiteCompletenessNotice, HasMore: hasMore || game.HasMore || reference.HasMore);
    }

    public async Task<FieldReferenceQueryResult> FieldReferencesAsync(
        string selector,
        IndexQueryOptions options,
        FieldReferenceFilter filter,
        CancellationToken cancellationToken,
        string? referenceIndexId = null,
        bool includeGenerated = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateOptions(options);
        ValidateLimit(options.Limit);

        if (options.Scope == IndexQueryScope.Game)
            return await _game.FieldReferencesAsync(selector, GameOptions(options, options.Limit), filter, cancellationToken, includeGenerated);

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
            return await ReferenceFieldReferencesAsync(selection, resolution, filter, options.Limit, cancellationToken, includeGenerated, options.Offset);

        var window = IndexPaging.PageWindow(options.Offset, options.Limit);
        var game = await _game.FieldReferencesInIndexAsync(
            selection.GameRun,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            selector,
            window,
            filter,
            cancellationToken,
            includeGenerated);
        var (reference, _) = await ReferenceFieldReferencesForTargetSymbolAsync(
            selection,
            resolution.Symbol.SymbolId,
            filter,
            window,
            cancellationToken,
            includeGenerated);
        var (page, hasMore) = MergeRelationshipPages(game.Page, reference, options.Limit, options.Offset);
        return new FieldReferenceQueryResult(resolution, page, HasMore: hasMore);
    }

    private async Task<RelationshipQuerySetResult> RelationshipsAsync(
        string selector,
        IndexQueryOptions options,
        RelationshipKind kind,
        CancellationToken cancellationToken,
        bool exact = false,
        bool includeGenerated = false,
        bool includeDelegates = false)
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
            return await ReferenceRelationshipsAsync(selector, options, kind, cancellationToken, exact, includeGenerated, includeDelegates);

        var game = await GameRelationshipsAsync(selector, options, kind, cancellationToken, selection?.GameRun, exact, includeGenerated, includeDelegates);
        if (options.Scope != IndexQueryScope.All || string.IsNullOrWhiteSpace(options.ReferenceCollection))
            return game;
        var window = IndexPaging.PageWindow(options.Offset, options.Limit);
        var reference = await ReferenceRelationshipsAsync(
            selector,
            options with { Limit = window, Offset = 0 },
            kind,
            cancellationToken,
            exact,
            includeGenerated,
            includeDelegates);
        return MergeRelationships(resolution, game, reference, kind, options.Limit, options.Offset);
    }

    private async Task<HierarchyQueryResult> HierarchyAsync(
        string selector,
        IndexQueryOptions options,
        HierarchyQueryMode mode,
        int depth,
        int offset,
        CancellationToken cancellationToken,
        string? referenceIndexId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateOptions(options);
        ValidateLimit(options.Limit);
        HierarchyTraversal.ValidateDepth(depth);
        HierarchyTraversal.ValidateOffset(offset);

        if (options.Scope == IndexQueryScope.Game)
            return await GameHierarchyAsync(selector, options, mode, depth, offset, cancellationToken, null);

        var selection = await _reference.GetSelectionForFederationAsync(options, referenceIndexId, cancellationToken);
        if (selection is null)
            return new HierarchyQueryResult(
                new SymbolResolutionResult(SymbolResolutionStatus.NoCompletedIndex, null, []),
                [],
                0,
                0);

        if (options.Scope == IndexQueryScope.Reference)
            return await _reference.HierarchyAsync(selector, options, mode, depth, offset, cancellationToken, referenceIndexId);

        var resolution = await ResolveAsync(selector, options, cancellationToken, referenceIndexId);
        if (resolution.Status != SymbolResolutionStatus.Resolved || resolution.Symbol is null)
            return new HierarchyQueryResult(resolution, [], 0, 0);

        if (resolution.Symbol.Origin == "reference")
            return await _reference.HierarchyAsync(selector, options, mode, depth, offset, cancellationToken, referenceIndexId);

        var game = await GameHierarchyAsync(selector, options, mode, depth, 0, cancellationToken, selection.GameRun, int.MaxValue);
        var reference = await _reference.HierarchyAsync(
            selector,
            options with { Limit = int.MaxValue },
            mode,
            depth,
            0,
            cancellationToken,
            referenceIndexId);
        return MergeHierarchies(resolution, game, reference, mode, options.Limit, offset);
    }

    private Task<HierarchyQueryResult> GameHierarchyAsync(
        string selector,
        IndexQueryOptions options,
        HierarchyQueryMode mode,
        int depth,
        int offset,
        CancellationToken cancellationToken,
        IndexRunRecord? pinnedRun,
        int? limitOverride = null)
    {
        if (pinnedRun is null)
        {
            var gameOptions = GameOptions(options, limitOverride ?? options.Limit) with { Offset = offset };
            return mode switch
            {
                HierarchyQueryMode.Overrides => _game.OverridesAsync(selector, gameOptions, cancellationToken),
                HierarchyQueryMode.OverriddenBy => _game.OverriddenByAsync(selector, gameOptions, depth, cancellationToken),
                _ => _game.DerivedAsync(selector, gameOptions, depth, offset, cancellationToken)
            };
        }

        // Pinned sides fetch the merge window; the merge applies the offset.
        // A limit override already carries the merge window with offset zero.
        var limit = limitOverride ?? IndexPaging.PageWindow(offset, options.Limit);
        var sideOffset = limitOverride is null ? 0 : offset;
        return mode switch
        {
            HierarchyQueryMode.Overrides => _game.OverridesInIndexAsync(
                pinnedRun, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, limit, cancellationToken, sideOffset),
            HierarchyQueryMode.OverriddenBy => _game.OverriddenByInIndexAsync(
                pinnedRun, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, limit, depth, cancellationToken, sideOffset),
            _ => _game.DerivedInIndexAsync(
                pinnedRun, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, limit, depth, sideOffset, cancellationToken)
        };
    }

    private static HierarchyQueryResult MergeHierarchies(
        SymbolResolutionResult resolution,
        HierarchyQueryResult game,
        HierarchyQueryResult reference,
        HierarchyQueryMode mode,
        int limit,
        int offset)
    {
        var (_, incoming, _) = HierarchyTraversal.Plan(mode);
        var ordered = HierarchyTraversal.Order(game.Nodes.Concat(reference.Nodes).ToArray(), incoming);
        var (rows, hasMore) = IndexPaging.TakePage(ordered, offset, limit);
        return new HierarchyQueryResult(resolution, rows, ordered.Count, rows.Count, HasMore: hasMore);
    }

    private Task<RelationshipQuerySetResult> GameRelationshipsAsync(
        string selector,
        IndexQueryOptions options,
        RelationshipKind kind,
        CancellationToken cancellationToken,
        IndexRunRecord? pinnedRun = null,
        bool exact = false,
        bool includeGenerated = false,
        bool includeDelegates = false)
    {
        if (pinnedRun is null)
        {
            var gameOptions = GameOptions(options, options.Limit);
            return kind switch
            {
                RelationshipKind.Refs => _game.RefsAsync(selector, gameOptions, cancellationToken, includeGenerated),
                RelationshipKind.Callers => _game.CallersAsync(selector, gameOptions, cancellationToken, exact, includeGenerated, includeDelegates),
                _ => _game.CalleesAsync(selector, gameOptions, cancellationToken, includeGenerated, includeDelegates)
            };
        }

        // Pinned sides fetch the merge window; the merge applies the offset.
        var window = IndexPaging.PageWindow(options.Offset, options.Limit);
        return kind switch
        {
            RelationshipKind.Refs => _game.RefsInIndexAsync(pinnedRun, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, window, cancellationToken, includeGenerated),
            RelationshipKind.Callers => _game.CallersInIndexAsync(pinnedRun, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, window, cancellationToken, exact, includeGenerated, includeDelegates),
            _ => _game.CalleesInIndexAsync(pinnedRun, CodebaseKind.ScheduleI, CodeChannel.Installed, selector, window, cancellationToken, includeGenerated, includeDelegates)
        };
    }

    private Task<RelationshipQuerySetResult> ReferenceRelationshipsAsync(
        string selector,
        IndexQueryOptions options,
        RelationshipKind kind,
        CancellationToken cancellationToken,
        bool exact = false,
        bool includeGenerated = false,
        bool includeDelegates = false) =>
        kind switch
        {
            RelationshipKind.Refs => _reference.RefsAsync(selector, options, cancellationToken, includeGenerated),
            RelationshipKind.Callers => _reference.CallersAsync(selector, options, cancellationToken, exact, includeGenerated, includeDelegates),
            _ => _reference.CalleesAsync(selector, options, cancellationToken, includeGenerated, includeDelegates)
        };

    private async Task<CallSiteQueryResult> ReferenceCallSitesAsync(
        string selector,
        ReferenceModQueryService.IndexSelection selection,
        int limit,
        CancellationToken cancellationToken,
        CallSiteTargetQuery? targetQuery = null,
        int offset = 0)
    {
        var query = targetQuery ?? await ResolveCallSiteTargetQueryAsync(
            selection.Run,
            CodebaseKind.ReferenceMod,
            CodeChannel.Installed,
            selector,
            cancellationToken);
        var totalCount = 0;
        foreach (var kind in CallSiteKinds.Names)
        {
            totalCount += await Repository.CountCompletedRelationshipsByTargetTextAsync(
                selection.Run.IndexId,
                query.TargetText,
                query.MatchMode,
                kind,
                cancellationToken);
        }

        if (totalCount == 0)
            return new CallSiteQueryResult(new RelationshipQueryPageResult(0, 0, []), CallSiteCompletenessNotice);

        var window = IndexPaging.PageWindow(offset, limit);
        var fetched = new List<IndexRelationshipRecord>();
        foreach (var kind in CallSiteKinds.Names)
        {
            fetched.AddRange(await Repository.GetCompletedRelationshipsByTargetTextAsync(
                selection.Run.IndexId,
                query.TargetText,
                query.MatchMode,
                kind,
                window,
                cancellationToken));
        }

        var edges = CallSiteKinds.MergeAndTake(fetched, window, offset);
        var (rows, hasMore) = IndexPaging.TakePage(edges, 0, limit);
        var page = await MapReferenceRelationshipPageAsync(
            selection,
            rows.Select(edge => (edge, "Incoming")).ToArray(),
            totalCount,
            includeGameEndpoints: true,
            cancellationToken);
        return new CallSiteQueryResult(page, CallSiteCompletenessNotice, HasMore: hasMore);
    }

    private async Task<FieldReferenceQueryResult> ReferenceFieldReferencesAsync(
        ReferenceModQueryService.IndexSelection selection,
        SymbolResolutionResult resolution,
        FieldReferenceFilter filter,
        int limit,
        CancellationToken cancellationToken,
        bool includeGenerated = false,
        int offset = 0)
    {
        var (page, hasMore) = await ReferenceFieldReferencesForTargetSymbolAsync(
            selection,
            resolution.Symbol!.SymbolId,
            filter,
            limit,
            cancellationToken,
            includeGenerated,
            offset);
        return new FieldReferenceQueryResult(resolution, page, HasMore: hasMore);
    }

    private async Task<(RelationshipQueryPageResult Page, bool HasMore)> ReferenceFieldReferencesForTargetSymbolAsync(
        ReferenceModQueryService.IndexSelection selection,
        string targetSymbolId,
        FieldReferenceFilter filter,
        int limit,
        CancellationToken cancellationToken,
        bool includeGenerated = false,
        int offset = 0)
    {
        if (!includeGenerated)
        {
            var target = await Repository.GetCompletedSymbolByIdAsync(
                selection.Run.IndexId,
                targetSymbolId,
                cancellationToken);
            if (target is not null && target.IsGenerated)
                return (new RelationshipQueryPageResult(0, 0, []), false);
        }

        var totalCount = 0;
        var edges = new List<IndexRelationshipRecord>();
        foreach (var kind in FieldRelationshipKinds(filter))
        {
            totalCount += await Repository.CountCompletedRelationshipsByTargetSymbolIdAsync(
                selection.Run.IndexId,
                targetSymbolId,
                kind,
                cancellationToken);
            // The metadata filter drops rows after the fetch, so per-kind
            // windows cannot bound it; the fetch stays bounded by fan-out.
            edges.AddRange(await Repository.GetCompletedRelationshipsByTargetSymbolIdAsync(
                selection.Run.IndexId,
                targetSymbolId,
                kind,
                int.MaxValue,
                cancellationToken));
        }

        var droppedMetadata = edges.RemoveAll(IsMetadataAddressTaken);
        totalCount = Math.Max(0, totalCount - droppedMetadata);

        var ordered = edges.Select(edge => (edge, "Incoming"))
            .OrderBy(item => item.edge.RelationshipId, StringComparer.Ordinal)
            .ToArray();
        var (rows, hasMore) = IndexPaging.TakePage(ordered, offset, limit);
        var page = await MapReferenceRelationshipPageAsync(
            selection,
            rows,
            totalCount,
            includeGameEndpoints: true,
            cancellationToken,
            includeGenerated,
            LabelContextFor(filter));
        return (page, hasMore);
    }

    private static RelationshipQuerySetResult MergeRelationships(
        SymbolResolutionResult resolution,
        RelationshipQuerySetResult game,
        RelationshipQuerySetResult reference,
        RelationshipKind kind,
        int limit,
        int offset)
    {
        var deduped = game.Relationships
            .Concat(reference.Relationships)
            .GroupBy(edge => (
                Origin: edge.Source.Origin ?? string.Empty,
                ModId: edge.Source.ReferenceModId ?? string.Empty,
                SymbolId: edge.Source.SymbolId ?? string.Empty,
                RelationshipId: edge.RelationshipId,
                Direction: edge.Direction))
            .Select(group => group.First())
            .ToArray();
        var relationships = kind == RelationshipKind.Callers
            ? deduped
                .OrderBy(edge => edge.IsDerived)
                .ThenBy(DispatchExpansion.CallerIdentity, StringComparer.Ordinal)
                .ThenBy(edge => edge.RelationshipId, StringComparer.Ordinal)
                .ThenBy(edge => edge.Source.Origin, StringComparer.Ordinal)
                .ThenBy(edge => edge.Source.ReferenceModId, StringComparer.Ordinal)
                .ThenBy(edge => edge.Target.SymbolId, StringComparer.Ordinal)
                .ToArray()
            : deduped
                .OrderBy(edge => edge.RelationshipId, StringComparer.Ordinal)
                .ThenBy(edge => edge.Source.Origin, StringComparer.Ordinal)
                .ThenBy(edge => edge.Source.ReferenceModId, StringComparer.Ordinal)
                .ThenBy(edge => edge.Target.SymbolId, StringComparer.Ordinal)
                .ToArray();
        var unpaged = game.TotalCount is int gameTotal && reference.TotalCount is int referenceTotal &&
                      game.Relationships.Count == gameTotal && reference.Relationships.Count == referenceTotal;
        var totalCount = unpaged ? (int?)relationships.Length : null;
        int? exactCount = null;
        int? derivedCount = null;
        if (unpaged
            && game.ExactCount is int gameExact
            && game.DerivedCount is int gameDerived
            && reference.ExactCount is int referenceExact
            && reference.DerivedCount is int referenceDerived)
        {
            exactCount = gameExact + referenceExact;
            derivedCount = gameDerived + referenceDerived;
        }

        var (rows, hasMore) = IndexPaging.TakePage(relationships, offset, limit);
        return new RelationshipQuerySetResult(
            resolution,
            rows,
            game.BodyRecoveryStatus ?? reference.BodyRecoveryStatus,
            kind == RelationshipKind.Callers,
            game.CompletenessNotice + reference.CompletenessNotice,
            totalCount,
            exactCount,
            derivedCount,
            HasMore: hasMore);
    }

    private async Task<RelationshipQueryPageResult> MapReferenceRelationshipPageAsync(
        ReferenceModQueryService.IndexSelection selection,
        IReadOnlyList<(IndexRelationshipRecord Edge, string Direction)> selectedEdges,
        int totalCount,
        bool includeGameEndpoints,
        CancellationToken cancellationToken,
        bool includeGenerated = false,
        RelationshipLabelContext labelContext = RelationshipLabelContext.Refs)
    {
        var ordered = selectedEdges
            .OrderBy(item => item.Edge.RelationshipId, StringComparer.Ordinal)
            .ThenBy(item => item.Direction, StringComparer.Ordinal)
            .ToArray();
        var ids = ordered
            .SelectMany(item => new[] { item.Edge.SourceSymbolId, item.Edge.TargetSymbolId, item.Edge.GeneratedSourceSymbolId })
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
                MapReferenceEndpoint(
                    includeGenerated && item.Edge.GeneratedSourceSymbolId is not null
                        ? item.Edge.GeneratedSourceSymbolId
                        : item.Edge.SourceSymbolId,
                    null,
                    referenceById,
                    gameById,
                    selection),
                MapReferenceEndpoint(item.Edge.TargetSymbolId, item.Edge.TargetText, referenceById, gameById, selection),
                GeneratedDetail: GeneratedBodyResolver.VisibleDetail(item.Edge.GeneratedDetail, includeGenerated),
                Label: RelationshipLabels.ForRelationship(item.Edge.Kind, item.Edge.Evidence, labelContext)))
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

    private static (RelationshipQueryPageResult Page, bool HasMore) MergeRelationshipPages(
        RelationshipQueryPageResult first,
        RelationshipQueryPageResult second,
        int limit,
        int offset)
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
            .ToArray();
        var (rows, hasMore) = IndexPaging.TakePage(relationships, offset, limit);
        return (new RelationshipQueryPageResult(first.TotalCount + second.TotalCount, rows.Count, rows), hasMore);
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
        return result with
        {
            Symbol = result.Symbol is null ? null : DecorateReferenceQuerySymbol(selection, result.Symbol),
            Candidates = result.Candidates.Select(candidate => DecorateReferenceQuerySymbol(selection, candidate)).ToArray(),
            Suggestions = result.Suggestions.Select(candidate => DecorateReferenceQuerySymbol(selection, candidate)).ToArray(),
        };
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

    private static RelationshipLabelContext LabelContextFor(FieldReferenceFilter filter) => filter switch
    {
        FieldReferenceFilter.Readers => RelationshipLabelContext.Readers,
        FieldReferenceFilter.Writers => RelationshipLabelContext.Writers,
        _ => RelationshipLabelContext.All
    };

    private static bool IsMetadataAddressTaken(IndexRelationshipRecord edge) =>
        string.Equals(edge.Kind, nameof(S1Atlas.Core.Indexing.RelationshipKind.TakesFieldAddress), StringComparison.Ordinal) &&
        string.Equals(edge.Evidence, nameof(RelationshipEvidence.Metadata), StringComparison.Ordinal);

    private IIndexRepository Repository => _repository;

    private SymbolResolver SymbolResolver => _symbolResolver;

    private enum RelationshipKind { Refs, Callers, Callees }
}
