using Xunit;

namespace S1Atlas.Mcp.Tests;

// AT-141: an exception nothing catches must leave a crash log with its stack
// and end the process with a chosen code, never the 0xE0434352 crash.
public sealed class McpCrashLogTests
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData("thread-io", 0, "System.IO.IOException")]
    [InlineData("stop-unexpected", 1, "System.InvalidOperationException")]
    public async Task UncaughtException_WritesCrashLogAndExitsWithoutCrashing(
        string fault,
        int expectedExitCode,
        string expectedExceptionType)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), $"s1atlas-crashlog-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
            await using var server = await McpRawStdioProcess.StartInitializedAsync(
                atlas.DataRoot,
                new Dictionary<string, string>
                {
                    ["S1ATLAS_MCP_TEST_FAULT"] = fault,
                    ["TMP"] = tempRoot,
                    ["TEMP"] = tempRoot
                },
                cancellationToken);

            server.CloseStdin();
            var exitCode = await server.WaitForExitCodeAsync(ExitTimeout, cancellationToken);

            Assert.Equal(expectedExitCode, exitCode);
            Assert.DoesNotContain("Unhandled exception", server.StandardErrorText, StringComparison.Ordinal);
            var crashLog = Assert.Single(Directory.GetFiles(Path.Combine(tempRoot, "s1atlas"), "mcp-crash-*.log"));
            Assert.Contains(expectedExceptionType, await File.ReadAllTextAsync(crashLog, cancellationToken), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
