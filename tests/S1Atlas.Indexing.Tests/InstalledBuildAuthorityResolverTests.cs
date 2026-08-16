using S1Atlas.Application.Authority;
using Xunit;

namespace S1Atlas.Indexing.Tests;

public sealed class InstalledBuildAuthorityResolverTests
{
    [Fact]
    public async Task Resolve_NoCurrentSnapshot_ReturnsNoCurrentBuild()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync(requestedBuildId: null, CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.NoCurrentBuild, result.Status);
        Assert.Null(result.IndexId);
    }

    [Fact]
    public async Task Resolve_ExplicitUnknownBuild_ReturnsBuildNotFound()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync("unknown-build", CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.BuildNotFound, result.Status);
    }

    [Fact]
    public async Task Resolve_NoPreference_ReturnsNoPreferredVerifiedExtraction()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedCurrentBuildAsync();
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync(requestedBuildId: null, CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.NoPreferredVerifiedExtraction, result.Status);
    }

    [Fact]
    public async Task Resolve_CorruptedPreferredExtraction_ReturnsExtractionIntegrityFailure()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedCorruptedPreferenceAsync();
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync(requestedBuildId: null, CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.ExtractionIntegrityFailure, result.Status);
    }

    [Fact]
    public async Task Resolve_PreferredButNoIndex_ReturnsNoCompletedIndex()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedPreferredVerifiedExtractionAsync();
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync(requestedBuildId: null, CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.NoCompletedIndex, result.Status);
    }

    [Fact]
    public async Task Resolve_HealthyBuild_ReturnsResolvedWithIndexId()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var seeded = await harness.SeedHealthyInstalledBuildAsync();
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync(requestedBuildId: null, CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.Resolved, result.Status);
        Assert.Equal(seeded.BuildId, result.ResolvedBuildId);
        Assert.Equal(seeded.ExtractionId, result.ExtractionId);
        Assert.Equal(seeded.IndexId, result.IndexId);
    }
}
