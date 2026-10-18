using Xunit;

namespace S1Atlas.Mcp.Tests;

// AT-141: a stdio MCP server whose client goes away must exit 0, not die
// with an unhandled .NET exception (0xE0434352) and a Windows crash dialog.
public sealed class McpClientDisconnectTests
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(20);

    public enum Disconnect
    {
        StdinClosed,
        StderrClosedFirst,
        AllPipesClosed
    }

    [Theory]
    [InlineData(Disconnect.StdinClosed)]
    [InlineData(Disconnect.StderrClosedFirst)]
    [InlineData(Disconnect.AllPipesClosed)]
    public async Task ClientGoingAway_ExitsCleanly(Disconnect disconnect)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        await using var server = await McpRawStdioProcess.StartInitializedAsync(
            atlas.DataRoot,
            environment: null,
            cancellationToken);

        switch (disconnect)
        {
            case Disconnect.StdinClosed:
                server.CloseStdin();
                break;
            case Disconnect.StderrClosedFirst:
                // The request makes the server log while stderr is closed.
                server.CloseStderr();
                var response = await server.RequestAsync(
                    """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
                    cancellationToken);
                Assert.Contains("\"tools\"", response, StringComparison.Ordinal);
                server.CloseStdin();
                break;
            case Disconnect.AllPipesClosed:
                // What the server sees when its parent process is killed.
                server.CloseStderr();
                server.CloseStdout();
                server.CloseStdin();
                break;
        }

        var exitCode = await server.WaitForExitCodeAsync(ExitTimeout, cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Unhandled exception", server.StandardErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisconnectErrorDuringShutdown_ExitsCleanly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        await using var server = await McpRawStdioProcess.StartInitializedAsync(
            atlas.DataRoot,
            new Dictionary<string, string> { ["S1ATLAS_MCP_TEST_FAULT"] = "stop-io" },
            cancellationToken);

        server.CloseStdin();
        var exitCode = await server.WaitForExitCodeAsync(ExitTimeout, cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Unhandled exception", server.StandardErrorText, StringComparison.Ordinal);
    }
}
