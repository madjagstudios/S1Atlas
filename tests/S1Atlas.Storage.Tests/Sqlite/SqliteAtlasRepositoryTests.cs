using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests.Sqlite;

public sealed class SqliteAtlasRepositoryTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory;
    private readonly string _databasePath;

    public SqliteAtlasRepositoryTests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-storage-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _databasePath = Path.Combine(_temporaryDirectory, "atlas.db");
    }

    [Fact]
    public async Task SaveSnapshotAsync_ValidSnapshot_RoundTripsAndBecomesCurrent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new SqliteAtlasRepository(_databasePath);
        await repository.InitializeAsync(cancellationToken);
        var snapshot = CreateSnapshot("build-a", DateTimeOffset.Parse("2026-08-12T12:00:00Z"));

        await repository.SaveSnapshotAsync(snapshot, cancellationToken);

        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);
        Assert.NotNull(current);
        Assert.Equal(snapshot.Build, current.Build);
        Assert.Equal(snapshot.AtlasVersion, current.AtlasVersion);
        Assert.Equal(snapshot.CapturedAtUtc, current.CapturedAtUtc);
        Assert.Equal(snapshot.Dependencies, current.Dependencies);
    }

    [Fact]
    public async Task SaveSnapshotAsync_SameGameBuildWithChangedDependency_PromotesNewEnvironmentWithoutDuplicatingBuild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new SqliteAtlasRepository(_databasePath);
        await repository.InitializeAsync(cancellationToken);
        var baselineTime = DateTimeOffset.Parse("2026-08-12T12:00:00Z");
        var changedTime = DateTimeOffset.Parse("2026-08-12T13:00:00Z");
        await repository.SaveSnapshotAsync(
            CreateSnapshot(
                "build-a",
                baselineTime,
                [
                    new DependencyVersion(
                        DependencyKind.S1Api,
                        "1.0.0",
                        "C:\\Mods\\S1API.dll",
                        true)
                ]),
            cancellationToken);
        var changedEnvironment = CreateSnapshot(
            "build-a",
            changedTime,
            [
                new DependencyVersion(
                    DependencyKind.S1Api,
                    "2.0.0",
                    "C:\\Mods\\S1API.dll",
                    true)
            ]);

        await repository.SaveSnapshotAsync(changedEnvironment, cancellationToken);

        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);
        var builds = await repository.ListBuildsAsync(cancellationToken);
        Assert.NotNull(current);
        Assert.Equal(changedTime, current.CapturedAtUtc);
        Assert.Collection(
            current.Dependencies,
            dependency => Assert.Equal("2.0.0", dependency.Version));
        Assert.Single(builds);
        Assert.Equal("build-a", builds[0].BuildId);
    }

    [Fact]
    public async Task SaveSnapshotAsync_WhenDependencyInsertFails_RollsBackAndKeepsPreviousCurrentBuild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new SqliteAtlasRepository(_databasePath);
        await repository.InitializeAsync(cancellationToken);
        var baseline = CreateSnapshot("build-a", DateTimeOffset.Parse("2026-08-12T12:00:00Z"));
        await repository.SaveSnapshotAsync(baseline, cancellationToken);

        var duplicateDependencies = new[]
        {
            new DependencyVersion(DependencyKind.S1Api, "1.0.0", "C:\\Mods\\S1API.dll", true),
            new DependencyVersion(DependencyKind.S1Api, "2.0.0", "C:\\Mods\\S1API-duplicate.dll", true)
        };
        var candidate = CreateSnapshot(
            "build-b",
            DateTimeOffset.Parse("2026-08-12T13:00:00Z"),
            duplicateDependencies);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveSnapshotAsync(candidate, cancellationToken));

        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);
        var builds = await repository.ListBuildsAsync(cancellationToken);

        Assert.NotNull(current);
        Assert.Equal("build-a", current.Build.BuildId);
        Assert.Collection(builds, build => Assert.Equal("build-a", build.BuildId));
    }

    [Fact]
    public async Task SaveSnapshotAsync_WhenBuildIsNotValid_RejectsCandidateWithoutChangingCurrentBuild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new SqliteAtlasRepository(_databasePath);
        await repository.InitializeAsync(cancellationToken);
        var baseline = CreateSnapshot("build-a", DateTimeOffset.Parse("2026-08-12T12:00:00Z"));
        await repository.SaveSnapshotAsync(baseline, cancellationToken);
        var invalidCandidate = CreateSnapshot(
            "build-b",
            DateTimeOffset.Parse("2026-08-12T13:00:00Z"),
            isValid: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveSnapshotAsync(invalidCandidate, cancellationToken));

        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);
        Assert.NotNull(current);
        Assert.Equal("build-a", current.Build.BuildId);
    }

    [Fact]
    public async Task ListBuildsAsync_ReturnsNewestScansFirst()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new SqliteAtlasRepository(_databasePath);
        await repository.InitializeAsync(cancellationToken);
        await repository.SaveSnapshotAsync(
            CreateSnapshot("build-a", DateTimeOffset.Parse("2026-08-12T12:00:00Z")),
            cancellationToken);
        await repository.SaveSnapshotAsync(
            CreateSnapshot("build-b", DateTimeOffset.Parse("2026-08-12T13:00:00Z")),
            cancellationToken);

        var builds = await repository.ListBuildsAsync(cancellationToken);

        Assert.Collection(
            builds,
            build => Assert.Equal("build-b", build.BuildId),
            build => Assert.Equal("build-a", build.BuildId));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static EnvironmentSnapshot CreateSnapshot(
        string buildId,
        DateTimeOffset timestamp,
        IReadOnlyList<DependencyVersion>? dependencies = null,
        bool isValid = true)
    {
        var build = new GameBuild(
            buildId,
            GameVersion: "0.4.0",
            SteamBuildId: "123456",
            GameAssemblySha256: $"assembly-{buildId}",
            MetadataSha256: $"metadata-{buildId}",
            ScannedAtUtc: timestamp,
            IsValid: isValid);

        return new EnvironmentSnapshot(
            build,
            dependencies ??
            [
                new DependencyVersion(
                    DependencyKind.S1Api,
                    "1.0.0",
                    "C:\\Mods\\S1API.dll",
                    true),
                new DependencyVersion(
                    DependencyKind.MelonLoader,
                    "0.7.1",
                    "C:\\MelonLoader",
                    true)
            ],
            AtlasVersion: "0.1.0",
            CapturedAtUtc: timestamp);
    }
}
