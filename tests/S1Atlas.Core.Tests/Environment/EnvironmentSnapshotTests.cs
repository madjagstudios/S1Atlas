using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using Xunit;

namespace S1Atlas.Core.Tests.Environment;

public sealed class EnvironmentSnapshotTests
{
    [Fact]
    public void Constructor_SeparatesBuildIdentityFromInstallationObservation()
    {
        var build = new GameBuild(
            "build-a",
            "assembly-hash",
            "metadata-hash",
            DateTimeOffset.Parse("2026-08-12T12:00:00Z"),
            true);
        var observation = new InstallationObservation(
            "2022.3.62.7762112",
            "3164500",
            "19420567",
            "C:\\Steam\\steamapps\\common\\Schedule I",
            "C:\\Steam\\steamapps\\common\\Schedule I\\GameAssembly.dll",
            "C:\\Steam\\steamapps\\common\\Schedule I\\Schedule I_Data\\il2cpp_data\\Metadata\\global-metadata.dat");

        var snapshot = new EnvironmentSnapshot(
            2,
            build,
            observation,
            [],
            "0.2.0",
            DateTimeOffset.Parse("2026-08-12T12:00:00Z"));

        Assert.Equal(2, snapshot.IdentityVersion);
        Assert.Equal("build-a", snapshot.Build.BuildId);
        Assert.Equal("19420567", snapshot.Installation.SteamBuildId);
        Assert.Equal("assembly-hash", snapshot.Build.GameAssemblySha256);
    }
}
