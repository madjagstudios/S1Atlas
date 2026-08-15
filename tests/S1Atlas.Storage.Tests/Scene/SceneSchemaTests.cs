using Microsoft.Data.Sqlite;
using S1Atlas.Storage.Migrations;
using Xunit;

namespace S1Atlas.Storage.Tests.Scene;

public sealed class SceneSchemaTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-scene-schema-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public SceneSchemaTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
    }

    [Fact]
    public async Task Migration_v8_enforces_scene_contract_constraints()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new SqliteMigrationRunner(_databasePath, Path.Combine(_root, "backups"))
            .MigrateAsync(cancellationToken);

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        await SeedAuthoritiesAsync(connection, cancellationToken);

        await ExecuteAsync(connection, "INSERT INTO scene_snapshots(scene_snapshot_id, build_id, extraction_id, input_snapshot_id, code_snapshot_id, code_index_id, parser_id, parser_version, container_manifest_digest, status, recovery_status, started_at_utc) VALUES ('snapshot', 'build', 'extraction', 'input', 'code', 'index', 'parser', '1', 'digest', 'Running', 'FullyRecovered', '2026-01-01');", cancellationToken);
        await AssertSqliteFailureAsync(connection, "UPDATE scene_snapshots SET status = 'Other' WHERE scene_snapshot_id = 'snapshot';", cancellationToken);
        await AssertSqliteFailureAsync(connection, "UPDATE scene_snapshots SET recovery_status = 'Other' WHERE scene_snapshot_id = 'snapshot';", cancellationToken);
        await ExecuteAsync(connection, "INSERT INTO scene_containers(container_id, scene_snapshot_id, relative_path, container_kind, unity_version, serialized_file_version, byte_count, sha256, sidecar_manifest) VALUES ('container', 'snapshot', 'a.assets', 'Assets', '2022.3', 1, 0, 'hash', 'manifest');", cancellationToken);
        await ExecuteAsync(connection, "INSERT INTO scenes(scene_id, scene_snapshot_id, container_id, kind, name, source_local_file_id, object_count, root_count, recovery_status) VALUES ('scene', 'snapshot', 'container', 'Scene', 'Main', 1, 0, 0, 'FullyRecovered');", cancellationToken);
        await AssertSqliteFailureAsync(connection, "UPDATE scenes SET kind = 'Other' WHERE scene_id = 'scene';", cancellationToken);
        await AssertSqliteFailureAsync(connection, "UPDATE scenes SET object_count = -1 WHERE scene_id = 'scene';", cancellationToken);
        await ExecuteAsync(connection, "INSERT INTO game_objects(game_object_id, scene_id, scene_snapshot_id, container_id, local_file_id, name, active, layer, tag, recovery_status) VALUES ('object', 'scene', 'snapshot', 'container', 9223372036854775807, 'Root', 1, 0, 'Untagged', 'FullyRecovered');", cancellationToken);
        await AssertSqliteFailureAsync(connection, "UPDATE game_objects SET active = 2 WHERE game_object_id = 'object';", cancellationToken);
        await ExecuteAsync(connection, "INSERT INTO components(component_id, game_object_id, container_id, local_file_id, unity_class_id, kind, type_resolution_status, recovery_status) VALUES ('component', 'object', 'container', 2, 1, 'Transform', 'NotIndexed', 'FullyRecovered');", cancellationToken);
        await AssertSqliteFailureAsync(connection, "UPDATE components SET type_resolution_status = 'Other' WHERE component_id = 'component';", cancellationToken);
        await AssertSqliteFailureAsync(connection, "INSERT INTO game_objects(game_object_id, scene_id, scene_snapshot_id, container_id, local_file_id, name, active, layer, tag, recovery_status) VALUES ('duplicate', 'scene', 'snapshot', 'container', 9223372036854775807, 'Duplicate', 1, 0, 'Untagged', 'FullyRecovered');", cancellationToken);
    }

    private static async Task SeedAuthoritiesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, "INSERT INTO builds(build_id, game_assembly_sha256, metadata_sha256, first_seen_at_utc, is_valid) VALUES ('build', 'assembly', 'metadata', '2026-01-01', 1); INSERT INTO input_snapshots(input_snapshot_id, build_id, root_path, manifest_digest, created_at_utc, replay_verified) VALUES ('input', 'build', 'root', 'digest', '2026-01-01', 1); INSERT INTO tool_instances(tool_instance_id, tool_name, version_label, platform, trust_level, definition_digest, package_sha256, executable_sha256, observed_path, first_observed_at_utc, last_verified_at_utc, status) VALUES ('tool', 'tool', NULL, 'win', 'test', NULL, NULL, 'hash', 'path', '2026-01-01', '2026-01-01', 'ok'); INSERT INTO extraction_attempts(attempt_id, build_id, profile_id, profile_version, profile_digest, validation_policy_id, validation_policy_version, validation_policy_digest, adapter_version, extraction_schema_version, status, created_at_utc, working_path, stdout_path, stderr_path, stdout_truncated, stderr_truncated, stdout_discarded_bytes, stderr_discarded_bytes, keep_failed_artifacts, discarded_file_count, discarded_byte_count) VALUES ('attempt', 'build', 'profile', 1, 'digest', 'policy', 1, 'digest', 1, 1, 'Completed', '2026-01-01', 'work', 'stdout', 'stderr', 0, 0, 0, 0, 0, 0, 0); INSERT INTO validated_extractions(extraction_id, recipe_id, build_id, tool_instance_id, source_attempt_id, profile_id, profile_version, profile_digest, adapter_version, extraction_schema_version, artifact_manifest_digest, root_path, created_at_utc, trust_level, validation_outcome, artifact_count, library_count, managed_assembly_count, type_count, method_count, field_count, property_count, event_count, total_output_bytes, total_managed_bytes) VALUES ('extraction', 'recipe', 'build', 'tool', 'attempt', 'profile', 1, 'digest', 1, 1, 'digest', 'root', '2026-01-01', 'trusted', 'Passed', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0); INSERT INTO environment_snapshots(snapshot_id, build_id, atlas_version, captured_at_utc, identity_version) VALUES ('environment', 'build', 'atlas', '2026-01-01', 1); INSERT INTO code_snapshots(snapshot_id, codebase, channel, environment_snapshot_id, source_identity, created_at_utc) VALUES ('code', 'ScheduleI', 'Installed', 'environment', 'source', '2026-01-01'); INSERT INTO index_runs(index_id, snapshot_id, status, started_at_utc) VALUES ('index', 'code', 'Completed', '2026-01-01');", cancellationToken);
    }

    private static async Task AssertSqliteFailureAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken) =>
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, sql, cancellationToken));

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
