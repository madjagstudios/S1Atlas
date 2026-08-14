namespace S1Atlas.Cli.Output;

internal sealed record IndexOutput(
    string Codebase,
    string Channel,
    string SourceInputIdentity,
    string IndexId,
    bool Reused,
    int SymbolCount,
    int SourceFileCount,
    int RelationshipCount,
    IReadOnlyList<string> Warnings);
