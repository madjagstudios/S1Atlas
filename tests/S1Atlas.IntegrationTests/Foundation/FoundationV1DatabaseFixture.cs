using Microsoft.Data.Sqlite;

namespace S1Atlas.IntegrationTests.Foundation;

internal static class FoundationV1DatabaseFixture
{
    public const string ReferenceBuildId =
        "6fbd38f8401afa2241a1322afd4b8a8eadc99aa1f1c660ece253da7859d54bdc";
    public const string ReferenceSnapshotId = "real-scan-foundation-v1";
    public const string ReferenceExecutableVersion = "2022.3.62.7762112";
    public const string ReferenceCapturedAtUtc = "2026-08-12T19:06:40.3468325+00:00";
    public const string ReferenceGameAssemblySha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string ReferenceMetadataSha256 =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string SchemaSql = """
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

    public static Task CreateReferenceDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken) =>
        CreateDatabaseAsync(
            databasePath,
            ReferenceBuildId,
            ReferenceSnapshotId,
            ReferenceGameAssemblySha256,
            ReferenceMetadataSha256,
            cancellationToken);

    public static async Task CreateDatabaseAsync(
        string databasePath,
        string buildId,
        string snapshotId,
        string gameAssemblySha256,
        string metadataSha256,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameAssemblySha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataSha256);

        var fullDatabasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullDatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = fullDatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = ON;\n" + SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

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
                        $executableVersion,
                        NULL,
                        $gameAssemblySha256,
                        $metadataSha256,
                        $capturedAtUtc,
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
                        $capturedAtUtc);
                    """;
                command.Parameters.AddWithValue("$buildId", buildId);
                command.Parameters.AddWithValue(
                    "$executableVersion",
                    ReferenceExecutableVersion);
                command.Parameters.AddWithValue(
                    "$gameAssemblySha256",
                    gameAssemblySha256);
                command.Parameters.AddWithValue("$metadataSha256", metadataSha256);
                command.Parameters.AddWithValue(
                    "$capturedAtUtc",
                    ReferenceCapturedAtUtc);
                command.Parameters.AddWithValue("$snapshotId", snapshotId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var dependencies = new (string Kind, string? Version, string? Path, bool Installed)[]
            {
                (
                    "S1Api",
                    "3.1.12.0",
                    @"C:\Program Files (x86)\Steam\steamapps\common\Schedule I\Mods\S1API.Il2Cpp.MelonLoader.dll",
                    true),
                ("S1Mapi", null, null, false),
                (
                    "MelonLoader",
                    "0.7.3.0",
                    @"C:\Program Files (x86)\Steam\steamapps\common\Schedule I\MelonLoader\net35\MelonLoader.dll",
                    true),
                (
                    "Sideload",
                    "1.30.0.0",
                    @"C:\Program Files (x86)\Steam\steamapps\common\Schedule I\Mods\Sideload.dll",
                    true)
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
                command.Parameters.AddWithValue("$snapshotId", snapshotId);
                command.Parameters.AddWithValue("$ordinal", ordinal);
                command.Parameters.AddWithValue("$kind", dependency.Kind);
                command.Parameters.AddWithValue(
                    "$version",
                    (object?)dependency.Version ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "$path",
                    (object?)dependency.Path ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "$isInstalled",
                    dependency.Installed ? 1 : 0);
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
                command.Parameters.AddWithValue("$snapshotId", snapshotId);
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
