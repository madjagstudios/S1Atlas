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
    public async Task DiscoverAsync_WithValidInstallation_CreatesV2FingerprintAndObservation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var capturedAt = DateTimeOffset.Parse("2026-08-12T12:00:00Z");
        var installation = new ScheduleOneInstallation(
            RootPath: "C:\\Fake\\Schedule I",
            ExecutablePath: "C:\\Fake\\Schedule I\\Schedule I.exe",
            GameAssemblyPath: "C:\\Fake\\Schedule I\\GameAssembly.dll",
            GlobalMetadataPath: "C:\\Fake\\Schedule I\\Schedule I_Data\\il2cpp_data\\Metadata\\global-metadata.dat",
            ModsPath: "C:\\Fake\\Schedule I\\Mods",
            MelonLoaderPath: "C:\\Fake\\Schedule I\\MelonLoader");
        var observation = new InstallationObservation(
            "2022.3.62.7762112",
            "3164500",
            "19420567",
            installation.RootPath,
            installation.GameAssemblyPath,
            installation.GlobalMetadataPath);
        var dependencies = new[]
        {
            new DependencyVersion(DependencyKind.S1Api, "3.0.0", "S1API.dll", true)
        };
        var service = new EnvironmentDiscoveryService(
            new FakeLocator(installation),
            new FakeHasher("assembly-hash", "metadata-hash"),
            new FakeDependencyDetector(dependencies),
            new FakeInstallationMetadataReader(observation),
            new FixedTimeProvider(capturedAt));

        var result = await service.DiscoverAsync(
            installation.RootPath,
            "0.2.0",
            cancellationToken);

        Assert.NotNull(result);
        Assert.Equal(2, result.IdentityVersion);
        Assert.Equal(
            BuildFingerprint.Create("assembly-hash", "metadata-hash"),
            result.Build.BuildId);
        Assert.Equal("assembly-hash", result.Build.GameAssemblySha256);
        Assert.Equal("metadata-hash", result.Build.MetadataSha256);
        Assert.Equal(capturedAt, result.Build.FirstSeenAtUtc);
        Assert.True(result.Build.IsValid);
        Assert.Equal(observation, result.Installation);
        Assert.Equal("0.2.0", result.AtlasVersion);
        Assert.Equal(capturedAt, result.CapturedAtUtc);
        Assert.Single(result.Dependencies);
    }

    [Fact]
    public async Task DiscoverAsync_WhenInstallationCannotBeLocated_ReturnsNullWithoutReadingMetadata()
    {
        var metadataReader = new FakeInstallationMetadataReader(
            InstallationObservation.Unknown);
        var service = new EnvironmentDiscoveryService(
            new FakeLocator(null),
            new FakeHasher("unused", "unused"),
            new FakeDependencyDetector([]),
            metadataReader);

        var result = await service.DiscoverAsync(
            null,
            "0.2.0",
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(0, metadataReader.CallCount);
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

    private sealed class FakeInstallationMetadataReader(
        InstallationObservation observation) : IInstallationMetadataReader
    {
        public int CallCount { get; private set; }

        public Task<InstallationObservation> ReadAsync(
            ScheduleOneInstallation installation,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(observation);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
