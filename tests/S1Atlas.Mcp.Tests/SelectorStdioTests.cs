using System.Text.Json;
using Xunit;

namespace S1Atlas.Mcp.Tests;

// Walks every symbol-taking MCP tool over a real stdio server (search
// excluded: list-shaped output with no resolution) to prove ambiguous
// selectors report candidates with exact totals and unknown selectors
// report near matches on the wire.
public sealed class SelectorStdioTests
{
    private static readonly IReadOnlyDictionary<string, string> AmbiguousSelectors =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["get_type"] = "DealerService",
            ["get_method"] = "worker",
            ["get_callable_surface"] = "worker",
            ["get_source"] = "worker",
            ["find_callers"] = "worker",
            ["find_callees"] = "worker",
            ["find_references"] = "worker",
            ["find_field_references"] = "SharedValue",
            ["find_related_types"] = "worker",
            ["find_overrides"] = "Render",
            ["find_overriders"] = "Render",
            ["find_derived_types"] = "Render",
        };

    private static readonly IReadOnlyDictionary<string, string> TypoSelectors =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["get_type"] = "Widjet",
            ["get_method"] = "Rendor",
            ["get_callable_surface"] = "Rendor",
            ["get_source"] = "Widjet",
            ["find_callers"] = "Widjet",
            ["find_callees"] = "Widjet",
            ["find_references"] = "Widjet",
            ["find_field_references"] = "SharedVaule",
            ["find_related_types"] = "Widjet",
            ["find_overrides"] = "Rendor",
            ["find_overriders"] = "Rendor",
            ["find_derived_types"] = "Rendor",
        };

    [Fact]
    public async Task Ambiguous_selectors_report_candidates_with_totals_over_stdio()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var calls = AmbiguousSelectors.ToDictionary(
            entry => entry.Key,
            entry => WithCodebase(entry.Key, entry.Value));

        var results = await McpTestHost.CallToolsThroughStdioAsync(atlas.DataRoot, calls);

        Assert.Equal(AmbiguousSelectors.Count, results.Count);
        foreach (var (tool, serialized) in results)
        {
            using var document = JsonDocument.Parse(serialized);
            var root = document.RootElement;
            Assert.Equal("ambiguous", root.GetProperty("status").GetString());
            var candidates = root.GetProperty("candidates");
            Assert.True(candidates.GetArrayLength() >= 2, tool);
            Assert.Equal(
                candidates.GetArrayLength(),
                root.GetProperty("totalCandidateCount").GetInt32());
            Assert.Equal(0, root.GetProperty("suggestions").GetArrayLength());
        }
    }

    [Fact]
    public async Task Typo_selectors_report_suggestions_over_stdio()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();

        foreach (var (tool, selector) in TypoSelectors)
        {
            var result = await McpTestHost.CallToolRawThroughStdioAsync(
                atlas.DataRoot,
                tool,
                WithCodebase(tool, selector));

            Assert.True(result.IsError ?? false, tool);
            var serialized = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(
                Assert.Single(result.Content)).Text;
            using var document = JsonDocument.Parse(serialized);
            var root = document.RootElement;
            Assert.Equal("not_found", root.GetProperty("status").GetString());
            Assert.True(root.GetProperty("suggestions").GetArrayLength() >= 1, tool);
        }
    }

    private static IReadOnlyDictionary<string, object?> WithCodebase(string tool, string selector)
    {
        var arguments = new Dictionary<string, object?> { ["selector"] = selector };
        if (!string.Equals(tool, "get_callable_surface", StringComparison.Ordinal))
        {
            arguments["codebase"] = "scheduleI";
        }

        return arguments;
    }
}
