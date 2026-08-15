namespace S1Atlas.Cli.Output;

internal sealed record DiffOutputData(
    string IdentifierA,
    string IdentifierB,
    string IndexIdA,
    string IndexIdB,
    string Codebase,
    string Channel,
    int TotalSymbolsA,
    int TotalSymbolsB,
    DiffOutputCounts Counts,
    int TotalChanged,
    int ReturnedCount,
    IReadOnlyList<DiffOutputChange> Changes);

internal sealed record DiffOutputCounts(
    int Added,
    int Removed,
    int MethodBodyChanged,
    int RelationshipsChanged,
    int Unchanged);

internal sealed record DiffOutputChange(
    string CanonicalKey,
    string QualifiedName,
    string Kind,
    string Classification,
    string? SignatureBefore,
    string? SignatureAfter);
