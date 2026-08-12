using S1Atlas.Cli;
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

        var repository = new SqliteAtlasRepository(
            Path.Combine(_dataDirectory, "atlas.db"));
        await repository.InitializeAsync(cancellationToken);
        var current = await repository.GetCurrentSnapshotAsync(cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Indexed Schedule I build", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
        Assert.NotNull(current);
        Assert.Equal(AtlasVersion, current.AtlasVersion);
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

        var repository = new SqliteAtlasRepository(
            Path.Combine(_dataDirectory, "atlas.db"));
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
        using var scanOutput = new StringWriter();
        using var scanError = new StringWriter();
        Assert.Equal(
            0,
            application.Invoke(
                ["scan", "--game-path", _gameDirectory],
                scanOutput,
                scanError,
                cancellationToken));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["env"],
            output,
            error,
            cancellationToken);

        var text = output.ToString();
        Assert.Equal(0, exitCode);
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
        using var scanOutput = new StringWriter();
        using var scanError = new StringWriter();
        Assert.Equal(
            0,
            application.Invoke(
                ["scan", "--game-path", _gameDirectory],
                scanOutput,
                scanError,
                cancellationToken));
        var repository = new SqliteAtlasRepository(
            Path.Combine(_dataDirectory, "atlas.db"));
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
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Status_AfterScan_ReportsCurrentBuild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateFakeInstallationAsync(cancellationToken);
        var application = CreateApplication();
        using var scanOutput = new StringWriter();
        using var scanError = new StringWriter();
        Assert.Equal(
            0,
            application.Invoke(
                ["scan", "--game-path", _gameDirectory],
                scanOutput,
                scanError,
                cancellationToken));
        var repository = new SqliteAtlasRepository(
            Path.Combine(_dataDirectory, "atlas.db"));
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
        Assert.Contains(current.Build.BuildId, text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private CliApplication CreateApplication() =>
        new(_dataDirectory, AtlasVersion);

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
