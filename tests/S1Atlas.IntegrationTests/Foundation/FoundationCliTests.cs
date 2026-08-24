using System.Text.Json;
using S1Atlas.Cli;
using S1Atlas.Cli.Configuration;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.IntegrationTests.Foundation;

public sealed class FoundationCliTests : IAsyncDisposable
{
    private const string AtlasVersion = "0.1.0-test";

    private readonly string _temporaryDirectory;
    private readonly string _dataDirectory;
    private readonly string _gameDirectory;

    public FoundationCliTests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-cli-tests-{Guid.NewGuid():N}");
        _dataDirectory = Path.Combine(_temporaryDirectory, "data");
        _gameDirectory = Path.Combine(_temporaryDirectory, "Schedule I");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public void Status_WhenNoBuilds_ReportsEmptyAtlas()
    {
        var application = CreateApplication();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["status"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("No indexed builds", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Status_WhenAtlasDirectoryCannotBeCreated_ReturnsCleanFailure()
    {
        File.WriteAllText(_dataDirectory, "this path is intentionally a file");
        var application = CreateApplication();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = -1;

        var exception = Record.Exception(() =>
            exitCode = application.Invoke(
                ["status"],
                output,
                error,
                TestContext.Current.CancellationToken));

        Assert.Null(exception);
        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("S1Atlas failed:", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_WithValidOverride_PersistsCurrentBuild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateFakeInstallationAsync(cancellationToken);
        var application = CreateApplication();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["scan", "--game-path", _gameDirectory],
            output,
            error,
            cancellationToken);

        var repository = new SqliteAtlasRepository(DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Indexed Schedule I build", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Executable version:", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Game version:", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
        Assert.NotNull(current);
        Assert.Equal(AtlasVersion, current.AtlasVersion);
    }

    [Fact]
    public async Task Scan_WithPerformance_WritesDiagnosticsJsonToStandardError()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateFakeInstallationAsync(cancellationToken);
        var application = CreateApplication();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["scan", "--game-path", _gameDirectory, "--performance"],
            output,
            error,
            cancellationToken);

        using var document = JsonDocument.Parse(error.ToString());
        var root = document.RootElement;
        Assert.Equal(0, exitCode);
        Assert.Contains("Indexed Schedule I build", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("scan", root.GetProperty("command").GetString());
        Assert.Contains(
            root.GetProperty("phases").EnumerateArray(),
            phase => phase.GetProperty("name").GetString() == "environment.discovery");
        Assert.True(root.GetProperty("counters").GetProperty("dependencies.total").GetInt64() >= 0);
    }

    [Fact]
    public async Task Scan_WithInvalidOverride_ReturnsFailureWithoutCurrentBuild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var application = CreateApplication();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var missingPath = Path.Combine(_temporaryDirectory, "missing-game");

        var exitCode = application.Invoke(
            ["scan", "--game-path", missingPath],
            output,
            error,
            cancellationToken);

        var repository = new SqliteAtlasRepository(DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains(
            "Schedule I installation could not be found",
            error.ToString(),
            StringComparison.Ordinal);
        Assert.Null(current);
    }

    [Fact]
    public async Task Environment_AfterScan_ReportsEveryTrackedDependency()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateFakeInstallationAsync(
            cancellationToken,
            installS1Api: true,
            installS1Mapi: false,
            installMelonLoader: false,
            installSideload: false);
        var application = CreateApplication();
        ScanSuccessfully(application, cancellationToken);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["env"],
            output,
            error,
            cancellationToken);

        var text = output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("Executable version:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Game version:", text, StringComparison.Ordinal);
        Assert.Contains("S1API: installed", text, StringComparison.Ordinal);
        Assert.Contains("S1MAPI: missing", text, StringComparison.Ordinal);
        Assert.Contains("MelonLoader: missing", text, StringComparison.Ordinal);
        Assert.Contains("Sideload: missing", text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Builds_AfterScan_ListsCurrentBuildId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateFakeInstallationAsync(cancellationToken);
        var application = CreateApplication();
        ScanSuccessfully(application, cancellationToken);
        var repository = new SqliteAtlasRepository(DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);
        Assert.NotNull(current);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["builds"],
            output,
            error,
            cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(current.Build.BuildId, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("first seen", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Status_AfterScan_ReportsCurrentBuild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateFakeInstallationAsync(cancellationToken);
        var application = CreateApplication();
        ScanSuccessfully(application, cancellationToken);
        var repository = new SqliteAtlasRepository(DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);
        Assert.NotNull(current);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["status"],
            output,
            error,
            cancellationToken);

        var text = output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("Current build:", text, StringComparison.Ordinal);
        Assert.Contains("Executable version:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Game version:", text, StringComparison.Ordinal);
        Assert.Contains(current.Build.BuildId, text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task StatusJson_AfterScan_WritesSingleStableEnvelope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateFakeInstallationAsync(cancellationToken);
        var application = CreateApplication();
        ScanSuccessfully(application, cancellationToken);
        var repository = new SqliteAtlasRepository(DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);
        Assert.NotNull(current);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["status", "--json"],
            output,
            error,
            cancellationToken);

        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        AssertSuccessEnvelope(root, "status");
        var data = root.GetProperty("data");
        Assert.True(data.GetProperty("hasCurrentBuild").GetBoolean());
        Assert.Equal(
            current.Build.BuildId,
            data.GetProperty("buildId").GetString());
        Assert.True(data.TryGetProperty("executableVersion", out _));
        Assert.True(data.TryGetProperty("steamAppId", out _));
        Assert.True(data.TryGetProperty("steamBuildId", out _));
    }

    [Fact]
    public async Task EnvironmentJson_AfterScan_IncludesObservationsAndDependencies()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateFakeInstallationAsync(
            cancellationToken,
            installS1Api: true,
            installS1Mapi: false,
            installMelonLoader: false,
            installSideload: true);
        var application = CreateApplication();
        ScanSuccessfully(application, cancellationToken);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["env", "--json"],
            output,
            error,
            cancellationToken);

        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        AssertSuccessEnvelope(root, "env");
        var data = root.GetProperty("data");
        Assert.Equal(
            Path.GetFullPath(_gameDirectory),
            data.GetProperty("installationRoot").GetString());
        var dependencies = data.GetProperty("dependencies");
        Assert.Equal(4, dependencies.GetArrayLength());
        Assert.Contains(
            dependencies.EnumerateArray(),
            item =>
                item.GetProperty("kind").GetString() == "S1Api" &&
                item.GetProperty("isInstalled").GetBoolean());
        Assert.Contains(
            dependencies.EnumerateArray(),
            item =>
                item.GetProperty("kind").GetString() == "S1Mapi" &&
                !item.GetProperty("isInstalled").GetBoolean());
    }

    [Fact]
    public void BuildsJson_WhenEmpty_ReturnsEmptyArray()
    {
        var application = CreateApplication();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["builds", "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        AssertSuccessEnvelope(root, "builds");
        Assert.Equal(
            0,
            root.GetProperty("data").GetProperty("builds").GetArrayLength());
    }

    [Fact]
    public void StatusJson_WhenDatabasePathFails_ReturnsJsonErrorWithoutStackTrace()
    {
        File.WriteAllText(_dataDirectory, "this path is intentionally a file");
        var application = CreateApplication();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["status", "--json"],
            output,
            error,
            TestContext.Current.CancellationToken);

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
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private string DatabasePath => new AtlasPaths(_dataDirectory).DatabasePath;

    private CliApplication CreateApplication() =>
        new(_dataDirectory, AtlasVersion);

    private static void AssertSuccessEnvelope(JsonElement root, string command)
    {
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(command, root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("data").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }

    private static void ScanSuccessfully(
        CliApplication application,
        CancellationToken cancellationToken,
        string? gameDirectory = null)
    {
        using var scanOutput = new StringWriter();
        using var scanError = new StringWriter();
        var arguments = new[]
        {
            "scan",
            "--game-path",
            gameDirectory ?? throw new ArgumentNullException(nameof(gameDirectory))
        };
        Assert.Equal(
            0,
            application.Invoke(
                arguments,
                scanOutput,
                scanError,
                cancellationToken));
        Assert.Equal(string.Empty, scanError.ToString());
    }

    private void ScanSuccessfully(
        CliApplication application,
        CancellationToken cancellationToken) =>
        ScanSuccessfully(application, cancellationToken, _gameDirectory);

    private async Task CreateFakeInstallationAsync(
        CancellationToken cancellationToken,
        bool installS1Api = true,
        bool installS1Mapi = true,
        bool installMelonLoader = true,
        bool installSideload = true)
    {
        Directory.CreateDirectory(_gameDirectory);
        Directory.CreateDirectory(Path.Combine(_gameDirectory, "Mods"));
        Directory.CreateDirectory(Path.Combine(_gameDirectory, "UserLibs"));
        Directory.CreateDirectory(Path.Combine(_gameDirectory, "Plugins"));
        Directory.CreateDirectory(Path.Combine(_gameDirectory, "MelonLoader"));

        await File.WriteAllBytesAsync(
            Path.Combine(_gameDirectory, "GameAssembly.dll"),
            [1, 2, 3, 4],
            cancellationToken);

        var metadataDirectory = Path.Combine(
            _gameDirectory,
            "Schedule I_Data",
            "il2cpp_data",
            "Metadata");
        Directory.CreateDirectory(metadataDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(metadataDirectory, "global-metadata.dat"),
            [5, 6, 7, 8],
            cancellationToken);

        if (installS1Api)
        {
            await WriteDummyDllAsync(
                Path.Combine(_gameDirectory, "UserLibs", "S1API.dll"),
                cancellationToken);
        }

        if (installS1Mapi)
        {
            await WriteDummyDllAsync(
                Path.Combine(_gameDirectory, "UserLibs", "S1MAPI.dll"),
                cancellationToken);
        }

        if (installMelonLoader)
        {
            await WriteDummyDllAsync(
                Path.Combine(_gameDirectory, "MelonLoader", "MelonLoader.dll"),
                cancellationToken);
        }

        if (installSideload)
        {
            await WriteDummyDllAsync(
                Path.Combine(_gameDirectory, "Plugins", "Sideload.dll"),
                cancellationToken);
        }
    }

    private static Task WriteDummyDllAsync(
        string path,
        CancellationToken cancellationToken) =>
        File.WriteAllBytesAsync(path, [0], cancellationToken);
}
