namespace S1Atlas.Core.Tools;

public sealed record ManagedToolInstallOutcome(
    ManagedToolInstallation Installation,
    bool WasAlreadyVerified,
    bool Repaired,
    string? QuarantinePath);

public sealed record ToolInstallResult(
    ManagedToolInstallation Installation,
    ToolInstance ToolInstance,
    bool WasAlreadyVerified,
    bool Repaired,
    string? QuarantinePath);
