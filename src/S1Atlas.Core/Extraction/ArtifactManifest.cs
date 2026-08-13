namespace S1Atlas.Core.Extraction;

public sealed record ArtifactManifestEntry(
    string RelativePath,
    ArtifactKind Kind,
    long Size,
    string Sha256,
    string? AssemblyName,
    string? ModuleName,
    int? TypeDefinitionCount,
    int? MethodDefinitionCount,
    int? FieldDefinitionCount,
    int? PropertyDefinitionCount,
    int? EventDefinitionCount);

public sealed record ArtifactManifest(
    int SchemaVersion,
    IReadOnlyList<ArtifactManifestEntry> Entries);
