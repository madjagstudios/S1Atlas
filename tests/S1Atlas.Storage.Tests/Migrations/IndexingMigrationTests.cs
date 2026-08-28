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
    public async Task Fresh_database_migrates_to_v11_relationship_query_schema()
    {
        await new SqliteMigrationRunner(_databasePath, Path.Combine(_root, "backups")).MigrateAsync(
            TestContext.Current.CancellationToken);

        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            Assert.Equal(11L, await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(11, SqliteMigrations.All.Count);
            foreach (var table in new[] { "code_snapshots", "index_runs", "symbols", "source_files", "source_locations", "symbol_fingerprints", "relationships", "upstream_repositories", "upstream_snapshots", "upstream_state" })
                Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name=$name;", ("$name", table)));
            Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('symbols') WHERE name='body_recovery_status';"));
            Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('symbols') WHERE name='is_public';"));
            Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='callable_surface';"));
            foreach (var table in new[] { "reference_index_context", "reference_mods", "reference_documents", "reference_symbol_owners" })
                Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name=$name;", ("$name", table)));
            foreach (var table in new[] { "scene_snapshots", "scene_containers", "scenes", "game_objects", "transforms", "components", "serialized_refs" })
                Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name=$name;", ("$name", table)));
            Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('scene_snapshots') WHERE name='published_at_utc';"));
            foreach (var index in new[] { "ix_relationships_snapshot_kind_target_text", "ix_scene_snapshots_build_status_completed", "ix_scene_containers_snapshot_path", "ix_scenes_snapshot_kind_name", "ix_game_objects_scene_name", "ix_game_objects_snapshot_name", "ux_game_objects_snapshot_container_local_file", "ix_transforms_parent_game_object", "ix_components_game_object_kind", "ix_components_resolved_type_symbol", "ix_serialized_refs_source_field_path", "ix_serialized_refs_target_game_object", "ix_serialized_refs_target_symbol" })
                Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type='index' AND name=$name;", ("$name", index)));

            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO code_snapshots(snapshot_id, codebase, channel, source_identity, created_at_utc) VALUES ('s', 'ScheduleI', 'Preview', 'x', '2026-01-01');";
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());

            command.CommandText = "INSERT INTO scene_snapshots(scene_snapshot_id, build_id, extraction_id, input_snapshot_id, code_snapshot_id, code_index_id, parser_id, parser_version, container_manifest_digest, status, recovery_status, started_at_utc) VALUES ('scene', 'missing', 'missing', 'missing', 'missing', 'missing', 'parser', '1', 'digest', 'Invalid', 'Unknown', '2026-01-01');";
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }
    }

    [Fact]
    public async Task V10_database_migrates_to_v11_and_adds_target_text_query_index()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new SqliteMigrationRunner(
            _databasePath,
            Path.Combine(_root, "backups-v10"),
            SqliteMigrations.All.Take(10).ToArray()).MigrateAsync(cancellationToken);

        await new SqliteMigrationRunner(_databasePath, Path.Combine(_root, "backups-v11"))
            .MigrateAsync(cancellationToken);

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        Assert.Equal(11L, await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 11 AND name = 'relationship-query-target-text-v11';"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type='index' AND name='ix_relationships_snapshot_kind_target_text';"));
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
