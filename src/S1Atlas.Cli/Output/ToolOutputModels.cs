namespace S1Atlas.Cli.Output;

internal sealed record ToolsStatusOutput(
    IReadOnlyList<ToolStatusOutput> Tools);

internal sealed record ToolStatusOutput(
    string ToolId,
    string DisplayName,
    string PinnedVersion,
    string Platform,
    string DefinitionDigest,
    string Status,
    string? TrustLevel,
    string? PackageSha256,
    string? ExecutableSha256,
    string? InstallRoot,
    DateTimeOffset? InstalledAtUtc,
    DateTimeOffset? LastVerifiedAtUtc,
    string? DiagnosticCode,
    string? DiagnosticMessage,
    IReadOnlyList<ToolProbeOutput> Probes);

internal sealed record ToolProbeOutput(
    string ProbeId,
    bool Succeeded,
    int? ExitCode,
    bool TimedOut,
    string? FailureCode,
    string? FailureMessage);

internal sealed record ToolInstallOutput(
    ToolStatusOutput Tool,
    bool WasAlreadyVerified,
    bool Repaired,
    string? QuarantinePath);
