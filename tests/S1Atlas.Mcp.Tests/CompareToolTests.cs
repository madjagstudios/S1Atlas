using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Mcp.Tools;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class CompareToolTests
{
    [Fact]
    public async Task CompareSymbol_MissingBuildId_ReturnsInvalid()
    {
        await using var atlas = await McpTestAtlas.SeedTwoInstalledBuildsAsync();
        var tools = CreateTools(atlas);

        ToolEnvelope<SymbolDiff> envelope = await tools.CompareSymbolAsync(
            selector: atlas.CompareSelector,
            buildIdA: atlas.BuildIdA,
            buildIdB: "",
            CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("InvalidArguments", envelope.Error?.Code);
    }

    [Fact]
    public async Task CompareSymbol_UnchangedSymbol_ReturnsUnchanged()
    {
        await using var atlas = await McpTestAtlas.SeedTwoInstalledBuildsAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.CompareSymbolAsync(
            selector: atlas.CompareSelector,
            buildIdA: atlas.BuildIdA,
            buildIdB: atlas.BuildIdB,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.NotNull(envelope.Build);
        Assert.Equal(atlas.BuildIdA, envelope.Build!.ResolvedBuildId);
        Assert.Equal(atlas.BuildIdB, envelope.BuildB!.ResolvedBuildId);
        Assert.Equal("ScheduleI", envelope.BuildB.Codebase);
        Assert.Equal("Installed", envelope.BuildB.Channel);
        Assert.True(envelope.BuildB.IntegrityVerified);
        Assert.Equal(DiffClassification.Unchanged, envelope.Data!.Classification);
        Assert.Contains(envelope.Provenance, entry => entry.BuildId == atlas.BuildIdA);
        Assert.Contains(envelope.Provenance, entry => entry.BuildId == atlas.BuildIdB);
        Assert.Contains(envelope.Provenance, entry => entry.BuildId == atlas.BuildIdA && entry.Classification == ProvenanceClassification.Fact);
        Assert.Contains(envelope.Provenance, entry => entry.BuildId == atlas.BuildIdB && entry.Classification == ProvenanceClassification.Fact);
        Assert.Contains(envelope.Provenance, entry => entry.BuildId == atlas.BuildIdA && entry.Classification == ProvenanceClassification.Derived);
        Assert.Contains(envelope.Provenance, entry => entry.BuildId == atlas.BuildIdB && entry.Classification == ProvenanceClassification.Derived);
    }

    [Fact]
    public async Task CompareSymbol_BodyChanged_ReturnsMethodBodyChanged()
    {
        await using var atlas = await McpTestAtlas.SeedTwoInstalledBuildsAsync(
            compareBodyFingerprintA: "compare-body-old",
            compareBodyFingerprintB: "compare-body-new");
        var tools = CreateTools(atlas);

        var envelope = await tools.CompareSymbolAsync(
            selector: atlas.CompareSelector,
            buildIdA: atlas.BuildIdA,
            buildIdB: atlas.BuildIdB,
            CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(DiffClassification.MethodBodyChanged, envelope.Data!.Classification);
        Assert.Equal(atlas.CompareSelector, envelope.Data.CanonicalKey);
        Assert.Equal(atlas.BuildIdA, envelope.Build!.ResolvedBuildId);
        Assert.Equal(atlas.BuildIdB, envelope.BuildB!.ResolvedBuildId);
        Assert.Contains(envelope.Provenance, entry => entry.BuildId == atlas.BuildIdA);
        Assert.Contains(envelope.Provenance, entry => entry.BuildId == atlas.BuildIdB);
    }

    [Fact]
    public async Task CompareSymbol_NoMatch_ReturnsNotFoundWithBothBuildContexts()
    {
        await using var atlas = await McpTestAtlas.SeedTwoInstalledBuildsAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.CompareSymbolAsync(
            selector: "Missing.Symbol()",
            buildIdA: atlas.BuildIdA,
            buildIdB: atlas.BuildIdB,
            CancellationToken.None);

        Assert.Equal(ToolStatus.NotFound, envelope.Status);
        Assert.Equal("SymbolNotFound", envelope.Error?.Code);
        Assert.Equal(atlas.BuildIdA, envelope.BuildA!.ResolvedBuildId);
        Assert.Equal(atlas.BuildIdB, envelope.BuildB!.ResolvedBuildId);
        Assert.Equal("ScheduleI", envelope.BuildA.Codebase);
        Assert.Equal("Installed", envelope.BuildA.Channel);
        Assert.True(envelope.BuildA.IntegrityVerified);
    }

    [Fact]
    public async Task CompareSymbol_RightBuildFailure_PreservesLeftBuildContext()
    {
        await using var atlas = await McpTestAtlas.SeedTwoInstalledBuildsAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.CompareSymbolAsync(
            selector: atlas.CompareSelector,
            buildIdA: atlas.BuildIdA,
            buildIdB: "missing-build",
            CancellationToken.None);

        Assert.Equal(ToolStatus.Invalid, envelope.Status);
        Assert.Equal("BuildNotFound", envelope.Error?.Code);
        Assert.Equal(atlas.BuildIdA, envelope.BuildA!.ResolvedBuildId);
        Assert.Equal("missing-build", envelope.BuildB!.RequestedBuildId);
        Assert.Equal("ScheduleI", envelope.BuildB.Codebase);
        Assert.Equal("Installed", envelope.BuildB.Channel);
        Assert.False(envelope.BuildB.IntegrityVerified);
    }

    private static CompareTools CreateTools(McpTestAtlas atlas)
    {
        var services = McpServerComposition.BuildReadOnlyServices(atlas.DataRoot);
        return new CompareTools(services);
    }
}
