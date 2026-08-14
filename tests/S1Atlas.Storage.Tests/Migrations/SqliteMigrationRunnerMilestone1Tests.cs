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
    public async Task VersionSixDatabase_MigratesToSeven_PreservesSymbolsAndAddsNullableBodyRecoveryStatus()
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
        Assert.Equal(7L, await ScalarAsync(migrated, "SELECT MAX(version) FROM schema_migrations;", cancellationToken));
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

        Assert.Single(Directory.GetFiles(_backupDirectory, "atlas-before-schema-7-*.db", SearchOption.TopDirectoryOnly));
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
