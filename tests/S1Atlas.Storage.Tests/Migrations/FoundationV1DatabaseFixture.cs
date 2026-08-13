using Microsoft.Data.Sqlite;

namespace S1Atlas.Storage.Tests.Migrations;

internal static class FoundationV1DatabaseFixture
{
    public const string ReferenceBuildId =
        "6fbd38f8401afa2241a1322afd4b8a8eadc99aa1f1c660ece253da7859d54bdc";
    public const string SnapshotId = "foundation-snapshot-v1";
    public const string ScannedAtUtc = "2026-08-12T19:06:40.3468325+00:00";

    public const string SchemaSql = """
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

    public static async Task<SqliteConnection> OpenAsync(
        string schemaSql,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        try
        {
            await EnableForeignKeysAndExecuteAsync(
                connection,
                schemaSql,
                cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public static async Task CreatePopulatedDatabaseAsync(
        string databasePath,
        string? steamBuildId,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenFileAsync(
            databasePath,
            SqliteOpenMode.ReadWriteCreate,
            cancellationToken);
        await EnableForeignKeysAndExecuteAsync(
            connection,
            SchemaSql,
            cancellationToken);
        await SeedKnownEnvironmentAsync(
            connection,
            steamBuildId,
            cancellationToken);
    }

    public static async Task<SqliteConnection> OpenFileAsync(
        string databasePath,
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = mode,
            Pooling = false
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task EnableForeignKeysAndExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;\n" + sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedKnownEnvironmentAsync(
        SqliteConnection connection,
        string? steamBuildId,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO builds (
                        build_id,
                        game_version,
                        steam_build_id,
                        game_assembly_sha256,
                        metadata_sha256,
                        scanned_at_utc,
                        is_valid)
                    VALUES (
                        $buildId,
                        $gameVersion,
                        $steamBuildId,
                        $gameAssemblyHash,
                        $metadataHash,
                        $scannedAtUtc,
                        1);

                    INSERT INTO environment_snapshots (
                        snapshot_id,
                        build_id,
                        atlas_version,
                        captured_at_utc)
                    VALUES (
                        $snapshotId,
                        $buildId,
                        '0.1.0',
                        $scannedAtUtc);
                    """;
                command.Parameters.AddWithValue("$buildId", ReferenceBuildId);
                command.Parameters.AddWithValue(
                    "$gameVersion",
                    "2022.3.62.7762112");
                command.Parameters.AddWithValue(
                    "$steamBuildId",
                    (object?)steamBuildId ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "$gameAssemblyHash",
                    "game-assembly-sha256");
                command.Parameters.AddWithValue(
                    "$metadataHash",
                    "metadata-sha256");
                command.Parameters.AddWithValue("$scannedAtUtc", ScannedAtUtc);
                command.Parameters.AddWithValue("$snapshotId", SnapshotId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var dependencies = new[]
            {
                ("S1Api", "3.1.12.0", "C:\\Game\\Mods\\S1API.dll", 1),
                ("S1Mapi", (string?)null, (string?)null, 0),
                ("MelonLoader", "0.7.3.0", "C:\\Game\\MelonLoader\\MelonLoader.dll", 1),
                ("Sideload", "1.30.0.0", "C:\\Game\\Mods\\Sideload.dll", 1)
            };

            for (var ordinal = 0; ordinal < dependencies.Length; ordinal++)
            {
                var dependency = dependencies[ordinal];
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO dependencies (
                        snapshot_id,
                        ordinal,
                        kind,
                        version,
                        path,
                        is_installed)
                    VALUES (
                        $snapshotId,
                        $ordinal,
                        $kind,
                        $version,
                        $path,
                        $isInstalled);
                    """;
                command.Parameters.AddWithValue("$snapshotId", SnapshotId);
                command.Parameters.AddWithValue("$ordinal", ordinal);
                command.Parameters.AddWithValue("$kind", dependency.Item1);
                command.Parameters.AddWithValue(
                    "$version",
                    (object?)dependency.Item2 ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "$path",
                    (object?)dependency.Item3 ?? DBNull.Value);
                command.Parameters.AddWithValue("$isInstalled", dependency.Item4);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE atlas_state
                    SET current_snapshot_id = $snapshotId
                    WHERE singleton_id = 1;
                    """;
                command.Parameters.AddWithValue("$snapshotId", SnapshotId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
