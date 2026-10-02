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
    public async Task Resolve_UniqueBuildPrefix_ResolvesFullBuild()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var buildId = "abcdef12" + new string('0', 56);
        var seeded = await harness.SeedHealthyInstalledBuildAsync(buildId);
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync("abcdef12", CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.Resolved, result.Status);
        Assert.Equal("abcdef12", result.RequestedBuildId);
        Assert.Equal(seeded.BuildId, result.ResolvedBuildId);
        Assert.Equal(seeded.IndexId, result.IndexId);
    }

    [Fact]
    public async Task Resolve_AmbiguousBuildPrefix_ListsMatchesAndFails()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var first = "abcdef12" + new string('0', 56);
        var second = "abcdef12" + new string('1', 56);
        await harness.SeedCurrentBuildAsync(first);
        await harness.SeedCurrentBuildAsync(second);
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync("ABCDEF12", CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.AmbiguousBuildPrefix, result.Status);
        Assert.Equal("ABCDEF12", result.RequestedBuildId);
        Assert.Null(result.ResolvedBuildId);
        Assert.Contains("2 builds", result.Message, StringComparison.Ordinal);
        Assert.Contains(first, result.Message, StringComparison.Ordinal);
        Assert.Contains(second, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_UnknownBuildPrefix_ReturnsBuildNotFound()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedCurrentBuildAsync("abcdef12" + new string('0', 56));
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync("12345678", CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.BuildNotFound, result.Status);
    }

    [Fact]
    public async Task Resolve_FullBuildId_MatchesEitherCase()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var buildId = "abcdef12" + new string('0', 56);
        var seeded = await harness.SeedHealthyInstalledBuildAsync(buildId);
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync(buildId.ToUpperInvariant(), CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.Resolved, result.Status);
        Assert.Equal(seeded.BuildId, result.ResolvedBuildId);
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

    [Fact]
    public async Task Resolve_PreferredIndexAssociatedWithDifferentBuild_ReturnsIndexBuildMismatch()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var extractionId = await harness.SeedPreferredVerifiedExtractionAsync();
        await harness.SeedCompletedInstalledIndexAssociatedWithDifferentBuildAsync(extractionId);
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync(requestedBuildId: null, CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.IndexBuildMismatch, result.Status);
        Assert.Null(result.IndexId);
    }
}
