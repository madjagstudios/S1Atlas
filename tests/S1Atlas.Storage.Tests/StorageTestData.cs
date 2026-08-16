using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.Storage.Tests;

internal static class StorageTestData
{
    public static Task SeedCurrentScheduleIBuildAsync(
        SqliteAtlasRepository repository,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.Parse("2026-08-13T01:00:00Z");
        var root = Path.GetFullPath(@"C:\games\build-a");

        return repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                IdentityVersion: 2,
                Build: new GameBuild(
                    "build-a",
                    "assembly-build-a",
                    "metadata-build-a",
                    timestamp,
                    IsValid: true),
                Installation: new InstallationObservation(
                    "2022.3",
                    "3164500",
                    "123",
                    root,
                    Path.Combine(root, "GameAssembly.dll"),
                    Path.Combine(root, "global-metadata.dat")),
                Dependencies: [],
                AtlasVersion: "0.2.0-test",
                CapturedAtUtc: timestamp),
            cancellationToken);
    }
}
