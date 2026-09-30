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
        CancellationToken cancellationToken,
        IReadOnlySet<SymbolKind>? kinds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var kindNames = kinds?.Select(kind => kind.ToString()).ToHashSet(StringComparer.Ordinal);

        var byId = await _repository.GetCompletedSymbolByIdAsync(indexId, selector, cancellationToken);
        if (byId is not null)
        {
            if (kindNames is null || kindNames.Contains(byId.Kind))
                return Resolved(ToQueryResult(indexId, codebase, channel, byId, OriginFor(codebase)));
            return KindMismatch(indexId, codebase, channel, byId);
        }

        if (kindNames is not null && IsCanonicalSelector(selector, codebase, channel))
            return await ResolveCanonicalKeyAsync(indexId, selector, codebase, channel, kindNames, cancellationToken);

        var searchQuery = SearchQueryForSelector(selector, codebase, channel);
        var records = await SearchKindsAsync(indexId, searchQuery, kindNames, cancellationToken);
        if (records.Count == 0)
            return NotFound();

        var exactCanonical = records
            .Where(record => string.Equals(record.CanonicalKey, selector, StringComparison.Ordinal))
            .ToArray();
        if (exactCanonical.Length == 1)
            return Resolved(ToQueryResult(indexId, codebase, channel, exactCanonical[0], OriginFor(codebase)));
        if (exactCanonical.Length > 1)
            return Ambiguous(indexId, codebase, channel, exactCanonical);

        var exactSignature = records
            .Where(record => string.Equals(record.Signature, selector, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactSignature.Length == 1)
            return Resolved(ToQueryResult(indexId, codebase, channel, exactSignature[0], OriginFor(codebase)));
        if (exactSignature.Length > 1)
            return Ambiguous(indexId, codebase, channel, exactSignature);

        var exactQualifiedName = records
            .Where(record => string.Equals(record.QualifiedName, selector, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactQualifiedName.Length == 1)
            return Resolved(ToQueryResult(indexId, codebase, channel, exactQualifiedName[0], OriginFor(codebase)));
        if (exactQualifiedName.Length > 1)
            return Ambiguous(indexId, codebase, channel, exactQualifiedName);

        var bestRank = Rank(records[0], searchQuery);
        var best = records
            .TakeWhile(record => Rank(record, searchQuery) == bestRank)
            .ToArray();
        return best.Length == 1
            ? Resolved(ToQueryResult(indexId, codebase, channel, best[0], OriginFor(codebase)))
            : Ambiguous(indexId, codebase, channel, best);
    }

    private async Task<IReadOnlyList<IndexSymbolRecord>> SearchKindsAsync(
        string indexId,
        string query,
        HashSet<string>? kindNames,
        CancellationToken cancellationToken)
    {
        if (kindNames is null)
        {
            return await _repository.SearchCompletedSymbolsAsync(
                indexId,
                query,
                CandidateLimit,
                cancellationToken);
        }

        var merged = new List<IndexSymbolRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kindName in kindNames.Order(StringComparer.Ordinal))
        {
            var records = await _repository.SearchCompletedSymbolsAsync(
                indexId,
                query,
                CandidateLimit,
                cancellationToken,
                kindName);
            foreach (var record in records)
            {
                if (seen.Add(record.SymbolId))
                    merged.Add(record);
            }
        }

        if (kindNames.Count > 1)
        {
            return merged
                .OrderBy(record => Rank(record, query))
                .ThenBy(record => record.QualifiedName, StringComparer.Ordinal)
                .ThenBy(record => record.Signature, StringComparer.Ordinal)
                .ThenBy(record => record.SymbolId, StringComparer.Ordinal)
                .ToArray();
        }

        return merged;
    }

    private async Task<SymbolResolutionResult> ResolveCanonicalKeyAsync(
        string indexId,
        string selector,
        CodebaseKind codebase,
        CodeChannel channel,
        HashSet<string> kindNames,
        CancellationToken cancellationToken)
    {
        var records = await _repository.GetCompletedSymbolByCanonicalKeyAsync(indexId, selector, cancellationToken);
        var kinded = records.Where(record => kindNames.Contains(record.Kind)).ToArray();
        if (kinded.Length == 1)
            return Resolved(ToQueryResult(indexId, codebase, channel, kinded[0], OriginFor(codebase)));
        if (kinded.Length > 1)
            return Ambiguous(indexId, codebase, channel, kinded);
        if (records.Count == 0)
            return NotFound();
        var mismatch = records
            .OrderBy(record => record.Kind, StringComparer.Ordinal)
            .ThenBy(record => record.SymbolId, StringComparer.Ordinal)
            .First();
        return KindMismatch(indexId, codebase, channel, mismatch);
    }

    private static bool IsCanonicalSelector(string selector, CodebaseKind codebase, CodeChannel channel) =>
        selector.StartsWith(codebase + ":" + channel + ":", StringComparison.Ordinal);

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

    private static string SearchQueryForSelector(
        string selector,
        CodebaseKind codebase,
        CodeChannel channel)
    {
        var prefix = codebase + ":" + channel + ":";
        if (!selector.StartsWith(prefix, StringComparison.Ordinal))
            return selector;

        var kindSeparator = selector.IndexOf(':', prefix.Length);
        return kindSeparator >= 0 && kindSeparator + 1 < selector.Length
            ? selector[(kindSeparator + 1)..]
            : selector;
    }

    private static SymbolResolutionResult Resolved(SymbolQueryResult symbol) =>
        new(SymbolResolutionStatus.Resolved, symbol, []);

    private static SymbolResolutionResult NotFound() =>
        new(SymbolResolutionStatus.NotFound, null, []);

    private static KindedSymbolResolutionResult KindMismatch(
        string indexId,
        CodebaseKind codebase,
        CodeChannel channel,
        IndexSymbolRecord record) =>
        new(
            SymbolResolutionStatus.NotFound,
            null,
            [],
            ToQueryResult(indexId, codebase, channel, record, OriginFor(codebase)));

    private static SymbolResolutionResult Ambiguous(
        string indexId,
        CodebaseKind codebase,
        CodeChannel channel,
        IReadOnlyList<IndexSymbolRecord> records) =>
        new(
            SymbolResolutionStatus.Ambiguous,
            null,
            records
                .Select(record => ToQueryResult(indexId, codebase, channel, record, OriginFor(codebase)))
                .OrderBy(result => result.QualifiedName, StringComparer.Ordinal)
                .ThenBy(result => result.Signature, StringComparer.Ordinal)
                .ThenBy(result => result.SymbolId, StringComparer.Ordinal)
                .ToArray());

    internal static SymbolQueryResult ToQueryResult(
        string indexId,
        CodebaseKind codebase,
        CodeChannel channel,
        IndexSymbolRecord record,
        string? origin = null,
        string? collection = null,
        string? referenceModId = null,
        string? displayName = null,
        string? version = null,
        string? license = null,
        string? relativePath = null,
        string? sha256 = null) =>
        new(
            indexId,
            codebase.ToString(),
            channel.ToString(),
            record.SymbolId,
            record.Kind,
            record.QualifiedName,
            record.Signature,
            record.IsBestEffort,
            origin,
            collection,
            referenceModId,
            displayName,
            version,
            license,
            relativePath,
            sha256);

    internal static string? OriginFor(CodebaseKind codebase) =>
        codebase == CodebaseKind.ScheduleI ? "game" : null;
}
