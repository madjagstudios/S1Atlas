namespace S1Atlas.Extraction.Tools;

internal sealed class ToolDefinitionDocument
{
    public int? SchemaVersion { get; init; }
    public string? ToolId { get; init; }
    public string? DisplayName { get; init; }
    public string? Version { get; init; }
    public string? Platform { get; init; }
    public ToolPackageDocument? Package { get; init; }
    public ToolLicenseDocument? License { get; init; }
    public List<ToolProbeDocument?>? Probes { get; init; }
}

internal sealed class ToolPackageDocument
{
    public string? Kind { get; init; }
    public string? ArchiveFormat { get; init; }
    public string? SourceUrl { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? AssetName { get; init; }
    public long? ExpectedSize { get; init; }
    public string? Sha256 { get; init; }
    public string? ExecutableRelativePath { get; init; }
    public ToolSafetyLimitsDocument? Limits { get; init; }
}

internal sealed class ToolSafetyLimitsDocument
{
    public long? MaximumDownloadBytes { get; init; }
    public long? MaximumExpandedBytes { get; init; }
    public int? MaximumFileCount { get; init; }
}

internal sealed class ToolLicenseDocument
{
    public string? SpdxIdentifier { get; init; }
    public string? SourceUrl { get; init; }
}

internal sealed class ToolProbeDocument
{
    public string? ProbeId { get; init; }
    public List<string?>? Arguments { get; init; }
    public List<int>? AcceptedExitCodes { get; init; }
    public int? TimeoutSeconds { get; init; }
    public List<string?>? RequiredOutputSubstrings { get; init; }
}
