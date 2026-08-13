using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests.Sqlite;

public sealed class EnvironmentSnapshotIdTests
{
    [Fact]
    public void Create_WithIdenticalV2Snapshots_ReturnsSameId()
    {
        var first = CreateSnapshot();
        var second = CreateSnapshot();

        Assert.Equal(
            EnvironmentSnapshotId.Create(first),
            EnvironmentSnapshotId.Create(second));
    }

    [Fact]
    public void Create_WhenCaptureTimestampChanges_ReturnsSameId()
    {
        var first = CreateSnapshot();
        var second = CreateSnapshot(
            capturedAtUtc: DateTimeOffset.Parse("2026-08-12T13:00:00Z"));

        Assert.Equal(
            EnvironmentSnapshotId.Create(first),
            EnvironmentSnapshotId.Create(second));
    }

    [Fact]
    public void Create_WhenExecutableVersionChanges_ReturnsDifferentId()
    {
        var first = CreateSnapshot();
        var second = CreateSnapshot(executableVersion: "2022.3.63.0");

        Assert.NotEqual(
            EnvironmentSnapshotId.Create(first),
            EnvironmentSnapshotId.Create(second));
    }

    [Fact]
    public void Create_WhenSteamBuildIdChanges_ReturnsDifferentId()
    {
        var first = CreateSnapshot();
        var second = CreateSnapshot(steamBuildId: "19420568");

        Assert.NotEqual(
            EnvironmentSnapshotId.Create(first),
            EnvironmentSnapshotId.Create(second));
    }

    [Fact]
    public void Create_WhenInstallationPathChanges_ReturnsDifferentId()
    {
        var first = CreateSnapshot();
        var second = CreateSnapshot(
            installationRoot: "D:\\Steam\\steamapps\\common\\Schedule I");

        Assert.NotEqual(
            EnvironmentSnapshotId.Create(first),
            EnvironmentSnapshotId.Create(second));
    }

    [Fact]
    public void Create_WhenDependencyChanges_ReturnsDifferentId()
    {
        var first = CreateSnapshot();
        var second = CreateSnapshot(
            dependencies:
            [
                new DependencyVersion(
                    DependencyKind.S1Api,
                    "4.0.0",
                    "C:\\Game\\Mods\\S1API.dll",
                    true)
            ]);

        Assert.NotEqual(
            EnvironmentSnapshotId.Create(first),
            EnvironmentSnapshotId.Create(second));
    }

    [Fact]
    public void Create_WhenIdentityVersionIsOne_RejectsNewIdCreation()
    {
        var snapshot = CreateSnapshot(identityVersion: 1);

        var exception = Assert.Throws<InvalidOperationException>(
            () => EnvironmentSnapshotId.Create(snapshot));

        Assert.Contains(
            "identity-version 2",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static EnvironmentSnapshot CreateSnapshot(
        int identityVersion = 2,
        string? executableVersion = "2022.3.62.7762112",
        string? steamBuildId = "19420567",
        string installationRoot = "C:\\Steam\\steamapps\\common\\Schedule I",
        IReadOnlyList<DependencyVersion>? dependencies = null,
        DateTimeOffset? capturedAtUtc = null)
    {
        var gameAssemblyPath = Path.Combine(
            installationRoot,
            "GameAssembly.dll");
        var metadataPath = Path.Combine(
            installationRoot,
            "Schedule I_Data",
            "il2cpp_data",
            "Metadata",
            "global-metadata.dat");

        return new EnvironmentSnapshot(
            identityVersion,
            new GameBuild(
                "build-a",
                "assembly-hash",
                "metadata-hash",
                DateTimeOffset.Parse("2026-08-12T12:00:00Z"),
                true),
            new InstallationObservation(
                executableVersion,
                "3164500",
                steamBuildId,
                installationRoot,
                gameAssemblyPath,
                metadataPath),
            dependencies ??
            [
                new DependencyVersion(
                    DependencyKind.S1Api,
                    "3.1.12.0",
                    "C:\\Game\\Mods\\S1API.dll",
                    true)
            ],
            "0.2.0",
            capturedAtUtc ?? DateTimeOffset.Parse("2026-08-12T12:00:00Z"));
    }
}
