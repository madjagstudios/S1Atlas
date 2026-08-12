using S1Atlas.Core.Discovery;
using S1Atlas.Extraction.Discovery;
using S1Atlas.Extraction.Steam;
using Xunit;

namespace S1Atlas.Extraction.Tests.Discovery;

public sealed class WindowsInstallationMetadataReaderTests
{
    [Fact]
    public async Task ReadAsync_CombinesExecutableVersionSteamIdsAndCanonicalPaths()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await InstallationFixture.CreateAsync(
            includeExecutable: true,
            includeManifest: true,
            cancellationToken);
        var reader = new WindowsInstallationMetadataReader(
            new SteamAppManifestLocator(),
            _ => "2022.3.62.7762112");

        var result = await reader.ReadAsync(
            fixture.Installation,
            cancellationToken);

        Assert.Equal("2022.3.62.7762112", result.ExecutableVersion);
        Assert.Equal("3164500", result.SteamAppId);
        Assert.Equal("19420567", result.SteamBuildId);
        Assert.Equal(Path.GetFullPath(fixture.GameDirectory), result.InstallationRoot);
        Assert.Equal(Path.GetFullPath(fixture.GameAssemblyPath), result.GameAssemblyPath);
        Assert.Equal(Path.GetFullPath(fixture.MetadataPath), result.GlobalMetadataPath);
    }

    [Fact]
    public async Task ReadAsync_WhenExecutableIsMissing_LeavesVersionUnknownAndKeepsSteamMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await InstallationFixture.CreateAsync(
            includeExecutable: false,
            includeManifest: true,
            cancellationToken);
        var probeCalled = false;
        var reader = new WindowsInstallationMetadataReader(
            new SteamAppManifestLocator(),
            _ =>
            {
                probeCalled = true;
                return "unexpected";
            });

        var result = await reader.ReadAsync(
            fixture.Installation,
            cancellationToken);

        Assert.Null(result.ExecutableVersion);
        Assert.False(probeCalled);
        Assert.Equal("3164500", result.SteamAppId);
        Assert.Equal("19420567", result.SteamBuildId);
        Assert.Equal(Path.GetFullPath(fixture.GameDirectory), result.InstallationRoot);
    }

    [Fact]
    public async Task ReadAsync_WhenVersionProbeFails_ReturnsUnknownVersion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await InstallationFixture.CreateAsync(
            includeExecutable: true,
            includeManifest: true,
            cancellationToken);
        var reader = new WindowsInstallationMetadataReader(
            new SteamAppManifestLocator(),
            _ => throw new IOException("simulated file-version failure"));

        var result = await reader.ReadAsync(
            fixture.Installation,
            cancellationToken);

        Assert.Null(result.ExecutableVersion);
        Assert.Equal("3164500", result.SteamAppId);
        Assert.Equal("19420567", result.SteamBuildId);
    }

    [Fact]
    public async Task ReadAsync_WhenSteamManifestIsAbsent_ReturnsUnknownSteamIds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await InstallationFixture.CreateAsync(
            includeExecutable: true,
            includeManifest: false,
            cancellationToken);
        var reader = new WindowsInstallationMetadataReader(
            new SteamAppManifestLocator(),
            _ => "2022.3.62.7762112");

        var result = await reader.ReadAsync(
            fixture.Installation,
            cancellationToken);

        Assert.Equal("2022.3.62.7762112", result.ExecutableVersion);
        Assert.Null(result.SteamAppId);
        Assert.Null(result.SteamBuildId);
    }

    [Fact]
    public async Task ReadAsync_DoesNotModifyGameOrSteamFiles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await InstallationFixture.CreateAsync(
            includeExecutable: true,
            includeManifest: true,
            cancellationToken);
        var before = fixture.CaptureFiles();
        var reader = new WindowsInstallationMetadataReader(
            new SteamAppManifestLocator(),
            _ => "2022.3.62.7762112");

        _ = await reader.ReadAsync(
            fixture.Installation,
            cancellationToken);

        Assert.Equal(before, fixture.CaptureFiles());
    }

    private sealed class InstallationFixture : IDisposable
    {
        private InstallationFixture(
            string rootDirectory,
            string steamAppsDirectory,
            string gameDirectory)
        {
            RootDirectory = rootDirectory;
            SteamAppsDirectory = steamAppsDirectory;
            GameDirectory = gameDirectory;
            ExecutablePath = Path.Combine(gameDirectory, "Schedule I.exe");
            GameAssemblyPath = Path.Combine(gameDirectory, "GameAssembly.dll");
            MetadataPath = Path.Combine(
                gameDirectory,
                "Schedule I_Data",
                "il2cpp_data",
                "Metadata",
                "global-metadata.dat");
            Installation = new ScheduleOneInstallation(
                RootPath: gameDirectory,
                ExecutablePath: ExecutablePath,
                GameAssemblyPath: GameAssemblyPath,
                GlobalMetadataPath: MetadataPath,
                ModsPath: Path.Combine(gameDirectory, "Mods"),
                MelonLoaderPath: Path.Combine(gameDirectory, "MelonLoader"));
        }

        public string RootDirectory { get; }
        public string SteamAppsDirectory { get; }
        public string GameDirectory { get; }
        public string ExecutablePath { get; }
        public string GameAssemblyPath { get; }
        public string MetadataPath { get; }
        public ScheduleOneInstallation Installation { get; }

        public static async Task<InstallationFixture> CreateAsync(
            bool includeExecutable,
            bool includeManifest,
            CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "S1Atlas.Tests",
                Guid.NewGuid().ToString("N"));
            var steamApps = Path.Combine(root, "steamapps");
            var game = Path.Combine(steamApps, "common", "Schedule I");
            var fixture = new InstallationFixture(root, steamApps, game);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.MetadataPath)!);
            Directory.CreateDirectory(Path.Combine(game, "Mods"));
            Directory.CreateDirectory(Path.Combine(game, "MelonLoader"));
            await File.WriteAllBytesAsync(
                fixture.GameAssemblyPath,
                [1, 2, 3],
                cancellationToken);
            await File.WriteAllBytesAsync(
                fixture.MetadataPath,
                [4, 5, 6],
                cancellationToken);

            if (includeExecutable)
            {
                await File.WriteAllBytesAsync(
                    fixture.ExecutablePath,
                    [7, 8, 9],
                    cancellationToken);
            }

            if (includeManifest)
            {
                var manifest = """
                    "AppState"
                    {
                        "appid"      "3164500"
                        "installdir" "Schedule I"
                        "buildid"    "19420567"
                    }
                    """;
                await File.WriteAllTextAsync(
                    Path.Combine(steamApps, "appmanifest_3164500.acf"),
                    manifest,
                    cancellationToken);
            }

            return fixture;
        }

        public string[] CaptureFiles()
        {
            return Directory
                .EnumerateFiles(RootDirectory, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path =>
                    $"{Path.GetRelativePath(RootDirectory, path)}:{Convert.ToHexString(File.ReadAllBytes(path))}")
                .ToArray();
        }

        public void Dispose()
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
    }
}
