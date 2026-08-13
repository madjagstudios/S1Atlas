using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Tools;

internal sealed class ToolInstallationDocument
{
    public int? SchemaVersion { get; init; }
    public string? ToolId { get; init; }
    public string? DisplayName { get; init; }
    public string? Version { get; init; }
    public string? Platform { get; init; }
    public string? DefinitionDigest { get; init; }
    public string? PackageSha256 { get; init; }
    public string? ExecutableSha256 { get; init; }
    public string? RootPath { get; init; }
    public ToolInstallationStatus? Status { get; init; }
    public DateTimeOffset? InstalledAtUtc { get; init; }
    public DateTimeOffset? LastVerifiedAtUtc { get; init; }
    public List<ToolProbeResultDocument?>? ProbeResults { get; init; }
    public string? ReplacedInstallationPath { get; init; }
}

internal sealed class ToolProbeResultDocument
{
    public string? ProbeId { get; init; }
    public bool? Succeeded { get; init; }
    public int? ExitCode { get; init; }
    public bool? TimedOut { get; init; }
    public bool? StandardOutputTruncated { get; init; }
    public bool? StandardErrorTruncated { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
}
