namespace S1Atlas.Extraction.Processes;

internal enum ProcessTerminationReason
{
    Exited,
    StartFailed,
    TimedOut,
    Canceled,
    StartPersistenceFailed
}

internal sealed record BoundedLogResult(
    string Path,
    long RetainedBytes,
    long DiscardedBytes,
    bool Truncated);

internal sealed record ProcessResult(
    ProcessTerminationReason TerminationReason,
    int? ProcessId,
    int? ExitCode,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    BoundedLogResult StandardOutput,
    BoundedLogResult StandardError,
    string? StartFailureMessage,
    Exception? StartPersistenceException);
