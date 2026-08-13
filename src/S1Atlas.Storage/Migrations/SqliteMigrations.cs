namespace S1Atlas.Storage.Migrations;

internal static class SqliteMigrations
{
    private const string FoundationV1Sql = """
        CREATE TABLE builds (
            build_id TEXT NOT NULL PRIMARY KEY,
            game_version TEXT NULL,
            steam_build_id TEXT NULL,
            game_assembly_sha256 TEXT NOT NULL,
            metadata_sha256 TEXT NOT NULL,
            scanned_at_utc TEXT NOT NULL,
            is_valid INTEGER NOT NULL CHECK (is_valid IN (0, 1))
        );

        CREATE TABLE environment_snapshots (
            snapshot_id TEXT NOT NULL PRIMARY KEY,
            build_id TEXT NOT NULL,
            atlas_version TEXT NOT NULL,
            captured_at_utc TEXT NOT NULL,
            FOREIGN KEY (build_id) REFERENCES builds(build_id)
        );

        CREATE INDEX ix_environment_snapshots_build_id
        ON environment_snapshots(build_id);

        CREATE TABLE dependencies (
            snapshot_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            kind TEXT NOT NULL,
            version TEXT NULL,
            path TEXT NULL,
            is_installed INTEGER NOT NULL CHECK (is_installed IN (0, 1)),
            PRIMARY KEY (snapshot_id, ordinal),
            FOREIGN KEY (snapshot_id)
                REFERENCES environment_snapshots(snapshot_id)
                ON DELETE CASCADE
        );

        CREATE INDEX ix_dependencies_snapshot_kind
        ON dependencies(snapshot_id, kind);

        CREATE TABLE atlas_state (
            singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
            current_snapshot_id TEXT NULL,
            FOREIGN KEY (current_snapshot_id)
                REFERENCES environment_snapshots(snapshot_id)
        );

        INSERT OR IGNORE INTO atlas_state (singleton_id, current_snapshot_id)
        VALUES (1, NULL);
        """;

    private const string EnvironmentObservationsV2Sql = """
        ALTER TABLE builds
        RENAME COLUMN scanned_at_utc TO first_seen_at_utc;

        ALTER TABLE environment_snapshots
        ADD COLUMN identity_version INTEGER NOT NULL DEFAULT 1
        CHECK (identity_version > 0);

        ALTER TABLE environment_snapshots
        ADD COLUMN executable_version TEXT NULL;

        ALTER TABLE environment_snapshots
        ADD COLUMN steam_app_id TEXT NULL;

        ALTER TABLE environment_snapshots
        ADD COLUMN steam_build_id TEXT NULL;

        ALTER TABLE environment_snapshots
        ADD COLUMN installation_root TEXT NULL;

        ALTER TABLE environment_snapshots
        ADD COLUMN game_assembly_path TEXT NULL;

        ALTER TABLE environment_snapshots
        ADD COLUMN global_metadata_path TEXT NULL;

        UPDATE environment_snapshots
        SET executable_version = (
                SELECT builds.game_version
                FROM builds
                WHERE builds.build_id = environment_snapshots.build_id),
            steam_build_id = (
                SELECT builds.steam_build_id
                FROM builds
                WHERE builds.build_id = environment_snapshots.build_id);

        ALTER TABLE builds DROP COLUMN game_version;
        ALTER TABLE builds DROP COLUMN steam_build_id;
        """;

    private const string ManagedToolsV3Sql = """
        CREATE TABLE managed_tool_installations (
            tool_id TEXT NOT NULL,
            version TEXT NOT NULL,
            platform TEXT NOT NULL,
            definition_digest TEXT NOT NULL,
            package_sha256 TEXT NOT NULL,
            executable_sha256 TEXT NOT NULL,
            root_path TEXT NOT NULL,
            status TEXT NOT NULL,
            installed_at_utc TEXT NOT NULL,
            last_verified_at_utc TEXT NOT NULL,
            probe_summary TEXT NOT NULL,
            PRIMARY KEY (tool_id, version, platform)
        );

        CREATE TABLE tool_instances (
            tool_instance_id TEXT NOT NULL PRIMARY KEY,
            tool_name TEXT NOT NULL,
            version_label TEXT NULL,
            platform TEXT NOT NULL,
            trust_level TEXT NOT NULL,
            definition_digest TEXT NULL,
            package_sha256 TEXT NULL,
            executable_sha256 TEXT NOT NULL,
            observed_path TEXT NOT NULL,
            first_observed_at_utc TEXT NOT NULL,
            last_verified_at_utc TEXT NOT NULL,
            status TEXT NOT NULL
        );

        CREATE INDEX ix_tool_instances_tool_trust
        ON tool_instances(tool_name, trust_level);
        """;

    private const string ExtractionAttemptsV4Sql = """
        CREATE TABLE input_snapshots (
            input_snapshot_id TEXT NOT NULL PRIMARY KEY,
            build_id TEXT NOT NULL,
            root_path TEXT NOT NULL,
            manifest_digest TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            replay_verified INTEGER NOT NULL CHECK (replay_verified IN (0, 1)),
            replay_verified_at_utc TEXT NULL,
            FOREIGN KEY (build_id) REFERENCES builds(build_id)
        );

        CREATE TABLE input_snapshot_files (
            input_snapshot_id TEXT NOT NULL,
            relative_path TEXT NOT NULL,
            role TEXT NOT NULL,
            size INTEGER NOT NULL CHECK (size >= 0),
            sha256 TEXT NOT NULL,
            PRIMARY KEY (input_snapshot_id, relative_path),
            FOREIGN KEY (input_snapshot_id)
                REFERENCES input_snapshots(input_snapshot_id)
                ON DELETE CASCADE
        );

        CREATE TABLE extraction_attempts (
            attempt_id TEXT NOT NULL PRIMARY KEY,
            recipe_id TEXT NULL,
            build_id TEXT NOT NULL,
            tool_instance_id TEXT NULL,
            profile_id TEXT NOT NULL,
            profile_version INTEGER NOT NULL,
            profile_digest TEXT NOT NULL,
            validation_policy_id TEXT NOT NULL,
            validation_policy_version INTEGER NOT NULL,
            validation_policy_digest TEXT NOT NULL,
            adapter_version INTEGER NOT NULL,
            extraction_schema_version INTEGER NOT NULL,
            input_source TEXT NULL,
            input_snapshot_id TEXT NULL,
            status TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            started_at_utc TEXT NULL,
            completed_at_utc TEXT NULL,
            pre_input_manifest_digest TEXT NULL,
            post_input_manifest_digest TEXT NULL,
            working_path TEXT NOT NULL,
            stdout_path TEXT NOT NULL,
            stderr_path TEXT NOT NULL,
            stdout_truncated INTEGER NOT NULL CHECK (stdout_truncated IN (0, 1)),
            stderr_truncated INTEGER NOT NULL CHECK (stderr_truncated IN (0, 1)),
            stdout_discarded_bytes INTEGER NOT NULL CHECK (stdout_discarded_bytes >= 0),
            stderr_discarded_bytes INTEGER NOT NULL CHECK (stderr_discarded_bytes >= 0),
            process_id INTEGER NULL,
            process_exit_code INTEGER NULL,
            failure_stage TEXT NULL,
            failure_code TEXT NULL,
            failure_message TEXT NULL,
            keep_failed_artifacts INTEGER NOT NULL CHECK (keep_failed_artifacts IN (0, 1)),
            discarded_file_count INTEGER NOT NULL CHECK (discarded_file_count >= 0),
            discarded_byte_count INTEGER NOT NULL CHECK (discarded_byte_count >= 0),
            candidate_output_path TEXT NULL,
            result_extraction_id TEXT NULL,
            FOREIGN KEY (build_id) REFERENCES builds(build_id),
            FOREIGN KEY (tool_instance_id) REFERENCES tool_instances(tool_instance_id),
            FOREIGN KEY (input_snapshot_id) REFERENCES input_snapshots(input_snapshot_id)
        );

        CREATE INDEX ix_extraction_attempts_build_created
        ON extraction_attempts(build_id, created_at_utc DESC);
        CREATE INDEX ix_extraction_attempts_recipe
        ON extraction_attempts(recipe_id);
        CREATE INDEX ix_extraction_attempts_status
        ON extraction_attempts(status);
        """;

    public static IReadOnlyList<SqliteMigration> All { get; } =
    [
        new(1, "foundation-v1", FoundationV1Sql),
        new(2, "environment-observations-v2", EnvironmentObservationsV2Sql),
        new(3, "managed-tools-v3", ManagedToolsV3Sql),
        new(4, "extraction-attempts-v4", ExtractionAttemptsV4Sql)
    ];
}
