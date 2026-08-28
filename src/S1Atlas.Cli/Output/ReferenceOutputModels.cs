namespace S1Atlas.Cli.Output;

internal sealed record ReferenceCollectionValidationOutput(
    string Collection,
    string? CollectionName,
    int ModCount,
    int FileCount,
    int ManagedAssemblyCount,
    int DocumentCount,
    string ContentSha256,
    IReadOnlyList<string> Warnings,
    ReferencePhaseTimings Phases);

internal sealed record ReferenceIndexOutput(
    string Collection,
    string? CollectionName,
    string IndexId,
    string SnapshotId,
    bool Reused,
    int ModCount,
    int DocumentCount,
    int SymbolCount,
    int SourceFileCount,
    int RelationshipCount,
    IReadOnlyList<string> Warnings,
    ReferencePhaseTimings Phases);

internal sealed record ReferenceCollectionListOutput(
    IReadOnlyList<ReferenceCollectionListItem> Collections);

internal sealed record ReferenceCollectionListItem(
    string Collection,
    string IndexId,
    string SnapshotId,
    string BuildId,
    int ModCount,
    IReadOnlyList<ReferenceCollectionModOutput> Mods);

internal sealed record ReferenceCollectionModOutput(
    string ModId,
    string DisplayName,
    string Version,
    string? License,
    string ContentSha256,
    string Provenance = "LocalOnly");

internal sealed record ReferencePhaseTimings(
    long ManifestValidationMilliseconds,
    long InputHashMilliseconds,
    long IndexWorkflowMilliseconds);
