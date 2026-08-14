namespace S1Atlas.Core.Indexing;

public sealed record NormalizedParameter(
    string Name,
    string Type,
    string RefKind = "");

public sealed record NormalizedSymbol(
    CodebaseKind Codebase,
    CodeChannel Channel,
    SymbolKind Kind,
    string QualifiedName,
    string Signature,
    bool IsBestEffort = false,
    string? SourceFile = null,
    int? SourceLine = null);
