namespace S1Atlas.Core.Extraction;

public interface IIl2CppExtractor
{
    Task<ExtractionProcessResult> ExtractAsync(
        ExtractionProcessRequest request,
        Func<int, CancellationToken, Task> processStarted,
        CancellationToken cancellationToken);
}

public sealed record ExtractionProcessRequest(
    string ExecutablePath,
    string GameRoot,
    string WorkingDirectory,
    string OutputDirectory,
    string StandardOutputPath,
    string StandardErrorPath,
    ResolvedExtractionProfile Profile);

public sealed record ExtractionLogResult(
    string Path,
    long RetainedBytes,
    long DiscardedBytes,
    bool Truncated);

public enum ExtractionProcessTerminationReason
{
    Exited,
    StartFailed,
    TimedOut,
    StartPersistenceFailed
}

public sealed record ExtractionProcessResult(
    ExtractionProcessTerminationReason TerminationReason,
    int? ProcessId,
    int? ExitCode,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    ExtractionLogResult StandardOutput,
    ExtractionLogResult StandardError,
    string? StartFailureMessage);
