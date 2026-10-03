using System.Text.Json;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Mcp.Tools;
using Xunit;

namespace S1Atlas.Mcp.Tests;

// Paging contract: paged tools return an opaque nextCursor exactly when more
// rows remain, follow it with identical arguments, and reject any other
// reuse with InvalidCursor.
public sealed class McpPagingTests : IClassFixture<SharedHealthyServerFixture>
{
    private readonly SharedHealthyServerFixture _fixture;

    public McpPagingTests(SharedHealthyServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FindCallers_RoundTrip_PagesMergedStreamsInStableOrder()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var first = await tools.FindCallersAsync(
            atlas.HierarchyDerivedMethodSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            buildId: null, limit: 1, ct: CancellationToken.None, cursor: null);

        Assert.Equal(ToolStatus.Resolved, first.Status);
        Assert.Equal(2, first.Data!.TotalCount);
        Assert.Equal("dispatch-direct-call", Assert.Single(first.Data.Relationships).RelationshipId);
        Assert.NotNull(first.Data.NextCursor);

        var second = await tools.FindCallersAsync(
            atlas.HierarchyDerivedMethodSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            buildId: null, limit: 1, ct: CancellationToken.None, cursor: first.Data.NextCursor);

        Assert.Equal(ToolStatus.Resolved, second.Status);
        Assert.Equal(2, second.Data!.TotalCount);
        Assert.Equal("dispatch-virtual-call", Assert.Single(second.Data.Relationships).RelationshipId);
        Assert.Null(second.Data.NextCursor);
    }

    [Fact]
    public async Task FindCallSites_RoundTrip_ExhaustsAllRows()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var first = await tools.FindCallSitesAsync(
            atlas.EngineCallSiteSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            buildId: null, limit: 2, ct: CancellationToken.None, cursor: null);

        Assert.Equal(ToolStatus.Resolved, first.Status);
        Assert.Equal(3, first.Data!.TotalCount);
        Assert.Equal(2, first.Data.ReturnedCount);
        Assert.NotNull(first.Data.NextCursor);

        var second = await tools.FindCallSitesAsync(
            atlas.EngineCallSiteSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            buildId: null, limit: 2, ct: CancellationToken.None, cursor: first.Data.NextCursor);

        Assert.Equal(ToolStatus.Resolved, second.Status);
        Assert.Equal(3, second.Data!.TotalCount);
        Assert.Equal(1, second.Data.ReturnedCount);
        Assert.Null(second.Data.NextCursor);

        var seen = first.Data.Relationships.Concat(second.Data.Relationships)
            .Select(edge => edge.RelationshipId).ToArray();
        Assert.Equal(3, seen.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("callsite-001", seen);
    }

    [Fact]
    public async Task FindFieldReferences_RoundTrip_CoversReadersAndWriters()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var first = await tools.FindFieldReferencesAsync(
            atlas.GameFieldSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            buildId: null, readers: false, writers: false, limit: 1,
            ct: CancellationToken.None, cursor: null);

        Assert.Equal(ToolStatus.Resolved, first.Status);
        Assert.Equal(2, first.Data!.TotalCount);
        Assert.Equal(1, first.Data.ReturnedCount);
        Assert.NotNull(first.Data.NextCursor);

        var second = await tools.FindFieldReferencesAsync(
            atlas.GameFieldSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            buildId: null, readers: false, writers: false, limit: 1,
            ct: CancellationToken.None, cursor: first.Data.NextCursor);

        Assert.Equal(ToolStatus.Resolved, second.Status);
        Assert.Equal(1, second.Data!.ReturnedCount);
        Assert.Null(second.Data.NextCursor);

        var kinds = new[]
        {
            first.Data.Relationships[0].Kind,
            second.Data.Relationships[0].Kind
        }.OrderBy(kind => kind, StringComparer.Ordinal).ToArray();
        Assert.Equal(["ReadsField", "WritesField"], kinds);
    }

    [Fact]
    public async Task FindDerivedTypes_ExactFit_ReturnsNoCursor()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var envelope = await tools.FindDerivedTypesAsync(
            atlas.HierarchyBaseTypeSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            buildId: null, limit: 1, depth: 10, ct: CancellationToken.None, cursor: null);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(1, envelope.Data!.TotalCount);
        Assert.Equal(1, envelope.Data.ReturnedCount);
        Assert.Null(envelope.Data.NextCursor);
    }

    [Fact]
    public async Task ListBuilds_RoundTrip_PagesBothBuilds()
    {
        await using var atlas = await McpTestAtlas.SeedTwoInstalledBuildsAsync();
        var tools = new BuildEnvironmentTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var first = await tools.ListBuildsAsync(1, CancellationToken.None, cursor: null);

        Assert.Equal(ToolStatus.Resolved, first.Status);
        var firstBuild = Assert.Single(first.Data!.Builds);
        Assert.NotNull(first.Data.NextCursor);

        var second = await tools.ListBuildsAsync(1, CancellationToken.None, cursor: first.Data.NextCursor);

        Assert.Equal(ToolStatus.Resolved, second.Status);
        var secondBuild = Assert.Single(second.Data!.Builds);
        Assert.Null(second.Data.NextCursor);
        Assert.NotEqual(firstBuild.BuildId, secondBuild.BuildId);
        Assert.Contains(second.Data.Builds.Concat(first.Data.Builds).Select(build => build.BuildId), id => id == atlas.BuildIdB);
    }

    [Fact]
    public async Task ListScenes_RoundTrip_PagesSceneAndPrefab()
    {
        await using var atlas = await McpTestAtlas.SeedTwoSceneBuildsAsync();
        var tools = new SceneTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var first = await tools.ListScenesAsync(atlas.BuildIdA, null, null, null, 1, CancellationToken.None, cursor: null);

        Assert.Equal(ToolStatus.Resolved, first.Status);
        Assert.Equal(2, first.Data!.Page.TotalCount);
        Assert.Equal(1, first.Data.Page.ReturnedCount);
        Assert.NotNull(first.Data.NextCursor);

        var second = await tools.ListScenesAsync(atlas.BuildIdA, null, null, null, 1, CancellationToken.None, cursor: first.Data.NextCursor);

        Assert.Equal(ToolStatus.Resolved, second.Status);
        Assert.Equal(1, second.Data!.Page.ReturnedCount);
        Assert.Null(second.Data.NextCursor);

        var names = new[] { first.Data.Page.Rows[0].Name, second.Data.Page.Rows[0].Name }
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(["Dealer Prefab", "Downtown"], names);
    }

    [Fact]
    public async Task ListReferenceCollections_RoundTrip_PagesBothCollections()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        await atlas.SeedReferenceCollectionAsync("qol-a");
        await atlas.SeedReferenceCollectionAsync("qol-b");
        var tools = new ReferenceCollectionTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var first = await tools.ListReferenceCollectionsAsync(CancellationToken.None, limit: 1, cursor: null);

        Assert.Equal(ToolStatus.Resolved, first.Status);
        Assert.Equal(2, first.Data!.TotalCount);
        Assert.Single(first.Data.Collections);
        Assert.NotNull(first.Data.NextCursor);

        var second = await tools.ListReferenceCollectionsAsync(CancellationToken.None, limit: 1, cursor: first.Data.NextCursor);

        Assert.Equal(ToolStatus.Resolved, second.Status);
        Assert.Single(second.Data!.Collections);
        Assert.Null(second.Data.NextCursor);
        Assert.NotEqual(first.Data.Collections[0].Collection, second.Data.Collections[0].Collection);
    }

    [Fact]
    public async Task MalformedCursor_ReturnsInvalidCursor()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var envelope = await tools.FindCallersAsync(
            atlas.HierarchyDerivedMethodSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            buildId: null, limit: 1, ct: CancellationToken.None, cursor: "!!!");

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidCursor", envelope.Error?.Code);
    }

    [Fact]
    public async Task CursorFromAnotherTool_ReturnsInvalidCursor()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var first = await tools.FindCallersAsync(
            atlas.HierarchyDerivedMethodSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            buildId: null, limit: 1, ct: CancellationToken.None, cursor: null);

        var envelope = await tools.FindCalleesAsync(
            atlas.HierarchyDerivedMethodSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            buildId: null, limit: 1, ct: CancellationToken.None, cursor: first.Data!.NextCursor);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidCursor", envelope.Error?.Code);
    }

    [Fact]
    public async Task CursorWithChangedLimit_ReturnsInvalidCursor()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var first = await tools.FindCallersAsync(
            atlas.HierarchyDerivedMethodSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            buildId: null, limit: 1, ct: CancellationToken.None, cursor: null);

        var envelope = await tools.FindCallersAsync(
            atlas.HierarchyDerivedMethodSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            buildId: null, limit: 2, ct: CancellationToken.None, cursor: first.Data!.NextCursor);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidCursor", envelope.Error?.Code);
    }

    [Fact]
    public async Task CursorWithChangedSelector_ReturnsInvalidCursor()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var first = await tools.FindCallersAsync(
            atlas.HierarchyDerivedMethodSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            buildId: null, limit: 1, ct: CancellationToken.None, cursor: null);

        var envelope = await tools.FindCallersAsync(
            atlas.MethodSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            buildId: null, limit: 1, ct: CancellationToken.None, cursor: first.Data!.NextCursor);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidCursor", envelope.Error?.Code);
    }

    [Fact]
    public async Task CursorFromAnotherBuild_ReturnsInvalidCursor()
    {
        await using var atlas = await McpTestAtlas.SeedTwoInstalledBuildsAsync();
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var first = await tools.FindCallersAsync(
            atlas.HierarchyDerivedMethodSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            buildId: null, limit: 1, ct: CancellationToken.None, cursor: null);

        Assert.Equal(ToolStatus.Resolved, first.Status);
        Assert.NotNull(first.Data!.NextCursor);

        var envelope = await tools.FindCallersAsync(
            atlas.HierarchyDerivedMethodSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            buildId: atlas.BuildIdA, limit: 1, ct: CancellationToken.None, cursor: first.Data.NextCursor);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidCursor", envelope.Error?.Code);
    }

    [Fact]
    public async Task RemainingTools_WalkAllPages_CoversEveryRowExactlyOnce()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var services = McpServerComposition.BuildReadOnlyServices(atlas.DataRoot);
        var tools = new CodeSymbolTools(services);
        var cancellationToken = CancellationToken.None;

        await WalkAsync(
            cursor => tools.FindCalleesAsync(atlas.MethodSelector, McpCodebase.scheduleI, CodeChannel.Installed, null, 1, cancellationToken, cursor),
            data => (data.Relationships.Select(edge => edge.RelationshipId).ToArray(), data.TotalCount, data.NextCursor));
        await WalkAsync(
            cursor => tools.FindReferencesAsync(atlas.MethodSelector, McpCodebase.scheduleI, CodeChannel.Installed, null, 1, cancellationToken, cursor),
            data => (data.Relationships.Select(edge => edge.RelationshipId).ToArray(), data.TotalCount, data.NextCursor));
        await WalkAsync(
            cursor => tools.FindRelatedTypesAsync(atlas.MethodSelector, McpCodebase.scheduleI, CodeChannel.Installed, null, null, 1, cancellationToken, cursor),
            data => (data.Relationships.Select(edge => edge.RelationshipId).ToArray(), data.TotalCount, data.NextCursor));
        await WalkAsync(
            cursor => tools.FindOverridesAsync(atlas.HierarchyDerivedMethodSelector, McpCodebase.scheduleI, CodeChannel.Installed, null, 1, cancellationToken, cursor),
            data => (data.Nodes.Select(node => node.Edge.RelationshipId).ToArray(), data.TotalCount, data.NextCursor));
        await WalkAsync(
            cursor => tools.FindOverridersAsync(atlas.HierarchyBaseMethodSelector, McpCodebase.scheduleI, CodeChannel.Installed, null, 1, 10, cancellationToken, cursor),
            data => (data.Nodes.Select(node => node.Edge.RelationshipId).ToArray(), data.TotalCount, data.NextCursor));
        await WalkAsync(
            cursor => tools.FindDerivedTypesAsync(atlas.HierarchyBaseTypeSelector, McpCodebase.scheduleI, CodeChannel.Installed, null, 1, 10, cancellationToken, cursor),
            data => (data.Nodes.Select(node => node.Edge.RelationshipId).ToArray(), data.TotalCount, data.NextCursor));
        await WalkAsync(
            cursor => tools.ListApiIndexesAsync(null, 1, cancellationToken, cursor),
            data => (data.Selections.Select(selection => $"{selection.Codebase}:{selection.Channel}").ToArray(), null, data.NextCursor));
    }

    [Fact]
    public async Task FindRelatedTypes_LargeWindow_ReportsExactTotalsAcrossPages()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync(includeRelatedTypesWindow: true);
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));
        var cancellationToken = CancellationToken.None;

        var first = await tools.FindRelatedTypesAsync(
            atlas.TypeSelector, McpCodebase.scheduleI, CodeChannel.Installed, null, null, 500, cancellationToken, null);
        Assert.Equal(ToolStatus.Resolved, first.Status);
        Assert.Equal(502, first.Data!.TotalCount);
        Assert.Equal(500, first.Data.Relationships.Count);
        Assert.NotNull(first.Data.NextCursor);

        await WalkAsync(
            cursor => tools.FindRelatedTypesAsync(atlas.TypeSelector, McpCodebase.scheduleI, CodeChannel.Installed, null, null, 500, cancellationToken, cursor),
            data => (data.Relationships.Select(edge => edge.RelationshipId).ToArray(), data.TotalCount, data.NextCursor));
    }

    [Fact]
    public async Task SingleRowHierarchies_ForgedSecondPage_ReturnsEmptyResolved()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));
        var cancellationToken = CancellationToken.None;

        var overrides = await tools.FindOverridesAsync(
            atlas.HierarchyDerivedMethodSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            null, 1, cancellationToken, ForgeHierarchyCursor("find_overrides", atlas.HierarchyDerivedMethodSelector, atlas, null));
        Assert.Equal(ToolStatus.Resolved, overrides.Status);
        Assert.Equal(1, overrides.Data!.TotalCount);
        Assert.Empty(overrides.Data.Nodes);
        Assert.Null(overrides.Data.NextCursor);

        var overriders = await tools.FindOverridersAsync(
            atlas.HierarchyBaseMethodSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            null, 1, 10, cancellationToken, ForgeHierarchyCursor("find_overriders", atlas.HierarchyBaseMethodSelector, atlas, "10"));
        Assert.Equal(ToolStatus.Resolved, overriders.Status);
        Assert.Equal(1, overriders.Data!.TotalCount);
        Assert.Empty(overriders.Data.Nodes);
        Assert.Null(overriders.Data.NextCursor);

        var derived = await tools.FindDerivedTypesAsync(
            atlas.HierarchyBaseTypeSelector, McpCodebase.scheduleI, CodeChannel.Installed,
            null, 1, 10, cancellationToken, ForgeHierarchyCursor("find_derived_types", atlas.HierarchyBaseTypeSelector, atlas, "10"));
        Assert.Equal(ToolStatus.Resolved, derived.Status);
        Assert.Equal(1, derived.Data!.TotalCount);
        Assert.Empty(derived.Data.Nodes);
        Assert.Null(derived.Data.NextCursor);
    }

    // Builds the cursor the tool mints for (selector, scheduleI, Installed,
    // no build, limit 1, game scope) at offset 1, spelling the tool name and
    // argument order out independently so a wrong binding fails here instead
    // of silently breaking multi-row hierarchies.
    private static string ForgeHierarchyCursor(string tool, string selector, McpTestAtlas atlas, string? depth)
    {
        var args = new List<string>
        {
            selector,
            McpCodebase.scheduleI.ToString(),
            CodeChannel.Installed.ToString(),
            string.Empty,
            "1"
        };
        if (depth is not null)
            args.Add(depth);
        args.Add(IndexQueryScope.Game.ToString());
        args.Add(string.Empty);
        return McpPageCursor.Encode(
            McpPageCursor.HashFor(tool, args, atlas.BuildIdValue, atlas.IndexId),
            1);
    }

    private static async Task WalkAsync<T>(
        Func<string?, Task<ToolEnvelope<T>>> call,
        Func<T, (IReadOnlyList<string> Ids, int? Total, string? Next)> shape)
        where T : class
    {
        var seen = new List<string>();
        int? total = null;
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var envelope = await call(cursor);
            Assert.Equal(ToolStatus.Resolved, envelope.Status);
            var (ids, pageTotal, next) = shape(envelope.Data!);
            total ??= pageTotal;
            Assert.Equal(total, pageTotal);
            seen.AddRange(ids);
            if (next is null)
                break;
            cursor = next;
            Assert.True(page < 9, "Paging did not terminate within ten pages.");
        }

        Assert.NotEmpty(seen);
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
        if (total is not null)
            Assert.Equal(total, seen.Count);
    }

    [Fact]
    public async Task SearchSymbols_HasNoCursorParameter()
    {
        var schemas = await McpTestHost.GetToolSchemasAsync(_fixture.Client);

        using var schema = JsonDocument.Parse(schemas["search_symbols"]);

        Assert.False(schema.RootElement.GetProperty("properties").TryGetProperty("cursor", out _));
    }

    [Fact]
    public async Task StdioWire_NextCursorRoundTrips()
    {
        var atlas = _fixture.Atlas;

        var first = await McpTestHost.CallToolRawAsync(
            _fixture.Client,
            "find_callers",
            new Dictionary<string, object?>
            {
                ["selector"] = atlas.HierarchyDerivedMethodSelector,
                ["codebase"] = "scheduleI",
                ["limit"] = 1
            });
        var firstText = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(first.Content)).Text;
        using var firstDocument = JsonDocument.Parse(firstText);
        var cursor = firstDocument.RootElement.GetProperty("data").GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        var second = await McpTestHost.CallToolRawAsync(
            _fixture.Client,
            "find_callers",
            new Dictionary<string, object?>
            {
                ["selector"] = atlas.HierarchyDerivedMethodSelector,
                ["codebase"] = "scheduleI",
                ["limit"] = 1,
                ["cursor"] = cursor
            });
        var secondText = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(second.Content)).Text;
        using var secondDocument = JsonDocument.Parse(secondText);
        Assert.Equal("resolved", secondDocument.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "dispatch-virtual-call",
            secondDocument.RootElement.GetProperty("data").GetProperty("relationships")[0].GetProperty("relationshipId").GetString());
        Assert.False(secondDocument.RootElement.GetProperty("data").TryGetProperty("nextCursor", out _));
    }
}
