using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class McpServerLifetimeTests
{
    private static readonly TimeSpan SurvivorTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task StdioServer_DisposedAfterUse_LeavesNoSurvivor()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var server = await McpTestServer.StartAsync(
            atlas.DataRoot,
            TestContext.Current.CancellationToken);
        Assert.NotEmpty(server.OwnedProcessIds);

        _ = await server.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        await server.DisposeAsync();

        await server.AssertNoSurvivorsAsync(SurvivorTimeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StdioServer_FailingUse_LeavesNoSurvivor()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        McpTestServer? server = null;
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var owned = await McpTestServer.StartAsync(
                atlas.DataRoot,
                TestContext.Current.CancellationToken);
            server = owned;
            _ = await owned.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
            throw new InvalidOperationException("Simulated test failure with a live server.");
        });

        Assert.NotNull(server);
        Assert.NotEmpty(server.OwnedProcessIds);
        await server.AssertNoSurvivorsAsync(SurvivorTimeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StdioServer_FailedConnect_LeavesNoSurvivor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var before = McpTestServer.FindMatchingProcessIds(IsPingTree).ToHashSet();

        _ = await Assert.ThrowsAnyAsync<Exception>(() =>
            McpTestServer.StartNonRespondingAsync(cancellationToken));

        var deadline = DateTime.UtcNow + SurvivorTimeout;
        while (true)
        {
            var leaked = McpTestServer.FindMatchingProcessIds(IsPingTree)
                .Where(pid => !before.Contains(pid))
                .ToArray();
            if (leaked.Length == 0)
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"Failed connect left survivors: {string.Join(", ", leaked)}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    private static bool IsPingTree(string commandLine) =>
        commandLine.Contains("ping", StringComparison.OrdinalIgnoreCase) &&
        commandLine.Contains("127.0.0.1", StringComparison.Ordinal);
}
