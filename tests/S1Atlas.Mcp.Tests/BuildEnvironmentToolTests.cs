using S1Atlas.Application.Envelope;
using S1Atlas.Mcp.Tools;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class BuildEnvironmentToolTests
{
    [Fact]
    public async Task GetEnvironment_ExplicitNonCurrentBuild_ReturnsNoMatchingSnapshot()
    {
        await using var atlas = await McpTestAtlas.SeedTwoInstalledBuildsAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetEnvironmentAsync(
            buildId: atlas.BuildIdA,
            ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Unavailable, envelope.Status);
        Assert.Equal("NoMatchingEnvironmentSnapshot", envelope.Error?.Code);
    }

    [Fact]
    public async Task ListBuilds_MarksCurrentAndAvailability()
    {
        await using var atlas = await McpTestAtlas.SeedTwoInstalledBuildsAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.ListBuildsAsync(ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        var current = Assert.Single(envelope.Data!.Builds, build => build.IsCurrent);
        Assert.Equal(atlas.BuildIdB, current.BuildId);
        Assert.True(current.HasPreferredVerifiedExtraction);
        Assert.True(current.HasCompletedIndex);
        Assert.All(envelope.Data.Builds, build =>
        {
            Assert.True(build.HasPreferredVerifiedExtraction);
            Assert.True(build.HasCompletedIndex);
        });
    }

    [Fact]
    public async Task GetEnvironment_NoBuild_ReturnsCurrent()
    {
        await using var atlas = await McpTestAtlas.SeedTwoInstalledBuildsAsync();
        var tools = CreateTools(atlas);

        var envelope = await tools.GetEnvironmentAsync(ct: CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(atlas.BuildIdB, envelope.Data!.BuildId);
        Assert.Equal(atlas.BuildIdB, envelope.Build!.ResolvedBuildId);
        Assert.Equal("ScheduleI", envelope.Build.Codebase);
        Assert.Equal("Installed", envelope.Build.Channel);
        Assert.True(envelope.Build.IntegrityVerified);
        Assert.Contains(envelope.Provenance, entry =>
            entry.Classification == ProvenanceClassification.Fact &&
            entry.BuildId == atlas.BuildIdB);
    }

    private static BuildEnvironmentTools CreateTools(McpTestAtlas atlas) =>
        new(McpServerComposition.BuildReadOnlyServices(atlas.DataRoot));
}
