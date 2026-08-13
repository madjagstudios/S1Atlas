namespace S1Atlas.Core.Tools;

public sealed record ToolSafetyLimits(
    long MaximumDownloadBytes,
    long MaximumExpandedBytes,
    int MaximumFileCount);

public sealed record ToolPackageDefinition(
    ToolPackageKind Kind,
    ToolArchiveFormat? ArchiveFormat,
    Uri SourceUri,
    Uri ReleaseUri,
    string AssetName,
    long ExpectedSize,
    string Sha256,
    string ExecutableRelativePath,
    ToolSafetyLimits Limits);

public sealed record ToolLicenseDefinition(
    string SpdxIdentifier,
    Uri SourceUri);

public sealed record ToolProbeDefinition(
    string ProbeId,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<int> AcceptedExitCodes,
    TimeSpan Timeout,
    IReadOnlyList<string> RequiredOutputSubstrings);

public sealed record ToolDefinition(
    int SchemaVersion,
    string ToolId,
    string DisplayName,
    string Version,
    string Platform,
    ToolPackageDefinition Package,
    ToolLicenseDefinition License,
    IReadOnlyList<ToolProbeDefinition> Probes);

public sealed record ResolvedToolDefinition(
    ToolDefinition Definition,
    string DefinitionDigest);
