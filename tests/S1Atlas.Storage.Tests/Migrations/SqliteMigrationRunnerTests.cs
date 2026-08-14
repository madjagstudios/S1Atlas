using Microsoft.Data.Sqlite;
using S1Atlas.Storage.Migrations;
using Xunit;

namespace S1Atlas.Storage.Tests.Migrations;

public sealed class SqliteMigrationRunnerTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory;
    private readonly string _databasePath;
    private readonly string _backupDirectory;
    private readonly TimeProvider _timeProvider = new FixedTimeProvider(
        DateTimeOffset.Parse("2026-08-12T23:30:00Z"));

    public SqliteMigrationRunnerTests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-migration-tests-{Guid.NewGuid():N}");
        _databasePath = Path.Combine(_temporaryDirectory, "atlas.db");
        _backupDirectory = Path.Combine(_temporaryDirectory, "backups");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task MigrateAsync_NewDatabase_AppliesV1ThroughV7WithoutBackup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var runner = new SqliteMigrationRunner(
            _databasePath,
            _backupDirectory,
            _timeProvider);

        await runner.MigrateAsync(cancellationToken);

        await using var connection = await FoundationV1DatabaseFixture.OpenFileAsync(
            _databasePath,
            SqliteOpenMode.ReadOnly,
            cancellationToken);
        Assert.Equal(
            7L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM schema_migrations;",
                cancellationToken));
        Assert.True(await ColumnExistsAsync(
            connection,
            "builds",
            "first_seen_at_utc",
            cancellationToken));
        Assert.False(await ColumnExistsAsync(
            connection,
            "builds",
            "game_version",
            cancellationToken));
        Assert.True(await ColumnExistsAsync(
            connection,
            "environment_snapshots",
            "identity_version",
            cancellationToken));
        Assert.True(await TableExistsAsync(
            connection,
            "managed_tool_installations",
            cancellationToken));
        Assert.True(await TableExistsAsync(
            connection,
            "tool_instances",
            cancellationToken));
        Assert.True(await TableExistsAsync(
            connection,
            "validated_extractions",
            cancellationToken));
        Assert.True(await ColumnExistsAsync(
            connection,
            "extraction_attempts",
            "validation_source_extraction_id",
            cancellationToken));
        Assert.True(await ColumnExistsAsync(
            connection,
            "symbols",
            "body_recovery_status",
            cancellationToken));
        Assert.Equal(
            1L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM atlas_state WHERE singleton_id = 1;",
                cancellationToken));
        Assert.False(Directory.Exists(_backupDirectory));
    }

    [Fact]
    public async Task MigrateAsync_SyntheticFoundationDatabaseWithSteamBuildId_CreatesBackupAndPreservesRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await FoundationV1DatabaseFixture.CreatePopulatedDatabaseAsync(
            _databasePath,
            steamBuildId: "12345678",
            cancellationToken);
        var runner = new SqliteMigrationRunner(
            _databasePath,
            _backupDirectory,
            _timeProvider);

        await runner.MigrateAsync(cancellationToken);

        var backups = Directory.GetFiles(_backupDirectory, "*.db");
        Assert.Single(backups);

        await using (var connection = await FoundationV1DatabaseFixture.OpenFileAsync(
            _databasePath,
            SqliteOpenMode.ReadOnly,
            cancellationToken))
        {
            Assert.Equal(
                7L,
                await ScalarInt64Async(
                    connection,
                    "SELECT COUNT(*) FROM schema_migrations;",
                    cancellationToken));
            Assert.Equal(
                FoundationV1DatabaseFixture.ReferenceBuildId,
                await ScalarStringAsync(
                    connection,
                    "SELECT build_id FROM builds;",
                    cancellationToken));
            Assert.Equal(
                "game-assembly-sha256",
                await ScalarStringAsync(
                    connection,
                    "SELECT game_assembly_sha256 FROM builds;",
                    cancellationToken));
            Assert.Equal(
                "metadata-sha256",
                await ScalarStringAsync(
                    connection,
                    "SELECT metadata_sha256 FROM builds;",
                    cancellationToken));
            Assert.Equal(
                FoundationV1DatabaseFixture.ScannedAtUtc,
                await ScalarStringAsync(
                    connection,
                    "SELECT first_seen_at_utc FROM builds;",
                    cancellationToken));
            Assert.False(await ColumnExistsAsync(
                connection,
                "builds",
                "game_version",
                cancellationToken));
            Assert.False(await ColumnExistsAsync(
                connection,
                "builds",
                "steam_build_id",
                cancellationToken));
            Assert.Equal(
                FoundationV1DatabaseFixture.SnapshotId,
                await ScalarStringAsync(
                    connection,
                    "SELECT snapshot_id FROM environment_snapshots;",
                    cancellationToken));
            Assert.Equal(
                1L,
                await ScalarInt64Async(
                    connection,
                    "SELECT identity_version FROM environment_snapshots;",
                    cancellationToken));
            Assert.Equal(
                "2022.3.62.7762112",
                await ScalarStringAsync(
                    connection,
                    "SELECT executable_version FROM environment_snapshots;",
                    cancellationToken));
            Assert.Equal(
                "12345678",
                await ScalarStringAsync(
                    connection,
                    "SELECT steam_build_id FROM environment_snapshots;",
                    cancellationToken));
            Assert.Equal(
                4L,
                await ScalarInt64Async(
                    connection,
                    "SELECT COUNT(*) FROM dependencies;",
                    cancellationToken));
            Assert.Equal(
                FoundationV1DatabaseFixture.SnapshotId,
                await ScalarStringAsync(
                    connection,
                    "SELECT current_snapshot_id FROM atlas_state WHERE singleton_id = 1;",
                    cancellationToken));
            Assert.True(await TableExistsAsync(
                connection,
                "managed_tool_installations",
                cancellationToken));
            Assert.True(await TableExistsAsync(
                connection,
                "tool_instances",
                cancellationToken));
            Assert.True(await ColumnExistsAsync(
                connection,
                "symbols",
                "body_recovery_status",
                cancellationToken));
        }

        await using var backup = await FoundationV1DatabaseFixture.OpenFileAsync(
            backups[0],
            SqliteOpenMode.ReadOnly,
            cancellationToken);
        Assert.True(await ColumnExistsAsync(
            backup,
            "builds",
            "game_version",
            cancellationToken));
        Assert.Equal(
            FoundationV1DatabaseFixture.ReferenceBuildId,
            await ScalarStringAsync(
                backup,
                "SELECT build_id FROM builds;",
                cancellationToken));
    }

    [Fact]
    public async Task MigrateAsync_UnknownNonemptySchema_RefusesWithoutMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var connection = await FoundationV1DatabaseFixture.OpenFileAsync(
            _databasePath,
            SqliteOpenMode.ReadWriteCreate,
            cancellationToken))
        {
            await ExecuteAsync(
                connection,
                "CREATE TABLE unrelated(value TEXT);",
                cancellationToken);
        }
        var runner = new SqliteMigrationRunner(
            _databasePath,
            _backupDirectory,
            _timeProvider);

        await Assert.ThrowsAsync<UnrecognizedAtlasSchemaException>(
            () => runner.MigrateAsync(cancellationToken));

        await using var reopened = await FoundationV1DatabaseFixture.OpenFileAsync(
            _databasePath,
            SqliteOpenMode.ReadOnly,
            cancellationToken);
        Assert.True(await TableExistsAsync(
            reopened,
            "unrelated",
            cancellationToken));
        Assert.False(await TableExistsAsync(
            reopened,
            "schema_migrations",
            cancellationToken));
        Assert.False(Directory.Exists(_backupDirectory));
    }

    [Fact]
    public async Task MigrateAsync_WhenAppliedChecksumDiffers_Refuses()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var runner = new SqliteMigrationRunner(
            _databasePath,
            _backupDirectory,
            _timeProvider);
        await runner.MigrateAsync(cancellationToken);
        await using (var connection = await FoundationV1DatabaseFixture.OpenFileAsync(
            _databasePath,
            SqliteOpenMode.ReadWrite,
            cancellationToken))
        {
            await ExecuteAsync(
                connection,
                "UPDATE schema_migrations SET checksum = 'tampered' WHERE version = 1;",
                cancellationToken);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.MigrateAsync(cancellationToken));

        Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MigrateAsync_WhenMigrationFails_RollsBackSchemaChanges()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var migrations = new[]
        {
            new SqliteMigration(
                1,
                "foundation-v1",
                FoundationV1DatabaseFixture.SchemaSql),
            new SqliteMigration(
                2,
                "broken-v2",
                """
                ALTER TABLE builds ADD COLUMN should_rollback TEXT NULL;
                THIS IS NOT VALID SQL;
                """)
        };
        var runner = new SqliteMigrationRunner(
            _databasePath,
            _backupDirectory,
            migrations,
            _timeProvider);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.MigrateAsync(cancellationToken));

        Assert.Contains("broken-v2", exception.Message, StringComparison.Ordinal);
        await using var connection = await FoundationV1DatabaseFixture.OpenFileAsync(
            _databasePath,
            SqliteOpenMode.ReadOnly,
            cancellationToken);
        Assert.False(await ColumnExistsAsync(
            connection,
            "builds",
            "should_rollback",
            cancellationToken));
        Assert.Equal(
            1L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM schema_migrations;",
                cancellationToken));
        Assert.Equal(
            0L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM schema_migrations WHERE version = 2;",
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

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
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

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{tableName.Replace("'", "''", StringComparison.Ordinal)}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(
                    reader.GetString(1),
                    columnName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<long> ScalarInt64Async(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarStringAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(
            value,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}