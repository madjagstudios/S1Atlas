namespace S1Atlas.Core.Indexing;

public enum DiffClassification
{
    Added,
    Removed,
    MethodBodyChanged,
    RelationshipsChanged,
    Unchanged
}

public sealed record SymbolDiff(
    string CanonicalKey,
    string QualifiedName,
    string Kind,
    DiffClassification Classification,
    string? SignatureBefore,
    string? SignatureAfter);

public sealed record BuildDiffResult(
    string IndexIdA,
    string IndexIdB,
    string Codebase,
    string Channel,
    int TotalSymbolsA,
    int TotalSymbolsB,
    IReadOnlyDictionary<DiffClassification, int> CountsByClassification,
    IReadOnlyList<SymbolDiff> Changes);
