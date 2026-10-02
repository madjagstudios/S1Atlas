using S1Atlas.Application.Envelope;
using S1Atlas.Mcp.Mapping;
using S1Atlas.Mcp.Tools;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class HierarchyToolTests
{
    [Fact]
    public async Task FindOverrides_ReturnsDirectSlot()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindOverridesAsync(
            atlas.HierarchyDerivedMethodSelector,
            buildId: null,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        var node = Assert.Single(envelope.Data!.Nodes);
        Assert.Equal(1, node.Depth);
        Assert.True(node.IsDirect);
        Assert.Equal("Overrides", node.Edge.Kind);
        Assert.Equal("Demo.WidgetBase.Render", node.Edge.Target.QualifiedName);
        Assert.All(
            envelope.Provenance,
            entry => Assert.NotEqual(ProvenanceClassification.Interpretation, entry.Classification));
    }

    [Fact]
    public async Task FindOverriders_ReturnsOverridingMethod()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindOverridersAsync(
            atlas.HierarchyBaseMethodSelector,
            buildId: null,
            limit: 50,
            depth: 10,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        var node = Assert.Single(envelope.Data!.Nodes);
        Assert.True(node.IsDirect);
        Assert.Equal("Demo.Widget.Render", node.Edge.Source.QualifiedName);
    }

    [Fact]
    public async Task FindOverriders_InvalidDepth_ReturnsInvalid()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindOverridersAsync(
            atlas.HierarchyBaseMethodSelector,
            buildId: null,
            limit: 50,
            depth: 0,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidDepth", envelope.Error?.Code);
    }

    [Fact]
    public async Task FindDerivedTypes_ReturnsSubtype()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindDerivedTypesAsync(
            atlas.HierarchyBaseTypeSelector,
            buildId: null,
            limit: 50,
            depth: 10,
            offset: 0,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(1, envelope.Data!.TotalCount);
        var node = Assert.Single(envelope.Data.Nodes);
        Assert.True(node.IsDirect);
        Assert.Equal("Inherits", node.Edge.Kind);
        Assert.Equal("Demo.Widget", node.Edge.Source.QualifiedName);
    }

    [Fact]
    public async Task FindDerivedTypes_InvalidOffset_ReturnsInvalid()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindDerivedTypesAsync(
            atlas.HierarchyBaseTypeSelector,
            buildId: null,
            limit: 50,
            depth: 10,
            offset: -1,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidOffset", envelope.Error?.Code);
    }

    [Fact]
    public async Task FindOverrides_NotFound_ReturnsNotFound()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindOverridesAsync(
            "No.Such.Symbol",
            buildId: null,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(ToolStatus.NotFound, envelope.Status);
        Assert.Equal("SymbolNotFound", envelope.Error?.Code);
        Assert.Empty(envelope.Suggestions);
        Assert.Null(envelope.TotalCandidateCount);
    }

    [Fact]
    public async Task FindOverrides_Ambiguous_ReturnsAmbiguous()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindOverridesAsync(
            "Render",
            buildId: null,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Ambiguous, envelope.Status);
        Assert.NotEmpty(envelope.Candidates);
        Assert.Equal(envelope.Candidates.Count, envelope.TotalCandidateCount);
        Assert.Empty(envelope.Suggestions);
    }

    [Fact]
    public async Task FindOverriders_Ambiguous_ReturnsCandidatesWithTotal()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindOverridersAsync(
            "Render",
            buildId: null,
            limit: 50,
            depth: 10,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Ambiguous, envelope.Status);
        Assert.NotEmpty(envelope.Candidates);
        Assert.Equal(envelope.Candidates.Count, envelope.TotalCandidateCount);
        Assert.Empty(envelope.Suggestions);
    }

    [Fact]
    public async Task FindDerivedTypes_Ambiguous_ReturnsCandidatesWithTotal()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindDerivedTypesAsync(
            "Render",
            buildId: null,
            limit: 50,
            depth: 10,
            offset: 0,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Ambiguous, envelope.Status);
        Assert.NotEmpty(envelope.Candidates);
        Assert.Equal(envelope.Candidates.Count, envelope.TotalCandidateCount);
        Assert.Empty(envelope.Suggestions);
    }

    private static CodeSymbolTools CreateTools(McpTestAtlas atlas)
    {
        var services = McpServerComposition.BuildReadOnlyServices(atlas.DataRoot);
        return new CodeSymbolTools(services);
    }
}
