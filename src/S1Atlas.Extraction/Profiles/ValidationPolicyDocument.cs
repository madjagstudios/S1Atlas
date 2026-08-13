namespace S1Atlas.Extraction.Profiles;

internal sealed class ValidationPolicyDocument
{
    public int? SchemaVersion { get; init; }
    public string? PolicyId { get; init; }
    public int? PolicyVersion { get; init; }
    public List<string?>? RequiredAssemblyIdentities { get; init; }
    public int? MinimumManagedAssemblyCount { get; init; }
    public int? MinimumTypeDefinitionCount { get; init; }
    public int? MinimumMethodDefinitionCount { get; init; }
    public long? MinimumTotalManagedBytes { get; init; }
    public double? ComparativeWarningRelativeChange { get; init; }
    public double? CatastrophicDecreaseRelativeChange { get; init; }
}
