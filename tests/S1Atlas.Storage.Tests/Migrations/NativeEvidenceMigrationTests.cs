using Microsoft.Data.Sqlite;
using S1Atlas.Storage.Migrations;
using Xunit;

namespace S1Atlas.Storage.Tests.Migrations;

public sealed class NativeEvidenceMigrationTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-native-evidence-migration-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public NativeEvidenceMigrationTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
    }

    [Fact]
    public async Task FreshDatabase_AppliesNativeEvidenceSchemaWithoutArtifactColumns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups"))
            .MigrateAsync(cancellationToken);

        await using var connection = await OpenAsync(cancellationToken);

        Assert.Equal(12L, await ScalarAsync(
            connection,
            "SELECT MAX(version) FROM schema_migrations;",
            cancellationToken));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM schema_migrations WHERE version = 12 AND name = 'native-evidence-v12';",
            cancellationToken));

        foreach (var table in new[]
                 {
                     "native_recovery_runs",
                     "native_recovery_edges",
                     "native_recovery_fields"
                 })
        {
            Assert.Equal(1L, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = $name;",
                cancellationToken,
                ("$name", table)));
        }

        foreach (var index in new[]
                 {
                     "ix_native_recovery_runs_input",
                     "ix_native_recovery_edges_recovery_edge"
                 })
        {
            Assert.Equal(1L, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = $name;",
                cancellationToken,
                ("$name", index)));
        }

        var runColumns = await ReadColumnNamesAsync(
            connection,
            "native_recovery_runs",
            cancellationToken);
        Assert.Contains("recovery_id", runColumns);
        Assert.Contains("build_id", runColumns);
        Assert.Contains("index_id", runColumns);
        Assert.Contains("game_assembly_sha256", runColumns);
        Assert.Contains("symbol_ids_json", runColumns);
        Assert.Contains("max_traversal_edges", runColumns);
        Assert.Contains("mapping_evidence_json", runColumns);
        Assert.DoesNotContain(
            runColumns,
            column => column.Contains("body", StringComparison.OrdinalIgnoreCase) ||
                      column.Contains("disassembly", StringComparison.OrdinalIgnoreCase) ||
                      column.Contains("artifact", StringComparison.OrdinalIgnoreCase) ||
                      column.Contains("path", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(0L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_check;",
            cancellationToken));
    }

    [Fact]
    public async Task V11Database_MigratesToNativeEvidenceSchemaAndPreservesExistingRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups-v11"),
            SqliteMigrations.All.Take(11).ToArray())
            .MigrateAsync(cancellationToken);

        await using (var connection = await OpenAsync(cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO code_snapshots (
                    snapshot_id,
                    codebase,
                    channel,
                    source_identity,
                    created_at_utc)
                VALUES (
                    'snapshot-existing',
                    'ScheduleI',
                    'Installed',
                    'existing-source',
                    '2026-08-30T00:00:00.0000000+00:00');

                INSERT INTO index_runs (
                    index_id,
                    snapshot_id,
                    status,
                    started_at_utc,
                    completed_at_utc)
                VALUES (
                    'index-existing',
                    'snapshot-existing',
                    'Completed',
                    '2026-08-30T00:00:00.0000000+00:00',
                    '2026-08-30T00:01:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups-v12"))
            .MigrateAsync(cancellationToken);

        await using var migrated = await OpenAsync(cancellationToken);
        Assert.Equal(12L, await ScalarAsync(
            migrated,
            "SELECT MAX(version) FROM schema_migrations;",
            cancellationToken));
        Assert.Equal(1L, await ScalarAsync(
            migrated,
            "SELECT COUNT(*) FROM index_runs WHERE index_id = 'index-existing' AND status = 'Completed';",
            cancellationToken));
        Assert.Equal(0L, await ScalarAsync(
            migrated,
            "SELECT COUNT(*) FROM pragma_foreign_key_check;",
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

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
