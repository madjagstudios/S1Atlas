using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Tools;
using Xunit;

namespace S1Atlas.Extraction.Tests.Tools;

public sealed class ToolProbeRunnerTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory =
        ToolTestFixture.CreateTemporaryDirectory();

    [Fact]
    public async Task RunAsync_WhenExitAndRequiredOutputMatch_ReturnsSucceeded()
    {
        var runner = new ToolProbeRunner();
        var executable = CopyCommandProcessor();
        var probe = Probe(
            ["/d", "/c", "echo dll_il_recovery"],
            requiredOutput: ["dll_il_recovery"]);

        var result = await runner.RunAsync(
            executable,
            _temporaryDirectory,
            probe,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Null(result.FailureCode);
        Assert.False(result.StandardOutputTruncated);
        Assert.False(result.StandardErrorTruncated);
    }

    [Fact]
    public async Task RunAsync_WhenExitCodeIsNotAccepted_ReturnsFailure()
    {
        var runner = new ToolProbeRunner();
        var result = await runner.RunAsync(
            CopyCommandProcessor(),
            _temporaryDirectory,
            Probe(["/d", "/c", "exit /b 7"]),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("ToolProbeExitCodeRejected", result.FailureCode);
    }

    [Fact]
    public async Task RunAsync_WhenRequiredOutputIsMissing_ReturnsFailure()
    {
        var runner = new ToolProbeRunner();
        var result = await runner.RunAsync(
            CopyCommandProcessor(),
            _temporaryDirectory,
            Probe(
                ["/d", "/c", "echo another-format"],
                requiredOutput: ["dll_il_recovery"]),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ToolProbeOutputMissing", result.FailureCode);
    }

    [Fact]
    public async Task RunAsync_WhenTimeoutExpires_KillsProcessAndReturnsTimedOut()
    {
        var runner = new ToolProbeRunner();
        var probe = Probe(
            ["/d", "/c", "ping 127.0.0.1 -n 6 > nul"],
            timeout: TimeSpan.FromMilliseconds(100));

        var result = await runner.RunAsync(
            CopyCommandProcessor(),
            _temporaryDirectory,
            probe,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
        Assert.Equal("ToolProbeTimedOut", result.FailureCode);
    }

    [Fact]
    public async Task RunAsync_WhenCancellationRequested_KillsProcessAndThrowsCancellation()
    {
        var runner = new ToolProbeRunner();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));
        var probe = Probe(
            ["/d", "/c", "ping 127.0.0.1 -n 6 > nul"],
            timeout: TimeSpan.FromSeconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(
                CopyCommandProcessor(),
                _temporaryDirectory,
                probe,
                cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_WhenOutputExceedsLimit_ContinuesDrainingAndMarksTruncated()
    {
        var runner = new ToolProbeRunner();
        var probe = Probe(
            [
                "/d",
                "/c",
                "for /L %i in (1,1,50000) do @echo 0123456789012345678901234567890123456789"
            ],
            timeout: TimeSpan.FromSeconds(30));

        var result = await runner.RunAsync(
            CopyCommandProcessor(),
            _temporaryDirectory,
            probe,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.StandardOutputTruncated);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private string CopyCommandProcessor()
    {
        var source = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(source))
        {
            source = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
        }

        var destination = Path.Combine(_temporaryDirectory, "FakeCpp2IL.exe");
        if (!File.Exists(destination))
        {
            File.Copy(source, destination);
        }

        return destination;
    }

    private static ToolProbeDefinition Probe(
        IReadOnlyList<string> arguments,
        IReadOnlyList<string>? requiredOutput = null,
        TimeSpan? timeout = null) =>
        new(
            ProbeId: "test-probe",
            Arguments: arguments,
            AcceptedExitCodes: [0],
            Timeout: timeout ?? TimeSpan.FromSeconds(10),
            RequiredOutputSubstrings: requiredOutput ?? []);
}
