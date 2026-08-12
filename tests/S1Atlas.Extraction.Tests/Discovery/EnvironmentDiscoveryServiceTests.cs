using S1Atlas.Core.Builds;
using S1Atlas.Core.Discovery;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Hashing;
using S1Atlas.Extraction.Discovery;
using Xunit;

namespace S1Atlas.Extraction.Tests.Discovery;

public sealed class EnvironmentDiscoveryServiceTests
{
    [Fact]
    public async Task DiscoverAsync_WithValidInstallation_CreatesFingerprintAndSnapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var installation = new ScheduleOneInstallation(
            RootPath: "C:\\Fake\\Schedule I",
            ExecutablePath: "C:\\Fake\\Schedule I\\Schedule I.exe",
            GameAssemblyPath: "C:\\Fake\\Schedule I\\GameAssembly.dll",
            GlobalMetadataPath: "C:\\Fake\\Schedule I\\Schedule I_Data\\il2cpp_data\\Metadata\\global-metadata.dat",
            ModsPath: "C:\\Fake\\Schedule I\\Mods",
            MelonLoaderPath: "C:\\Fake\\Schedule I\\MelonLoader");
        var dependencies = new[]
        {
            new DependencyVersion(DependencyKind.S1Api, "3.0.0", "S1API.dll", true)
        };
        var service = new EnvironmentDiscoveryService(
            new FakeLocator(installation),
            new FakeHasher("assembly-hash", "metadata-hash"),
            new FakeDependencyDetector(dependencies));

        var result = await service.DiscoverAsync(
            "C:\\Fake\\Schedule I",
            "0.1.0",
            cancellationToken);

        Assert.NotNull(result);
        Assert.Equal(
            BuildFingerprint.Create("assembly-hash", "metadata-hash"),
            result.Build.BuildId);
        Assert.Equal("assembly-hash", result.Build.GameAssemblySha256);
        Assert.Equal("metadata-hash", result.Build.MetadataSha256);
        Assert.True(result.Build.IsValid);
        Assert.Equal("0.1.0", result.AtlasVersion);
        Assert.Single(result.Dependencies);
    }

    [Fact]
    public async Task DiscoverAsync_WhenInstallationCannotBeLocated_ReturnsNull()
    {
        var service = new EnvironmentDiscoveryService(
            new FakeLocator(null),
            new FakeHasher("unused", "unused"),
            new FakeDependencyDetector([]));

        var result = await service.DiscoverAsync(
            null,
            "0.1.0",
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    private sealed class FakeLocator(
        ScheduleOneInstallation? installation) : IScheduleOneLocator
    {
        public Task<ScheduleOneInstallation?> LocateAsync(
            string? overridePath,
            CancellationToken cancellationToken) =>
            Task.FromResult(installation);
    }

    private sealed class FakeHasher(params string[] hashes) : IFileHasher
    {
        private int _index;

        public Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken) =>
            Task.FromResult(hashes[_index++]);
    }

    private sealed class FakeDependencyDetector(
        IReadOnlyList<DependencyVersion> dependencies) : IDependencyDetector
    {
        public IReadOnlyList<DependencyVersion> Detect(
            ScheduleOneInstallation installation) =>
            dependencies;
    }
}
