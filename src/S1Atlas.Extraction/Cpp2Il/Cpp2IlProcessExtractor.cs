using System.Runtime.ExceptionServices;
using S1Atlas.Core.Extraction;
using S1Atlas.Extraction.Inputs;
using S1Atlas.Extraction.Processes;

namespace S1Atlas.Extraction.Cpp2Il;

internal sealed class Cpp2IlProcessCanceledException : OperationCanceledException
{
    public Cpp2IlProcessCanceledException(
        int? processId,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset completedAtUtc,
        ExtractionLogResult standardOutput,
        ExtractionLogResult standardError,
        CancellationToken cancellationToken)
        : base("The Cpp2IL process was canceled by the caller.", cancellationToken)
    {
        ProcessId = processId;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public int? ProcessId { get; }
    public DateTimeOffset? StartedAtUtc { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public ExtractionLogResult StandardOutput { get; }
    public ExtractionLogResult StandardError { get; }
}

internal sealed class Cpp2IlProcessExtractor : IIl2CppExtractor
{
    private readonly Func<
        ProcessRequest,
        Func<int, CancellationToken, Task>,
        CancellationToken,
        Task<ProcessResult>> _runProcessAsync;

    public Cpp2IlProcessExtractor()
        : this(new ProcessRunner().RunAsync)
    {
    }

    internal Cpp2IlProcessExtractor(
        Func<
            ProcessRequest,
            Func<int, CancellationToken, Task>,
            CancellationToken,
            Task<ProcessResult>> runProcessAsync)
    {
        _runProcessAsync = runProcessAsync ??
            throw new ArgumentNullException(nameof(runProcessAsync));
    }

    public async Task<ExtractionProcessResult> ExtractAsync(
        ExtractionProcessRequest request,
        Func<int, CancellationToken, Task> processStarted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(processStarted);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOwnedExecutionPaths(request);

        var arguments = Cpp2IlArgumentBuilder.Build(
            request.Profile.Profile,
            request.GameRoot,
            request.OutputDirectory);
        var processRequest = new ProcessRequest(
            request.ExecutablePath,
            request.WorkingDirectory,
            arguments,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["NO_COLOR"] = "true"
            },
            request.StandardOutputPath,
            request.StandardErrorPath,
            request.Profile.Profile.MaximumRetainedStandardOutputBytes,
            request.Profile.Profile.MaximumRetainedStandardErrorBytes,
            request.Profile.Profile.Timeout);
        var result = await _runProcessAsync(
            processRequest,
            processStarted,
            cancellationToken);

        if (result.TerminationReason == ProcessTerminationReason.Canceled)
        {
            throw new Cpp2IlProcessCanceledException(
                result.ProcessId,
                result.StartedAtUtc,
                result.CompletedAtUtc,
                MapLog(result.StandardOutput),
                MapLog(result.StandardError),
                cancellationToken);
        }

        if (result.TerminationReason == ProcessTerminationReason.StartPersistenceFailed)
        {
            if (result.StartPersistenceException is null)
            {
                throw new InvalidOperationException(
                    "Cpp2IL process-start persistence failed without an exception.");
            }

            ExceptionDispatchInfo.Capture(result.StartPersistenceException).Throw();
        }

        return new ExtractionProcessResult(
            result.TerminationReason switch
            {
                ProcessTerminationReason.Exited => ExtractionProcessTerminationReason.Exited,
                ProcessTerminationReason.StartFailed => ExtractionProcessTerminationReason.StartFailed,
                ProcessTerminationReason.TimedOut => ExtractionProcessTerminationReason.TimedOut,
                _ => throw new InvalidOperationException(
                    $"Unexpected Cpp2IL process termination reason '{result.TerminationReason}'.")
            },
            result.ProcessId,
            result.ExitCode,
            result.StartedAtUtc,
            result.CompletedAtUtc,
            MapLog(result.StandardOutput),
            MapLog(result.StandardError),
            result.StartFailureMessage);
    }

    private static ExtractionLogResult MapLog(BoundedLogResult result) => new(
        result.Path,
        result.RetainedBytes,
        result.DiscardedBytes,
        result.Truncated);

    private static void ValidateOwnedExecutionPaths(ExtractionProcessRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.GameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StandardOutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StandardErrorPath);
        ArgumentNullException.ThrowIfNull(request.Profile);

        var output = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(request.OutputDirectory));
        var staging = Directory.GetParent(output)?.FullName;
        if (staging is null ||
            !PathSafety.PathsEqual(
                Path.Combine(staging, "working"),
                Path.GetFullPath(request.WorkingDirectory)) ||
            !PathSafety.PathsEqual(
                Path.Combine(staging, "logs", "stdout.log"),
                Path.GetFullPath(request.StandardOutputPath)) ||
            !PathSafety.PathsEqual(
                Path.Combine(staging, "logs", "stderr.log"),
                Path.GetFullPath(request.StandardErrorPath)) ||
            !PathSafety.IsNormalDirectory(request.WorkingDirectory) ||
            !PathSafety.IsNormalDirectory(request.OutputDirectory) ||
            !PathSafety.IsNormalDirectory(Path.GetDirectoryName(request.StandardOutputPath)!))
        {
            throw new ArgumentException(
                "Cpp2IL execution paths must match the owned Atlas attempt staging allocation.",
                nameof(request));
        }
    }
}
