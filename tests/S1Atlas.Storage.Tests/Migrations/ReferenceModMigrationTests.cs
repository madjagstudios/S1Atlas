using Microsoft.Data.Sqlite;
using S1Atlas.Storage.Migrations;
using Xunit;

namespace S1Atlas.Storage.Tests.Migrations;

public sealed class ReferenceModMigrationTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-reference-mod-migration-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private readonly string _backupDirectory;

    public ReferenceModMigrationTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
        _backupDirectory = Path.Combine(_root, "backups");
    }

    [Fact]
    public async Task Fresh_database_migrates_to_v12_and_preserves_reference_mod_schema()
    {
        await new SqliteMigrationRunner(_databasePath, _backupDirectory).MigrateAsync(
            TestContext.Current.CancellationToken);

        await using var connection = await OpenAsync(SqliteOpenMode.ReadWrite, TestContext.Current.CancellationToken);
        Assert.Equal(12L, await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(12, SqliteMigrations.All.Count);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 10 AND name = 'reference-mods-v10';"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 11 AND name = 'relationship-query-target-text-v11';"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 12 AND name = 'native-evidence-v12';"));
        foreach (var table in new[] { "reference_index_context", "reference_mods", "reference_documents", "reference_symbol_owners" })
            Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = $name;", ("$name", table)));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'ix_relationships_snapshot_kind_target_text';"));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO builds (
                build_id,
                game_assembly_sha256,
                metadata_sha256,
                first_seen_at_utc,
                is_valid)
            VALUES (
                'build-1',
                'assembly-1',
                'metadata-1',
                '2026-08-27T00:00:00.0000000+00:00',
                1);

            INSERT INTO code_snapshots (
                snapshot_id,
                codebase,
                channel,
                environment_snapshot_id,
                source_identity,
                created_at_utc)
            VALUES
                ('snapshot-game', 'ScheduleI', 'Installed', NULL, 'game-extraction', '2026-08-27T00:00:00.0000000+00:00'),
                ('snapshot-ref', 'ReferenceMod', 'Installed', NULL, 'reference-collection', '2026-08-27T00:05:00.0000000+00:00');

            INSERT INTO index_runs (
                index_id,
                snapshot_id,
                status,
                started_at_utc,
                completed_at_utc)
            VALUES
                ('index-game', 'snapshot-game', 'Completed', '2026-08-27T00:00:00.0000000+00:00', '2026-08-27T00:01:00.0000000+00:00'),
                ('index-ref', 'snapshot-ref', 'Completed', '2026-08-27T00:05:00.0000000+00:00', '2026-08-27T00:06:00.0000000+00:00');

            INSERT INTO symbols (
                symbol_id,
                snapshot_id,
                canonical_key,
                kind,
                qualified_name,
                signature,
                is_best_effort,
                is_public)
            VALUES
                ('symbol-game', 'snapshot-game', 'ScheduleI:Installed:Method:Demo.Game::Run()', 'Method', 'Demo.Game.Run', 'System.Void Demo.Game::Run()', 0, 1),
                ('symbol-ref', 'snapshot-ref', 'ReferenceMod:Installed:Method:Demo.Mod::Run()', 'Method', 'Demo.Mod.Run', 'System.Void Demo.Mod::Run()', 0, 1);

            INSERT INTO reference_index_context (
                reference_index_id,
                reference_snapshot_id,
                game_index_id,
                game_snapshot_id,
                build_id)
            VALUES (
                'index-ref',
                'snapshot-ref',
                'index-game',
                'snapshot-game',
                'build-1');

            INSERT INTO reference_mods (
                index_id,
                snapshot_id,
                mod_id,
                display_name,
                version,
                license,
                root_path,
                content_sha256)
            VALUES (
                'index-ref',
                'snapshot-ref',
                'mod-a',
                'Mod A',
                '1.0.0',
                'MIT',
                'C:\Mods\A',
                'mod-content');

            INSERT INTO reference_documents (
                index_id,
                snapshot_id,
                mod_id,
                relative_path,
                kind,
                sha256,
                byte_count,
                content)
            VALUES (
                'index-ref',
                'snapshot-ref',
                'mod-a',
                'README.md',
                'Readme',
                'doc-hash',
                12,
                'hello atlas');

            INSERT INTO reference_symbol_owners (
                index_id,
                snapshot_id,
                symbol_id,
                mod_id)
            VALUES (
                'index-ref',
                'snapshot-ref',
                'symbol-ref',
                'mod-a');
            """,
            TestContext.Current.CancellationToken);

        await using (var duplicateDocument = connection.CreateCommand())
        {
            duplicateDocument.CommandText = """
                INSERT INTO reference_documents (
                    index_id,
                    snapshot_id,
                    mod_id,
                    relative_path,
                    kind,
                    sha256,
                    byte_count,
                    content)
                VALUES (
                    'index-ref',
                    'snapshot-ref',
                    'mod-a',
                    'README.md',
                    'Readme',
                    'doc-hash-2',
                    99,
                    'duplicate');
                """;
            Assert.Throws<SqliteException>(() => duplicateDocument.ExecuteNonQuery());
        }

        await using (var invalidOwner = connection.CreateCommand())
        {
            invalidOwner.CommandText = """
                INSERT INTO reference_symbol_owners (
                    index_id,
                    snapshot_id,
                    symbol_id,
                    mod_id)
                VALUES (
                    'index-ref',
                    'snapshot-ref',
                    'symbol-game',
                    'mod-a');
                """;
            Assert.Throws<SqliteException>(() => invalidOwner.ExecuteNonQuery());
        }

        await using (var invalidChannel = connection.CreateCommand())
        {
            invalidChannel.CommandText = """
                INSERT INTO code_snapshots (
                    snapshot_id,
                    codebase,
                    channel,
                    environment_snapshot_id,
                    source_identity,
                    created_at_utc)
                VALUES (
                    'snapshot-preview',
                    'ReferenceMod',
                    'Preview',
                    NULL,
                    'preview-source',
                    '2026-08-27T00:10:00.0000000+00:00');
                """;
            Assert.Throws<SqliteException>(() => invalidChannel.ExecuteNonQuery());
        }
    }

    [Fact]
    public async Task ReferenceMigration_PreservesPopulatedV9DatabaseAndForeignKeys()
    {
        await CreatePopulatedV9DatabaseAsync();

        await new SqliteMigrationRunner(_databasePath, _backupDirectory, SqliteMigrations.All)
            .MigrateAsync(TestContext.Current.CancellationToken);

        await using var connection = await OpenAsync(SqliteOpenMode.ReadOnly, TestContext.Current.CancellationToken);
        Assert.Equal(1, await QueryIntAsync(connection, "SELECT COUNT(*) FROM builds WHERE build_id = 'build-legacy';"));
        Assert.Equal(1, await QueryIntAsync(connection, "SELECT COUNT(*) FROM environment_snapshots WHERE snapshot_id = 'env-legacy';"));
        Assert.Equal(1, await QueryIntAsync(connection, "SELECT COUNT(*) FROM tool_instances WHERE tool_instance_id = 'tool-1';"));
        Assert.Equal(1, await QueryIntAsync(connection, "SELECT COUNT(*) FROM input_snapshots WHERE input_snapshot_id = 'input-1';"));
        Assert.Equal(1, await QueryIntAsync(connection, "SELECT COUNT(*) FROM extraction_attempts WHERE attempt_id = 'attempt-1';"));
        Assert.Equal(1, await QueryIntAsync(connection, "SELECT COUNT(*) FROM validated_extractions WHERE extraction_id = 'extraction-1';"));
        Assert.Equal(1, await QueryIntAsync(connection, "SELECT COUNT(*) FROM code_snapshots;"));
        Assert.Equal(1, await QueryIntAsync(connection, "SELECT COUNT(*) FROM index_runs WHERE index_id = 'index-1';"));
        Assert.Equal(1, await QueryIntAsync(connection, "SELECT COUNT(*) FROM symbols;"));
        Assert.Equal(1, await QueryIntAsync(connection, "SELECT COUNT(*) FROM source_files WHERE source_file_id = 'source-1';"));
        Assert.Equal(1, await QueryIntAsync(connection, "SELECT COUNT(*) FROM source_locations WHERE symbol_id = 'symbol-1';"));
        Assert.Equal(1, await QueryIntAsync(connection, "SELECT COUNT(*) FROM symbol_fingerprints WHERE symbol_id = 'symbol-1';"));
        Assert.Equal(1, await QueryIntAsync(connection, "SELECT COUNT(*) FROM relationships;"));
        Assert.Equal(1, await QueryIntAsync(connection, "SELECT COUNT(*) FROM scene_snapshots WHERE scene_snapshot_id = 'scene-snapshot-1';"));
        Assert.Equal(1, await QueryIntAsync(connection, "SELECT COUNT(*) FROM scene_containers WHERE container_id = 'container-1';"));
        Assert.Equal(1, await QueryIntAsync(connection, "SELECT COUNT(*) FROM scenes;"));
        Assert.Equal(0, await QueryIntAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task ReferenceMigration_RollsBackSchemaWhenLedgerInsertFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new SqliteMigrationRunner(
            _databasePath,
            _backupDirectory,
            SqliteMigrations.All.Take(9).ToArray())
            .MigrateAsync(cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteMigrationRunner(
                _databasePath,
                _backupDirectory,
                SqliteMigrations.All.Take(9).Append(
                    new SqliteMigration(
                        10,
                        "reference-mods-v10-test-failure",
                        """
                        PRAGMA foreign_keys = OFF;
                        BEGIN IMMEDIATE;
                        CREATE TABLE atomicity_probe (value TEXT NOT NULL);
                        INSERT INTO atomicity_probe(value) VALUES ('schema-work');
                        CREATE TRIGGER fail_reference_migration_ledger
                        BEFORE INSERT ON schema_migrations
                        WHEN NEW.version = 10
                        BEGIN
                            SELECT RAISE(ABORT, 'deterministic ledger failure');
                        END;
                        /* S1ATLAS_MIGRATION_LEDGER */
                        COMMIT;
                        PRAGMA foreign_keys = ON;
                        """,
                        RequiresTransaction: false)).ToArray())
                .MigrateAsync(cancellationToken));

        await using var connection = await OpenAsync(SqliteOpenMode.ReadOnly, cancellationToken);
        Assert.Equal(9L, await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'atomicity_probe';"));
        Assert.Equal(0, await QueryIntAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    private async Task CreatePopulatedV9DatabaseAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await new SqliteMigrationRunner(
            _databasePath,
            _backupDirectory,
            SqliteMigrations.All.Take(9).ToArray()).MigrateAsync(cancellationToken);

        await using var connection = await OpenAsync(SqliteOpenMode.ReadWrite, cancellationToken);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO builds (
                build_id,
                game_assembly_sha256,
                metadata_sha256,
                first_seen_at_utc,
                is_valid)
            VALUES (
                'build-legacy',
                'assembly-legacy',
                'metadata-legacy',
                '2026-08-27T00:00:00.0000000+00:00',
                1);

            INSERT INTO environment_snapshots (
                snapshot_id,
                build_id,
                atlas_version,
                captured_at_utc,
                identity_version,
                executable_version,
                steam_app_id,
                steam_build_id,
                installation_root,
                game_assembly_path,
                global_metadata_path)
            VALUES (
                'env-legacy',
                'build-legacy',
                '0.3.0-test',
                '2026-08-27T00:00:00.0000000+00:00',
                2,
                '1.0.0',
                '3164500',
                '9001',
                'C:\Game',
                'C:\Game\GameAssembly.dll',
                'C:\Game\global-metadata.dat');

            INSERT INTO tool_instances (
                tool_instance_id,
                tool_name,
                version_label,
                platform,
                trust_level,
                definition_digest,
                package_sha256,
                executable_sha256,
                observed_path,
                first_observed_at_utc,
                last_verified_at_utc,
                status)
            VALUES (
                'tool-1',
                'cpp2il',
                'test',
                'win-x64',
                'RepositoryManaged',
                'definition-1',
                'package-1',
                'exe-1',
                'C:\tools\cpp2il.exe',
                '2026-08-27T00:00:00.0000000+00:00',
                '2026-08-27T00:00:00.0000000+00:00',
                'Verified');

            INSERT INTO input_snapshots (
                input_snapshot_id,
                build_id,
                root_path,
                manifest_digest,
                created_at_utc,
                replay_verified,
                replay_verified_at_utc)
            VALUES (
                'input-1',
                'build-legacy',
                'C:\input',
                'manifest-1',
                '2026-08-27T00:00:00.0000000+00:00',
                1,
                '2026-08-27T00:00:00.0000000+00:00');

            INSERT INTO extraction_attempts (
                attempt_id,
                recipe_id,
                build_id,
                tool_instance_id,
                profile_id,
                profile_version,
                profile_digest,
                validation_policy_id,
                validation_policy_version,
                validation_policy_digest,
                adapter_version,
                extraction_schema_version,
                input_source,
                input_snapshot_id,
                status,
                created_at_utc,
                started_at_utc,
                completed_at_utc,
                working_path,
                stdout_path,
                stderr_path,
                stdout_truncated,
                stderr_truncated,
                stdout_discarded_bytes,
                stderr_discarded_bytes,
                keep_failed_artifacts,
                discarded_file_count,
                discarded_byte_count)
            VALUES (
                'attempt-1',
                'recipe-1',
                'build-legacy',
                'tool-1',
                'profile-1',
                1,
                'profile-digest-1',
                'policy-1',
                1,
                'policy-digest-1',
                1,
                1,
                'LiveInstall',
                'input-1',
                'Completed',
                '2026-08-27T00:00:00.0000000+00:00',
                '2026-08-27T00:00:01.0000000+00:00',
                '2026-08-27T00:00:02.0000000+00:00',
                'C:\work',
                'C:\logs\stdout.txt',
                'C:\logs\stderr.txt',
                0,
                0,
                0,
                0,
                0,
                0,
                0);

            INSERT INTO validated_extractions (
                extraction_id,
                recipe_id,
                build_id,
                tool_instance_id,
                source_attempt_id,
                profile_id,
                profile_version,
                profile_digest,
                adapter_version,
                extraction_schema_version,
                artifact_manifest_digest,
                root_path,
                created_at_utc,
                trust_level,
                validation_outcome,
                artifact_count,
                library_count,
                managed_assembly_count,
                type_count,
                method_count,
                field_count,
                property_count,
                event_count,
                total_output_bytes,
                total_managed_bytes)
            VALUES (
                'extraction-1',
                'recipe-1',
                'build-legacy',
                'tool-1',
                'attempt-1',
                'profile-1',
                1,
                'profile-digest-1',
                1,
                1,
                'artifact-manifest-1',
                'C:\extraction',
                '2026-08-27T00:00:03.0000000+00:00',
                'RepositoryManaged',
                'Passed',
                1,
                1,
                1,
                1,
                1,
                0,
                0,
                0,
                128,
                128);

            INSERT INTO code_snapshots (
                snapshot_id,
                codebase,
                channel,
                environment_snapshot_id,
                source_identity,
                created_at_utc)
            VALUES (
                'snapshot-1',
                'ScheduleI',
                'Installed',
                'env-legacy',
                'extraction-1',
                '2026-08-27T00:00:04.0000000+00:00');

            INSERT INTO index_runs (
                index_id,
                snapshot_id,
                status,
                started_at_utc,
                completed_at_utc)
            VALUES (
                'index-1',
                'snapshot-1',
                'Completed',
                '2026-08-27T00:00:04.0000000+00:00',
                '2026-08-27T00:00:05.0000000+00:00');

            INSERT INTO symbols (
                symbol_id,
                snapshot_id,
                canonical_key,
                kind,
                qualified_name,
                signature,
                is_best_effort,
                body_recovery_status,
                is_public)
            VALUES (
                'symbol-1',
                'snapshot-1',
                'ScheduleI:Installed:Method:Demo.Widget::Run()',
                'Method',
                'Demo.Widget.Run',
                'System.Void Demo.Widget::Run()',
                0,
                'Recovered',
                1);

            INSERT INTO source_files (
                source_file_id,
                snapshot_id,
                relative_path,
                sha256,
                byte_count)
            VALUES (
                'source-1',
                'snapshot-1',
                'Assets/Scripts/Demo/Widget.cs',
                'source-hash-1',
                64);

            INSERT INTO source_locations (
                symbol_id,
                source_file_id,
                start_line,
                start_column,
                end_line,
                end_column)
            VALUES (
                'symbol-1',
                'source-1',
                1,
                1,
                10,
                2);

            INSERT INTO symbol_fingerprints (
                symbol_id,
                fingerprint_kind,
                fingerprint)
            VALUES (
                'symbol-1',
                'IL',
                'fingerprint-1');

            INSERT INTO relationships (
                relationship_id,
                snapshot_id,
                source_symbol_id,
                target_symbol_id,
                target_text,
                relationship_kind,
                evidence)
            VALUES (
                'relationship-1',
                'snapshot-1',
                'symbol-1',
                'symbol-1',
                NULL,
                'Calls',
                'IL:call');

            INSERT INTO scene_snapshots (
                scene_snapshot_id,
                build_id,
                extraction_id,
                input_snapshot_id,
                code_snapshot_id,
                code_index_id,
                parser_id,
                parser_version,
                container_manifest_digest,
                status,
                recovery_status,
                started_at_utc,
                completed_at_utc,
                published_at_utc)
            VALUES (
                'scene-snapshot-1',
                'build-legacy',
                'extraction-1',
                'input-1',
                'snapshot-1',
                'index-1',
                'scene-parser',
                '1.0.0',
                'container-manifest-1',
                'Completed',
                'FullyRecovered',
                '2026-08-27T00:00:06.0000000+00:00',
                '2026-08-27T00:00:07.0000000+00:00',
                '2026-08-27T00:00:08.0000000+00:00');

            INSERT INTO scene_containers (
                container_id,
                scene_snapshot_id,
                relative_path,
                container_kind,
                unity_version,
                serialized_file_version,
                byte_count,
                sha256,
                sidecar_manifest)
            VALUES (
                'container-1',
                'scene-snapshot-1',
                'Scenes/Main.unity',
                'SerializedFile',
                '2022.3.0f1',
                22,
                512,
                'container-sha',
                '{}');

            INSERT INTO scenes (
                scene_id,
                scene_snapshot_id,
                container_id,
                kind,
                name,
                source_local_file_id,
                object_count,
                root_count,
                recovery_status)
            VALUES (
                'scene-1',
                'scene-snapshot-1',
                'container-1',
                'Scene',
                'Main',
                1,
                2,
                1,
                'FullyRecovered');
            """,
            cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = mode,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<int> QueryIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
