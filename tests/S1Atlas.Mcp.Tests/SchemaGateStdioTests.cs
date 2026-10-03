using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using S1Atlas.Application.Readiness;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Mcp.Tests;

// Proves the MCP schema gate: on a behind database every tool call
// short-circuits with the shared atlas_unavailable envelope before any
// binding or query runs, the database file is untouched, and upgrading
// through `s1atlas status` recovers without a restart.
public sealed class SchemaGateStdioTests
{
    [Fact]
    public async Task EveryToolReturnsTheSchemaEnvelopeOnABehindDatabase()
    {
        await using var atlas = await McpTestAtlas.CreateBehindSchemaRootAsync();
        await using var server = await StartServerAsync(atlas.DataRoot);

        var tools = await McpTestHost.ListToolsAsync(server.Client);
        Assert.NotEmpty(tools);

        foreach (var tool in tools)
        {
            var error = await CallToolErrorAsync(server.Client, tool);
            Assert.Equal("atlas_unavailable", error.GetProperty("code").GetString());
            Assert.Equal(ReadinessFixCommands.Status, error.GetProperty("hint").GetString());
        }
    }

    [Fact]
    public async Task GatedToolCallLeavesTheDatabaseBytesUnchanged()
    {
        await using var atlas = await McpTestAtlas.CreateBehindSchemaRootAsync();
        var databasePath = Path.Combine(atlas.DataRoot, "atlas.db");
        var before = HashFile(databasePath);
        var filesBefore = Directory.GetFiles(atlas.DataRoot);

        await using var server = await StartServerAsync(atlas.DataRoot);
        var error = await CallToolErrorAsync(server.Client, "get_type");

        Assert.Equal("atlas_unavailable", error.GetProperty("code").GetString());
        Assert.Equal(before, HashFile(databasePath));
        Assert.Equal(
            filesBefore.OrderBy(file => file),
            Directory.GetFiles(atlas.DataRoot).OrderBy(file => file));
    }

    [Fact]
    public async Task UpgradingRecoversWithoutARestart()
    {
        await using var atlas = await McpTestAtlas.CreateBehindSchemaRootAsync();
        await using var server = await StartServerAsync(atlas.DataRoot);

        var blocked = await CallToolErrorAsync(server.Client, "get_type");
        Assert.Equal("atlas_unavailable", blocked.GetProperty("code").GetString());

        await SchemaVersionFixtures.UpgradeToCurrentAsync(
            Path.Combine(atlas.DataRoot, "atlas.db"),
            Path.Combine(atlas.DataRoot, "backups-upgrade"),
            CancellationToken.None);

        var recovered = await CallToolErrorAsync(server.Client, "get_type");
        Assert.Equal("no_current_build", recovered.GetProperty("code").GetString());
        Assert.Equal(ReadinessFixCommands.Scan, recovered.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task StartupLogsTheSchemaStatusToStderr()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var cancellationToken = cancellationTokenSource.Token;
        await using var atlas = await McpTestAtlas.CreateBehindSchemaRootAsync();

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(
            "dotnet",
            $"\"{typeof(McpToolCatalog).Assembly.Location}\" mcp serve")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.StartInfo.Environment["S1ATLAS_HOME"] = atlas.DataRoot;
        process.Start();
        try
        {
            string? line = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = process.StandardError.ReadLineAsync(cancellationToken).AsTask();
                var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken));
                Assert.Same(read, finished);
                line = await read;
                if (line is not null && line.Contains("Atlas database schema v", StringComparison.Ordinal))
                {
                    break;
                }
            }

            Assert.NotNull(line);
            Assert.Contains("is older than", line, StringComparison.Ordinal);
            Assert.Contains("Run 's1atlas status' to fix.", line, StringComparison.Ordinal);
            Assert.False(process.HasExited);
        }
        finally
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static Task<McpTestServer> StartServerAsync(string dataRoot) =>
        McpTestServer.StartAsync(dataRoot);

    private static async Task<JsonElement> CallToolErrorAsync(
        ModelContextProtocol.Client.McpClient client,
        string toolName)
    {
        var outcome = await McpTestHost.CallToolRawAsync(
            client,
            toolName,
            new Dictionary<string, object?> { ["selector"] = "Demo.Widget", ["codebase"] = "scheduleI" });

        Assert.True(outcome.IsError ?? false);
        var serialized = Assert.IsType<TextContentBlock>(Assert.Single(outcome.Content)).Text;
        using var document = JsonDocument.Parse(serialized);
        Assert.Equal("unavailable", document.RootElement.GetProperty("status").GetString());
        return document.RootElement.GetProperty("error").Clone();
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
