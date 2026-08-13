using Microsoft.Data.Sqlite;
using S1Atlas.Storage.Migrations;
using Xunit;

namespace S1Atlas.Storage.Tests.Migrations;

public sealed class ManagedToolMigrationTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory;
    private readonly string _databasePath;
    private readonly string _backupDirectory;
    private readonly TimeProvider _timeProvider = new FixedTimeProvider(
        DateTimeOffset.Parse("2026-08-13T01:45:00Z"));

    public ManagedToolMigrationTests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-managed-tool-migration-tests-{Guid.NewGuid():N}");
        _databasePath = Path.Combine(_temporaryDirectory, "atlas.db");
        _backupDirectory = Path.Combine(_temporaryDirectory, "backups");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task MigrateAsync_V2Database_AddsToolTablesAndCreatesOneSchema3Backup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var v2Runner = new SqliteMigrationRunner(
            _databasePath,
            _backupDirectory,
            SqliteMigrations.All.Take(2).ToArray(),
            _timeProvider);
        await v2Runner.MigrateAsync(cancellationToken);
        var productionRunner = new SqliteMigrationRunner(
            _databasePath,
            _backupDirectory,
            _timeProvider);

        await productionRunner.MigrateAsync(cancellationToken);

        await using var connection = await FoundationV1DatabaseFixture.OpenFileAsync(
            _databasePath,
            SqliteOpenMode.ReadOnly,
            cancellationToken);
        Assert.Equal(
            3L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM schema_migrations;",
                cancellationToken));
        Assert.True(await TableExistsAsync(
            connection,
            "managed_tool_installations",
            cancellationToken));
        Assert.True(await TableExistsAsync(
            connection,
            "tool_instances",
            cancellationToken));
        Assert.True(await IndexExistsAsync(
            connection,
            "ix_tool_instances_tool_trust",
            cancellationToken));
        Assert.Single(Directory.GetFiles(
            _backupDirectory,
            "atlas-before-schema-3-*.db"));
    }

    [Fact]
    public async Task MigrateAsync_NewDatabase_AppliesThreeMigrationsWithoutBackup()
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
            3L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM schema_migrations;",
                cancellationToken));
        Assert.True(await TableExistsAsync(
            connection,
            "managed_tool_installations",
            cancellationToken));
        Assert.True(await TableExistsAsync(
            connection,
            "tool_instances",
            cancellationToken));
        Assert.False(Directory.Exists(_backupDirectory));
    }

    [Fact]
    public async Task MigrateAsync_FoundationV1Database_AppliesThroughV3AndPreservesFoundationState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await FoundationV1DatabaseFixture.CreatePopulatedDatabaseAsync(
            _databasePath,
            steamBuildId: null,
            cancellationToken);
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
            3L,
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
            FoundationV1DatabaseFixture.SnapshotId,
            await ScalarStringAsync(
                connection,
                "SELECT current_snapshot_id FROM atlas_state WHERE singleton_id = 1;",
                cancellationToken));
        Assert.Equal(
            4L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM dependencies;",
                cancellationToken));
        Assert.True(await TableExistsAsync(
            connection,
            "managed_tool_installations",
            cancellationToken));
        Assert.True(await TableExistsAsync(
            connection,
            "tool_instances",
            cancellationToken));
        Assert.Single(Directory.GetFiles(
            _backupDirectory,
            "atlas-before-schema-3-*.db"));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
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

    private static async Task<bool> IndexExistsAsync(
        SqliteConnection connection,
        string indexName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_schema
            WHERE type = 'index' AND name = $name;
            """;
        command.Parameters.AddWithValue("$name", indexName);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
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
        return value is null or DBNull
            ? null
            : Convert.ToString(
                value,
                System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
