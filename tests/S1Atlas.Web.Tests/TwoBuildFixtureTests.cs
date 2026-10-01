using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Web.Tests;

// The two-build atlas: build A (older) and build B (current), each with a
// completed game index, plus the shared API index. Environment snapshots
// carry distinctive fake absolute paths for the leak tests.
public sealed class TwoBuildFixtureTests
{
    [Fact]
    public async Task SeedsTwoBuildsWithBCurrent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var atlas = await SyntheticAtlas.SeedTwoBuildFixtureAsync(cancellationToken);
        var repository = new SqliteAtlasRepository(
            Path.Combine(atlas.DataRoot, "atlas.db"),
            Path.Combine(atlas.DataRoot, "backups"));

        var builds = await repository.ListBuildsAsync(cancellationToken);
        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);

        Assert.Equal(2, builds.Count);
        Assert.Equal(SyntheticAtlas.BuildIdBValue, builds[0].BuildId);
        Assert.Equal(SyntheticAtlas.BuildIdAValue, builds[1].BuildId);
        Assert.NotNull(current);
        Assert.Equal(SyntheticAtlas.BuildIdBValue, current.Build.BuildId);
    }

    [Fact]
    public async Task SeedsDistinctiveSnapshotPaths()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var atlas = await SyntheticAtlas.SeedTwoBuildFixtureAsync(cancellationToken);
        var repository = new SqliteAtlasRepository(
            Path.Combine(atlas.DataRoot, "atlas.db"),
            Path.Combine(atlas.DataRoot, "backups"));

        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);

        Assert.NotNull(current);
        Assert.Contains(SyntheticAtlas.LeakRootToken, current.Installation.InstallationRoot);
        Assert.Equal(4, current.Dependencies.Count);
        Assert.Contains(current.Dependencies, dependency => dependency.Path != null && dependency.Path.StartsWith(@"\\"));
        Assert.Contains(current.Dependencies, dependency => dependency.Path != null && dependency.Path.StartsWith("D:"));
    }

    [Fact]
    public async Task CompletesBothGameIndexes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var atlas = await SyntheticAtlas.SeedTwoBuildFixtureAsync(cancellationToken);
        var repository = new SqliteAtlasRepository(
            Path.Combine(atlas.DataRoot, "atlas.db"),
            Path.Combine(atlas.DataRoot, "backups"));

        var indexA = await repository.GetCompletedIndexAsync(SyntheticAtlas.GameIndexAValue, cancellationToken);
        var indexB = await repository.GetCompletedIndexAsync(SyntheticAtlas.GameIndexBValue, cancellationToken);

        Assert.NotNull(indexA);
        Assert.NotNull(indexB);
        Assert.Equal(9, await repository.CountCompletedSymbolsAsync(SyntheticAtlas.GameIndexAValue, cancellationToken));
        Assert.Equal(40, await repository.CountCompletedSymbolsAsync(SyntheticAtlas.GameIndexBValue, cancellationToken));
    }
}
