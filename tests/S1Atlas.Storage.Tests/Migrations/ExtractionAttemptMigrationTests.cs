using Microsoft.Data.Sqlite;
using S1Atlas.Storage.Migrations;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests.Migrations;

public sealed class ExtractionAttemptMigrationTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory;
    private readonly string _databasePath;
    private readonly string _backupDirectory;
    private readonly TimeProvider _timeProvider = new FixedTimeProvider(
        DateTimeOffset.Parse("2026-08-13T03:00:00Z"));

    public ExtractionAttemptMigrationTests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-extraction-migration-tests-{Guid.NewGuid():N}");
        _databasePath = Path.Combine(_temporaryDirectory, "atlas.db");
        _backupDirectory = Path.Combine(_temporaryDirectory, "backups");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task InitializeAsync_RealVersionThreeDatabase_UpgradesOnceAndPreservesState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateVersionThreeDatabaseAsync(cancellationToken);
        await SeedVersionThreeStateAsync(cancellationToken);
        var repository = new SqliteAtlasRepository(
            _databasePath,
            _backupDirectory);

        await repository.InitializeAsync(cancellationToken);

        var migrationVersions = await ReadMigrationVersionsAsync(cancellationToken);
        Assert.Equal([1, 2, 3, 4, 5, 6], migrationVersions);
        Assert.True(await TableExistsAsync("extraction_attempts", cancellationToken));
        Assert.True(await TableExistsAsync("input_snapshots", cancellationToken));
        Assert.True(await TableExistsAsync("input_snapshot_files", cancellationToken));
        Assert.True(await TableExistsAsync("validated_extractions", cancellationToken));
        Assert.True(await TableExistsAsync("extraction_artifacts", cancellationToken));
        Assert.True(await TableExistsAsync("extraction_validation_results", cancellationToken));
        Assert.True(await TableExistsAsync("extraction_validation_issues", cancellationToken));
        Assert.True(await TableExistsAsync("preferred_extractions", cancellationToken));
        Assert.True(await TableExistsAsync("extraction_preference_events", cancellationToken));
        Assert.True(await ColumnExistsAsync(
            "extraction_attempts",
            "validation_source_extraction_id",
            cancellationToken));
        Assert.Equal(
            "current-build",
            (await repository.GetCurrentSnapshotAsync(cancellationToken))!
                .Build.BuildId);
        Assert.NotNull(await repository.GetManagedToolAsync(
            "cpp2il",
            "test-version",
            "win-x64",
            cancellationToken));
        Assert.Single(GetVersionSixBackups());

        await repository.InitializeAsync(cancellationToken);

        migrationVersions = await ReadMigrationVersionsAsync(cancellationToken);
        Assert.Equal([1, 2, 3, 4, 5, 6], migrationVersions);
        Assert.Single(GetVersionSixBackups());
    }

    [Fact]
    public void CommittedMigrationsOneThroughThree_RetainTheirChecksums()
    {
        Assert.Equal(
            [
                "90ee69e49a9763c6443b4db0b5b2752ff78292fb7a7f7e7b5d86fd22137fd92e",
                "39cb7f3c2c6fa047e718b101da950ea39da03277a1763b1ba14d4abd79c519ae",
                "c730f9db46ae1565f82df2cde3651f27e3c6f835d85f68b2e52f72fe36e8ebea"
            ],
            SqliteMigrations.All.Take(3).Select(migration => migration.Checksum));
    }

    [Fact]
    public async Task MigrateAsync_FailingVersionFour_RollsBackSchemaAndLedger()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateVersionThreeDatabaseAsync(cancellationToken);
        var migrations = SqliteMigrations.All
            .Take(3)
            .Append(new SqliteMigration(
                4,
                "broken-extraction-attempts-v4",
                """
                CREATE TABLE rollback_probe (id INTEGER NOT NULL PRIMARY KEY);
                THIS IS NOT VALID SQL;
                """))
            .ToArray();
        var runner = new SqliteMigrationRunner(
            _databasePath,
            _backupDirectory,
            migrations,
            _timeProvider);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.MigrateAsync(cancellationToken));

        Assert.Contains("broken-extraction-attempts-v4", exception.Message);
        Assert.False(await TableExistsAsync("rollback_probe", cancellationToken));
        var migrationVersions = await ReadMigrationVersionsAsync(cancellationToken);
        Assert.Equal([1, 2, 3], migrationVersions);
    }

    [Fact]
    public async Task InitializeAsync_UnknownSchema_RefusesWithoutMutationOrBackup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var connection = await OpenAsync(
                         SqliteOpenMode.ReadWriteCreate,
                         cancellationToken))
        {
            await ExecuteAsync(
                connection,
                "CREATE TABLE unrelated (id INTEGER NOT NULL PRIMARY KEY);",
                cancellationToken);
        }
        var repository = new SqliteAtlasRepository(
            _databasePath,
            _backupDirectory);

        await Assert.ThrowsAsync<UnrecognizedAtlasSchemaException>(
            () => repository.InitializeAsync(cancellationToken));

        Assert.True(await TableExistsAsync("unrelated", cancellationToken));
        Assert.False(await TableExistsAsync("schema_migrations", cancellationToken));
        Assert.False(Directory.Exists(_backupDirectory));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private async Task CreateVersionThreeDatabaseAsync(
        CancellationToken cancellationToken)
    {
        var runner = new SqliteMigrationRunner(
            _databasePath,
            _backupDirectory,
            SqliteMigrations.All.Take(3).ToArray(),
            _timeProvider);
        await runner.MigrateAsync(cancellationToken);
    }

    private async Task SeedVersionThreeStateAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(
            SqliteOpenMode.ReadWrite,
            cancellationToken);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO builds (
                build_id,
                game_assembly_sha256,
                metadata_sha256,
                first_seen_at_utc,
                is_valid)
            VALUES
                ('historical-build', 'assembly-old', 'metadata-old',
                 '2026-08-11T12:00:00.0000000+00:00', 1),
                ('current-build', 'assembly-current', 'metadata-current',
                 '2026-08-12T12:00:00.0000000+00:00', 1);

            INSERT INTO environment_snapshots (
                snapshot_id,
                build_id,
                atlas_version,
                captured_at_utc,
                identity_version,
                executable_version,
                steam_app_id,
                steam_build_id,
                installation_root,
                game_assembly_path,
                global_metadata_path)
            VALUES
                ('historical-snapshot', 'historical-build', '0.2.0-test',
                 '2026-08-11T12:00:00.0000000+00:00', 2, '2022.3',
                 '3164500', '100', 'C:\old', 'C:\old\GameAssembly.dll',
                 'C:\old\global-metadata.dat'),
                ('current-snapshot', 'current-build', '0.2.0-test',
                 '2026-08-12T12:00:00.0000000+00:00', 2, '2022.3',
                 '3164500', '200', 'C:\current',
                 'C:\current\GameAssembly.dll',
                 'C:\current\global-metadata.dat');

            UPDATE atlas_state
            SET current_snapshot_id = 'current-snapshot'
            WHERE singleton_id = 1;

            INSERT INTO managed_tool_installations (
                tool_id,
                version,
                platform,
                definition_digest,
                package_sha256,
                executable_sha256,
                root_path,
                status,
                installed_at_utc,
                last_verified_at_utc,
                probe_summary)
            VALUES (
                'cpp2il', 'test-version', 'win-x64',
                'definition-digest', 'package-sha256', 'executable-sha256',
                'C:\tools\cpp2il', 'Verified',
                '2026-08-12T13:00:00.0000000+00:00',
                '2026-08-12T14:00:00.0000000+00:00', '[]');
            """,
            cancellationToken);
    }

    private string[] GetVersionSixBackups() =>
        Directory.Exists(_backupDirectory)
            ? Directory.GetFiles(
                _backupDirectory,
                "atlas-before-schema-6-*.db",
                SearchOption.TopDirectoryOnly)
            : [];

    private async Task<int[]> ReadMigrationVersionsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(
            SqliteOpenMode.ReadOnly,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
        var versions = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions.ToArray();
    }

    private async Task<bool> TableExistsAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(
            SqliteOpenMode.ReadOnly,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_schema
            WHERE type = 'table' AND name = $name;
            """;
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private async Task<bool> ColumnExistsAsync(
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(
            SqliteOpenMode.ReadOnly,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA table_info('{tableName.Replace("'", "''", StringComparison.Ordinal)}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<SqliteConnection> OpenAsync(
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = mode,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
