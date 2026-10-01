using System.Net;
using Xunit;

namespace S1Atlas.Web.Tests;

public sealed class ServeRunnerTests
{
    [Fact]
    public async Task ServesRequestsAndShutsDownOnCancel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        using var runCts = LinkedCts(cancellationToken);
        var output = new StringWriter();
        var run = ServeRunner.RunAsync(
            fixture.Atlas.DataRoot, 0, false, new RecordingLauncher(), output, TextWriter.Null, runCts.Token);

        var address = await WaitForAddressAsync(output, runCts.Token);
        using var client = new HttpClient { BaseAddress = address };
        using var response = await client.GetAsync("/api/status", runCts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        runCts.Cancel();
        Assert.Equal(0, await run);
    }

    [Fact]
    public async Task SecondServerOnTakenPortExitsOne()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        using var runCts = LinkedCts(cancellationToken);
        var error = new StringWriter();

        var exit = await ServeRunner.RunAsync(
            fixture.Atlas.DataRoot,
            fixture.BaseAddress.Port,
            false,
            new RecordingLauncher(),
            TextWriter.Null,
            error,
            runCts.Token);

        Assert.Equal(1, exit);
        Assert.Contains("already in use", error.ToString());
    }

    [Fact]
    public async Task OpenLaunchesTheListeningAddress()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        using var runCts = LinkedCts(cancellationToken);
        var launcher = new RecordingLauncher();
        var output = new StringWriter();
        var run = ServeRunner.RunAsync(
            fixture.Atlas.DataRoot, 0, true, launcher, output, TextWriter.Null, runCts.Token);

        var address = await WaitForAddressAsync(output, runCts.Token);
        runCts.Cancel();

        Assert.Equal(0, await run);
        Assert.Equal(address, launcher.Launched);
    }

    [Fact]
    public async Task LauncherFailureStillServes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        using var runCts = LinkedCts(cancellationToken);
        var output = new StringWriter();
        var error = new StringWriter();
        var run = ServeRunner.RunAsync(
            fixture.Atlas.DataRoot, 0, true, new ThrowingLauncher(), output, error, runCts.Token);

        await WaitForAddressAsync(output, runCts.Token);
        runCts.Cancel();

        Assert.Equal(0, await run);
        Assert.Contains("Could not open the browser", error.ToString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(70000)]
    public async Task InvalidPortExitsOne(int port)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ServeFixture.CreateAsync(cancellationToken);
        var error = new StringWriter();

        var exit = await ServeRunner.RunAsync(
            fixture.Atlas.DataRoot, port, false, new RecordingLauncher(), TextWriter.Null, error, cancellationToken);

        Assert.Equal(1, exit);
        Assert.Contains("Invalid port", error.ToString());
    }

    [Fact]
    public async Task MissingDirectoryServesUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var missing = Path.Combine(
            Path.GetTempPath(),
            "s1atlas-serve-missing-" + Guid.NewGuid().ToString("N"));
        using var runCts = LinkedCts(cancellationToken);
        var output = new StringWriter();
        var run = ServeRunner.RunAsync(
            missing, 0, false, new RecordingLauncher(), output, TextWriter.Null, runCts.Token);

        var address = await WaitForAddressAsync(output, runCts.Token);
        using var client = new HttpClient { BaseAddress = address };
        using var response = await client.GetAsync("/api/status", runCts.Token);
        var body = await response.Content.ReadAsStringAsync(runCts.Token);
        runCts.Cancel();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("AtlasUnavailable", body);
        Assert.Equal(0, await run);
    }

    private static CancellationTokenSource LinkedCts(CancellationToken testToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        return cts;
    }

    private static async Task<Uri> WaitForAddressAsync(StringWriter output, CancellationToken ct)
    {
        const string marker = "S1Atlas serve listening on ";
        while (!ct.IsCancellationRequested)
        {
            var text = output.ToString();
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                var line = text[(index + marker.Length)..].Split('\n')[0].Trim();
                return new Uri(line);
            }

            await Task.Delay(50, CancellationToken.None);
        }

        throw new TimeoutException("The server never printed its listening address.");
    }

    private sealed class RecordingLauncher : IBrowserLauncher
    {
        public Uri? Launched { get; private set; }

        public void Launch(Uri url) => Launched = url;
    }

    private sealed class ThrowingLauncher : IBrowserLauncher
    {
        public void Launch(Uri url) => throw new InvalidOperationException("No browser here.");
    }
}
