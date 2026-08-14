using Microsoft.Data.Sqlite;
using S1Atlas.Storage.Migrations;
using Xunit;

namespace S1Atlas.Storage.Tests.Migrations;

public sealed class IndexingMigrationTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-index-migration-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public IndexingMigrationTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
    }

    [Fact]
    public async Task Fresh_database_migrates_to_v6_index_schema()
    {
        await new SqliteMigrationRunner(_databasePath, Path.Combine(_root, "backups")).MigrateAsync(
            TestContext.Current.CancellationToken);

        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            Assert.Equal(6L, await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;"));
            foreach (var table in new[] { "code_snapshots", "index_runs", "symbols", "source_files", "source_locations", "symbol_fingerprints", "relationships", "upstream_repositories", "upstream_snapshots", "upstream_state" })
                Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name=$name;", ("$name", table)));

            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO code_snapshots(snapshot_id, codebase, channel, source_identity, created_at_utc) VALUES ('s', 'ScheduleI', 'Preview', 'x', '2026-01-01');";
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
