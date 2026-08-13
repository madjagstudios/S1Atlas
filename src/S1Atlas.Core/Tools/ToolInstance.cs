namespace S1Atlas.Core.Tools;

public sealed record ToolInstance(
    string ToolInstanceId,
    string ToolName,
    string? VersionLabel,
    string Platform,
    ToolTrustLevel TrustLevel,
    string? DefinitionDigest,
    string? PackageSha256,
    string ExecutableSha256,
    string ObservedPath,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset LastVerifiedAtUtc,
    ToolInstallationStatus Status);
