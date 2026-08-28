using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Mcp.Tools;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class ReferenceCollectionToolTests
{
    [Fact]
    public async Task ListsCompletedCollectionsWithBaseIndexAndLocalOnlyModProvenance()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var reference = await atlas.SeedReferenceCollectionAsync("qol");
        var tools = new ReferenceCollectionTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var result = await tools.ListReferenceCollectionsAsync(CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, result.Status);
        var collection = Assert.Single(result.Data!.Collections);
        Assert.Equal(reference.Collection, collection.Collection);
        Assert.Equal(reference.IndexId, collection.IndexId);
        Assert.Equal(atlas.IndexId, collection.BaseIndexId);
        var mod = Assert.Single(collection.Mods);
        Assert.Equal("qol", mod.ModId);
        Assert.Equal("LocalOnly", mod.Provenance);
        Assert.DoesNotContain(atlas.DataRoot, System.Text.Json.JsonSerializer.Serialize(result.Data), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectionCatalogUsesNewestCompletionAndMatchesCollectionNameQueries()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var runs = await atlas.SeedTwoCompletedReferenceRunsAsync("qol");
        var services = McpServerComposition.BuildReadOnlyServices(atlas.DataRoot);
        var catalog = await new ReferenceCollectionTools(services).ListReferenceCollectionsAsync(CancellationToken.None);
        var symbols = new CodeSymbolTools(services);

        var collection = Assert.Single(catalog.Data!.Collections);
        var search = await symbols.SearchSymbolsAsync(
            "Fresh",
            null,
            null,
            50,
            CancellationToken.None,
            "reference",
            "qol");

        Assert.Equal(runs.NewestIndexId, collection.IndexId);
        Assert.NotEqual(runs.StaleIndexId, collection.IndexId);
        Assert.Equal(collection.IndexId, Assert.Single(search.Data!.Results).IndexId);
    }

    [Fact]
    public async Task FederatedQueriesUseRecordedBaseAndRejectExplicitBuildMismatch()
    {
        await using var atlas = await McpTestAtlas.SeedTwoInstalledBuildsAsync();
        var reference = await atlas.SeedReferenceCollectionAsync("qol");
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var all = await tools.SearchSymbolsAsync(
            atlas.KnownSymbolFragment,
            null,
            null,
            50,
            CancellationToken.None,
            "all",
            reference.Collection);
        var mismatch = await tools.SearchSymbolsAsync(
            atlas.KnownSymbolFragment,
            atlas.BuildIdB,
            null,
            50,
            CancellationToken.None,
            "all",
            reference.Collection);

        Assert.Equal(ToolStatus.Resolved, all.Status);
        Assert.Equal(atlas.BuildIdA, all.Build!.ResolvedBuildId);
        Assert.Equal(atlas.IndexIdA, all.Build.IndexId);
        Assert.NotEmpty(all.Data!.Results);
        Assert.DoesNotContain(all.Data.Results, result => result.Origin == "game" && result.IndexId != atlas.IndexIdA);
        Assert.Equal(ToolStatus.Invalid, mismatch.Status);
        Assert.Equal("ReferenceCollectionBuildMismatch", mismatch.Error!.Code);
        Assert.Equal(atlas.BuildIdA, mismatch.Build!.ResolvedBuildId);
        Assert.Equal(atlas.IndexIdA, mismatch.Build.IndexId);
    }

    [Fact]
    public async Task ReferenceSearchAndRelationshipsPreserveCrossOriginProvenance()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var reference = await atlas.SeedReferenceCollectionAsync("qol");
        await atlas.AddReferenceSourceLocationAsync(reference);
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));

        var search = await tools.SearchSymbolsAsync("Qol.Mod::Run", null, null, 50, CancellationToken.None, "reference", reference.Collection);
        var callees = await tools.FindCalleesAsync("qol/Qol.Mod::Run():System.Void", null, 50, CancellationToken.None, "all", reference.Collection);
        var allCallers = await tools.FindCallersAsync(atlas.MethodSelector, null, 50, CancellationToken.None, "all", reference.Collection);

        Assert.Equal(ToolStatus.Resolved, search.Status);
        Assert.Equal("reference", search.Data!.Results[0].Origin);
        Assert.Equal(reference.IndexId, search.Data.Results[0].IndexId);
        Assert.Equal("qol", search.Data.Results[0].ReferenceModId);
        Assert.Contains(search.Provenance, entry => entry.Source == "reference-collection");
        Assert.Equal(ToolStatus.Resolved, callees.Status);
        Assert.Equal("game", Assert.Single(callees.Data!.Relationships).Target.Origin);
        Assert.Equal(ToolStatus.Resolved, allCallers.Status);
        Assert.Contains(allCallers.Data!.Relationships, edge => edge.Source.Origin == "reference");
        Assert.Contains(allCallers.Data.Relationships, edge => edge.Source.Origin == "game");
    }

    [Fact]
    public async Task ReferenceSourceIsBoundedAndIntegrityCheckedAndMissingCollectionIsExplicit()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var reference = await atlas.SeedReferenceCollectionAsync("qol");
        await atlas.AddReferenceSourceLocationAsync(reference);
        var tools = new CodeSymbolTools(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));
        var selected = await tools.SearchSymbolsAsync("Qol.Mod::Run", null, null, 50, CancellationToken.None, "reference", reference.Collection);
        var selector = Assert.Single(selected.Data!.Results).Signature;

        var source = await tools.GetSourceAsync(
            selector,
            null,
            0,
            CancellationToken.None,
            "reference",
            reference.Collection);
        Assert.Equal(ToolStatus.Resolved, source.Status);
        Assert.Equal(reference.IndexId, source.Data!.IndexId);
        Assert.Equal("reference", source.Data.Origin);
        Assert.True(source.Data.Text.Length <= 16 * 1024);

        var generatedSource = Directory.GetFiles(
            Path.Combine(atlas.DataRoot, "reference", reference.IndexId),
            "*.cs",
            SearchOption.AllDirectories).Single();
        await File.AppendAllTextAsync(generatedSource, "tampered", CancellationToken.None);
        var integrity = await tools.GetSourceAsync(
            selector,
            null,
            0,
            CancellationToken.None,
            "reference",
            reference.Collection);
        Assert.Equal(ToolStatus.Unavailable, integrity.Status);
        Assert.Equal("SourceIntegrityFailure", integrity.Error!.Code);

        var missing = await tools.SearchSymbolsAsync(
            "Qol.Mod",
            null,
            null,
            50,
            CancellationToken.None,
            "reference",
            "missing-collection");
        Assert.Equal(ToolStatus.NotFound, missing.Status);
        Assert.Equal("NoCompletedIndex", missing.Error!.Code);
    }
}
