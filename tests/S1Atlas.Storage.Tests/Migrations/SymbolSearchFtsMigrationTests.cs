using Microsoft.Data.Sqlite;
using S1Atlas.Storage.Migrations;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Storage.Tests.Migrations;

public sealed class SymbolSearchFtsMigrationTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-symbol-fts-migration-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public SymbolSearchFtsMigrationTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
    }

    [Fact]
    public async Task FreshDatabase_CreatesSymbolSearchIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups"))
            .MigrateAsync(cancellationToken);

        await using var connection = await OpenAsync(cancellationToken);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM schema_migrations WHERE version = 16 AND name = 'symbol-search-fts-v16';",
            cancellationToken));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'symbols_fts';",
            cancellationToken));

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'symbols_fts';";
            var createSql = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
            Assert.Contains("tokenize='trigram'", createSql, StringComparison.Ordinal);
            Assert.Contains("content='symbols'", createSql, StringComparison.Ordinal);
        }

        foreach (var trigger in new[] { "symbols_fts_ai", "symbols_fts_ad", "symbols_fts_au" })
        {
            Assert.Equal(1L, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'trigger' AND name = $name;",
                cancellationToken,
                ("$name", trigger)));
        }

        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'ix_symbols_qualified_name';",
            cancellationToken));
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'ix_symbols_qualified_name';";
            Assert.Contains(
                "COLLATE NOCASE",
                Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)),
                StringComparison.Ordinal);
        }
        var symbolColumns = await ReadColumnNamesAsync(connection, "symbols", cancellationToken);
        Assert.Contains("simple_name", symbolColumns);
    }

    [Fact]
    public async Task UpgradeFromFifteen_BackfillsSimpleNamesAndSearchIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups"),
            SqliteMigrations.All.Take(15).ToArray())
            .MigrateAsync(cancellationToken);

        await using (var seed = await OpenAsync(cancellationToken))
        {
            await using var command = seed.CreateCommand();
            command.CommandText = """
                INSERT INTO code_snapshots(snapshot_id, codebase, channel, environment_snapshot_id, source_identity, created_at_utc)
                VALUES ('code-fts', 'ScheduleI', 'Installed', NULL, 'source', '2026-01-01');
                INSERT INTO symbols(symbol_id, snapshot_id, canonical_key, kind, qualified_name, signature, is_best_effort)
                VALUES
                  ('symbol-dotted', 'code-fts', 'key-dotted', 'Method', 'Demo.Widget.Run', 'System.Void Demo.Widget::Run()', 0),
                  ('symbol-plain', 'code-fts', 'key-plain', 'Type', 'Widget', 'Widget', 0);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups-upgrade"))
            .MigrateAsync(cancellationToken);

        await using var connection = await OpenAsync(cancellationToken);
        Assert.Equal("Run", await TextAsync(
            connection,
            "SELECT simple_name FROM symbols WHERE symbol_id = 'symbol-dotted';",
            cancellationToken));
        Assert.Equal("Widget", await TextAsync(
            connection,
            "SELECT simple_name FROM symbols WHERE symbol_id = 'symbol-plain';",
            cancellationToken));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM symbols_fts WHERE symbols_fts MATCH '\"get.R\"';",
            cancellationToken));
        Assert.Equal(2L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM symbols_fts WHERE symbols_fts MATCH '\"Widget\"';",
            cancellationToken));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid;";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
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
