namespace S1Atlas.Core.ReferenceMods;

public sealed record ReferenceCollectionDefinition(
    string BuildId,
    string GameIndexId,
    IReadOnlyList<ReferenceModDefinition> Mods);

public sealed record ReferenceModDefinition(
    string ModId,
    string DisplayName,
    string Version,
    string? License,
    string RootPath,
    string ContentSha256);
