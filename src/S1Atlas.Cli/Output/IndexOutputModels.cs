namespace S1Atlas.Cli.Output;

internal sealed record InstalledApiIndexOutput(
    string Codebase,
    string Channel,
    string IndexId,
    bool Reused,
    int SymbolCount);

internal sealed record IndexOutput(
    string Codebase,
    string Channel,
    string SourceInputIdentity,
    string IndexId,
    bool Reused,
    int SymbolCount,
    int SourceFileCount,
    int RelationshipCount,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<InstalledApiIndexOutput> ApiIndexes { get; init; } = [];
}
