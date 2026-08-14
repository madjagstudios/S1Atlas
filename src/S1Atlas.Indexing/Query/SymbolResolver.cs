using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Query;

public sealed class SymbolResolver
{
    private const int CandidateLimit = 50;
    private readonly IIndexRepository _repository;

    public SymbolResolver(IIndexRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<SymbolResolutionResult> ResolveAsync(
        string indexId,
        string selector,
        CodebaseKind codebase,
        CodeChannel channel,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var byId = await _repository.GetCompletedSymbolByIdAsync(indexId, selector, cancellationToken);
        if (byId is not null)
            return Resolved(ToQueryResult(indexId, codebase, channel, byId));

        var records = await _repository.SearchCompletedSymbolsAsync(
            indexId,
            selector,
            CandidateLimit,
            cancellationToken);
        if (records.Count == 0)
            return NotFound();

        var exactCanonical = records
            .Where(record => string.Equals(record.CanonicalKey, selector, StringComparison.Ordinal))
            .ToArray();
        if (exactCanonical.Length == 1)
            return Resolved(ToQueryResult(indexId, codebase, channel, exactCanonical[0]));
        if (exactCanonical.Length > 1)
            return Ambiguous(indexId, codebase, channel, exactCanonical);

        var exactSignature = records
            .Where(record => string.Equals(record.Signature, selector, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactSignature.Length == 1)
            return Resolved(ToQueryResult(indexId, codebase, channel, exactSignature[0]));
        if (exactSignature.Length > 1)
            return Ambiguous(indexId, codebase, channel, exactSignature);

        var exactQualifiedName = records
            .Where(record => string.Equals(record.QualifiedName, selector, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactQualifiedName.Length == 1)
            return Resolved(ToQueryResult(indexId, codebase, channel, exactQualifiedName[0]));
        if (exactQualifiedName.Length > 1)
            return Ambiguous(indexId, codebase, channel, exactQualifiedName);

        var bestRank = Rank(records[0], selector);
        var best = records
            .TakeWhile(record => Rank(record, selector) == bestRank)
            .ToArray();
        return best.Length == 1
            ? Resolved(ToQueryResult(indexId, codebase, channel, best[0]))
            : Ambiguous(indexId, codebase, channel, best);
    }

    internal static int Rank(IndexSymbolRecord record, string query)
    {
        if (string.Equals(record.QualifiedName, query, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(record.Signature, query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (record.QualifiedName.EndsWith("." + query, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (record.QualifiedName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (record.QualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 3;
        if (record.Signature.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 4;
        return 5;
    }

    private static SymbolResolutionResult Resolved(SymbolQueryResult symbol) =>
        new(SymbolResolutionStatus.Resolved, symbol, []);

    private static SymbolResolutionResult NotFound() =>
        new(SymbolResolutionStatus.NotFound, null, []);

    private static SymbolResolutionResult Ambiguous(
        string indexId,
        CodebaseKind codebase,
        CodeChannel channel,
        IReadOnlyList<IndexSymbolRecord> records) =>
        new(
            SymbolResolutionStatus.Ambiguous,
            null,
            records
                .Select(record => ToQueryResult(indexId, codebase, channel, record))
                .OrderBy(result => result.QualifiedName, StringComparer.Ordinal)
                .ThenBy(result => result.Signature, StringComparer.Ordinal)
                .ThenBy(result => result.SymbolId, StringComparer.Ordinal)
                .ToArray());

    internal static SymbolQueryResult ToQueryResult(
        string indexId,
        CodebaseKind codebase,
        CodeChannel channel,
        IndexSymbolRecord record) =>
        new(
            indexId,
            codebase.ToString(),
            channel.ToString(),
            record.SymbolId,
            record.Kind,
            record.QualifiedName,
            record.Signature,
            record.IsBestEffort);
}
