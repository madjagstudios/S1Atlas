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

    public static IReadOnlyList<SqliteMigration> All { get; } =
    [
        new(1, "foundation-v1", FoundationV1Sql),
        new(2, "environment-observations-v2", EnvironmentObservationsV2Sql),
        new(3, "managed-tools-v3", ManagedToolsV3Sql)
    ];
}
