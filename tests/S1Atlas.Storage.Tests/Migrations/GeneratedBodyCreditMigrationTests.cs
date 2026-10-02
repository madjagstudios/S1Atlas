using Microsoft.Data.Sqlite;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Migrations;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Storage.Tests.Migrations;

public sealed class GeneratedBodyCreditMigrationTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-generated-credit-migration-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public GeneratedBodyCreditMigrationTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
    }

    [Fact]
    public async Task FreshDatabase_CreatesCreditColumns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups"))
            .MigrateAsync(cancellationToken);

        await using var connection = await OpenAsync(_databasePath, cancellationToken);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM schema_migrations WHERE version = 17 AND name = 'generated-body-credit-v17';",
            cancellationToken));
        var symbolColumns = await ReadColumnNamesAsync(connection, "symbols", cancellationToken);
        Assert.Contains("is_generated", symbolColumns);
        var relationshipColumns = await ReadColumnNamesAsync(connection, "relationships", cancellationToken);
        Assert.Contains("generated_source_symbol_id", relationshipColumns);
        Assert.Contains("generated_detail", relationshipColumns);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'ix_symbols_generated';",
            cancellationToken));
    }

    [Fact]
    public async Task UpgradeFromSixteen_BackfillsIsGenerated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups"),
            SqliteMigrations.All.Take(16).ToArray())
            .MigrateAsync(cancellationToken);

        await using (var seed = await OpenAsync(_databasePath, cancellationToken))
        {
            await using var command = seed.CreateCommand();
            command.CommandText = """
                INSERT INTO code_snapshots(snapshot_id, codebase, channel, environment_snapshot_id, source_identity, created_at_utc)
                VALUES ('code-gen', 'ScheduleI', 'Installed', NULL, 'source', '2026-01-01');
                INSERT INTO symbols(symbol_id, snapshot_id, canonical_key, kind, qualified_name, signature, is_best_effort)
                VALUES
                  ('symbol-lambda', 'code-gen', 'key-lambda', 'Method', 'A.B.C+<>c::<Foo>b__0_0():System.Void', 'sig', 0),
                  ('symbol-move', 'code-gen', 'key-move', 'Method', 'A.B.C+<Foo>d__1::MoveNext():System.Void', 'sig', 0),
                  ('symbol-field', 'code-gen', 'key-field', 'Field', 'A.B.C+<>c__DisplayClass0_0::System.Int32 x', 'sig', 0),
                  ('symbol-plain', 'code-gen', 'key-plain', 'Method', 'A.B.C::Foo():System.Void', 'sig', 0),
                  ('symbol-generic', 'code-gen', 'key-generic', 'Method', 'A.B.C::Foo():System.Collections.Generic.List`1<System.Int32>', 'sig', 0),
                  ('symbol-top', 'code-gen', 'key-top', 'Type', '<PrivateImplementationDetails>', '<PrivateImplementationDetails>', 0),
                  ('symbol-mod', 'code-gen', 'key-mod', 'Method', 'mod/A.B.C+<>c::<Foo>b__0_0():System.Void', 'sig', 0);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups-upgrade"))
            .MigrateAsync(cancellationToken);

        await using var connection = await OpenAsync(_databasePath, cancellationToken);
        Assert.Equal(1L, await ScalarAsync(
            connection, "SELECT is_generated FROM symbols WHERE symbol_id = 'symbol-lambda';", cancellationToken));
        Assert.Equal(1L, await ScalarAsync(
            connection, "SELECT is_generated FROM symbols WHERE symbol_id = 'symbol-move';", cancellationToken));
        Assert.Equal(1L, await ScalarAsync(
            connection, "SELECT is_generated FROM symbols WHERE symbol_id = 'symbol-field';", cancellationToken));
        Assert.Equal(0L, await ScalarAsync(
            connection, "SELECT is_generated FROM symbols WHERE symbol_id = 'symbol-plain';", cancellationToken));
        Assert.Equal(0L, await ScalarAsync(
            connection, "SELECT is_generated FROM symbols WHERE symbol_id = 'symbol-generic';", cancellationToken));
        Assert.Equal(1L, await ScalarAsync(
            connection, "SELECT is_generated FROM symbols WHERE symbol_id = 'symbol-top';", cancellationToken));
        Assert.Equal(1L, await ScalarAsync(
            connection, "SELECT is_generated FROM symbols WHERE symbol_id = 'symbol-mod';", cancellationToken));
    }

    [Fact]
    public async Task Repository_RoundtripsCreditColumns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(_root, "roundtrip.db");
        var repository = new SqliteAtlasRepository(databasePath);
        await repository.InitializeAsync(cancellationToken);
        await repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord("snap-1", CodebaseKind.ScheduleI, CodeChannel.Installed, "source", "2026-01-01T00:00:00Z"),
            cancellationToken);
        await repository.StartIndexRunAsync(
            new IndexRunRecord("index-1", "snap-1", IndexRunStatus.Running, "2026-01-01T00:00:00Z"),
            cancellationToken);
        var foo = new IndexSymbolRecord("id-foo", "snap-1", "key-foo", "Method", "A::Foo():System.Void", "sig", false);
        var lambda = new IndexSymbolRecord(
            "id-lambda", "snap-1", "key-lambda", "Method", "A+<>c::<Foo>b__0_0():System.Void", "sig", false,
            IsGenerated: true);
        var leaf = new IndexSymbolRecord("id-leaf", "snap-1", "key-leaf", "Method", "A::Leaf():System.Void", "sig", false);
        await repository.CompleteIndexRunAsync(
            "index-1",
            new IndexWriteSet(
                [foo, lambda, leaf],
                [],
                [],
                [],
                [
                    new IndexRelationshipRecord(
                        "rel-1", "snap-1", "id-foo", "id-leaf", "leaf-text", "Calls", "Body",
                        GeneratedSourceSymbolId: "id-lambda", GeneratedDetail: "in lambda")
                ]),
            "2026-01-01T01:00:00Z",
            cancellationToken);

        var symbols = await repository.GetCompletedSymbolsByIdsAsync("index-1", ["id-foo", "id-lambda"], cancellationToken);
        Assert.True(symbols.Single(symbol => symbol.SymbolId == "id-lambda").IsGenerated);
        Assert.False(symbols.Single(symbol => symbol.SymbolId == "id-foo").IsGenerated);
        var edges = await repository.GetCompletedRelationshipsBySourceSymbolIdAsync("index-1", "id-foo", cancellationToken);
        var edge = Assert.Single(edges);
        Assert.Equal("id-lambda", edge.GeneratedSourceSymbolId);
        Assert.Equal("in lambda", edge.GeneratedDetail);
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
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
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        TestDirectory.DeleteTree(_root);
        return ValueTask.CompletedTask;
    }
}
