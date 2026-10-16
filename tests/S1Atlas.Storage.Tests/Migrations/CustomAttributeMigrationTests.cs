using Microsoft.Data.Sqlite;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Migrations;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Storage.Tests.Migrations;

public sealed class CustomAttributeMigrationTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-custom-attribute-migration-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public CustomAttributeMigrationTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
    }

    [Fact]
    public async Task FreshDatabase_CreatesCustomAttributesTableAndIndexes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups"))
            .MigrateAsync(cancellationToken);

        await using var connection = await OpenAsync(cancellationToken);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM schema_migrations WHERE version = 21 AND name = 'custom-attributes-v21';",
            cancellationToken));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'custom_attributes';",
            cancellationToken));
        Assert.Equal(
            ["attribute_id", "snapshot_id", "symbol_id", "target", "parameter_index", "parameter_name", "attribute_type", "args_json"],
            await ReadColumnNamesAsync(connection, "custom_attributes", cancellationToken));

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'custom_attributes';";
            var createSql = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
            Assert.Contains("target IN ('Type', 'Member', 'Parameter')", createSql, StringComparison.Ordinal);
            Assert.Contains("REFERENCES symbols(symbol_id)", createSql, StringComparison.Ordinal);
            Assert.Contains("ON DELETE CASCADE", createSql, StringComparison.Ordinal);
        }

        foreach (var index in new[] { "ix_custom_attributes_snapshot_type", "ix_custom_attributes_symbol" })
        {
            Assert.Equal(1L, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = $name;",
                cancellationToken,
                ("$name", index)));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'ix_custom_attributes_snapshot_type';";
            var indexSql = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
            Assert.Contains("snapshot_id", indexSql, StringComparison.Ordinal);
            Assert.Contains("attribute_type", indexSql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UpgradeFromTwenty_PreservesExistingRowsAndPersistsAttributes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups"),
            SqliteMigrations.All.Take(20).ToArray())
            .MigrateAsync(cancellationToken);

        var repository = new SqliteAtlasRepository(_databasePath);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-legacy", CodebaseKind.ScheduleI, CodeChannel.Installed,
            "source-legacy", "2026-10-15T00:00:00Z");
        var symbol = new IndexSymbolRecord(
            "symbol-legacy", snapshot.SnapshotId, "key-legacy", "Method",
            "Demo.Widget::Legacy()", "System.Void Demo.Widget::Legacy()", false);
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await repository.StartIndexRunAsync(new IndexRunRecord(
            "index-legacy", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc), cancellationToken);
        await repository.CompleteIndexRunAsync(
            "index-legacy",
            new IndexWriteSet([symbol], [], [], [], []),
            "2026-10-15T00:01:00Z", cancellationToken);

        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups-upgrade"))
            .MigrateAsync(cancellationToken);

        Assert.Equal(symbol, Assert.Single(
            await repository.GetCompletedSymbolsAsync("index-legacy", cancellationToken)));

        var newSnapshot = snapshot with { SnapshotId = "snapshot-new", SourceIdentity = "source-new" };
        var newSymbol = symbol with { SymbolId = "symbol-new", SnapshotId = newSnapshot.SnapshotId };
        var attribute = new IndexCustomAttributeRecord(
            "attribute-new", newSnapshot.SnapshotId, newSymbol.SymbolId, "Member",
            null, null, "UnityEngine.SerializeField", """{"type":"UnityEngine.SerializeField","fixed":[],"named":[]}""");
        await repository.CreateCodeSnapshotAsync(newSnapshot, cancellationToken);
        await repository.StartIndexRunAsync(new IndexRunRecord(
            "index-new", newSnapshot.SnapshotId, IndexRunStatus.Running, "2026-10-15T00:02:00Z"), cancellationToken);
        await repository.CompleteIndexRunAsync(
            "index-new",
            new IndexWriteSet([newSymbol], [], [], [], [], CustomAttributes: [attribute]),
            "2026-10-15T00:03:00Z", cancellationToken);

        Assert.Equal(attribute, Assert.Single(
            await repository.GetCompletedAttributesBySymbolIdsAsync("index-new", [newSymbol.SymbolId], cancellationToken)));
        var readOnly = new ReadOnlySqliteAtlasRepository(new ReadOnlySqliteConnectionFactory(_databasePath));
        Assert.Equal(attribute, Assert.Single(
            await readOnly.GetCompletedAttributesBySymbolIdsAsync("index-new", [newSymbol.SymbolId], cancellationToken)));
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

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        TestDirectory.DeleteTree(_root);
        return ValueTask.CompletedTask;
    }
}
