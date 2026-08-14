namespace S1Atlas.Core.Indexing;

public sealed record IndexQueryOptions(
    CodebaseKind Codebase,
    CodeChannel? Channel = CodeChannel.Installed,
    bool AllChannels = false);

public sealed record SymbolQueryResult(
    string IndexId,
    string Codebase,
    string Channel,
    string SymbolId,
    string Kind,
    string QualifiedName,
    string Signature,
    bool IsBestEffort);

public sealed record RelationshipQueryResult(
    string RelationshipId,
    string Kind,
    string Evidence,
    string SourceSymbolId,
    string? TargetSymbolId,
    string? TargetText);

public sealed record SourceQueryResult(
    string IndexId,
    string RelativePath,
    string Sha256,
    long ByteCount,
    string Provenance,
    IReadOnlyList<SourceLocationQueryResult> Locations);

public sealed record SourceLocationQueryResult(
    string SymbolId,
    int StartLine,
    int StartColumn,
    int? EndLine,
    int? EndColumn);
