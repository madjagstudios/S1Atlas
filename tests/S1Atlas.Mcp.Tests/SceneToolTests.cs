using S1Atlas.Application.Envelope;
using S1Atlas.Mcp;
using S1Atlas.Mcp.Tools;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class SceneToolTests
{
    [Fact]
    public async Task GetScene_SnapshotFromDifferentBuild_ReturnsInvalid()
    {
        await using var atlas = await McpTestAtlas.SeedTwoSceneBuildsAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetSceneAsync(
            selector: atlas.SceneNameB,
            buildId: atlas.BuildIdA,
            sceneSnapshotId: atlas.SceneSnapshotIdB,
            kind: null,
            includeChildren: false,
            includeComponents: false,
            includeReferences: false,
            limit: 50,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("SceneSnapshotNotFound", envelope.Error!.Code);
    }

    [Fact]
    public async Task ListScenes_ReturnsBoundedPage()
    {
        await using var atlas = await McpTestAtlas.SeedTwoSceneBuildsAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.ListScenesAsync(
            buildId: atlas.BuildIdA,
            sceneSnapshotId: null,
            kind: null,
            query: null,
            limit: 1,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(2, envelope.Data!.Page.TotalCount);
        Assert.Equal(1, envelope.Data.Page.ReturnedCount);
        Assert.Single(envelope.Data.Containers!);
    }

    [Fact]
    public async Task GetComponent_WithCode_ReturnsSymbolHandoff()
    {
        await using var atlas = await McpTestAtlas.SeedTwoSceneBuildsAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetComponentAsync(
            selector: atlas.ComponentSelector,
            buildId: atlas.BuildIdA,
            sceneSnapshotId: null,
            includeReferences: false,
            includeCode: true,
            limit: 50,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal("type-widget", envelope.Data!.Component!.ResolvedTypeSymbolId);
        Assert.Equal(atlas.IndexIdA, envelope.Data.Component.ResolvedCodeIndexId);
    }

    [Fact]
    public async Task GetScene_NoSceneIndex_ReturnsNoCompletedSceneIndex()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetSceneAsync(
            selector: atlas.SceneNameA,
            buildId: null,
            sceneSnapshotId: null,
            kind: null,
            includeChildren: false,
            includeComponents: false,
            includeReferences: false,
            limit: 50,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.NotFound, envelope.Status);
        Assert.Equal("NoCompletedSceneIndex", envelope.Error!.Code);
    }

    private static SceneTools CreateTools(McpTestAtlas atlas) =>
        new(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));
}
