using S1Atlas.Core;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Query;

public sealed class SymbolResolver
{
    private const int CandidateLimit = 50;
    private const int MaxSuggestionDistance = 2;
    private const int LadderMinimumLength = 3;
    private const int LadderMaximumRungs = 10;
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

        if (ShortId.TryParsePrefix(selector, out var shortIdPrefix))
        {
            var prefixed = await ResolveShortIdAsync(indexId, shortIdPrefix, codebase, channel, kindNames, cancellationToken);
            if (prefixed is not null)
                return prefixed;
        }

        if (kindNames is not null && IsCanonicalSelector(selector, codebase, channel))
            return await ResolveCanonicalKeyAsync(indexId, selector, codebase, channel, kindNames, cancellationToken);

        var searchQuery = SearchQueryForSelector(selector, codebase, channel);
        var records = await SearchKindsAsync(indexId, searchQuery, kindNames, cancellationToken, includeGenerated: true, limit: CandidateLimit + 1);
        if (records.Count == 0)
            return NotFound(await SuggestAsync(indexId, codebase, channel, searchQuery, kindNames, cancellationToken));

        var exactCanonical = records
            .Where(record => string.Equals(record.CanonicalKey, selector, StringComparison.Ordinal))
            .ToArray();
        if (exactCanonical.Length == 1)
            return Resolved(ToQueryResult(indexId, codebase, channel, exactCanonical[0], OriginFor(codebase)));
        if (exactCanonical.Length > 1)
            return Ambiguous(indexId, codebase, channel, exactCanonical, TotalUnlessTruncated(records.Count, exactCanonical.Length));

        var exactSignature = records
            .Where(record => string.Equals(record.Signature, selector, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactSignature.Length == 1)
            return Resolved(ToQueryResult(indexId, codebase, channel, exactSignature[0], OriginFor(codebase)));
        if (exactSignature.Length > 1)
            return Ambiguous(indexId, codebase, channel, exactSignature, TotalUnlessTruncated(records.Count, exactSignature.Length));

        var exactQualifiedName = records
            .Where(record => string.Equals(record.QualifiedName, selector, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactQualifiedName.Length == 1)
            return Resolved(ToQueryResult(indexId, codebase, channel, exactQualifiedName[0], OriginFor(codebase)));
        if (exactQualifiedName.Length > 1)
            return Ambiguous(indexId, codebase, channel, exactQualifiedName, TotalUnlessTruncated(records.Count, exactQualifiedName.Length));

        var bestRank = Rank(records[0], searchQuery);
        var best = records
            .TakeWhile(record => Rank(record, searchQuery) == bestRank)
            .ToArray();
        if (best.Length == 1)
            return Resolved(ToQueryResult(indexId, codebase, channel, best[0], OriginFor(codebase)));
        var shown = best.Length > CandidateLimit ? best.Take(CandidateLimit).ToArray() : best;
        return Ambiguous(indexId, codebase, channel, shown, TotalUnlessTruncated(records.Count, best.Length));
    }

    private async Task<IReadOnlyList<IndexSymbolRecord>> SearchKindsAsync(
        string indexId,
        string query,
        HashSet<string>? kindNames,
        CancellationToken cancellationToken,
        bool includeGenerated,
        int limit)
    {
        if (kindNames is null)
        {
            return await _repository.SearchCompletedSymbolsAsync(
                indexId,
                query,
                limit,
                cancellationToken,
                includeGenerated: includeGenerated);
        }

        var merged = new List<IndexSymbolRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kindName in kindNames.Order(StringComparer.Ordinal))
        {
            var records = await _repository.SearchCompletedSymbolsAsync(
                indexId,
                query,
                limit,
                cancellationToken,
                kindName,
                includeGenerated: includeGenerated);
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
            return Ambiguous(indexId, codebase, channel, kinded, kinded.Length);
        if (records.Count == 0)
            return NotFound(await SuggestAsync(indexId, codebase, channel, SearchQueryForSelector(selector, codebase, channel), kindNames, cancellationToken));
        var mismatch = records
            .OrderBy(record => record.Kind, StringComparer.Ordinal)
            .ThenBy(record => record.SymbolId, StringComparer.Ordinal)
            .First();
        return KindMismatch(indexId, codebase, channel, mismatch);
    }

    private async Task<SymbolResolutionResult?> ResolveShortIdAsync(
        string indexId,
        string prefix,
        CodebaseKind codebase,
        CodeChannel channel,
        HashSet<string>? kindNames,
        CancellationToken cancellationToken)
    {
        var rows = await _repository.GetCompletedSymbolsByIdPrefixAsync(indexId, prefix, CandidateLimit + 1, cancellationToken);
        if (rows.Count == 0)
            return null;
        var kinded = kindNames is null
            ? rows
            : rows.Where(record => kindNames.Contains(record.Kind)).ToArray();
        if (kinded.Count == 1)
            return Resolved(ToQueryResult(indexId, codebase, channel, kinded[0], OriginFor(codebase)));
        if (kinded.Count > 1)
        {
            var shown = kinded.Count > CandidateLimit ? kinded.Take(CandidateLimit).ToArray() : kinded;
            int? total = rows.Count <= CandidateLimit
                ? kinded.Count
                : kindNames is null
                    ? await _repository.CountCompletedSymbolsByIdPrefixAsync(indexId, prefix, cancellationToken)
                    : null;
            return Ambiguous(indexId, codebase, channel, shown, total);
        }

        var mismatch = rows
            .OrderBy(record => record.Kind, StringComparer.Ordinal)
            .ThenBy(record => record.SymbolId, StringComparer.Ordinal)
            .First();
        return KindMismatch(indexId, codebase, channel, mismatch);
    }

    private async Task<IReadOnlyList<SymbolQueryResult>> SuggestAsync(
        string indexId,
        CodebaseKind codebase,
        CodeChannel channel,
        string searchQuery,
        HashSet<string>? kindNames,
        CancellationToken cancellationToken)
    {
        var segment = SymbolNames.SimpleName(searchQuery);
        var loweredSegment = segment.ToLowerInvariant();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scored = new List<(IndexSymbolRecord Record, int Distance, int Order)>();
        var order = 0;
        foreach (var rung in LadderRungs(segment, searchQuery))
        {
            var pool = await SearchKindsAsync(indexId, rung, kindNames, cancellationToken, includeGenerated: false, limit: CandidateLimit);
            foreach (var record in pool)
            {
                if (!seen.Add(record.SymbolId))
                    continue;
                var distance = EditDistance(SymbolNames.SimpleName(record.QualifiedName).ToLowerInvariant(), loweredSegment);
                if (distance <= MaxSuggestionDistance)
                    scored.Add((record, distance, order++));
            }
        }

        return scored
            .OrderBy(scored => scored.Distance)
            .ThenBy(scored => scored.Order)
            .Take(ResolutionMerge.MaxSuggestions)
            .Select(scored => ToQueryResult(indexId, codebase, channel, scored.Record, OriginFor(codebase)))
            .ToArray();
    }

    private static IEnumerable<string> LadderRungs(string segment, string searchQuery)
    {
        var yielded = 0;
        for (var length = segment.Length; length >= LadderMinimumLength && yielded < LadderMaximumRungs; length--)
        {
            var rung = segment[..length];
            if (string.Equals(rung, searchQuery, StringComparison.Ordinal))
                continue;
            yielded++;
            yield return rung;
        }
    }

    private static int EditDistance(string left, string right)
    {
        if (left.Length == 0)
            return right.Length;
        if (right.Length == 0)
            return left.Length;
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
            previous[column] = column;
        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1);
                current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
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

    private static int? TotalUnlessTruncated(int fetched, int matches) =>
        fetched > CandidateLimit ? null : matches;

    private static SymbolResolutionResult Resolved(SymbolQueryResult symbol) =>
        new(SymbolResolutionStatus.Resolved, symbol, []);

    private static SymbolResolutionResult NotFound(IReadOnlyList<SymbolQueryResult> suggestions) =>
        new(SymbolResolutionStatus.NotFound, null, [], suggestions);

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
        IReadOnlyList<IndexSymbolRecord> records,
        int? totalCandidateCount) =>
        new(
            SymbolResolutionStatus.Ambiguous,
            null,
            records
                .Select(record => ToQueryResult(indexId, codebase, channel, record, OriginFor(codebase)))
                .OrderBy(result => result.QualifiedName, StringComparer.Ordinal)
                .ThenBy(result => result.Signature, StringComparer.Ordinal)
                .ThenBy(result => result.SymbolId, StringComparer.Ordinal)
                .ToArray(),
            TotalCandidateCount: totalCandidateCount);

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
            sha256,
            record.SymbolId.Length >= 12 ? record.SymbolId[..12] : record.SymbolId);

    internal static string? OriginFor(CodebaseKind codebase) =>
        codebase == CodebaseKind.ScheduleI ? "game" : null;

}
