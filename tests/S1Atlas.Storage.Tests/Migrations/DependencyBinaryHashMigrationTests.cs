using Microsoft.Data.Sqlite;
using S1Atlas.Core.Environment;
using S1Atlas.Storage.Migrations;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Storage.Tests.Migrations;

public sealed class DependencyBinaryHashMigrationTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-dependency-hash-migration-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public DependencyBinaryHashMigrationTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
    }

    [Fact]
    public async Task UpgradeFromNineteen_PreservesDependenciesAndRoundTripsNewHashes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups"),
            SqliteMigrations.All.Take(19).ToArray())
            .MigrateAsync(cancellationToken);
        await SeedV19SnapshotAsync(cancellationToken);

        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups-upgrade"))
            .MigrateAsync(cancellationToken);

        var repository = new SqliteAtlasRepository(_databasePath);
        var upgraded = await repository.GetCurrentSnapshotAsync(cancellationToken);
        Assert.NotNull(upgraded);
        var legacy = Assert.Single(upgraded.Dependencies);
        Assert.Equal(DependencyKind.S1Api, legacy.Kind);
        Assert.Null(legacy.BinarySha256);

        var rescanned = upgraded with
        {
            Build = upgraded.Build with { BuildId = "build-new" },
            Dependencies =
            [
                legacy with { BinarySha256 = "dll-hash-new" }
            ]
        };
        await repository.SaveSnapshotAsync(rescanned, cancellationToken);

        var reread = await repository.GetCurrentSnapshotAsync(cancellationToken);
        Assert.NotNull(reread);
        Assert.Equal(
            "dll-hash-new",
            Assert.Single(reread.Dependencies).BinarySha256);
        var readOnly = new ReadOnlySqliteAtlasRepository(
            new ReadOnlySqliteConnectionFactory(_databasePath));
        var readOnlyCurrent = await readOnly.GetCurrentSnapshotAsync(cancellationToken);
        Assert.NotNull(readOnlyCurrent);
        Assert.Equal(
            "dll-hash-new",
            Assert.Single(readOnlyCurrent.Dependencies).BinarySha256);
    }

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await TestDirectory.DeleteTreeAsync(_root);
    }

    private async Task SeedV19SnapshotAsync(CancellationToken cancellationToken)
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
            INSERT INTO builds (
                build_id, game_assembly_sha256, metadata_sha256,
                first_seen_at_utc, is_valid)
            VALUES (
                'build-legacy', 'assembly-legacy', 'metadata-legacy',
                '2026-08-12T12:00:00.0000000+00:00', 1);
            INSERT INTO environment_snapshots (
                snapshot_id, build_id, atlas_version, captured_at_utc,
                identity_version, executable_version, steam_app_id,
                steam_build_id, installation_root, game_assembly_path,
                global_metadata_path)
            VALUES (
                'snapshot-legacy', 'build-legacy', 's1atlas-test',
                '2026-08-12T12:00:00.0000000+00:00', 2, '2022.3.62f1', '3164500',
                'steam-build-1', 'C:\\Game', 'C:\\Game\\GameAssembly.dll',
                'C:\\Game\\global-metadata.dat');
            INSERT INTO dependencies (
                snapshot_id, ordinal, kind, version, path, is_installed)
            VALUES (
                'snapshot-legacy', 0, 'S1Api', '3.2.1.0',
                'C:\\Game\\Mods\\S1API.dll', 1);
            UPDATE atlas_state
            SET current_snapshot_id = 'snapshot-legacy'
            WHERE singleton_id = 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
