using System.Text.Json;
using S1Atlas.Cli;
using Xunit;

namespace S1Atlas.Mcp.Tests;

/// <summary>
/// Proves AT-37 acceptance criterion #4: once a native recovery record is persisted (satisfying
/// <c>RequireCompletedNativeInputAsync</c>'s completed-index precondition), the CLI and MCP
/// <c>investigate_seam</c> surfaces expose the same bounded native evidence model for the same
/// <c>nativeSymbolIds</c> + <c>nativeTraversalBudget</c>. Both surfaces are backed by the same
/// <see cref="S1Atlas.Indexing.Query.SeamInvestigationService"/>, so this test demonstrates parity
/// by construction rather than duplicating coverage of the service's evidence-shaping logic
/// (already exercised in S1Atlas.Indexing.Tests and S1Atlas.Mcp.Tests/McpTrustBoundaryTests.cs).
/// </summary>
public sealed class NativeRecoveryParityTests
{
    [Fact]
    public async Task InvestigateSeam_NativeEvidenceMatchesBetweenCliAndMcpForPersistedRecoveryRecord()
    {
        await using var atlas = await SeamMcpTestAtlas.CreateOc32Async();
        await atlas.SeedNativeRecoveredEvidenceAsync();

        var cli = RunCli(atlas.DataRoot, atlas.TargetSymbolId, atlas.NativeSymbolId, 25);
        var mcp = await McpTestHost.CallToolThroughStdioAsync(
            atlas.DataRoot,
            "investigate_seam",
            new Dictionary<string, object?>
            {
                ["behavioralQuestion"] = "Which seam owns settlement clearing?",
                ["selector"] = atlas.TargetSymbolId,
                ["relationshipLimit"] = 3,
                ["ownerLimit"] = 5,
                ["context"] = 0,
                ["nativeSymbolIds"] = new[] { atlas.NativeSymbolId },
                ["nativeTraversalBudget"] = 25
            });

        Assert.Equal(0, cli.ExitCode);
        Assert.Equal(string.Empty, cli.StandardError);

        using var cliDocument = JsonDocument.Parse(cli.StandardOutput);
        using var mcpDocument = JsonDocument.Parse(mcp);

        Assert.True(cliDocument.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("resolved", mcpDocument.RootElement.GetProperty("status").GetString());

        var cliNative = cliDocument.RootElement.GetProperty("data").GetProperty("nativeEvidence");
        var mcpNative = mcpDocument.RootElement.GetProperty("data").GetProperty("nativeEvidence");

        // Demonstrate the MCP tool itself surfaces the persisted native evidence (not merely that
        // the CLI does), then assert the two surfaces expose the identical bounded model.
        Assert.Equal("Matched", mcpNative.GetProperty("lookupStatus").GetString());
        Assert.Equal("Recovered", mcpNative.GetProperty("status").GetString());
        Assert.True(mcpNative.GetProperty("isComplete").GetBoolean());
        var mcpEdge = Assert.Single(mcpNative.GetProperty("directEdges").EnumerateArray());
        Assert.Equal("DirectCall", mcpEdge.GetProperty("kind").GetString());
        Assert.NotEmpty(mcpNative.GetProperty("fieldAccesses").EnumerateArray());

        AssertNativeEvidenceEquivalent(cliNative, mcpNative);
    }

    private static void AssertNativeEvidenceEquivalent(JsonElement cliNative, JsonElement mcpNative)
    {
        Assert.Equal(cliNative.GetProperty("status").GetString(), mcpNative.GetProperty("status").GetString());
        Assert.Equal(cliNative.GetProperty("lookupStatus").GetString(), mcpNative.GetProperty("lookupStatus").GetString());
        Assert.Equal(cliNative.GetProperty("isComplete").GetBoolean(), mcpNative.GetProperty("isComplete").GetBoolean());
        Assert.Equal(cliNative.GetProperty("toolProvenance").GetString(), mcpNative.GetProperty("toolProvenance").GetString());
        Assert.Equal(
            cliNative.GetProperty("fieldAccesses").EnumerateArray().Select(value => value.GetString()).ToArray(),
            mcpNative.GetProperty("fieldAccesses").EnumerateArray().Select(value => value.GetString()).ToArray());

        var cliEdges = cliNative.GetProperty("directEdges").EnumerateArray().ToArray();
        var mcpEdges = mcpNative.GetProperty("directEdges").EnumerateArray().ToArray();
        Assert.Equal(cliEdges.Length, mcpEdges.Length);
        Assert.NotEmpty(cliEdges);

        for (var index = 0; index < cliEdges.Length; index++)
        {
            foreach (var field in new[]
            {
                "edgeId",
                "sourceMethodPointer",
                "targetMethodPointer",
                "targetText",
                "kind",
                "evidence",
                "isComplete"
            })
            {
                Assert.Equal(
                    cliEdges[index].GetProperty(field).GetRawText(),
                    mcpEdges[index].GetProperty(field).GetRawText());
            }
        }
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunCli(
        string dataRoot,
        string selector,
        string nativeSymbolId,
        int nativeTraversalBudget)
    {
        var application = new CliApplication(dataRoot, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(
            [
                "investigate_seam",
                selector,
                "--question",
                "Which seam owns settlement clearing?",
                "--relationship-limit",
                "3",
                "--owner-limit",
                "5",
                "--context",
                "0",
                "--native-symbol-id",
                nativeSymbolId,
                "--native-traversal-budget",
                nativeTraversalBudget.ToString(),
                "--json"
            ],
            output,
            error,
            CancellationToken.None);
        return (exitCode, output.ToString(), error.ToString());
    }
}
