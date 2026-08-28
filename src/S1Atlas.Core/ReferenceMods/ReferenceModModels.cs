namespace S1Atlas.Core.ReferenceMods;

public sealed record ReferenceCollectionDefinition(
    string BuildId,
    string GameIndexId,
    IReadOnlyList<ReferenceModDefinition> Mods,
    string CollectionId = "",
    string? CollectionName = null);

public sealed record ReferenceModDefinition(
    string ModId,
    string DisplayName,
    string Version,
    string? License,
    string RootPath,
    string ContentSha256,
    IReadOnlyList<string>? IncludeSelectors = null,
    IReadOnlyList<string>? ExcludeSelectors = null)
{
    public IReadOnlyList<string> Include => IncludeSelectors ?? [];
    public IReadOnlyList<string> Exclude => ExcludeSelectors ?? [];
}
