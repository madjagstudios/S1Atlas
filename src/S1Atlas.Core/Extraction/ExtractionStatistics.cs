namespace S1Atlas.Core.Extraction;

public sealed record AssemblyIdentityStatistics(
    string AssemblyName,
    int FileCount,
    long ManagedBytes,
    int TypeDefinitionCount,
    int MethodDefinitionCount,
    int FieldDefinitionCount,
    int PropertyDefinitionCount,
    int EventDefinitionCount);

public sealed record ExtractionStatistics(
    int ArtifactCount,
    int LibraryCount,
    int ManagedAssemblyCount,
    int TypeDefinitionCount,
    int MethodDefinitionCount,
    int FieldDefinitionCount,
    int PropertyDefinitionCount,
    int EventDefinitionCount,
    long TotalOutputBytes,
    long TotalManagedBytes,
    IReadOnlyList<AssemblyIdentityStatistics> Assemblies);
