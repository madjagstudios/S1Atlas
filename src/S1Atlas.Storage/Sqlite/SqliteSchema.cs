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
            is_valid INTEGER NOT NULL CHECK (is_valid IN (0, 1)),
            atlas_version TEXT NOT NULL,
            captured_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS dependencies (
            build_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            version TEXT NULL,
            path TEXT NULL,
            is_installed INTEGER NOT NULL CHECK (is_installed IN (0, 1)),
            PRIMARY KEY (build_id, kind),
            FOREIGN KEY (build_id) REFERENCES builds(build_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS atlas_state (
            singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
            current_build_id TEXT NULL,
            FOREIGN KEY (current_build_id) REFERENCES builds(build_id)
        );

        INSERT OR IGNORE INTO atlas_state (singleton_id, current_build_id)
        VALUES (1, NULL);
        """;
}
