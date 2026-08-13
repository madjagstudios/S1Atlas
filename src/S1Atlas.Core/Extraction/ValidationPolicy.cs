namespace S1Atlas.Core.Extraction;

public sealed record ValidationPolicy(
    int SchemaVersion,
    string PolicyId,
    int PolicyVersion,
    IReadOnlyList<string> RequiredAssemblyIdentities,
    int MinimumManagedAssemblyCount,
    int MinimumTypeDefinitionCount,
    int MinimumMethodDefinitionCount,
    long MinimumTotalManagedBytes,
    double ComparativeWarningRelativeChange,
    double CatastrophicDecreaseRelativeChange);

public sealed record ResolvedValidationPolicy(
    ValidationPolicy Policy,
    string PolicyDigest);
