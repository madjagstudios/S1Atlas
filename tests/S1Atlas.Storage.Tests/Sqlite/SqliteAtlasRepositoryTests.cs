using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Storage.Sqlite;
using S1Atlas.Storage.Tests.Migrations;
using Xunit;

namespace S1Atlas.Storage.Tests.Sqlite;

public sealed class SqliteAtlasRepositoryTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory;
    private readonly string _databasePath;
    private readonly string _backupDirectory;

    public SqliteAtlasRepositoryTests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-storage-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _databasePath = Path.Combine(_temporaryDirectory, "atlas.db");
        _backupDirectory = Path.Combine(_temporaryDirectory, "backups");
    }

    [Fact]
    public async Task SaveSnapshotAsync_ValidV2Snapshot_RoundTripsAndBecomesCurrent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = CreateRepository();
        await repository.InitializeAsync(cancellationToken);
        var snapshot = CreateSnapshot(
            "build-a",
            DateTimeOffset.Parse("2026-08-12T12:00:00Z"));

        await repository.SaveSnapshotAsync(snapshot, cancellationToken);

        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);
        Assert.NotNull(current);
        Assert.Equal(snapshot.IdentityVersion, current.IdentityVersion);
        Assert.Equal(snapshot.Build, current.Build);
        Assert.Equal(snapshot.Installation, current.Installation);
        Assert.Equal(snapshot.Dependencies, current.Dependencies);
        Assert.Equal(snapshot.AtlasVersion, current.AtlasVersion);
        Assert.Equal(snapshot.CapturedAtUtc, current.CapturedAtUtc);
    }

    [Fact]
    public async Task SaveSnapshotAsync_SameGameBuildWithChangedSteamBuild_PromotesNewEnvironmentWithoutDuplicatingBuild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = CreateRepository();
        await repository.InitializeAsync(cancellationToken);
        await repository.SaveSnapshotAsync(
            CreateSnapshot(
                "build-a",
                DateTimeOffset.Parse("2026-08-12T12:00:00Z"),
                steamBuildId: "100"),
            cancellationToken);
        var changedEnvironment = CreateSnapshot(
            "build-a",
            DateTimeOffset.Parse("2026-08-12T13:00:00Z"),
            steamBuildId: "200");

        await repository.SaveSnapshotAsync(changedEnvironment, cancellationToken);

        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);
        var builds = await repository.ListBuildsAsync(cancellationToken);
        Assert.NotNull(current);
        Assert.Equal("200", current.Installation.SteamBuildId);
        Assert.Equal(changedEnvironment.CapturedAtUtc, current.CapturedAtUtc);
        Assert.Single(builds);
        Assert.Equal("build-a", builds[0].BuildId);
    }

    [Fact]
    public async Task SaveSnapshotAsync_SameGameBuildWithChangedInstallationPath_PromotesNewEnvironment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = CreateRepository();
        await repository.InitializeAsync(cancellationToken);
        await repository.SaveSnapshotAsync(
            CreateSnapshot(
                "build-a",
                DateTimeOffset.Parse("2026-08-12T12:00:00Z"),
                installationRoot: "C:\\Steam\\Schedule I"),
            cancellationToken);
        var changedEnvironment = CreateSnapshot(
            "build-a",
            DateTimeOffset.Parse("2026-08-12T13:00:00Z"),
            installationRoot: "D:\\Steam\\Schedule I");

        await repository.SaveSnapshotAsync(changedEnvironment, cancellationToken);

        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);
        Assert.NotNull(current);
        Assert.Equal(
            Path.GetFullPath("D:\\Steam\\Schedule I"),
            current.Installation.InstallationRoot);
        Assert.Equal(2L, await CountAsync("environment_snapshots", cancellationToken));
    }

    [Fact]
    public async Task SaveSnapshotAsync_MultipleDependenciesOfSameKind_RoundTripsEveryEntry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = CreateRepository();
        await repository.InitializeAsync(cancellationToken);
        var snapshot = CreateSnapshot(
            "build-a",
            DateTimeOffset.Parse("2026-08-12T12:00:00Z"),
            dependencies:
            [
                new DependencyVersion(
                    DependencyKind.S1Api,
                    "2.0.0",
                    "C:\\Mods\\S1API-2.dll",
                    true),
                new DependencyVersion(
                    DependencyKind.S1Api,
                    "1.0.0",
                    "C:\\Mods\\S1API-1.dll",
                    true),
                new DependencyVersion(
                    DependencyKind.Sideload,
                    null,
                    null,
                    false)
            ]);

        await repository.SaveSnapshotAsync(snapshot, cancellationToken);

        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);
        Assert.NotNull(current);
        Assert.Collection(
            current.Dependencies,
            dependency =>
            {
                Assert.Equal(DependencyKind.S1Api, dependency.Kind);
                Assert.Equal("1.0.0", dependency.Version);
            },
            dependency =>
            {
                Assert.Equal(DependencyKind.S1Api, dependency.Kind);
                Assert.Equal("2.0.0", dependency.Version);
            },
            dependency =>
            {
                Assert.Equal(DependencyKind.Sideload, dependency.Kind);
                Assert.False(dependency.IsInstalled);
            });
    }

    [Fact]
    public async Task SaveSnapshotAsync_WhenDependencyInsertFails_RollsBackAndKeepsPreviousCurrentBuild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = CreateRepository();
        await repository.InitializeAsync(cancellationToken);
        var baseline = CreateSnapshot(
            "build-a",
            DateTimeOffset.Parse("2026-08-12T12:00:00Z"));
        await repository.SaveSnapshotAsync(baseline, cancellationToken);
        await CreateFailingDependencyTriggerAsync(cancellationToken);
        var candidate = CreateSnapshot(
            "build-b",
            DateTimeOffset.Parse("2026-08-12T13:00:00Z"),
            dependencies:
            [
                new DependencyVersion(
                    DependencyKind.Sideload,
                    "1.0.0",
                    "C:\\Mods\\Sideload.dll",
                    true)
            ]);

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
        var repository = CreateRepository();
        await repository.InitializeAsync(cancellationToken);
        var baseline = CreateSnapshot(
            "build-a",
            DateTimeOffset.Parse("2026-08-12T12:00:00Z"));
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
    public async Task SaveSnapshotAsync_WhenIdentityVersionIsOne_RejectsCandidate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = CreateRepository();
        await repository.InitializeAsync(cancellationToken);
        var candidate = CreateSnapshot(
            "build-a",
            DateTimeOffset.Parse("2026-08-12T12:00:00Z"),
            identityVersion: 1);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveSnapshotAsync(candidate, cancellationToken));

        Assert.Contains(
            "identity-version 2",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListBuildsAsync_ReturnsNewestFirstSeenBuildsFirst()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = CreateRepository();
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

    [Fact]
    public async Task InitializeAsync_OnFoundationDatabase_MigratesAndReadsV1Snapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await FoundationV1DatabaseFixture.CreatePopulatedDatabaseAsync(
            _databasePath,
            steamBuildId: null,
            cancellationToken);
        var repository = CreateRepository();

        await repository.InitializeAsync(cancellationToken);

        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);
        Assert.NotNull(current);
        Assert.Equal(1, current.IdentityVersion);
        Assert.Equal(
            FoundationV1DatabaseFixture.ReferenceBuildId,
            current.Build.BuildId);
        Assert.Equal(
            "2022.3.62.7762112",
            current.Installation.ExecutableVersion);
        Assert.Null(current.Installation.SteamBuildId);
        Assert.Null(current.Installation.InstallationRoot);
        Assert.Equal(4, current.Dependencies.Count);
        Assert.Single(Directory.GetFiles(_backupDirectory, "*.db"));
    }

    [Fact]
    public async Task SaveSnapshotAsync_AfterFoundationMigration_PromotesV2AndRetainsV1History()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await FoundationV1DatabaseFixture.CreatePopulatedDatabaseAsync(
            _databasePath,
            steamBuildId: null,
            cancellationToken);
        var repository = CreateRepository();
        await repository.InitializeAsync(cancellationToken);
        var v2 = CreateSnapshot(
            FoundationV1DatabaseFixture.ReferenceBuildId,
            DateTimeOffset.Parse("2026-08-12T20:00:00Z"),
            gameAssemblyHash: "game-assembly-sha256",
            metadataHash: "metadata-sha256",
            steamBuildId: "19420567");

        await repository.SaveSnapshotAsync(v2, cancellationToken);

        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);
        var builds = await repository.ListBuildsAsync(cancellationToken);
        Assert.NotNull(current);
        Assert.Equal(2, current.IdentityVersion);
        Assert.Equal("19420567", current.Installation.SteamBuildId);
        Assert.Single(builds);
        Assert.Equal(2L, await CountAsync("environment_snapshots", cancellationToken));
        Assert.Equal(
            1L,
            await CountWhereAsync(
                "environment_snapshots",
                "identity_version = 1",
                cancellationToken));
        Assert.Equal(
            1L,
            await CountWhereAsync(
                "environment_snapshots",
                "identity_version = 2",
                cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private SqliteAtlasRepository CreateRepository() =>
        new(_databasePath, _backupDirectory);

    private async Task CreateFailingDependencyTriggerAsync(
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER fail_sideload_dependency
            BEFORE INSERT ON dependencies
            WHEN NEW.kind = 'Sideload'
            BEGIN
                SELECT RAISE(ABORT, 'forced dependency insert failure');
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> CountAsync(
        string tableName,
        CancellationToken cancellationToken) =>
        await CountWhereAsync(tableName, "1 = 1", cancellationToken);

    private async Task<long> CountWhereAsync(
        string tableName,
        string condition,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName} WHERE {condition};";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static EnvironmentSnapshot CreateSnapshot(
        string buildId,
        DateTimeOffset timestamp,
        IReadOnlyList<DependencyVersion>? dependencies = null,
        bool isValid = true,
        int identityVersion = 2,
        string gameAssemblyHash = "assembly-hash",
        string metadataHash = "metadata-hash",
        string? steamBuildId = "19420567",
        string installationRoot = "C:\\Steam\\steamapps\\common\\Schedule I")
    {
        var fullRoot = Path.GetFullPath(installationRoot);
        var build = new GameBuild(
            buildId,
            gameAssemblyHash,
            metadataHash,
            timestamp,
            isValid);
        var observation = new InstallationObservation(
            "2022.3.62.7762112",
            "3164500",
            steamBuildId,
            fullRoot,
            Path.Combine(fullRoot, "GameAssembly.dll"),
            Path.Combine(
                fullRoot,
                "Schedule I_Data",
                "il2cpp_data",
                "Metadata",
                "global-metadata.dat"));

        return new EnvironmentSnapshot(
            identityVersion,
            build,
            observation,
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
            "0.2.0",
            timestamp);
    }
}
