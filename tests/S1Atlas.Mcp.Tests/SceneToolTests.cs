using S1Atlas.Application.Envelope;
using S1Atlas.Core.Scenes;
using S1Atlas.Indexing.Scene;
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
        Assert.Equal(atlas.BuildIdA, envelope.Build!.ResolvedBuildId);
        Assert.Contains(envelope.Provenance, entry => entry.Classification == ProvenanceClassification.Fact);
        Assert.Contains(envelope.Provenance, entry => entry.Classification == ProvenanceClassification.Derived);
    }

    [Fact]
    public async Task GetComponent_ReturnsDecodedFieldsWithFactProvenance()
    {
        await using var atlas = await McpTestAtlas.SeedTwoSceneBuildsAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetComponentAsync(
            selector: atlas.ComponentSelector,
            buildId: atlas.BuildIdA,
            sceneSnapshotId: null,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal("50000", Assert.Single(envelope.Data!.ScriptFields!.Fields).Value);
        Assert.Contains(envelope.Provenance, entry =>
            entry.Classification == ProvenanceClassification.Fact &&
            entry.Source == "serialized-script-fields");
    }

    [Fact]
    public async Task GetScriptableObject_ReturnsFieldsByNameWithFactProvenance()
    {
        await using var atlas = await McpTestAtlas.SeedTwoSceneBuildsAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetScriptableObjectAsync(
            selector: atlas.ScriptableAssetSelector,
            buildId: atlas.BuildIdA,
            sceneSnapshotId: null,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal("SCD_Bikers", envelope.Data!.Asset!.Name);
        var fields = envelope.Data.ScriptFields!.Fields;
        Assert.Equal("100", fields[0].Value);
        Assert.Equal((S1Atlas.Core.Scenes.SceneScriptFieldTargetStatus.Resolved, "Root"), (fields[1].Target!.Status, fields[1].Target!.Name));
        Assert.Contains(envelope.Provenance, entry =>
            entry.Classification == ProvenanceClassification.Fact &&
            entry.Source == "serialized-script-fields");
    }

    [Fact]
    public async Task GetScriptableObject_UnknownSelector_ReturnsNotFound()
    {
        await using var atlas = await McpTestAtlas.SeedTwoSceneBuildsAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetScriptableObjectAsync(
            selector: "NoSuchAsset",
            buildId: atlas.BuildIdA,
            sceneSnapshotId: null,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.NotFound, envelope.Status);
        Assert.Equal("ScriptableAssetNotFound", envelope.Error!.Code);
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
    public async Task GetScene_PartiallyRecoveredScene_PreservesDataAndProvenance()
    {
        await using var atlas = await McpTestAtlas.SeedTwoSceneBuildsAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetSceneAsync(
            selector: atlas.SceneNameB,
            buildId: atlas.BuildIdB,
            sceneSnapshotId: null,
            kind: null,
            includeChildren: false,
            includeComponents: false,
            includeReferences: false,
            limit: 50,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(SceneQueryStatus.PartialRecovery, envelope.Data!.Status);
        Assert.Equal(atlas.BuildIdB, envelope.Build!.ResolvedBuildId);
        Assert.Contains(envelope.Provenance, entry => entry.Classification == ProvenanceClassification.Fact);
        Assert.Contains(envelope.Provenance, entry => entry.Classification == ProvenanceClassification.Derived);
    }

    [Fact]
    public async Task GetScene_BlankSelector_ReturnsInvalidWithResolvedBuildContext()
    {
        await using var atlas = await McpTestAtlas.SeedTwoSceneBuildsAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetSceneAsync(
            selector: " ",
            buildId: atlas.BuildIdA,
            sceneSnapshotId: null,
            kind: null,
            includeChildren: false,
            includeComponents: false,
            includeReferences: false,
            limit: 50,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidArguments", envelope.Error!.Code);
        Assert.Equal(atlas.BuildIdA, envelope.Build!.ResolvedBuildId);
    }

    [Fact]
    public async Task GetScene_PrefabKind_ReturnsPrefab()
    {
        await using var atlas = await McpTestAtlas.SeedTwoSceneBuildsAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetSceneAsync(
            selector: atlas.PrefabSelector,
            buildId: atlas.BuildIdA,
            sceneSnapshotId: null,
            kind: SceneDocumentKind.Prefab,
            includeChildren: false,
            includeComponents: false,
            includeReferences: false,
            limit: 50,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(atlas.PrefabSelector, envelope.Data!.Scene!.Name);
        Assert.Equal(SceneDocumentKind.Prefab, envelope.Data.Scene.Kind);
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

    [Fact]
    public async Task ListGameObjects_FindsANameFragmentInOneSceneAndPagesByCursor()
    {
        await using var atlas = await McpTestAtlas.SeedTwoSceneBuildsAsync();
        var tools = CreateTools(atlas);

        var first = await tools.ListGameObjectsAsync(query: "DOWNTOWN", scene: atlas.SceneNameA, buildId: atlas.BuildIdA, limit: 1, ct: CancellationToken.None);
        var second = await tools.ListGameObjectsAsync(query: "DOWNTOWN", scene: atlas.SceneNameA, buildId: atlas.BuildIdA, limit: 1, ct: CancellationToken.None, cursor: first.Data!.NextCursor);

        Assert.Equal(ToolStatus.Resolved, first.Status);
        Assert.Equal(2, first.Data.Page.TotalCount);
        Assert.Equal("Downtown Root", Assert.Single(first.Data.Page.Rows).Name);
        Assert.Equal(atlas.SceneNameA, first.Data.Scene!.Name);
        Assert.Equal("downtown kiosk", Assert.Single(second.Data!.Page.Rows).Name);
        Assert.Null(second.Data.NextCursor);
    }

    [Fact]
    public async Task GetScene_PagesChildrenByCursor()
    {
        await using var atlas = await McpTestAtlas.SeedTwoSceneBuildsAsync();
        var tools = CreateTools(atlas);

        var first = await tools.GetSceneAsync(selector: atlas.SceneNameA, buildId: atlas.BuildIdA, includeChildren: true, limit: 1, ct: CancellationToken.None);
        var second = await tools.GetSceneAsync(selector: atlas.SceneNameA, buildId: atlas.BuildIdA, includeChildren: true, limit: 1, ct: CancellationToken.None, cursor: first.Data!.NextCursor);

        Assert.Equal(2, first.Data.Children.TotalCount);
        Assert.NotNull(first.Data.NextCursor);
        Assert.NotEqual(Assert.Single(first.Data.Children.Rows).GameObjectId, Assert.Single(second.Data!.Children.Rows).GameObjectId);
        Assert.Null(second.Data.NextCursor);
    }

    [Fact]
    public async Task GetGameObject_IncludeIncoming_ReportsInsideAndOutsideReferencesWithCoverage()
    {
        await using var atlas = await McpTestAtlas.SeedTwoSceneBuildsAsync();
        var tools = CreateTools(atlas);
        var root = "game-object-" + atlas.BuildIdA;

        var all = await tools.GetGameObjectAsync(selector: root, buildId: atlas.BuildIdA, ct: CancellationToken.None, includeIncoming: true);
        var external = await tools.GetGameObjectAsync(selector: root, buildId: atlas.BuildIdA, ct: CancellationToken.None, includeIncoming: true, externalOnly: true);

        var incoming = all.Data!.Incoming!;
        Assert.Equal(2, incoming.TotalCount);
        Assert.Equal(1, incoming.ExternalCount);
        Assert.Equal(3, incoming.Coverage.DecodedCount);
        Assert.Equal(1, incoming.Coverage.NotParsedCount);
        Assert.Collection(incoming.Rows,
            outside =>
            {
                Assert.False(outside.WithinTarget);
                Assert.Equal(("SCD_Bikers", "Leader.Data", SceneScriptFieldTargetKind.Component), (outside.SourceName, outside.FieldPath, outside.TargetKind));
            },
            inside =>
            {
                Assert.True(inside.WithinTarget);
                Assert.Equal(("downtown kiosk", "Owner", root), (inside.SourceName, inside.FieldPath, inside.TargetId));
            });
        Assert.Equal("SCD_Bikers", Assert.Single(external.Data!.Incoming!.Rows).SourceName);
        Assert.Equal(1, external.Data.Incoming.TotalCount);
    }

    // A requested path keeps the array elements under it but not a same-prefix sibling.
    [Fact]
    public async Task ListComponents_ReturnsOnlyRequestedFieldPathsWithFactProvenance()
    {
        await using var atlas = await McpTestAtlas.SeedTwoSceneBuildsAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.ListComponentsAsync(type: "Demo.KioskController", fields: ["Stock.Array"], buildId: atlas.BuildIdA, ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(ComponentTypeMatch.ScriptText, envelope.Data!.MatchedBy);
        var row = Assert.Single(envelope.Data.Page.Rows);
        Assert.Equal("downtown kiosk", row.GameObject!.Name);
        Assert.Equal(["Stock.Array.size", "Stock.Array[0]"], row.ScriptFields!.Fields.Select(field => field.Path));
        Assert.Contains(envelope.Provenance, entry => entry.Classification == ProvenanceClassification.Fact && entry.Source == "serialized-script-fields");
    }

    [Fact]
    public async Task ListComponents_IncludeDerived_MatchesSubclassesByResolvedSymbol()
    {
        await using var atlas = await McpTestAtlas.SeedTwoSceneBuildsAsync();
        var tools = CreateTools(atlas);

        var exact = await tools.ListComponentsAsync(type: atlas.HierarchyBaseTypeSelector, buildId: atlas.BuildIdA, ct: CancellationToken.None);
        var derived = await tools.ListComponentsAsync(type: atlas.HierarchyBaseTypeSelector, includeDerived: true, buildId: atlas.BuildIdA, ct: CancellationToken.None);

        Assert.Equal(0, exact.Data!.Page.TotalCount);
        Assert.Equal(ComponentTypeMatch.ResolvedCodeSymbol, derived.Data!.MatchedBy);
        Assert.Equal(["Demo.Widget", "Demo.WidgetBase"], derived.Data.IncludedTypes);
        Assert.Equal("component-" + atlas.BuildIdA, Assert.Single(derived.Data.Page.Rows).Component.ComponentId);
    }

    private static SceneTools CreateTools(McpTestAtlas atlas) =>
        new(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));
}
