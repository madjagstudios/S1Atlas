namespace S1Atlas.Core.Indexing;

public sealed record IndexQueryOptions(
    CodebaseKind Codebase,
    CodeChannel? Channel = CodeChannel.Installed,
    bool AllChannels = false,
    int Limit = 50);

public sealed record SymbolQueryResult(
    string IndexId,
    string Codebase,
    string Channel,
    string SymbolId,
    string Kind,
    string QualifiedName,
    string Signature,
    bool IsBestEffort);

public enum SymbolResolutionStatus
{
    Resolved,
    NotFound,
    Ambiguous
}

public sealed record SymbolResolutionResult(
    SymbolResolutionStatus Status,
    SymbolQueryResult? Symbol,
    IReadOnlyList<SymbolQueryResult> Candidates);

public sealed record SymbolSearchResult(
    int TotalCount,
    int ReturnedCount,
    IReadOnlyList<SymbolQueryResult> Results);

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
