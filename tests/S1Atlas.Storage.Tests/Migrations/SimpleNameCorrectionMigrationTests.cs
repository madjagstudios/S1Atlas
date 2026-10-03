using Microsoft.Data.Sqlite;
using S1Atlas.Storage.Migrations;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Storage.Tests.Migrations;

public sealed class SimpleNameCorrectionMigrationTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-simple-name-migration-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public SimpleNameCorrectionMigrationTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
    }

    [Fact]
    public async Task UpgradeFromSeventeen_CorrectsMemberSimpleNamesAndRebuildsFts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups"),
            SqliteMigrations.All.Take(17).ToArray())
            .MigrateAsync(cancellationToken);

        await using (var seed = await OpenAsync(cancellationToken))
        {
            await using var command = seed.CreateCommand();
            // simple_name values below are what the last-dot backfill wrote.
            command.CommandText = """
                INSERT INTO code_snapshots(snapshot_id, codebase, channel, environment_snapshot_id, source_identity, created_at_utc)
                VALUES ('code-simple', 'ScheduleI', 'Installed', NULL, 'source', '2026-01-01');
                INSERT INTO symbols(symbol_id, snapshot_id, canonical_key, kind, qualified_name, signature, is_best_effort, simple_name)
                VALUES
                  ('symbol-method', 'code-simple', 'key-method', 'Method', 'A.B.C::Foo(System.Int32):System.Int32', 'System.Int32 A.B.C::Foo(System.Int32)', 0, 'Int32)'),
                  ('symbol-ctor', 'code-simple', 'key-ctor', 'Method', 'Demo.Factory::.ctor()', 'System.Void Demo.Factory::.ctor()', 0, 'ctor()'),
                  ('symbol-field', 'code-simple', 'key-field', 'Field', 'A.B.C::System.Int32 offset', 'System.Int32 A.B.C::offset', 0, 'Int32 offset'),
                  ('symbol-property', 'code-simple', 'key-property', 'Property', 'A.B.C::System.String Name', 'System.String A.B.C::Name', 0, 'String Name'),
                  ('symbol-nested', 'code-simple', 'key-nested', 'Type', 'A.B.C+Inner', 'A.B.C+Inner', 0, 'C+Inner'),
                  ('symbol-plain', 'code-simple', 'key-plain', 'Type', 'Widget', 'Widget', 0, 'Widget');
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups-upgrade"))
            .MigrateAsync(cancellationToken);

        await using var connection = await OpenAsync(cancellationToken);
        Assert.Equal("Foo", await TextAsync(
            connection,
            "SELECT simple_name FROM symbols WHERE symbol_id = 'symbol-method';",
            cancellationToken));
        Assert.Equal(".ctor", await TextAsync(
            connection,
            "SELECT simple_name FROM symbols WHERE symbol_id = 'symbol-ctor';",
            cancellationToken));
        Assert.Equal("offset", await TextAsync(
            connection,
            "SELECT simple_name FROM symbols WHERE symbol_id = 'symbol-field';",
            cancellationToken));
        Assert.Equal("Name", await TextAsync(
            connection,
            "SELECT simple_name FROM symbols WHERE symbol_id = 'symbol-property';",
            cancellationToken));
        Assert.Equal("Inner", await TextAsync(
            connection,
            "SELECT simple_name FROM symbols WHERE symbol_id = 'symbol-nested';",
            cancellationToken));
        Assert.Equal("Widget", await TextAsync(
            connection,
            "SELECT simple_name FROM symbols WHERE symbol_id = 'symbol-plain';",
            cancellationToken));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM symbols_fts WHERE symbols_fts MATCH '{simple_name} : \"Foo\"';",
            cancellationToken));

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'symbols_fts_au';";
            var triggerSql = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
            Assert.Equal(NormalizeSql(ExpectedUpdateTriggerSql), NormalizeSql(triggerSql));
        }

        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM schema_migrations WHERE version = 18 AND name = 'simple-name-correction-v18';",
            cancellationToken));
    }

    private const string ExpectedUpdateTriggerSql = """
        CREATE TRIGGER symbols_fts_au AFTER UPDATE ON symbols BEGIN
            INSERT INTO symbols_fts(symbols_fts, rowid, qualified_name, simple_name, signature)
            VALUES ('delete', old.rowid, old.qualified_name, old.simple_name, old.signature);
            INSERT INTO symbols_fts(rowid, qualified_name, simple_name, signature)
            VALUES (new.rowid, new.qualified_name, new.simple_name, new.signature);
        END
        """;

    private static string NormalizeSql(string? sql) =>
        string.Join(" ", (sql ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<string?> TextAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        TestDirectory.DeleteTree(_root);
        return ValueTask.CompletedTask;
    }
}
