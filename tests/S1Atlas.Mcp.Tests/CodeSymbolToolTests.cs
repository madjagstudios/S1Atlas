using S1Atlas.Application.Envelope;
using S1Atlas.Mcp.Tools;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class CodeSymbolToolTests
{
    [Fact]
    public async Task SearchSymbols_HealthyBuild_ResolvesAgainstPreferredIndex()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.SearchSymbolsAsync(
            atlas.KnownSymbolFragment,
            buildId: null,
            kind: null,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(atlas.IndexId, envelope.Build!.IndexId);
        Assert.NotNull(envelope.Data);
        Assert.All(
            envelope.Provenance,
            entry => Assert.NotEqual(ProvenanceClassification.Interpretation, entry.Classification));
    }

    [Fact]
    public async Task SearchSymbols_NoCurrentBuild_ReturnsUnavailable()
    {
        await using var atlas = await McpTestAtlas.EmptyAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.SearchSymbolsAsync(
            "Dealer",
            buildId: null,
            kind: null,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Unavailable, envelope.Status);
        Assert.Equal("NoCurrentBuild", envelope.Error?.Code);
    }

    [Fact]
    public async Task GetType_UnknownSelector_ReturnsNotFound()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetTypeAsync(
            "Demo.DoesNotExist",
            buildId: null,
            CancellationToken.None);

        Assert.Equal(ToolStatus.NotFound, envelope.Status);
        Assert.Null(envelope.Data);
    }

    [Fact]
    public async Task GetMethod_AmbiguousSelector_ReturnsAmbiguousWithCandidates()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetMethodAsync(
            "worker",
            buildId: null,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Ambiguous, envelope.Status);
        Assert.True(envelope.Candidates.Count >= 2);
    }

    [Fact]
    public async Task GetSource_ReturnsHashVerifiedSnippet()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetSourceAsync(
            atlas.MethodSelector,
            buildId: null,
            context: 0,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal("public void Run() { }", envelope.Data!.Text);
        Assert.Equal(atlas.SourceRelativePath, envelope.Data.RelativePath);
    }

    [Fact]
    public async Task GetSource_TamperedFile_ReturnsSourceIntegrityFailure()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);
        await File.WriteAllTextAsync(atlas.SourcePath, "tampered", CancellationToken.None);

        var envelope = await tools.GetSourceAsync(
            atlas.MethodSelector,
            buildId: null,
            context: 0,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Unavailable, envelope.Status);
        Assert.Equal("SourceIntegrityFailure", envelope.Error?.Code);
    }

    [Fact]
    public async Task FindCallers_PreservesCompletenessNotice()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindCallersAsync(
            atlas.MethodSelector,
            buildId: null,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.NotEmpty(envelope.Data!.CompletenessNotice);
        Assert.True(envelope.Data.CallerCompletenessBoundedByTargetResolution);
    }

    [Fact]
    public async Task FindReferences_ReturnsIncomingAndOutgoingEdges()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindReferencesAsync(
            atlas.MethodSelector,
            buildId: null,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Contains(envelope.Data!.Relationships, edge => edge.RelationshipId == "incoming-call");
        Assert.Contains(envelope.Data.Relationships, edge => edge.RelationshipId == "outgoing-call");
        Assert.Contains(envelope.Data.Relationships, edge => edge.RelationshipId == "reads-widget-field");
    }

    [Fact]
    public async Task FindRelatedTypes_FiltersToTypeRelations()
    {
        await using var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.FindRelatedTypesAsync(
            atlas.MethodSelector,
            buildId: null,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(
            ["parameter-type-payload", "return-type-result"],
            envelope.Data!.Relationships.Select(edge => edge.RelationshipId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.DoesNotContain(envelope.Data.Relationships, edge => edge.Kind == "Calls");
        Assert.DoesNotContain(envelope.Data.Relationships, edge => edge.Kind == "ReadsField");
    }

    private static CodeSymbolTools CreateTools(McpTestAtlas atlas)
    {
        var services = McpServerComposition.BuildReadOnlyServices(atlas.DataRoot);
        return new CodeSymbolTools(services);
    }
}
