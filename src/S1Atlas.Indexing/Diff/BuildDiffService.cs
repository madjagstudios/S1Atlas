using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Diff;

public sealed class BuildDiffService
{
    private readonly IIndexRepository _repository;

    public BuildDiffService(IIndexRepository repository)
    {
        _repository = repository;
    }

    public async Task<BuildDiffResult> DiffAsync(
        string indexIdA,
        string indexIdB,
        string codebase,
        string channel,
        string? kindFilter,
        CancellationToken cancellationToken)
    {
        var symbolsA = await _repository.GetCompletedSymbolsAsync(indexIdA, cancellationToken);
        var symbolsB = await _repository.GetCompletedSymbolsAsync(indexIdB, cancellationToken);
        var fingerprintsA = await _repository.GetCompletedFingerprintsAsync(indexIdA, cancellationToken);
        var fingerprintsB = await _repository.GetCompletedFingerprintsAsync(indexIdB, cancellationToken);
        var relationshipsA = await _repository.GetCompletedRelationshipsAsync(indexIdA, cancellationToken);
        var relationshipsB = await _repository.GetCompletedRelationshipsAsync(indexIdB, cancellationToken);

        var mapA = symbolsA.ToDictionary(s => s.CanonicalKey, StringComparer.Ordinal);
        var mapB = symbolsB.ToDictionary(s => s.CanonicalKey, StringComparer.Ordinal);

        var fpBySymbolA = GroupFingerprints(fingerprintsA);
        var fpBySymbolB = GroupFingerprints(fingerprintsB);

        var symIdToKeyA = symbolsA.ToDictionary(s => s.SymbolId, s => s.CanonicalKey, StringComparer.Ordinal);
        var symIdToKeyB = symbolsB.ToDictionary(s => s.SymbolId, s => s.CanonicalKey, StringComparer.Ordinal);

        var relBySourceA = GroupRelationships(relationshipsA);
        var relBySourceB = GroupRelationships(relationshipsB);

        var allKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in mapA.Keys) allKeys.Add(key);
        foreach (var key in mapB.Keys) allKeys.Add(key);

        var changes = new List<SymbolDiff>();
        var counts = new Dictionary<DiffClassification, int>();
        foreach (var c in Enum.GetValues<DiffClassification>())
            counts[c] = 0;

        foreach (var key in allKeys)
        {
            var inA = mapA.TryGetValue(key, out var symA);
            var inB = mapB.TryGetValue(key, out var symB);

            var classification = Classify(
                inA, inB, symA, symB,
                fpBySymbolA, fpBySymbolB,
                relBySourceA, relBySourceB,
                symIdToKeyA, symIdToKeyB);

            var kind = (inB ? symB! : symA!).Kind;

            if (kindFilter is not null && !string.Equals(kind, kindFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            counts[classification]++;

            if (classification == DiffClassification.Unchanged)
                continue;

            var qualifiedName = (inB ? symB! : symA!).QualifiedName;
            string? sigBefore = inA ? symA!.Signature : null;
            string? sigAfter = inB ? symB!.Signature : null;

            if (classification is DiffClassification.MethodBodyChanged or DiffClassification.RelationshipsChanged)
            {
                sigBefore = null;
                sigAfter = null;
            }

            changes.Add(new SymbolDiff(key, qualifiedName, kind, classification, sigBefore, sigAfter));
        }

        changes.Sort((a, b) =>
        {
            var cmp = ((int)a.Classification).CompareTo((int)b.Classification);
            return cmp != 0 ? cmp : string.Compare(a.QualifiedName, b.QualifiedName, StringComparison.Ordinal);
        });

        return new BuildDiffResult(
            indexIdA, indexIdB,
            codebase, channel,
            symbolsA.Count, symbolsB.Count,
            counts, changes);
    }

    public async Task<SymbolDiff?> DiffSymbolAsync(
        string indexIdA,
        string indexIdB,
        string codebase,
        string channel,
        string canonicalKey,
        CancellationToken cancellationToken)
    {
        var symbolsA = await _repository.GetCompletedSymbolsAsync(indexIdA, cancellationToken);
        var symbolsB = await _repository.GetCompletedSymbolsAsync(indexIdB, cancellationToken);
        var fingerprintsA = await _repository.GetCompletedFingerprintsAsync(indexIdA, cancellationToken);
        var fingerprintsB = await _repository.GetCompletedFingerprintsAsync(indexIdB, cancellationToken);
        var relationshipsA = await _repository.GetCompletedRelationshipsAsync(indexIdA, cancellationToken);
        var relationshipsB = await _repository.GetCompletedRelationshipsAsync(indexIdB, cancellationToken);

        var mapA = symbolsA.ToDictionary(s => s.CanonicalKey, StringComparer.Ordinal);
        var mapB = symbolsB.ToDictionary(s => s.CanonicalKey, StringComparer.Ordinal);

        var inA = mapA.TryGetValue(canonicalKey, out var symA);
        var inB = mapB.TryGetValue(canonicalKey, out var symB);
        if (!inA && !inB)
            return null;

        var fpBySymbolA = GroupFingerprints(fingerprintsA);
        var fpBySymbolB = GroupFingerprints(fingerprintsB);

        var symIdToKeyA = symbolsA.ToDictionary(s => s.SymbolId, s => s.CanonicalKey, StringComparer.Ordinal);
        var symIdToKeyB = symbolsB.ToDictionary(s => s.SymbolId, s => s.CanonicalKey, StringComparer.Ordinal);

        var relBySourceA = GroupRelationships(relationshipsA);
        var relBySourceB = GroupRelationships(relationshipsB);

        var classification = Classify(
            inA, inB, symA, symB,
            fpBySymbolA, fpBySymbolB,
            relBySourceA, relBySourceB,
            symIdToKeyA, symIdToKeyB);

        var chosen = inB ? symB! : symA!;
        return new SymbolDiff(
            canonicalKey,
            chosen.QualifiedName,
            chosen.Kind,
            classification,
            inA ? symA!.Signature : null,
            inB ? symB!.Signature : null);
    }

    private static DiffClassification Classify(
        bool inA, bool inB,
        IndexSymbolRecord? symA, IndexSymbolRecord? symB,
        Dictionary<string, Dictionary<string, string>> fpA,
        Dictionary<string, Dictionary<string, string>> fpB,
        Dictionary<string, IReadOnlyList<IndexRelationshipRecord>> relA,
        Dictionary<string, IReadOnlyList<IndexRelationshipRecord>> relB,
        Dictionary<string, string> symIdToKeyA,
        Dictionary<string, string> symIdToKeyB)
    {
        if (!inA) return DiffClassification.Added;
        if (!inB) return DiffClassification.Removed;

        var kindIsMethodLike = symA!.Kind is "Method" or "Constructor";

        if (kindIsMethodLike)
        {
            var bodyResult = ClassifyMethodBody(symA, symB!, fpA, fpB);
            if (bodyResult == DiffClassification.MethodBodyChanged)
                return DiffClassification.MethodBodyChanged;
        }

        var relHashA = HashRelationships(symA!.SymbolId, relA, symIdToKeyA);
        var relHashB = HashRelationships(symB!.SymbolId, relB, symIdToKeyB);
        if (!string.Equals(relHashA, relHashB, StringComparison.Ordinal))
            return DiffClassification.RelationshipsChanged;

        return DiffClassification.Unchanged;
    }

    private static DiffClassification? ClassifyMethodBody(
        IndexSymbolRecord symA,
        IndexSymbolRecord symB,
        Dictionary<string, Dictionary<string, string>> fpA,
        Dictionary<string, Dictionary<string, string>> fpB)
    {
        var hasBodyFpA = TryGetFingerprint(symA.SymbolId, "method-body", fpA, out var bodyHashA);
        var hasBodyFpB = TryGetFingerprint(symB.SymbolId, "method-body", fpB, out var bodyHashB);

        if (hasBodyFpA && hasBodyFpB)
            return string.Equals(bodyHashA, bodyHashB, StringComparison.Ordinal) ? null : DiffClassification.MethodBodyChanged;

        var statusA = symA.BodyRecoveryStatus;
        var statusB = symB.BodyRecoveryStatus;

        if (hasBodyFpA && !hasBodyFpB)
        {
            if (statusB == BodyRecoveryStatus.Recovered)
                return DiffClassification.MethodBodyChanged;
            return null;
        }

        if (!hasBodyFpA && hasBodyFpB)
        {
            if (statusA == BodyRecoveryStatus.Recovered)
                return DiffClassification.MethodBodyChanged;
            return null;
        }

        return null;
    }

    private static string HashRelationships(
        string symbolId,
        Dictionary<string, IReadOnlyList<IndexRelationshipRecord>> relBySource,
        Dictionary<string, string> symIdToKey)
    {
        if (!relBySource.TryGetValue(symbolId, out var rels) || rels.Count == 0)
            return string.Empty;

        var tuples = rels
            .Select(r =>
            {
                var target = r.TargetSymbolId is not null && symIdToKey.TryGetValue(r.TargetSymbolId, out var key)
                    ? key
                    : r.TargetText ?? string.Empty;
                return r.Kind + "\n" + target;
            })
            .Order(StringComparer.Ordinal);

        var input = string.Join("\n\n", tuples);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static bool TryGetFingerprint(
        string symbolId, string kind,
        Dictionary<string, Dictionary<string, string>> grouped,
        out string hash)
    {
        hash = string.Empty;
        return grouped.TryGetValue(symbolId, out var kinds) && kinds.TryGetValue(kind, out hash!);
    }

    private static Dictionary<string, Dictionary<string, string>> GroupFingerprints(
        IReadOnlyList<IndexFingerprintRecord> fingerprints)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var fp in fingerprints)
        {
            if (!result.TryGetValue(fp.SymbolId, out var inner))
            {
                inner = new Dictionary<string, string>(StringComparer.Ordinal);
                result[fp.SymbolId] = inner;
            }
            inner[fp.Kind] = fp.Fingerprint;
        }
        return result;
    }

    private static Dictionary<string, IReadOnlyList<IndexRelationshipRecord>> GroupRelationships(
        IReadOnlyList<IndexRelationshipRecord> relationships)
    {
        return relationships
            .GroupBy(r => r.SourceSymbolId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<IndexRelationshipRecord>)g.ToArray(), StringComparer.Ordinal);
    }
}
