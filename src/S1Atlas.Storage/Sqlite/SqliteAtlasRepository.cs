using System.Globalization;
using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Storage;

namespace S1Atlas.Storage.Sqlite;

public sealed class SqliteAtlasRepository : IAtlasRepository
{
    private readonly string _databasePath;

    public SqliteAtlasRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SqliteSchema.Create;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveSnapshotAsync(
        EnvironmentSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.Build.IsValid)
        {
            throw new InvalidOperationException(
                "Only validated build snapshots can become the current Atlas build.");
        }

        var orderedDependencies =
            EnvironmentSnapshotId.OrderDependencies(snapshot.Dependencies);
        var snapshotId = EnvironmentSnapshotId.Create(snapshot);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await InsertBuildAsync(
                connection,
                transaction,
                snapshot.Build,
                cancellationToken);

            var snapshotInserted = await InsertEnvironmentSnapshotAsync(
                connection,
                transaction,
                snapshotId,
                snapshot,
                cancellationToken);

            if (snapshotInserted)
            {
                for (var ordinal = 0; ordinal < orderedDependencies.Length; ordinal++)
                {
                    await InsertDependencyAsync(
                        connection,
                        transaction,
                        snapshotId,
                        ordinal,
                        orderedDependencies[ordinal],
                        cancellationToken);
                }
            }

            await PromoteCurrentSnapshotAsync(
                connection,
                transaction,
                snapshotId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqliteException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new InvalidOperationException(
                $"The environment snapshot for build '{snapshot.Build.BuildId}' " +
                "could not be saved atomically.",
                exception);
        }
    }

    public async Task<EnvironmentSnapshot?> GetCurrentSnapshotAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        SnapshotHeader? header;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    b.build_id,
                    b.game_version,
                    b.steam_build_id,
                    b.game_assembly_sha256,
                    b.metadata_sha256,
                    b.scanned_at_utc,
                    b.is_valid,
                    snapshot.atlas_version,
                    snapshot.captured_at_utc,
                    snapshot.snapshot_id
                FROM atlas_state AS state
                INNER JOIN environment_snapshots AS snapshot
                    ON snapshot.snapshot_id = state.current_snapshot_id
                INNER JOIN builds AS b
                    ON b.build_id = snapshot.build_id
                WHERE state.singleton_id = 1;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            header = await reader.ReadAsync(cancellationToken)
                ? ReadSnapshotHeader(reader)
                : null;
        }

        if (header is null)
        {
            return null;
        }

        var dependencies = await GetDependenciesAsync(
            connection,
            header.SnapshotId,
            cancellationToken);

        return new EnvironmentSnapshot(
            header.Build,
            dependencies,
            header.AtlasVersion,
            header.CapturedAtUtc);
    }

    public async Task<IReadOnlyList<GameBuild>> ListBuildsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                build_id,
                game_version,
                steam_build_id,
                game_assembly_sha256,
                metadata_sha256,
                scanned_at_utc,
                is_valid
            FROM builds
            ORDER BY scanned_at_utc DESC, build_id DESC;
            """;

        var builds = new List<GameBuild>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            builds.Add(ReadBuild(reader));
        }

        return builds;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task InsertBuildAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GameBuild build,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO builds (
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
                $gameAssemblySha256,
                $metadataSha256,
                $scannedAtUtc,
                $isValid);
            """;
        command.Parameters.AddWithValue("$buildId", build.BuildId);
        command.Parameters.AddWithValue(
            "$gameVersion",
            (object?)build.GameVersion ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$steamBuildId",
            (object?)build.SteamBuildId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$gameAssemblySha256",
            build.GameAssemblySha256);
        command.Parameters.AddWithValue("$metadataSha256", build.MetadataSha256);
        command.Parameters.AddWithValue(
            "$scannedAtUtc",
            FormatTimestamp(build.ScannedAtUtc));
        command.Parameters.AddWithValue("$isValid", build.IsValid ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> InsertEnvironmentSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string snapshotId,
        EnvironmentSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO environment_snapshots (
                snapshot_id,
                build_id,
                atlas_version,
                captured_at_utc)
            VALUES (
                $snapshotId,
                $buildId,
                $atlasVersion,
                $capturedAtUtc);
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        command.Parameters.AddWithValue("$buildId", snapshot.Build.BuildId);
        command.Parameters.AddWithValue("$atlasVersion", snapshot.AtlasVersion);
        command.Parameters.AddWithValue(
            "$capturedAtUtc",
            FormatTimestamp(snapshot.CapturedAtUtc));

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task InsertDependencyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string snapshotId,
        int ordinal,
        DependencyVersion dependency,
        CancellationToken cancellationToken)
    {
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
        command.Parameters.AddWithValue("$kind", dependency.Kind.ToString());
        command.Parameters.AddWithValue(
            "$version",
            (object?)dependency.Version ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", (object?)dependency.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$isInstalled", dependency.IsInstalled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task PromoteCurrentSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE atlas_state
            SET current_snapshot_id = $snapshotId
            WHERE singleton_id = 1;
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<DependencyVersion>> GetDependenciesAsync(
        SqliteConnection connection,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind, version, path, is_installed
            FROM dependencies
            WHERE snapshot_id = $snapshotId
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);

        var dependencies = new List<DependencyVersion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            dependencies.Add(new DependencyVersion(
                Enum.Parse<DependencyKind>(reader.GetString(0), ignoreCase: false),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3) == 1));
        }

        return dependencies;
    }

    private static SnapshotHeader ReadSnapshotHeader(SqliteDataReader reader)
    {
        var build = ReadBuild(reader);
        return new SnapshotHeader(
            build,
            reader.GetString(7),
            ParseTimestamp(reader.GetString(8)),
            reader.GetString(9));
    }

    private static GameBuild ReadBuild(SqliteDataReader reader)
    {
        return new GameBuild(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            ParseTimestamp(reader.GetString(5)),
            reader.GetInt64(6) == 1);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture);

    private sealed record SnapshotHeader(
        GameBuild Build,
        string AtlasVersion,
        DateTimeOffset CapturedAtUtc,
        string SnapshotId);
}
