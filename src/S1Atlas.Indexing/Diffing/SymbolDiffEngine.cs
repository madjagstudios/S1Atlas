using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Diffing;

public sealed class SymbolDiffEngine
{
    private static readonly string[] FingerprintLayers = ["declaration", "structural", "method-body", "source"];

    public IndexDiffResult Compare(IndexSnapshotFacts from, IndexSnapshotFacts to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.Run.Status != IndexRunStatus.Completed || to.Run.Status != IndexRunStatus.Completed)
            throw new InvalidDataException("Only completed index runs can be compared.");
        if (from.Snapshot.Codebase != to.Snapshot.Codebase)
            throw new ArgumentException("Only snapshots from the same codebase can be compared.");

        var fromByKey = BuildUniqueMap(from.Symbols, ComparisonKey);
        var toByKey = BuildUniqueMap(to.Symbols, ComparisonKey);
        var fromFingerprints = BuildFingerprintMap(from.Fingerprints);
        var toFingerprints = BuildFingerprintMap(to.Fingerprints);
        var fromById = from.Symbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        var toById = to.Symbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        var pairs = PairSymbols(fromByKey, toByKey);
        var changes = pairs
            .Select(pair => ComparePair(pair, from, to, fromFingerprints, toFingerprints, fromById, toById))
            .OrderBy(change => change.ComparisonKey, StringComparer.Ordinal)
            .ThenBy(change => change.FromSymbolId, StringComparer.Ordinal)
            .ThenBy(change => change.ToSymbolId, StringComparer.Ordinal)
            .ToArray();

        return new IndexDiffResult(
            from.Run.IndexId,
            to.Run.IndexId,
            from.Snapshot,
            to.Snapshot,
            FidelityBasis(from.Snapshot),
            FidelityBasis(to.Snapshot),
            changes);
    }

    public static string ComparisonKey(IndexSymbolRecord symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var firstSeparator = symbol.CanonicalKey.IndexOf(':');
        var secondSeparator = firstSeparator < 0 ? -1 : symbol.CanonicalKey.IndexOf(':', firstSeparator + 1);
        if (firstSeparator <= 0 || secondSeparator <= firstSeparator + 1 || secondSeparator == symbol.CanonicalKey.Length - 1)
            throw new InvalidDataException($"The symbol canonical key '{symbol.CanonicalKey}' does not contain codebase, channel, and symbol segments.");
        return symbol.CanonicalKey[(secondSeparator + 1)..];
    }

    public static string LineageKey(IndexSymbolRecord symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var comparisonKey = ComparisonKey(symbol);
        var kindSeparator = comparisonKey.IndexOf(':');
        var renderedSignature = kindSeparator >= 0 ? comparisonKey[(kindSeparator + 1)..] : comparisonKey;
        var separator = renderedSignature.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0)
            return symbol.Kind + ":" + symbol.QualifiedName;

        var declaringPart = renderedSignature[..separator];
        var declaringSeparator = declaringPart.LastIndexOf(' ');
        var declaringType = declaringSeparator >= 0 ? declaringPart[(declaringSeparator + 1)..] : declaringPart;
        var memberPart = renderedSignature[(separator + 2)..];
        var parameterStart = memberPart.IndexOf('(');
        var memberName = parameterStart >= 0 ? memberPart[..parameterStart] : memberPart;
        return symbol.Kind + ":" + declaringType + "::" + memberName;
    }

    private static SymbolDiff ComparePair(
        SymbolPair pair,
        IndexSnapshotFacts from,
        IndexSnapshotFacts to,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> fromFingerprints,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> toFingerprints,
        IReadOnlyDictionary<string, IndexSymbolRecord> fromById,
        IReadOnlyDictionary<string, IndexSymbolRecord> toById)
    {
        var fromSymbol = pair.From;
        var toSymbol = pair.To;
        var comparisonKey = toSymbol is not null ? ComparisonKey(toSymbol) : ComparisonKey(fromSymbol!);
        var lineageKey = toSymbol is not null ? LineageKey(toSymbol) : LineageKey(fromSymbol!);
        var kinds = new List<SymbolChangeKind>();
        var evidence = new List<SymbolDiffEvidence>();
        var relationships = new List<RelationshipDiff>();

        if (fromSymbol is null)
        {
            kinds.Add(SymbolChangeKind.Added);
        }
        else if (toSymbol is null)
        {
            kinds.Add(SymbolChangeKind.Removed);
        }
        else
        {
            var oldFingerprint = GetFingerprints(fromFingerprints, fromSymbol.SymbolId);
            var newFingerprint = GetFingerprints(toFingerprints, toSymbol.SymbolId);
            if (!string.Equals(ComparisonKey(fromSymbol), ComparisonKey(toSymbol), StringComparison.Ordinal) ||
                DifferentLayer(oldFingerprint, newFingerprint, "declaration"))
            {
                kinds.Add(SymbolChangeKind.SignatureChanged);
                evidence.Add(Evidence("declaration", oldFingerprint, newFingerprint));
            }

            if (DifferentLayer(oldFingerprint, newFingerprint, "structural"))
            {
                kinds.Add(SymbolChangeKind.StructuralChanged);
                evidence.Add(Evidence("structural", oldFingerprint, newFingerprint));
            }

            if (IsCallable(fromSymbol) || IsCallable(toSymbol))
            {
                if (fromSymbol.BodyRecoveryStatus != BodyRecoveryStatus.Recovered || toSymbol.BodyRecoveryStatus != BodyRecoveryStatus.Recovered)
                {
                    kinds.Add(SymbolChangeKind.BodyUnavailable);
                    evidence.Add(new SymbolDiffEvidence(
                        "method-body",
                        fromSymbol.BodyRecoveryStatus?.ToString(),
                        toSymbol.BodyRecoveryStatus?.ToString()));
                }
                else if (DifferentLayer(oldFingerprint, newFingerprint, "method-body"))
                {
                    kinds.Add(SymbolChangeKind.BodyChanged);
                    evidence.Add(Evidence("method-body", oldFingerprint, newFingerprint));
                }
            }

            if (DifferentSourceLayer(oldFingerprint, newFingerprint))
            {
                kinds.Add(SymbolChangeKind.SourceChanged);
                evidence.Add(new SymbolDiffEvidence(
                    "source",
                    SourceFingerprint(oldFingerprint),
                    SourceFingerprint(newFingerprint)));
            }

            relationships.AddRange(CompareRelationships(
                fromSymbol,
                toSymbol,
                from,
                to,
                fromById,
                toById));
            if (relationships.Count > 0)
                kinds.Add(SymbolChangeKind.RelationshipsChanged);

            if (kinds.Count == 0)
                kinds.Add(SymbolChangeKind.Unchanged);
        }

        return new SymbolDiff(
            comparisonKey,
            lineageKey,
            fromSymbol?.SymbolId,
            toSymbol?.SymbolId,
            fromSymbol?.QualifiedName,
            toSymbol?.QualifiedName,
            fromSymbol?.Signature,
            toSymbol?.Signature,
            kinds,
            evidence,
            relationships,
            fromSymbol?.BodyRecoveryStatus,
            toSymbol?.BodyRecoveryStatus);
    }

    private static IReadOnlyList<RelationshipDiff> CompareRelationships(
        IndexSymbolRecord fromSymbol,
        IndexSymbolRecord toSymbol,
        IndexSnapshotFacts from,
        IndexSnapshotFacts to,
        IReadOnlyDictionary<string, IndexSymbolRecord> fromById,
        IReadOnlyDictionary<string, IndexSymbolRecord> toById)
    {
        var oldEdges = from.Relationships
            .Where(edge => edge.SourceSymbolId == fromSymbol.SymbolId)
            .Select(edge => (Key: RelationshipKey(edge, fromById), Edge: edge))
            .ToDictionary(item => item.Key, item => item.Edge, StringComparer.Ordinal);
        var newEdges = to.Relationships
            .Where(edge => edge.SourceSymbolId == toSymbol.SymbolId)
            .Select(edge => (Key: RelationshipKey(edge, toById), Edge: edge))
            .ToDictionary(item => item.Key, item => item.Edge, StringComparer.Ordinal);
        var changes = new List<RelationshipDiff>();
        foreach (var item in oldEdges.Where(item => !newEdges.ContainsKey(item.Key)).OrderBy(item => item.Key, StringComparer.Ordinal))
            changes.Add(ToRelationshipDiff(item.Value, RelationshipChangeKind.Removed, fromById));
        foreach (var item in newEdges.Where(item => !oldEdges.ContainsKey(item.Key)).OrderBy(item => item.Key, StringComparer.Ordinal))
            changes.Add(ToRelationshipDiff(item.Value, RelationshipChangeKind.Added, toById));
        return changes;
    }

    private static string RelationshipKey(IndexRelationshipRecord edge, IReadOnlyDictionary<string, IndexSymbolRecord> symbols) =>
        edge.Kind + ":" + (edge.TargetSymbolId is { } targetId && symbols.TryGetValue(targetId, out var target)
            ? ComparisonKey(target)
            : "?" + edge.TargetText);

    private static RelationshipDiff ToRelationshipDiff(
        IndexRelationshipRecord edge,
        RelationshipChangeKind change,
        IReadOnlyDictionary<string, IndexSymbolRecord> symbols)
    {
        var source = symbols[edge.SourceSymbolId];
        var target = edge.TargetSymbolId is { } targetId && symbols.TryGetValue(targetId, out var targetSymbol)
            ? new DiffEndpoint(targetSymbol.SymbolId, ComparisonKey(targetSymbol), targetSymbol.QualifiedName, targetSymbol.Signature, null, true)
            : new DiffEndpoint(edge.TargetSymbolId, null, null, null, edge.TargetText, false);
        return new RelationshipDiff(
            edge.Kind,
            change,
            new DiffEndpoint(source.SymbolId, ComparisonKey(source), source.QualifiedName, source.Signature, null, true),
            target,
            edge.Evidence);
    }

    private static bool DifferentSourceLayer(IReadOnlyDictionary<string, string> from, IReadOnlyDictionary<string, string> to)
    {
        var oldSource = SourceFingerprint(from);
        var newSource = SourceFingerprint(to);
        return oldSource is not null || newSource is not null
            ? !string.Equals(oldSource, newSource, StringComparison.Ordinal)
            : false;
    }

    private static bool DifferentLayer(IReadOnlyDictionary<string, string> from, IReadOnlyDictionary<string, string> to, string layer)
    {
        from.TryGetValue(layer, out var oldValue);
        to.TryGetValue(layer, out var newValue);
        return oldValue is not null || newValue is not null
            ? !string.Equals(oldValue, newValue, StringComparison.Ordinal)
            : false;
    }

    private static SymbolDiffEvidence Evidence(string layer, IReadOnlyDictionary<string, string> from, IReadOnlyDictionary<string, string> to)
    {
        from.TryGetValue(layer, out var oldValue);
        to.TryGetValue(layer, out var newValue);
        return new SymbolDiffEvidence(layer, oldValue, newValue);
    }

    private static string? SourceFingerprint(IReadOnlyDictionary<string, string> fingerprints) =>
        fingerprints.TryGetValue("source", out var source) ? source :
        fingerprints.TryGetValue("SourceLine", out var sourceLine) ? sourceLine : null;

    private static bool IsCallable(IndexSymbolRecord symbol) =>
        symbol.Kind is "Method" or "Constructor";

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuildFingerprintMap(IReadOnlyList<IndexFingerprintRecord> fingerprints) =>
        fingerprints
            .GroupBy(fingerprint => fingerprint.SymbolId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, string>)group
                    .ToDictionary(fingerprint => fingerprint.Kind, fingerprint => fingerprint.Fingerprint, StringComparer.Ordinal),
                StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> GetFingerprints(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> all,
        string symbolId) =>
        all.TryGetValue(symbolId, out var result) ? result : new Dictionary<string, string>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IndexSymbolRecord> BuildUniqueMap(
        IReadOnlyList<IndexSymbolRecord> symbols,
        Func<IndexSymbolRecord, string> keySelector)
    {
        var result = new Dictionary<string, IndexSymbolRecord>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            var key = keySelector(symbol);
            if (!result.TryAdd(key, symbol))
                throw new InvalidDataException($"The completed index contains duplicate symbol comparison key '{key}'.");
        }
        return result;
    }

    private static IReadOnlyList<SymbolPair> PairSymbols(
        IReadOnlyDictionary<string, IndexSymbolRecord> from,
        IReadOnlyDictionary<string, IndexSymbolRecord> to)
    {
        var pairs = new List<SymbolPair>();
        var remainingFrom = new HashSet<string>(from.Keys, StringComparer.Ordinal);
        var remainingTo = new HashSet<string>(to.Keys, StringComparer.Ordinal);
        foreach (var key in from.Keys.Intersect(to.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            pairs.Add(new SymbolPair(from[key], to[key]));
            remainingFrom.Remove(key);
            remainingTo.Remove(key);
        }

        var fromLineages = remainingFrom
            .Select(key => from[key])
            .GroupBy(LineageKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(ComparisonKey, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var toLineages = remainingTo
            .Select(key => to[key])
            .GroupBy(LineageKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(ComparisonKey, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        foreach (var lineage in fromLineages.Keys.Union(toLineages.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            fromLineages.TryGetValue(lineage, out var oldSymbols);
            toLineages.TryGetValue(lineage, out var newSymbols);
            oldSymbols ??= [];
            newSymbols ??= [];
            var paired = Math.Min(oldSymbols.Length, newSymbols.Length);
            for (var index = 0; index < paired; index++)
                pairs.Add(new SymbolPair(oldSymbols[index], newSymbols[index]));
            for (var index = paired; index < oldSymbols.Length; index++)
                pairs.Add(new SymbolPair(oldSymbols[index], null));
            for (var index = paired; index < newSymbols.Length; index++)
                pairs.Add(new SymbolPair(null, newSymbols[index]));
        }
        return pairs;
    }

    private static string FidelityBasis(CodeSnapshotRecord snapshot) => snapshot.Codebase switch
    {
        CodebaseKind.ScheduleI => "declaration, structural, metadata relationships, and normalized source; method bodies require Recovered symbols",
        _ when snapshot.Channel == CodeChannel.Installed => "declaration, structural, relationships, normalized source, and recovered managed IL where available",
        _ => "declaration, structural, normalized source, and behavioral relationships only where source binding resolved a target"
    };

    private sealed record SymbolPair(IndexSymbolRecord? From, IndexSymbolRecord? To);
}
