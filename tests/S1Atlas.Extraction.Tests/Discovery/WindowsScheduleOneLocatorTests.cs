using S1Atlas.Extraction.Discovery;
using Xunit;

namespace S1Atlas.Extraction.Tests.Discovery;

public sealed class WindowsScheduleOneLocatorTests
{
    [Fact]
    public async Task LocateAsync_WithValidOverride_ReturnsInstallation()
    {
        using var fixture = FakeScheduleOneInstall.Create();
        var locator = new WindowsScheduleOneLocator();

        var result = await locator.LocateAsync(
            fixture.RootPath,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(fixture.RootPath, result.RootPath);
        Assert.Equal(fixture.ExecutablePath, result.ExecutablePath);
        Assert.Equal(fixture.GameAssemblyPath, result.GameAssemblyPath);
        Assert.Equal(fixture.MetadataPath, result.GlobalMetadataPath);
    }

    [Fact]
    public async Task LocateAsync_WhenRequiredMetadataIsMissing_ReturnsNull()
    {
        using var fixture = FakeScheduleOneInstall.Create(includeMetadata: false);
        var locator = new WindowsScheduleOneLocator();

        var result = await locator.LocateAsync(
            fixture.RootPath,
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task LocateAsync_WhenGameAssemblyIsMissing_ReturnsNull()
    {
        using var fixture = FakeScheduleOneInstall.Create(includeGameAssembly: false);
        var locator = new WindowsScheduleOneLocator();

        var result = await locator.LocateAsync(
            fixture.RootPath,
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task LocateAsync_UsesAdditionalQuotedSteamLibraryPath()
    {
        var container = Path.Combine(
            Path.GetTempPath(),
            "s1atlas-steam-tests",
            Guid.NewGuid().ToString("N"));
        var libraryRoot = Path.Combine(container, "Library");
        using var fixture = FakeScheduleOneInstall.Create(
            rootPath: Path.Combine(
                libraryRoot,
                "steamapps",
                "common",
                "Schedule I"));
        var escapedLibraryRoot = libraryRoot.Replace("\\", "\\\\", StringComparison.Ordinal);
        var steamRoot = Path.Combine(container, "Steam");
        Directory.CreateDirectory(Path.Combine(steamRoot, "config"));
        File.WriteAllText(
            Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
            $$"""
            "libraryfolders"
            {
                "0" { "path" "{{escapedLibraryRoot}}" }
            }
            """);
        try
        {
            var locator = new WindowsScheduleOneLocator(
                new TestCandidateSource([Path.Combine(steamRoot, "steamapps", "common", "Schedule I")]));

            var result = await locator.LocateAsync(
                overridePath: null,
                TestContext.Current.CancellationToken);

            Assert.NotNull(result);
            Assert.Equal(Path.GetFullPath(fixture.RootPath), result.RootPath);
        }
        finally
        {
            if (Directory.Exists(container))
            {
                Directory.Delete(container, recursive: true);
            }
        }
    }

    [Fact]
    public void GetCandidatePaths_DeduplicatesCaseInsensitivelyInDeterministicOrder()
    {
        var first = Path.Combine(Path.GetTempPath(), "Steam", "steamapps", "common", "Schedule I");
        var second = Path.Combine(Path.GetTempPath(), "Library", "steamapps", "common", "Schedule I");
        var locator = new WindowsScheduleOneLocator(
            new TestCandidateSource([first, first.ToUpperInvariant(), second]));

        var candidates = locator.GetCandidatePaths(overridePath: null);

        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], candidates);
    }

    [Fact]
    public async Task LocateAsync_MalformedOrLockedLibraryFile_IsIgnored()
    {
        var steamRoot = Path.Combine(
            Path.GetTempPath(),
            "s1atlas-steam-tests",
            Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(steamRoot, "config", "libraryfolders.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "not vdf");
        await using var locked = new FileStream(
            configPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var locator = new WindowsScheduleOneLocator(
            new TestCandidateSource([Path.Combine(steamRoot, "steamapps", "common", "Schedule I")]));

        var result = await locator.LocateAsync(
            overridePath: null,
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        locked.Close();
        Directory.Delete(steamRoot, recursive: true);
    }

    private sealed class TestCandidateSource(IReadOnlyList<string> candidates)
        : IWindowsScheduleOneCandidateSource
    {
        public IReadOnlyList<string> GetCandidatePaths() => candidates;
    }

    private sealed class FakeScheduleOneInstall : IDisposable
    {
        private FakeScheduleOneInstall(string rootPath)
        {
            RootPath = rootPath;
            ExecutablePath = Path.Combine(rootPath, "Schedule I.exe");
            GameAssemblyPath = Path.Combine(rootPath, "GameAssembly.dll");
            MetadataPath = Path.Combine(
                rootPath,
                "Schedule I_Data",
                "il2cpp_data",
                "Metadata",
                "global-metadata.dat");
        }

        public string RootPath { get; }
        public string ExecutablePath { get; }
        public string GameAssemblyPath { get; }
        public string MetadataPath { get; }

        public static FakeScheduleOneInstall Create(
            bool includeMetadata = true,
            bool includeGameAssembly = true,
            string? rootPath = null)
        {
            var root = rootPath ?? Path.Combine(
                Path.GetTempPath(),
                "S1Atlas.Tests",
                Guid.NewGuid().ToString("N"));
            var fixture = new FakeScheduleOneInstall(root);

            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "Mods"));
            Directory.CreateDirectory(Path.Combine(root, "MelonLoader"));
            File.WriteAllBytes(fixture.ExecutablePath, [0x00]);

            if (includeGameAssembly)
            {
                File.WriteAllBytes(fixture.GameAssemblyPath, [0x01]);
            }

            if (includeMetadata)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.MetadataPath)!);
                File.WriteAllBytes(fixture.MetadataPath, [0x02]);
            }

            return fixture;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
