using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using S1Atlas.Cli;
using S1Atlas.Cli.Configuration;
using S1Atlas.Core.Builds;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.IntegrationTests.Foundation;

public sealed class FoundationMigrationTests : IAsyncDisposable
{
    private const string AtlasVersion = "0.2.0-test";
    private const string SteamAppId = "3164500";
    private const string SteamBuildId = "19628042";

    private readonly string _temporaryDirectory;
    private readonly string _dataDirectory;
    private readonly string _steamAppsDirectory;
    private readonly string _gameDirectory;
    private readonly string _manifestPath;

    public FoundationMigrationTests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-migration-tests-{Guid.NewGuid():N}");
        _dataDirectory = Path.Combine(_temporaryDirectory, "data");
        _steamAppsDirectory = Path.Combine(_temporaryDirectory, "Steam", "steamapps");
        _gameDirectory = Path.Combine(
            _steamAppsDirectory,
            "common",
            "Schedule I");
        _manifestPath = Path.Combine(
            _steamAppsDirectory,
            $"appmanifest_{SteamAppId}.acf");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task Status_OnFoundationDatabase_MigratesAndPreservesCurrentState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AtlasPaths(_dataDirectory);
        await FoundationV1DatabaseFixture.CreateReferenceDatabaseAsync(
            paths.DatabasePath,
            cancellationToken);
        var application = new CliApplication(_dataDirectory, AtlasVersion);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["status"],
            output,
            error,
            cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains(
            FoundationV1DatabaseFixture.ReferenceBuildId,
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            $"Executable version: {FoundationV1DatabaseFixture.ReferenceExecutableVersion}",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Game version:",
            output.ToString(),
            StringComparison.Ordinal);

        var repository = new SqliteAtlasRepository(
            paths.DatabasePath,
            paths.BackupsDirectory);
        await repository.InitializeAsync(cancellationToken);
        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);

        Assert.NotNull(current);
        Assert.Equal(1, current.IdentityVersion);
        Assert.Equal(
            FoundationV1DatabaseFixture.ReferenceBuildId,
            current.Build.BuildId);
        Assert.Equal(
            FoundationV1DatabaseFixture.ReferenceExecutableVersion,
            current.Installation.ExecutableVersion);
        Assert.Null(current.Installation.SteamAppId);
        Assert.Null(current.Installation.SteamBuildId);
        Assert.Null(current.Installation.InstallationRoot);
        Assert.Null(current.Installation.GameAssemblyPath);
        Assert.Null(current.Installation.GlobalMetadataPath);
        Assert.Equal(4, current.Dependencies.Count);
        Assert.Contains(
            current.Dependencies,
            dependency =>
                dependency.Kind.ToString() == "S1Api" &&
                dependency.Version == "3.1.12.0" &&
                dependency.IsInstalled);
        Assert.Contains(
            current.Dependencies,
            dependency =>
                dependency.Kind.ToString() == "S1Mapi" &&
                !dependency.IsInstalled);

        await using (var connection = await OpenDatabaseAsync(
                         paths.DatabasePath,
                         cancellationToken))
        {
            Assert.Equal(
                FoundationV1DatabaseFixture.ReferenceSnapshotId,
                await ReadScalarStringAsync(
                    connection,
                    "SELECT current_snapshot_id FROM atlas_state WHERE singleton_id = 1;",
                    cancellationToken));
            Assert.Equal(
                1,
                await ReadScalarInt64Async(
                    connection,
                    "SELECT COUNT(*) FROM environment_snapshots WHERE snapshot_id = 'real-scan-foundation-v1';",
                    cancellationToken));
            Assert.Equal(
                8,
                await ReadScalarInt64Async(
                    connection,
                    "SELECT COUNT(*) FROM schema_migrations;",
                    cancellationToken));
            Assert.Equal(
                1,
                await ReadScalarInt64Async(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'managed_tool_installations';",
                    cancellationToken));
            Assert.Equal(
                1,
                await ReadScalarInt64Async(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'tool_instances';",
                    cancellationToken));
            Assert.Equal(
                3,
                await ReadScalarInt64Async(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name IN ('extraction_attempts', 'input_snapshots', 'input_snapshot_files');",
                    cancellationToken));
        }

        var backups = GetBackups(paths.BackupsDirectory);
        var backup = Assert.Single(backups);
        await using (var backupConnection = await OpenDatabaseAsync(
                         backup,
                         cancellationToken,
                         SqliteOpenMode.ReadOnly))
        {
            var columns = await ReadColumnNamesAsync(
                backupConnection,
                "builds",
                cancellationToken);
            Assert.Contains("game_version", columns);
            Assert.Contains("scanned_at_utc", columns);
            Assert.DoesNotContain("first_seen_at_utc", columns);
        }

        using var secondOutput = new StringWriter();
        using var secondError = new StringWriter();
        Assert.Equal(
            0,
            application.Invoke(
                ["status"],
                secondOutput,
                secondError,
                cancellationToken));
        Assert.Equal(string.Empty, secondError.ToString());
        Assert.Single(GetBackups(paths.BackupsDirectory));

        await using var finalConnection = await OpenDatabaseAsync(
            paths.DatabasePath,
            cancellationToken);
        Assert.Equal(
            8,
            await ReadScalarInt64Async(
                finalConnection,
                "SELECT COUNT(*) FROM schema_migrations;",
                cancellationToken));
    }

    [Fact]
    public async Task Scan_AfterFoundationMigration_PromotesV2ObservationAndRetainsV1History()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var gameAssemblyBytes = new byte[] { 1, 2, 3, 4 };
        var metadataBytes = new byte[] { 5, 6, 7, 8 };
        var gameAssemblyHash = Hash(gameAssemblyBytes);
        var metadataHash = Hash(metadataBytes);
        var matchingBuildId = BuildFingerprint.Create(
            gameAssemblyHash,
            metadataHash);
        const string legacySnapshotId = "matching-foundation-v1";
        var paths = new AtlasPaths(_dataDirectory);
        await FoundationV1DatabaseFixture.CreateDatabaseAsync(
            paths.DatabasePath,
            matchingBuildId,
            legacySnapshotId,
            gameAssemblyHash,
            metadataHash,
            cancellationToken);
        await CreateFakeSteamInstallationAsync(
            gameAssemblyBytes,
            metadataBytes,
            cancellationToken);
        var application = new CliApplication(_dataDirectory, AtlasVersion);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["scan", "--game-path", _gameDirectory],
            output,
            error,
            cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains(
            $"Indexed Schedule I build {matchingBuildId}",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            $"Steam app ID: {SteamAppId}",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            $"Steam build ID: {SteamBuildId}",
            output.ToString(),
            StringComparison.Ordinal);

        var repository = new SqliteAtlasRepository(
            paths.DatabasePath,
            paths.BackupsDirectory);
        await repository.InitializeAsync(cancellationToken);
        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);
        var builds = await repository.ListBuildsAsync(cancellationToken);

        Assert.NotNull(current);
        Assert.Equal(2, current.IdentityVersion);
        Assert.Equal(matchingBuildId, current.Build.BuildId);
        Assert.Equal(SteamAppId, current.Installation.SteamAppId);
        Assert.Equal(SteamBuildId, current.Installation.SteamBuildId);
        Assert.Equal(
            Path.GetFullPath(_gameDirectory),
            current.Installation.InstallationRoot);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_gameDirectory, "GameAssembly.dll")),
            current.Installation.GameAssemblyPath);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                _gameDirectory,
                "Schedule I_Data",
                "il2cpp_data",
                "Metadata",
                "global-metadata.dat")),
            current.Installation.GlobalMetadataPath);
        Assert.Single(builds);

        await using var connection = await OpenDatabaseAsync(
            paths.DatabasePath,
            cancellationToken);
        Assert.Equal(
            2,
            await ReadScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM environment_snapshots;",
                cancellationToken));
        Assert.Equal(
            1,
            await ReadScalarInt64Async(
                connection,
                $"SELECT COUNT(*) FROM environment_snapshots WHERE snapshot_id = '{legacySnapshotId}' AND identity_version = 1;",
                cancellationToken));
        var currentSnapshotId = await ReadScalarStringAsync(
            connection,
            "SELECT current_snapshot_id FROM atlas_state WHERE singleton_id = 1;",
            cancellationToken);
        Assert.NotEqual(legacySnapshotId, currentSnapshotId);
    }

    [Fact]
    public async Task Status_OnUnknownDatabaseSchema_ReturnsCleanFailureWithoutMigration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AtlasPaths(_dataDirectory);
        await CreateUnknownDatabaseAsync(paths.DatabasePath, cancellationToken);
        var application = new CliApplication(_dataDirectory, AtlasVersion);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["status"],
            output,
            error,
            cancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.StartsWith(
            "S1Atlas failed:",
            error.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", error.ToString(), StringComparison.Ordinal);
        await AssertUnknownDatabaseUnchangedAsync(paths, cancellationToken);
    }

    [Fact]
    public async Task StatusJson_OnUnknownDatabaseSchema_ReturnsStructuredFailureWithoutMigration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AtlasPaths(_dataDirectory);
        await CreateUnknownDatabaseAsync(paths.DatabasePath, cancellationToken);
        var application = new CliApplication(_dataDirectory, AtlasVersion);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["status", "--json"],
            output,
            error,
            cancellationToken);

        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("status", root.GetProperty("command").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(1, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind);
        var publicError = root.GetProperty("error");
        Assert.Equal("OperationalFailure", publicError.GetProperty("code").GetString());
        Assert.StartsWith(
            "S1Atlas failed:",
            publicError.GetProperty("message").GetString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", output.ToString(), StringComparison.Ordinal);
        await AssertUnknownDatabaseUnchangedAsync(paths, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private async Task CreateFakeSteamInstallationAsync(
        byte[] gameAssemblyBytes,
        byte[] metadataBytes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_gameDirectory);
        Directory.CreateDirectory(Path.Combine(_gameDirectory, "Mods"));
        Directory.CreateDirectory(Path.Combine(_gameDirectory, "UserLibs"));
        Directory.CreateDirectory(Path.Combine(_gameDirectory, "Plugins"));
        Directory.CreateDirectory(Path.Combine(_gameDirectory, "MelonLoader"));

        await File.WriteAllBytesAsync(
            Path.Combine(_gameDirectory, "Schedule I.exe"),
            [77, 90],
            cancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(_gameDirectory, "GameAssembly.dll"),
            gameAssemblyBytes,
            cancellationToken);
        var metadataDirectory = Path.Combine(
            _gameDirectory,
            "Schedule I_Data",
            "il2cpp_data",
            "Metadata");
        Directory.CreateDirectory(metadataDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(metadataDirectory, "global-metadata.dat"),
            metadataBytes,
            cancellationToken);

        Directory.CreateDirectory(_steamAppsDirectory);
        await File.WriteAllTextAsync(
            _manifestPath,
            """
            "AppState"
            {
                "appid" "3164500"
                "installdir" "Schedule I"
                "buildid" "19628042"
            }
            """,
            cancellationToken);
    }

    private static async Task CreateUnknownDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenDatabaseAsync(
            databasePath,
            cancellationToken,
            SqliteOpenMode.ReadWriteCreate);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE unrelated (id INTEGER NOT NULL PRIMARY KEY);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AssertUnknownDatabaseUnchangedAsync(
        AtlasPaths paths,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenDatabaseAsync(
            paths.DatabasePath,
            cancellationToken);
        Assert.Equal(
            1,
            await ReadScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'unrelated';",
                cancellationToken));
        Assert.Equal(
            0,
            await ReadScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'schema_migrations';",
                cancellationToken));
        Assert.Empty(GetBackups(paths.BackupsDirectory));
    }

    private static string[] GetBackups(string backupDirectory) =>
        Directory.Exists(backupDirectory)
            ? Directory
                .EnumerateFiles(
                    backupDirectory,
                    "atlas-before-schema-8-*.db",
                    SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    private static async Task<SqliteConnection> OpenDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken,
        SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath),
                Mode = mode,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<long> ReadScalarInt64Async(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ReadScalarStringAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}');";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static string Hash(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
