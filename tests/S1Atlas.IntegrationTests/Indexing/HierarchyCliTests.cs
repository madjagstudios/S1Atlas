using System.Text.Json;
using Xunit;

namespace S1Atlas.IntegrationTests.Indexing;

public sealed class HierarchyCliTests
{
    [Fact]
    public async Task Overrides_returns_chain_with_depth_labels()
    {
        await using var atlas = await TargetRelationshipCliAtlas.CreateAsync();

        var result = atlas.Run("overrides", "Demo.Hierarchy.Leaf.Run", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(2, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, data.GetProperty("returnedCount").GetInt32());
        var nodes = data.GetProperty("hierarchyNodes").EnumerateArray().ToArray();
        Assert.Equal(2, nodes.Length);
        Assert.Equal(1, nodes[0].GetProperty("depth").GetInt32());
        Assert.True(nodes[0].GetProperty("isDirect").GetBoolean());
        Assert.Equal(
            "hier-002-leaf-overrides",
            nodes[0].GetProperty("edge").GetProperty("relationshipId").GetString());
        Assert.Equal(
            "Demo.Hierarchy.Mid.Run",
            nodes[0].GetProperty("edge").GetProperty("target").GetProperty("qualifiedName").GetString());
        Assert.Equal(2, nodes[1].GetProperty("depth").GetInt32());
        Assert.False(nodes[1].GetProperty("isDirect").GetBoolean());
        Assert.Equal(
            "hier-001-mid-overrides",
            nodes[1].GetProperty("edge").GetProperty("relationshipId").GetString());
    }

    [Fact]
    public async Task Overrides_human_output_labels_direct_steps()
    {
        await using var atlas = await TargetRelationshipCliAtlas.CreateAsync();

        var result = atlas.Run("overrides", "Demo.Hierarchy.Leaf.Run");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("direct", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("depth 2", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverriddenBy_respects_depth_limit()
    {
        await using var atlas = await TargetRelationshipCliAtlas.CreateAsync();

        var result = atlas.Run("overridden-by", "Demo.Hierarchy.Base.Run", "--depth", "1", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetProperty("totalCount").GetInt32());
        var nodes = data.GetProperty("hierarchyNodes").EnumerateArray().ToArray();
        var node = Assert.Single(nodes);
        Assert.True(node.GetProperty("isDirect").GetBoolean());
        Assert.Equal(
            "hier-001-mid-overrides",
            node.GetProperty("edge").GetProperty("relationshipId").GetString());
    }

    [Fact]
    public async Task OverriddenBy_rejects_invalid_depth()
    {
        await using var atlas = await TargetRelationshipCliAtlas.CreateAsync();

        var result = atlas.Run("overridden-by", "Demo.Hierarchy.Base.Run", "--depth", "0", "--json");

        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            "InvalidDepth",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Derived_pages_with_true_totals()
    {
        await using var atlas = await TargetRelationshipCliAtlas.CreateAsync();

        var result = atlas.Run("derived", "Demo.Hierarchy.Base", "--limit", "1", "--offset", "1", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(2, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, data.GetProperty("returnedCount").GetInt32());
        var nodes = data.GetProperty("hierarchyNodes").EnumerateArray().ToArray();
        var node = Assert.Single(nodes);
        Assert.Equal(2, node.GetProperty("depth").GetInt32());
        Assert.Equal(
            "hier-004-leaf-inherits",
            node.GetProperty("edge").GetProperty("relationshipId").GetString());
    }

    [Fact]
    public async Task Derived_rejects_negative_offset()
    {
        await using var atlas = await TargetRelationshipCliAtlas.CreateAsync();

        var result = atlas.Run("derived", "Demo.Hierarchy.Base", "--offset", "-1", "--json");

        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            "InvalidOffset",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Overrides_not_found_reports_symbol_not_found()
    {
        await using var atlas = await TargetRelationshipCliAtlas.CreateAsync();

        var result = atlas.Run("overrides", "No.Such.Symbol", "--json");

        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            "SymbolNotFound",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Overrides_ambiguous_reports_ambiguous_symbol()
    {
        await using var atlas = await TargetRelationshipCliAtlas.CreateAsync();

        var result = atlas.Run("overrides", "Run", "--json");

        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            "AmbiguousSymbol",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task OverriddenBy_scope_all_merges_reference_overrider()
    {
        await using var atlas = await TargetRelationshipCliAtlas.CreateAsync();

        var result = atlas.Run(
            "overridden-by",
            "Demo.Hierarchy.Base.Run",
            "--scope",
            "all",
            "--collection",
            atlas.Collection,
            "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(3, data.GetProperty("totalCount").GetInt32());
        var nodes = data.GetProperty("hierarchyNodes").EnumerateArray().ToArray();
        Assert.Contains(
            nodes,
            node => node.GetProperty("edge").GetProperty("relationshipId").GetString() == "hier-005-mod-overrides");
    }

    [Fact]
    public async Task Derived_defaults_to_game_scope()
    {
        await using var atlas = await TargetRelationshipCliAtlas.CreateAsync();

        var result = atlas.Run("derived", "Demo.Hierarchy.Base", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(2, data.GetProperty("totalCount").GetInt32());
    }
}
