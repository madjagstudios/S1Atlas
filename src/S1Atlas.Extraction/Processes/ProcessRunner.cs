using System.ComponentModel;
using System.Diagnostics;

namespace S1Atlas.Extraction.Processes;

internal sealed class ProcessRunner
{
    private readonly BoundedLogWriter _logWriter;

    public ProcessRunner()
        : this(new BoundedLogWriter())
    {
    }

    internal ProcessRunner(BoundedLogWriter logWriter)
    {
        ArgumentNullException.ThrowIfNull(logWriter);
        _logWriter = logWriter;
    }

    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        Func<int, CancellationToken, Task> processStarted,
        CancellationToken cancellationToken)
    {
        Validate(request, processStarted);
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process
        {
            StartInfo = CreateStartInfo(request)
        };

        try
        {
            if (!process.Start())
            {
                return CreateStartFailure(
                    request,
                    "The extraction process did not start.");
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return CreateStartFailure(
                request,
                $"The extraction process could not start: {exception.Message}");
        }

        var startedAtUtc = DateTimeOffset.UtcNow;
        var processId = process.Id;
        var standardOutputTask = _logWriter.DrainAsync(
            process.StandardOutput.BaseStream,
            request.StandardOutputPath,
            request.MaximumRetainedStandardOutputBytes,
            CancellationToken.None);
        var standardErrorTask = _logWriter.DrainAsync(
            process.StandardError.BaseStream,
            request.StandardErrorPath,
            request.MaximumRetainedStandardErrorBytes,
            CancellationToken.None);

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var processLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            await processStarted(processId, processLifetime.Token);
        }
        catch (OperationCanceledException) when (processLifetime.IsCancellationRequested)
        {
            return await TerminateAndCreateResultAsync(
                process,
                request,
                startedAtUtc,
                processId,
                GetCancellationReason(cancellationToken),
                standardOutputTask,
                standardErrorTask,
                startFailureMessage: null,
                startPersistenceException: null);
        }
        catch (Exception exception)
        {
            return await TerminateAndCreateResultAsync(
                process,
                request,
                startedAtUtc,
                processId,
                ProcessTerminationReason.StartPersistenceFailed,
                standardOutputTask,
                standardErrorTask,
                $"Process-start persistence failed: {exception.Message}",
                exception);
        }

        try
        {
            await WaitForExitOrDrainFailureAsync(
                process,
                processLifetime.Token,
                standardOutputTask,
                standardErrorTask);
        }
        catch (OperationCanceledException) when (processLifetime.IsCancellationRequested)
        {
            return await TerminateAndCreateResultAsync(
                process,
                request,
                startedAtUtc,
                processId,
                GetCancellationReason(cancellationToken),
                standardOutputTask,
                standardErrorTask,
                startFailureMessage: null,
                startPersistenceException: null);
        }

        var (standardOutput, standardError) = await AwaitLogsAsync(
            standardOutputTask,
            standardErrorTask);
        return new ProcessResult(
            ProcessTerminationReason.Exited,
            processId,
            process.ExitCode,
            startedAtUtc,
            DateTimeOffset.UtcNow,
            standardOutput,
            standardError,
            StartFailureMessage: null,
            StartPersistenceException: null);
    }

    private static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in request.EnvironmentOverrides)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        return startInfo;
    }

    private static ProcessResult CreateStartFailure(
        ProcessRequest request,
        string message)
    {
        var (standardOutput, standardError) = CreateEmptyLogs(request);
        return new ProcessResult(
            ProcessTerminationReason.StartFailed,
            ProcessId: null,
            ExitCode: null,
            StartedAtUtc: null,
            DateTimeOffset.UtcNow,
            standardOutput,
            standardError,
            message,
            StartPersistenceException: null);
    }

    private static (BoundedLogResult StandardOutput, BoundedLogResult StandardError)
        CreateEmptyLogs(ProcessRequest request)
    {
        var createdOutput = false;
        try
        {
            using (new FileStream(
                request.StandardOutputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read))
            {
            }

            createdOutput = true;
            using (new FileStream(
                request.StandardErrorPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read))
            {
            }
        }
        catch
        {
            if (createdOutput)
            {
                File.Delete(request.StandardOutputPath);
            }

            throw;
        }

        return (
            new BoundedLogResult(
                request.StandardOutputPath,
                RetainedBytes: 0,
                DiscardedBytes: 0,
                Truncated: false),
            new BoundedLogResult(
                request.StandardErrorPath,
                RetainedBytes: 0,
                DiscardedBytes: 0,
                Truncated: false));
    }

    private static async Task<ProcessResult> TerminateAndCreateResultAsync(
        Process process,
        ProcessRequest request,
        DateTimeOffset startedAtUtc,
        int processId,
        ProcessTerminationReason reason,
        Task<BoundedLogResult> standardOutputTask,
        Task<BoundedLogResult> standardErrorTask,
        string? startFailureMessage,
        Exception? startPersistenceException)
    {
        await TerminateProcessTreeAsync(process);
        var (standardOutput, standardError) = await AwaitLogsAsync(
            standardOutputTask,
            standardErrorTask);
        return new ProcessResult(
            reason,
            processId,
            ExitCode: null,
            startedAtUtc,
            DateTimeOffset.UtcNow,
            standardOutput,
            standardError,
            startFailureMessage,
            startPersistenceException);
    }

    private static async Task TerminateProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            Win32Exception or
            NotSupportedException)
        {
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task<(BoundedLogResult StandardOutput, BoundedLogResult StandardError)>
        AwaitLogsAsync(
            Task<BoundedLogResult> standardOutputTask,
            Task<BoundedLogResult> standardErrorTask)
    {
        await Task.WhenAll(standardOutputTask, standardErrorTask);
        return (await standardOutputTask, await standardErrorTask);
    }

    private static async Task WaitForExitOrDrainFailureAsync(
        Process process,
        CancellationToken cancellationToken,
        Task<BoundedLogResult> standardOutputTask,
        Task<BoundedLogResult> standardErrorTask)
    {
        var drainFailed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ObserveNonSuccessfulCompletion(standardOutputTask, drainFailed);
        ObserveNonSuccessfulCompletion(standardErrorTask, drainFailed);

        var processExitTask = process.WaitForExitAsync(cancellationToken);
        var firstCompletion = await Task.WhenAny(
            processExitTask,
            drainFailed.Task);
        if (firstCompletion == drainFailed.Task)
        {
            await TerminateProcessTreeAsync(process);
            await AwaitLogsAsync(standardOutputTask, standardErrorTask);
            throw new InvalidOperationException(
                "A failed log drain did not propagate its exception.");
        }

        await processExitTask;
    }

    private static void ObserveNonSuccessfulCompletion(
        Task task,
        TaskCompletionSource drainFailed)
    {
        _ = task.ContinueWith(
            static (_, state) => ((TaskCompletionSource)state!).TrySetResult(),
            drainFailed,
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static ProcessTerminationReason GetCancellationReason(
        CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? ProcessTerminationReason.Canceled
            : ProcessTerminationReason.TimedOut;

    private static void Validate(
        ProcessRequest request,
        Func<int, CancellationToken, Task> processStarted)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(processStarted);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        ArgumentNullException.ThrowIfNull(request.Arguments);
        ArgumentNullException.ThrowIfNull(request.EnvironmentOverrides);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StandardOutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StandardErrorPath);
        if (request.MaximumRetainedStandardOutputBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.MaximumRetainedStandardOutputBytes));
        }

        if (request.MaximumRetainedStandardErrorBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.MaximumRetainedStandardErrorBytes));
        }

        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Timeout));
        }
    }
}
