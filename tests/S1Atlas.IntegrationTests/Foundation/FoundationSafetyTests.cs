using S1Atlas.Cli;
using S1Atlas.Cli.Configuration;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.IntegrationTests.Foundation;

public sealed class FoundationSafetyTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory;
    private readonly string _dataDirectory;
    private readonly string _steamAppsDirectory;
    private readonly string _gameDirectory;
    private readonly string _manifestPath;

    public FoundationSafetyTests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-safety-tests-{Guid.NewGuid():N}");
        _dataDirectory = Path.Combine(_temporaryDirectory, "data");
        _steamAppsDirectory = Path.Combine(_temporaryDirectory, "Steam", "steamapps");
        _gameDirectory = Path.Combine(
            _steamAppsDirectory,
            "common",
            "Schedule I");
        _manifestPath = Path.Combine(
            _steamAppsDirectory,
            "appmanifest_3164500.acf");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task Scan_DoesNotModifyTheGameInstallation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateFakeInstallationAsync(cancellationToken);
        var filesBefore = CaptureFiles();
        var directoriesBefore = CaptureDirectories();
        var manifestBefore = await File.ReadAllBytesAsync(
            _manifestPath,
            cancellationToken);
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            ["scan", "--game-path", _gameDirectory],
            output,
            error,
            cancellationToken);

        var filesAfter = CaptureFiles();
        var directoriesAfter = CaptureDirectories();
        var manifestAfter = await File.ReadAllBytesAsync(
            _manifestPath,
            cancellationToken);
        Assert.Equal(0, exitCode);
        Assert.Equal(directoriesBefore, directoriesAfter);
        Assert.Equal(filesBefore.Keys, filesAfter.Keys);
        foreach (var relativePath in filesBefore.Keys)
        {
            Assert.Equal(filesBefore[relativePath], filesAfter[relativePath]);
        }

        Assert.Equal(manifestBefore, manifestAfter);
    }

    [Fact]
    public async Task Scan_WhenLaterDiscoveryFails_KeepsThePreviousCurrentBuild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateFakeInstallationAsync(cancellationToken);
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var firstOutput = new StringWriter();
        using var firstError = new StringWriter();
        Assert.Equal(
            0,
            application.Invoke(
                ["scan", "--game-path", _gameDirectory],
                firstOutput,
                firstError,
                cancellationToken));
        var repository = new SqliteAtlasRepository(DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        var before = await repository.GetCurrentSnapshotAsync(cancellationToken);
        Assert.NotNull(before);
        using var failedOutput = new StringWriter();
        using var failedError = new StringWriter();

        var exitCode = application.Invoke(
            ["scan", "--game-path", Path.Combine(_temporaryDirectory, "missing")],
            failedOutput,
            failedError,
            cancellationToken);

        var after = await repository.GetCurrentSnapshotAsync(cancellationToken);
        Assert.Equal(1, exitCode);
        Assert.NotNull(after);
        Assert.Equal(before.Build.BuildId, after.Build.BuildId);
    }

    [Fact]
    public async Task Scan_OfTheSameBuildTwice_KeepsOneImmutableBuildRecord()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateFakeInstallationAsync(cancellationToken);
        var application = new CliApplication(_dataDirectory, "0.1.0-test");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(
                0,
                application.Invoke(
                    ["scan", "--game-path", _gameDirectory],
                    output,
                    error,
                    cancellationToken));
        }

        var repository = new SqliteAtlasRepository(DatabasePath);
        await repository.InitializeAsync(cancellationToken);
        var builds = await repository.ListBuildsAsync(cancellationToken);
        Assert.Single(builds);
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

    private SortedDictionary<string, string> CaptureFiles()
    {
        return Directory
            .EnumerateFiles(_gameDirectory, "*", SearchOption.AllDirectories)
            .ToSortedDictionary(
                path => Path.GetRelativePath(_gameDirectory, path),
                path => Convert.ToHexString(File.ReadAllBytes(path)));
    }

    private string[] CaptureDirectories()
    {
        return Directory
            .EnumerateDirectories(_gameDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_gameDirectory, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task CreateFakeInstallationAsync(CancellationToken cancellationToken)
    {
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
        await File.WriteAllBytesAsync(
            Path.Combine(_gameDirectory, "UserLibs", "S1API.dll"),
            [0],
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
}

internal static class EnumerableExtensions
{
    public static SortedDictionary<TKey, TValue> ToSortedDictionary<TSource, TKey, TValue>(
        this IEnumerable<TSource> source,
        Func<TSource, TKey> keySelector,
        Func<TSource, TValue> valueSelector)
        where TKey : notnull
    {
        var dictionary = new SortedDictionary<TKey, TValue>();
        foreach (var item in source)
        {
            dictionary.Add(keySelector(item), valueSelector(item));
        }

        return dictionary;
    }
}
