using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Query;

public sealed class FederatedIndexQueryService
{
    private readonly IndexQueryService _game;
    private readonly ReferenceModQueryService _reference;

    public FederatedIndexQueryService(IIndexRepository repository, string? dataRoot = null)
        : this(new IndexQueryService(repository, dataRoot), new ReferenceModQueryService(repository, dataRoot))
    {
    }

    public FederatedIndexQueryService(IndexQueryService game, ReferenceModQueryService reference)
    {
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _reference = reference ?? throw new ArgumentNullException(nameof(reference));
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

        var game = await _game.SearchAsync(query, GameOptions(options, int.MaxValue), cancellationToken, kind);
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
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ValidateOptions(options);
        if (options.Scope == IndexQueryScope.Game)
            return await _gameResolution(selector, options, cancellationToken);
        if (options.Scope == IndexQueryScope.Reference)
            return await _reference.ResolveAsync(selector, options, cancellationToken);

        var game = await _gameResolution(selector, options, cancellationToken);
        var reference = await _reference.ResolveAsync(selector, options with { Scope = IndexQueryScope.Reference }, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        ValidateOptions(options);
        var resolution = await ResolveAsync(selector, options, cancellationToken);
        if (resolution.Status != SymbolResolutionStatus.Resolved || resolution.Symbol is null)
            return new SourceSnippetResolutionResult(resolution, null);
        return resolution.Symbol.Origin == "reference"
            ? await _reference.SourceAsync(selector, options with { Scope = IndexQueryScope.Reference }, context, cancellationToken)
            : await _game.SourceAsync(selector, GameOptions(options, options.Limit), context, cancellationToken);
    }

    public Task<RelationshipQuerySetResult> RefsAsync(string selector, IndexQueryOptions options, CancellationToken cancellationToken) =>
        RelationshipsAsync(selector, options, RelationshipKind.Refs, cancellationToken);

    public Task<RelationshipQuerySetResult> CallersAsync(string selector, IndexQueryOptions options, CancellationToken cancellationToken) =>
        RelationshipsAsync(selector, options, RelationshipKind.Callers, cancellationToken);

    public Task<RelationshipQuerySetResult> CalleesAsync(string selector, IndexQueryOptions options, CancellationToken cancellationToken) =>
        RelationshipsAsync(selector, options, RelationshipKind.Callees, cancellationToken);

    private async Task<RelationshipQuerySetResult> RelationshipsAsync(
        string selector,
        IndexQueryOptions options,
        RelationshipKind kind,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(selector, options, cancellationToken);
        if (resolution.Status != SymbolResolutionStatus.Resolved || resolution.Symbol is null)
            return new RelationshipQuerySetResult(resolution, [], null, kind == RelationshipKind.Callers, string.Empty);

        if (resolution.Symbol.Origin == "reference")
            return await ReferenceRelationshipsAsync(selector, options, kind, cancellationToken);

        var game = await GameRelationshipsAsync(selector, options, kind, cancellationToken);
        if (options.Scope != IndexQueryScope.All || string.IsNullOrWhiteSpace(options.ReferenceCollection))
            return game;
        var reference = await ReferenceRelationshipsAsync(selector, options, kind, cancellationToken);
        return MergeRelationships(resolution, game, reference, kind);
    }

    private Task<RelationshipQuerySetResult> GameRelationshipsAsync(string selector, IndexQueryOptions options, RelationshipKind kind, CancellationToken cancellationToken) =>
        kind switch
        {
            RelationshipKind.Refs => _game.RefsAsync(selector, GameOptions(options, options.Limit), cancellationToken),
            RelationshipKind.Callers => _game.CallersAsync(selector, GameOptions(options, options.Limit), cancellationToken),
            _ => _game.CalleesAsync(selector, GameOptions(options, options.Limit), cancellationToken)
        };

    private Task<RelationshipQuerySetResult> ReferenceRelationshipsAsync(string selector, IndexQueryOptions options, RelationshipKind kind, CancellationToken cancellationToken) =>
        kind switch
        {
            RelationshipKind.Refs => _reference.RefsAsync(selector, options with { Scope = IndexQueryScope.Reference }, cancellationToken),
            RelationshipKind.Callers => _reference.CallersAsync(selector, options with { Scope = IndexQueryScope.Reference }, cancellationToken),
            _ => _reference.CalleesAsync(selector, options with { Scope = IndexQueryScope.Reference }, cancellationToken)
        };

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

    private Task<SymbolResolutionResult> _gameResolution(string selector, IndexQueryOptions options, CancellationToken cancellationToken) =>
        _game.ResolveAsync(selector, GameOptions(options, options.Limit), cancellationToken);

    private enum RelationshipKind { Refs, Callers, Callees }
}
