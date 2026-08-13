namespace S1Atlas.Extraction.Profiles;

internal sealed class ExtractionProfileDocument
{
    public int? SchemaVersion { get; init; }
    public string? ProfileId { get; init; }
    public int? ProfileVersion { get; init; }
    public int? AdapterVersion { get; init; }
    public int? ExtractionSchemaVersion { get; init; }
    public string? ExecutableName { get; init; }
    public string? OutputFormat { get; init; }
    public int? TimeoutSeconds { get; init; }
    public long? MaximumRetainedStandardOutputBytes { get; init; }
    public long? MaximumRetainedStandardErrorBytes { get; init; }
    public List<int>? AcceptedExitCodes { get; init; }
    public List<string?>? RequiredAssemblyIdentities { get; init; }
    public List<SnapshotInputDocument?>? SnapshotInputs { get; init; }
    public List<string?>? UnityVersionSources { get; init; }
}

internal sealed class SnapshotInputDocument
{
    public string? RelativePath { get; init; }
    public string? Role { get; init; }
}
