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

        var result = await locator.LocateAsync(fixture.RootPath, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(fixture.RootPath, result.RootPath);
        Assert.Equal(fixture.GameAssemblyPath, result.GameAssemblyPath);
        Assert.Equal(fixture.MetadataPath, result.GlobalMetadataPath);
    }

    [Fact]
    public async Task LocateAsync_WhenRequiredMetadataIsMissing_ReturnsNull()
    {
        using var fixture = FakeScheduleOneInstall.Create(includeMetadata: false);
        var locator = new WindowsScheduleOneLocator();

        var result = await locator.LocateAsync(fixture.RootPath, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task LocateAsync_WhenGameAssemblyIsMissing_ReturnsNull()
    {
        using var fixture = FakeScheduleOneInstall.Create(includeGameAssembly: false);
        var locator = new WindowsScheduleOneLocator();

        var result = await locator.LocateAsync(fixture.RootPath, CancellationToken.None);

        Assert.Null(result);
    }

    private sealed class FakeScheduleOneInstall : IDisposable
    {
        private FakeScheduleOneInstall(string rootPath)
        {
            RootPath = rootPath;
            GameAssemblyPath = Path.Combine(rootPath, "GameAssembly.dll");
            MetadataPath = Path.Combine(rootPath, "Schedule I_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        }

        public string RootPath { get; }
        public string GameAssemblyPath { get; }
        public string MetadataPath { get; }

        public static FakeScheduleOneInstall Create(bool includeMetadata = true, bool includeGameAssembly = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "S1Atlas.Tests", Guid.NewGuid().ToString("N"));
            var fixture = new FakeScheduleOneInstall(root);

            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "Mods"));
            Directory.CreateDirectory(Path.Combine(root, "MelonLoader"));

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
