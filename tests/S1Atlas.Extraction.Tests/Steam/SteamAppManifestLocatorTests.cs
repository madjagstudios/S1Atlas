using S1Atlas.Extraction.Steam;
using Xunit;

namespace S1Atlas.Extraction.Tests.Steam;

public sealed class SteamAppManifestLocatorTests
{
    [Fact]
    public async Task LocateAsync_MatchingInstallDirectory_ReturnsAppAndBuildIds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await SteamLayoutFixture.CreateAsync(cancellationToken);
        await fixture.WriteManifestAsync(
            "appmanifest_111.acf",
            "111",
            "Other Game",
            "100",
            cancellationToken);
        await fixture.WriteManifestAsync(
            "appmanifest_3164500.acf",
            "3164500",
            "Schedule I",
            "19420567",
            cancellationToken);
        var locator = new SteamAppManifestLocator();

        var result = await locator.LocateAsync(
            fixture.GameDirectory,
            cancellationToken);

        Assert.NotNull(result);
        Assert.Equal("3164500", result.AppId);
        Assert.Equal("Schedule I", result.InstallDirectory);
        Assert.Equal("19420567", result.BuildId);
    }

    [Fact]
    public async Task LocateAsync_UnrelatedManifests_ReturnsUnknown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await SteamLayoutFixture.CreateAsync(cancellationToken);
        await fixture.WriteManifestAsync(
            "appmanifest_111.acf",
            "111",
            "Other Game",
            "100",
            cancellationToken);
        var locator = new SteamAppManifestLocator();

        var result = await locator.LocateAsync(
            fixture.GameDirectory,
            cancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task LocateAsync_MalformedMatchingFile_ReturnsUnknown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await SteamLayoutFixture.CreateAsync(cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.SteamAppsDirectory, "appmanifest_3164500.acf"),
            "\"AppState\" { \"appid\" \"3164500\"",
            cancellationToken);
        var locator = new SteamAppManifestLocator();

        var result = await locator.LocateAsync(
            fixture.GameDirectory,
            cancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task LocateAsync_NonSteamInstallation_ReturnsUnknown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "S1Atlas.Tests",
            Guid.NewGuid().ToString("N"),
            "Schedule I");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            var locator = new SteamAppManifestLocator();

            var result = await locator.LocateAsync(
                temporaryDirectory,
                cancellationToken);

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(
                Directory.GetParent(temporaryDirectory)!.FullName,
                recursive: true);
        }
    }

    private sealed class SteamLayoutFixture : IDisposable
    {
        private SteamLayoutFixture(
            string rootDirectory,
            string steamAppsDirectory,
            string gameDirectory)
        {
            RootDirectory = rootDirectory;
            SteamAppsDirectory = steamAppsDirectory;
            GameDirectory = gameDirectory;
        }

        public string RootDirectory { get; }
        public string SteamAppsDirectory { get; }
        public string GameDirectory { get; }

        public static Task<SteamLayoutFixture> CreateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.Combine(
                Path.GetTempPath(),
                "S1Atlas.Tests",
                Guid.NewGuid().ToString("N"));
            var steamApps = Path.Combine(root, "steamapps");
            var game = Path.Combine(steamApps, "common", "Schedule I");
            Directory.CreateDirectory(game);
            return Task.FromResult(new SteamLayoutFixture(root, steamApps, game));
        }

        public Task WriteManifestAsync(
            string fileName,
            string appId,
            string installDirectory,
            string buildId,
            CancellationToken cancellationToken)
        {
            var content = $$"""
                "AppState"
                {
                    "appid"      "{{appId}}"
                    "installdir" "{{installDirectory}}"
                    "buildid"    "{{buildId}}"
                }
                """;
            return File.WriteAllTextAsync(
                Path.Combine(SteamAppsDirectory, fileName),
                content,
                cancellationToken);
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
