using S1Atlas.Application.Authority;
using S1Atlas.Application.Readiness;
using Xunit;

namespace S1Atlas.Indexing.Tests;

public sealed class InstalledBuildAuthorityHintTests
{
    [Fact]
    public async Task Resolve_NoCurrentSnapshot_NamesScan()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync(requestedBuildId: null, CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.NoCurrentBuild, result.Status);
        Assert.Equal(ReadinessFixCommands.Scan, result.Hint);
    }

    [Fact]
    public async Task Resolve_UnknownBuild_NamesBuilds()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync("unknown-build", CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.BuildNotFound, result.Status);
        Assert.Equal(ReadinessFixCommands.Builds, result.Hint);
    }

    [Fact]
    public async Task Resolve_AmbiguousBuildPrefix_HasNoHint()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedCurrentBuildAsync("abcdef12" + new string('0', 56));
        await harness.SeedCurrentBuildAsync("abcdef12" + new string('1', 56));
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync("abcdef12", CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.AmbiguousBuildPrefix, result.Status);
        Assert.Null(result.Hint);
    }

    [Fact]
    public async Task Resolve_NoPreference_NamesExtract()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedCurrentBuildAsync();
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync(requestedBuildId: null, CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.NoPreferredVerifiedExtraction, result.Status);
        Assert.Equal(ReadinessFixCommands.Extract, result.Hint);
    }

    [Fact]
    public async Task Resolve_CorruptedPreferredExtraction_HasNoHint()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedCorruptedPreferenceAsync();
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync(requestedBuildId: null, CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.ExtractionIntegrityFailure, result.Status);
        Assert.Null(result.Hint);
    }

    [Fact]
    public async Task Resolve_PreferredButNoIndex_NamesIndex()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedPreferredVerifiedExtractionAsync();
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync(requestedBuildId: null, CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.NoCompletedIndex, result.Status);
        Assert.Equal(ReadinessFixCommands.Index, result.Hint);
    }

    [Fact]
    public async Task Resolve_IndexBoundToAnotherBuild_HasNoHint()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        var extractionId = await harness.SeedPreferredVerifiedExtractionAsync("build-a");
        await harness.SeedCompletedInstalledIndexAssociatedWithDifferentBuildAsync(extractionId);
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync(requestedBuildId: null, CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.IndexBuildMismatch, result.Status);
        Assert.Null(result.Hint);
    }

    [Fact]
    public async Task Resolve_HealthyBuild_HasNoHint()
    {
        await using var harness = await AuthorityHarness.EmptyAsync();
        await harness.SeedHealthyInstalledBuildAsync();
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync(requestedBuildId: null, CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.Resolved, result.Status);
        Assert.Null(result.Hint);
    }
}
