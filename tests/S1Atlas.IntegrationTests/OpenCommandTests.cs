using System.Net;
using System.Net.Sockets;
using S1Atlas.Cli;
using S1Atlas.TestSupport;
using S1Atlas.TestSupport.Seeding;
using S1Atlas.Web;
using Xunit;

namespace S1Atlas.IntegrationTests;

public sealed class OpenCommandTests : IAsyncDisposable
{
    private readonly List<string> _configurationDirectories = [];

    [Fact]
    public async Task Open_ResolvesSelectorAndLaunchesSymbolUrl()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var atlas = await SyntheticAtlas.SeedServeFixtureAsync(cancellationToken);
        var host = ServeHost.Create(new ServeOptions(atlas.DataRoot, 0));
        await using (host)
        {
            await host.StartAsync(cancellationToken);
            try
            {
                var launcher = new RecordingLauncher();
                using var output = new StringWriter();
                using var error = new StringWriter();
                var application = CreateApplication(atlas.DataRoot, launcher);

                var exitCode = application.Invoke(
                    ["open", "Demo.Widget", "--port", host.BaseAddress.Port.ToString()],
                    output,
                    error,
                    cancellationToken);

                var expected = $"http://127.0.0.1:{host.BaseAddress.Port}/symbol/{SyntheticAtlas.WidgetTypeId}";
                Assert.Equal(0, exitCode);
                Assert.Equal(expected, output.ToString().Trim());
                Assert.Equal(expected, Assert.Single(launcher.Launched).ToString());
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Open_ClosedPortPrintsUrlAndServeHint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var atlas = await SyntheticAtlas.SeedServeFixtureAsync(cancellationToken);
        var launcher = new RecordingLauncher();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = CreateApplication(atlas.DataRoot, launcher);
        var port = ClosedLoopbackPort();

        var exitCode = application.Invoke(
            ["open", "Demo.Widget", "--port", port.ToString()],
            output,
            error,
            cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains($"/symbol/{SyntheticAtlas.WidgetTypeId}", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("s1atlas serve", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task Open_DifferentBuildWarnsWithoutOpening()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var atlas = await SyntheticAtlas.SeedServeFixtureAsync(cancellationToken);
        await using var other = await SyntheticAtlas.SeedTwoBuildFixtureAsync(cancellationToken);
        var host = ServeHost.Create(new ServeOptions(other.DataRoot, 0));
        await using (host)
        {
            await host.StartAsync(cancellationToken);
            try
            {
                var launcher = new RecordingLauncher();
                using var output = new StringWriter();
                using var error = new StringWriter();
                var application = CreateApplication(atlas.DataRoot, launcher);

                var exitCode = application.Invoke(
                    ["open", "Demo.Widget", "--port", host.BaseAddress.Port.ToString()],
                    output,
                    error,
                    cancellationToken);

                Assert.Equal(1, exitCode);
                Assert.Contains("different build or index", error.ToString(), StringComparison.Ordinal);
                Assert.Empty(launcher.Launched);
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Open_AmbiguousSelectorMatchesQueryCommands()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var atlas = await SyntheticAtlas.SeedServeFixtureAsync(cancellationToken);
        var launcher = new RecordingLauncher();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = CreateApplication(atlas.DataRoot, launcher);

        var exitCode = application.Invoke(
            ["open", "Demo"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("AmbiguousSymbol", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task Open_UnknownSelectorMatchesQueryCommands()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var atlas = await SyntheticAtlas.SeedServeFixtureAsync(cancellationToken);
        var launcher = new RecordingLauncher();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = CreateApplication(atlas.DataRoot, launcher);

        var exitCode = application.Invoke(
            ["open", "ZzzNoSuchSymbol"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("SymbolNotFound", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(launcher.Launched);
    }

    private CliApplication CreateApplication(string dataRoot, RecordingLauncher launcher)
    {
        var configurationDirectory = Path.Combine(
            Path.GetTempPath(),
            "s1atlas-open-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configurationDirectory);
        _configurationDirectories.Add(configurationDirectory);
        return new CliApplication(
            dataRoot,
            "0.1.0-test",
            configurationDirectory,
            () => new HttpClient(),
            browserLauncher: launcher);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var directory in _configurationDirectories)
            TestDirectory.DeleteTree(directory);
        return ValueTask.CompletedTask;
    }

    private static int ClosedLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class RecordingLauncher : IBrowserLauncher
    {
        private readonly List<Uri> _launched = [];

        public IReadOnlyList<Uri> Launched => _launched;

        public void Launch(Uri url)
        {
            _launched.Add(url);
        }
    }
}
