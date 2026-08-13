using System.Diagnostics;
using System.Text.Json;
using S1Atlas.Extraction.Processes;
using Xunit;

namespace S1Atlas.Extraction.Tests.Processes;

public sealed class ProcessRunnerTests : IDisposable
{
    private readonly string _temporaryDirectory = CreateTemporaryDirectory();

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task RunAsync_WhenProcessExits_ReportsExactExitCode(int exitCode)
    {
        var callbackProcessId = 0;
        var before = DateTimeOffset.UtcNow;

        var result = await new ProcessRunner().RunAsync(
            Request(["process", "success", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)]),
            (processId, _) =>
            {
                callbackProcessId = processId;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(callbackProcessId, result.ProcessId);
        Assert.NotNull(result.StartedAtUtc);
        Assert.InRange(result.StartedAtUtc.Value, before, result.CompletedAtUtc);
        Assert.Empty(await File.ReadAllBytesAsync(
            result.StandardOutput.Path,
            TestContext.Current.CancellationToken));
        Assert.Empty(await File.ReadAllBytesAsync(
            result.StandardError.Path,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_PreservesArgumentBoundariesAndMetacharacters()
    {
        var recordPath = Path.Combine(_temporaryDirectory, "arguments.json");
        var expected = new[] { "a b", "&", "$(literal)", "`quoted`", "雪" };

        var result = await new ProcessRunner().RunAsync(
            Request(["process", "print-args", recordPath, .. expected]),
            ProcessStarted,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(
            expected,
            JsonSerializer.Deserialize<string[]>(
                await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task RunAsync_UsesRequestedWorkingDirectory()
    {
        var workingDirectory = Path.Combine(_temporaryDirectory, "working with spaces");
        Directory.CreateDirectory(workingDirectory);
        var recordPath = Path.Combine(_temporaryDirectory, "working.txt");

        var result = await new ProcessRunner().RunAsync(
            Request(
                ["process", "print-working-directory", recordPath],
                workingDirectory: workingDirectory),
            ProcessStarted,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory)),
            Path.TrimEndingDirectorySeparator(
                await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task RunAsync_AppliesExplicitEnvironmentOverride()
    {
        var recordPath = Path.Combine(_temporaryDirectory, "environment.txt");
        const string variable = "S1ATLAS_PROCESS_RUNNER_TEST_VALUE";

        var result = await new ProcessRunner().RunAsync(
            Request(
                ["process", "print-environment", recordPath, variable],
                environment: new Dictionary<string, string?> { [variable] = "literal value & $()" }),
            ProcessStarted,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(
            "literal value & $()",
            await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_WhenEnvironmentOverrideIsNull_RemovesInheritedValue()
    {
        var recordPath = Path.Combine(_temporaryDirectory, "environment-removed.txt");
        const string variable = "PATH";

        var result = await new ProcessRunner().RunAsync(
            Request(
                ["process", "print-environment", recordPath, variable],
                environment: new Dictionary<string, string?> { [variable] = null }),
            ProcessStarted,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(
            "<null>",
            await File.ReadAllTextAsync(recordPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_WhenStartFails_CreatesOwnedEmptyLogsAndDoesNotInvokeCallback()
    {
        var callbackInvoked = false;
        var request = Request(["unused"]) with
        {
            ExecutablePath = Path.Combine(_temporaryDirectory, "missing.exe")
        };

        var result = await new ProcessRunner().RunAsync(
            request,
            (_, _) =>
            {
                callbackInvoked = true;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ProcessTerminationReason.StartFailed, result.TerminationReason);
        Assert.Null(result.ProcessId);
        Assert.Null(result.ExitCode);
        Assert.Null(result.StartedAtUtc);
        Assert.NotNull(result.StartFailureMessage);
        Assert.False(callbackInvoked);
        Assert.Equal(0, new FileInfo(result.StandardOutput.Path).Length);
        Assert.Equal(0, new FileInfo(result.StandardError.Path).Length);
    }

    [Fact]
    public async Task RunAsync_WhenTimeoutExpires_KillsProcessAndReportsTimedOut()
    {
        var result = await new ProcessRunner().RunAsync(
            Request(["process", "wait"], timeout: TimeSpan.FromMilliseconds(300)),
            ProcessStarted,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProcessTerminationReason.TimedOut, result.TerminationReason);
        Assert.Null(result.ExitCode);
        Assert.NotNull(result.ProcessId);
        AssertProcessExited(result.ProcessId.Value);
    }

    [Fact]
    public async Task RunAsync_WhenCallerCancels_KillsProcessAndReportsCanceled()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var result = await new ProcessRunner().RunAsync(
            Request(["process", "wait"], timeout: TimeSpan.FromSeconds(20)),
            ProcessStarted,
            cancellation.Token);

        Assert.Equal(ProcessTerminationReason.Canceled, result.TerminationReason);
        Assert.Null(result.ExitCode);
        Assert.NotNull(result.ProcessId);
        AssertProcessExited(result.ProcessId.Value);
    }

    [Fact]
    public async Task RunAsync_DrainsLargeStandardOutputAndErrorSimultaneouslyAfterCaps()
    {
        const int totalBytes = 2 * 1024 * 1024;
        const int retainedBytes = 4096;

        var result = await new ProcessRunner().RunAsync(
            Request(
                ["process", "emit", totalBytes.ToString(), totalBytes.ToString()],
                maximumOutputBytes: retainedBytes,
                maximumErrorBytes: retainedBytes,
                timeout: TimeSpan.FromSeconds(20)),
            ProcessStarted,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(retainedBytes, result.StandardOutput.RetainedBytes);
        Assert.Equal(totalBytes - retainedBytes, result.StandardOutput.DiscardedBytes);
        Assert.True(result.StandardOutput.Truncated);
        Assert.Equal(retainedBytes, result.StandardError.RetainedBytes);
        Assert.Equal(totalBytes - retainedBytes, result.StandardError.DiscardedBytes);
        Assert.True(result.StandardError.Truncated);
    }

    [Fact]
    public async Task RunAsync_StartsBothLogDrainsBeforePersistenceCallback()
    {
        var request = Request(["process", "success", "0"]);
        var callbackCompleted = false;

        var result = await new ProcessRunner().RunAsync(
            request,
            (_, _) =>
            {
                Assert.True(File.Exists(request.StandardOutputPath));
                Assert.True(File.Exists(request.StandardErrorPath));
                callbackCompleted = true;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.True(callbackCompleted);
        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
    }

    [Fact]
    public async Task RunAsync_WhenPersistenceCallbackFails_KillsProcessAndPreservesFailure()
    {
        var failure = new InvalidOperationException("injected persistence failure");

        var result = await new ProcessRunner().RunAsync(
            Request(["process", "wait"], timeout: TimeSpan.FromSeconds(20)),
            (_, _) => Task.FromException(failure),
            TestContext.Current.CancellationToken);

        Assert.Equal(ProcessTerminationReason.StartPersistenceFailed, result.TerminationReason);
        Assert.Null(result.ExitCode);
        Assert.Same(failure, result.StartPersistenceException);
        Assert.Contains("injected persistence failure", result.StartFailureMessage);
        Assert.NotNull(result.ProcessId);
        AssertProcessExited(result.ProcessId.Value);
    }

    [Fact]
    public async Task RunAsync_WhenTimedOut_KillsSpawnedChildProcessTree()
    {
        var childPidPath = Path.Combine(_temporaryDirectory, "child.pid");

        var result = await new ProcessRunner().RunAsync(
            Request(
                ["process", "spawn-child", childPidPath],
                timeout: TimeSpan.FromSeconds(3)),
            ProcessStarted,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProcessTerminationReason.TimedOut, result.TerminationReason);
        Assert.True(File.Exists(childPidPath));
        var childPid = int.Parse(
            await File.ReadAllTextAsync(childPidPath, TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        await AssertProcessExitedEventuallyAsync(childPid);
    }

    [Fact]
    public async Task RunAsync_WhenLogDrainFails_KillsWaitingProcessPromptly()
    {
        var request = Request(
            ["process", "wait"],
            timeout: TimeSpan.FromSeconds(5));
        await File.WriteAllTextAsync(
            request.StandardOutputPath,
            "existing",
            TestContext.Current.CancellationToken);
        var processId = 0;
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAsync<IOException>(() =>
            new ProcessRunner().RunAsync(
                request,
                (startedProcessId, _) =>
                {
                    processId = startedProcessId;
                    return Task.CompletedTask;
                },
                TestContext.Current.CancellationToken));

        elapsed.Stop();
        Assert.InRange(elapsed.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.NotEqual(0, processId);
        AssertProcessExited(processId);
        Assert.Equal(
            "existing",
            await File.ReadAllTextAsync(
                request.StandardOutputPath,
                TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private ProcessRequest Request(
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        long maximumOutputBytes = 4096,
        long maximumErrorBytes = 4096,
        TimeSpan? timeout = null) =>
        new(
            ExecutablePath: FakeCpp2IlLocator.ExecutablePath,
            WorkingDirectory: workingDirectory ?? _temporaryDirectory,
            Arguments: arguments,
            EnvironmentOverrides: environment ?? new Dictionary<string, string?>(),
            StandardOutputPath: Path.Combine(_temporaryDirectory, $"{Guid.NewGuid():N}.stdout.log"),
            StandardErrorPath: Path.Combine(_temporaryDirectory, $"{Guid.NewGuid():N}.stderr.log"),
            MaximumRetainedStandardOutputBytes: maximumOutputBytes,
            MaximumRetainedStandardErrorBytes: maximumErrorBytes,
            Timeout: timeout ?? TimeSpan.FromSeconds(10));

    private static Task ProcessStarted(int _, CancellationToken __) => Task.CompletedTask;

    private static void AssertProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException)
        {
        }
    }

    private static async Task AssertProcessExitedEventuallyAsync(int processId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Child process {processId} survived process-tree termination.");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"s1atlas-process-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
