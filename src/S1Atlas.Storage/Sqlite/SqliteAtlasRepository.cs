using System.Globalization;
using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Migrations;

namespace S1Atlas.Storage.Sqlite;

public sealed partial class SqliteAtlasRepository :
    IAtlasRepository,
    IToolRepository,
    IExtractionRepository,
    IValidatedExtractionRepository,
    IIndexRepository
{
    private readonly string _databasePath;
    private readonly SqliteMigrationRunner _migrationRunner;

    public SqliteAtlasRepository(string databasePath)
        : this(
            databasePath,
            Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(databasePath))!,
                "backups"))
    {
    }

    public SqliteAtlasRepository(
        string databasePath,
        string backupDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);

        _databasePath = Path.GetFullPath(databasePath);
        _migrationRunner = new SqliteMigrationRunner(
            _databasePath,
            Path.GetFullPath(backupDirectory));
    }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        _migrationRunner.MigrateAsync(cancellationToken);

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

        if (snapshot.IdentityVersion != 2)
        {
            throw new InvalidOperationException(
                "Only identity-version 2 snapshots can be saved as new Atlas candidates.");
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
                    b.game_assembly_sha256,
                    b.metadata_sha256,
                    b.first_seen_at_utc,
                    b.is_valid,
                    snapshot.atlas_version,
                    snapshot.captured_at_utc,
                    snapshot.snapshot_id,
                    snapshot.identity_version,
                    snapshot.executable_version,
                    snapshot.steam_app_id,
                    snapshot.steam_build_id,
                    snapshot.installation_root,
                    snapshot.game_assembly_path,
                    snapshot.global_metadata_path
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
            header.IdentityVersion,
            header.Build,
            header.Installation,
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
                game_assembly_sha256,
                metadata_sha256,
                first_seen_at_utc,
                is_valid
            FROM builds
            ORDER BY first_seen_at_utc DESC, build_id DESC;
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
                game_assembly_sha256,
                metadata_sha256,
                first_seen_at_utc,
                is_valid)
            VALUES (
                $buildId,
                $gameAssemblySha256,
                $metadataSha256,
                $firstSeenAtUtc,
                $isValid);
            """;
        command.Parameters.AddWithValue("$buildId", build.BuildId);
        command.Parameters.AddWithValue(
            "$gameAssemblySha256",
            build.GameAssemblySha256);
        command.Parameters.AddWithValue("$metadataSha256", build.MetadataSha256);
        command.Parameters.AddWithValue(
            "$firstSeenAtUtc",
            FormatTimestamp(build.FirstSeenAtUtc));
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
                captured_at_utc,
                identity_version,
                executable_version,
                steam_app_id,
                steam_build_id,
                installation_root,
                game_assembly_path,
                global_metadata_path)
            VALUES (
                $snapshotId,
                $buildId,
                $atlasVersion,
                $capturedAtUtc,
                $identityVersion,
                $executableVersion,
                $steamAppId,
                $steamBuildId,
                $installationRoot,
                $gameAssemblyPath,
                $globalMetadataPath);
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        command.Parameters.AddWithValue("$buildId", snapshot.Build.BuildId);
        command.Parameters.AddWithValue("$atlasVersion", snapshot.AtlasVersion);
        command.Parameters.AddWithValue(
            "$capturedAtUtc",
            FormatTimestamp(snapshot.CapturedAtUtc));
        command.Parameters.AddWithValue("$identityVersion", snapshot.IdentityVersion);
        command.Parameters.AddWithValue(
            "$executableVersion",
            (object?)snapshot.Installation.ExecutableVersion ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$steamAppId",
            (object?)snapshot.Installation.SteamAppId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$steamBuildId",
            (object?)snapshot.Installation.SteamBuildId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$installationRoot",
            (object?)snapshot.Installation.InstallationRoot ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$gameAssemblyPath",
            (object?)snapshot.Installation.GameAssemblyPath ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$globalMetadataPath",
            (object?)snapshot.Installation.GlobalMetadataPath ?? DBNull.Value);

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
        var installation = new InstallationObservation(
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));

        return new SnapshotHeader(
            build,
            reader.GetString(5),
            ParseTimestamp(reader.GetString(6)),
            reader.GetString(7),
            reader.GetInt32(8),
            installation);
    }

    private static GameBuild ReadBuild(SqliteDataReader reader)
    {
        return new GameBuild(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseTimestamp(reader.GetString(3)),
            reader.GetInt64(4) == 1);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture);

    private sealed record SnapshotHeader(
        GameBuild Build,
        string AtlasVersion,
        DateTimeOffset CapturedAtUtc,
        string SnapshotId,
        int IdentityVersion,
        InstallationObservation Installation);
}
