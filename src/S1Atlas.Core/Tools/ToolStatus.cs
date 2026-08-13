namespace S1Atlas.Core.Tools;

public sealed record ToolProbeResult(
    string ProbeId,
    bool Succeeded,
    int? ExitCode,
    bool TimedOut,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated,
    string? FailureCode,
    string? FailureMessage);

public sealed record ManagedToolInstallation(
    int SchemaVersion,
    string ToolId,
    string DisplayName,
    string Version,
    string Platform,
    string DefinitionDigest,
    string PackageSha256,
    string ExecutableSha256,
    string RootPath,
    ToolInstallationStatus Status,
    DateTimeOffset InstalledAtUtc,
    DateTimeOffset LastVerifiedAtUtc,
    IReadOnlyList<ToolProbeResult> ProbeResults,
    string? ReplacedInstallationPath);

public sealed record ManagedToolStatus(
    ResolvedToolDefinition Definition,
    ToolInstallationStatus Status,
    ManagedToolInstallation? Installation,
    string? DiagnosticCode,
    string? DiagnosticMessage);
