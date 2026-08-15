using Microsoft.Data.Sqlite;
using S1Atlas.Storage.Migrations;
using Xunit;

namespace S1Atlas.Storage.Tests.Migrations;

public sealed class SqliteMigrationRunnerMilestone1Tests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-milestone1-migration-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private readonly string _backupDirectory;

    public SqliteMigrationRunnerMilestone1Tests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
        _backupDirectory = Path.Combine(_root, "backups");
    }

    [Fact]
    public void MigrationSeven_HasCommittedChecksum_AndEarlierMigrationsRemainPinned()
    {
        Assert.Equal(9, SqliteMigrations.All.Count);
        Assert.Equal("d03021f97dfe3cd5e52305ae945258aa7fdbc8ccb086808a8255df7df0d10bb0", SqliteMigrations.All[6].Checksum);
        Assert.Equal(
            [
                "90ee69e49a9763c6443b4db0b5b2752ff78292fb7a7f7e7b5d86fd22137fd92e",
                "39cb7f3c2c6fa047e718b101da950ea39da03277a1763b1ba14d4abd79c519ae",
                "c730f9db46ae1565f82df2cde3651f27e3c6f835d85f68b2e52f72fe36e8ebea",
                "e735858f725c4c285edc82a6170d9fdb3f5161eb960f87f8cece165e416c899d"
            ],
            SqliteMigrations.All.Take(4).Select(migration => migration.Checksum));
    }

    [Fact]
    public async Task VersionSixDatabase_MigratesToNine_PreservesSymbolsAndAddsNullableBodyRecoveryStatus()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var v6Migrations = SqliteMigrations.All.Take(6).ToArray();
        await new SqliteMigrationRunner(_databasePath, _backupDirectory, v6Migrations)
            .MigrateAsync(cancellationToken);

        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteAsync(connection, """
                INSERT INTO code_snapshots(snapshot_id, codebase, channel, source_identity, created_at_utc)
                VALUES ('snapshot-1', 'ScheduleI', 'Installed', 'extraction-1', '2026-08-14T00:00:00Z');
                INSERT INTO symbols(symbol_id, snapshot_id, canonical_key, kind, qualified_name, signature, is_best_effort)
                VALUES ('symbol-1', 'snapshot-1', 'ScheduleI:Installed:Method:Demo.Widget::Run()', 'Method', 'Demo.Widget.Run', 'System.Void Demo.Widget::Run()', 0);
                """, cancellationToken);
        }

        await new SqliteMigrationRunner(_databasePath, _backupDirectory).MigrateAsync(cancellationToken);

        await using var migrated = new SqliteConnection($"Data Source={_databasePath}");
        await migrated.OpenAsync(cancellationToken);
        Assert.Equal(9L, await ScalarAsync(migrated, "SELECT MAX(version) FROM schema_migrations;", cancellationToken));
        Assert.Equal(1L, await ScalarAsync(migrated, "SELECT COUNT(*) FROM symbols WHERE symbol_id = 'symbol-1';", cancellationToken));
        Assert.Equal(1L, await ScalarAsync(migrated, "SELECT COUNT(*) FROM symbols WHERE symbol_id = 'symbol-1' AND body_recovery_status IS NULL;", cancellationToken));

        await using (var tableInfo = migrated.CreateCommand())
        {
            tableInfo.CommandText = "PRAGMA table_info(symbols);";
            var columns = new List<string>();
            await using var reader = await tableInfo.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
            Assert.Contains("body_recovery_status", columns);
        }

        await using (var invalid = migrated.CreateCommand())
        {
            invalid.CommandText = "UPDATE symbols SET body_recovery_status = 'DefinitelyNotValid' WHERE symbol_id = 'symbol-1';";
            await Assert.ThrowsAsync<SqliteException>(() => invalid.ExecuteNonQueryAsync(cancellationToken));
        }

        Assert.Single(Directory.GetFiles(_backupDirectory, "atlas-before-schema-9-*.db", SearchOption.TopDirectoryOnly));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
