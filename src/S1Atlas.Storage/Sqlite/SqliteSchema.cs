namespace S1Atlas.Storage.Sqlite;

internal static class SqliteSchema
{
    public const string Create = """
        CREATE TABLE IF NOT EXISTS builds (
            build_id TEXT NOT NULL PRIMARY KEY,
            game_version TEXT NULL,
            steam_build_id TEXT NULL,
            game_assembly_sha256 TEXT NOT NULL,
            metadata_sha256 TEXT NOT NULL,
            scanned_at_utc TEXT NOT NULL,
            is_valid INTEGER NOT NULL CHECK (is_valid IN (0, 1))
        );

        CREATE TABLE IF NOT EXISTS environment_snapshots (
            snapshot_id TEXT NOT NULL PRIMARY KEY,
            build_id TEXT NOT NULL,
            atlas_version TEXT NOT NULL,
            captured_at_utc TEXT NOT NULL,
            FOREIGN KEY (build_id) REFERENCES builds(build_id)
        );

        CREATE INDEX IF NOT EXISTS ix_environment_snapshots_build_id
        ON environment_snapshots(build_id);

        CREATE TABLE IF NOT EXISTS dependencies (
            snapshot_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            version TEXT NULL,
            path TEXT NULL,
            is_installed INTEGER NOT NULL CHECK (is_installed IN (0, 1)),
            PRIMARY KEY (snapshot_id, kind),
            FOREIGN KEY (snapshot_id)
                REFERENCES environment_snapshots(snapshot_id)
                ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS atlas_state (
            singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
            current_snapshot_id TEXT NULL,
            FOREIGN KEY (current_snapshot_id)
                REFERENCES environment_snapshots(snapshot_id)
        );

        INSERT OR IGNORE INTO atlas_state (singleton_id, current_snapshot_id)
        VALUES (1, NULL);
        """;
}
