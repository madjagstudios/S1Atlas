using System.Security.Cryptography;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;

namespace S1Atlas.Extraction.Tests.Inputs;

internal sealed class InputTestFixture : IDisposable
{
    private static readonly byte[] AssemblyBytes = [0x10, 0x20, 0x30, 0x40];
    private static readonly byte[] MetadataBytes = [0x50, 0x60, 0x70];

    private InputTestFixture(string rootPath)
    {
        RootPath = rootPath;
        GameAssemblyPath = Path.Combine(rootPath, "GameAssembly.dll");
        MetadataPath = Path.Combine(
            rootPath,
            "Schedule I_Data",
            "il2cpp_data",
            "Metadata",
            "global-metadata.dat");
        ExecutablePath = Path.Combine(rootPath, "Schedule I.exe");
        UnityVersionPath = Path.Combine(rootPath, "Schedule I_Data", "globalgamemanagers");
    }

    public string RootPath { get; }
    public string GameAssemblyPath { get; }
    public string MetadataPath { get; }
    public string ExecutablePath { get; }
    public string UnityVersionPath { get; }

    public static ExtractionProfile Profile { get; } = new(
        SchemaVersion: 1,
        ProfileId: "test-profile",
        ProfileVersion: 1,
        AdapterVersion: 1,
        ExtractionSchemaVersion: 1,
        ExecutableName: "Schedule I",
        OutputFormat: "dll_il_recovery",
        Timeout: TimeSpan.FromMinutes(1),
        MaximumRetainedStandardOutputBytes: 1024,
        MaximumRetainedStandardErrorBytes: 1024,
        AcceptedExitCodes: [0],
        RequiredAssemblyIdentities: ["Assembly-CSharp"],
        SnapshotInputs:
        [
            new("GameAssembly.dll", "gameAssembly"),
            new(
                "Schedule I_Data/il2cpp_data/Metadata/global-metadata.dat",
                "globalMetadata"),
            new("Schedule I.exe", "executableSupport")
        ],
        UnityVersionSources:
        [
            "Schedule I_Data/globalgamemanagers",
            "Schedule I_Data/data.unity3d"
        ]);

    public GameBuild Build => new(
        BuildId: "test-build",
        GameAssemblySha256: Hash(AssemblyBytes),
        MetadataSha256: Hash(MetadataBytes),
        FirstSeenAtUtc: new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero),
        IsValid: true);

    public static InputTestFixture Create(
        bool includeAssembly = true,
        bool includeMetadata = true,
        bool includeExecutable = true,
        bool includeUnityVersion = true)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "s1atlas-input-tests",
            Guid.NewGuid().ToString("N"));
        var fixture = new InputTestFixture(root);
        Directory.CreateDirectory(root);

        if (includeAssembly)
        {
            File.WriteAllBytes(fixture.GameAssemblyPath, AssemblyBytes);
        }

        if (includeMetadata)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.MetadataPath)!);
            File.WriteAllBytes(fixture.MetadataPath, MetadataBytes);
        }

        if (includeExecutable)
        {
            File.WriteAllBytes(fixture.ExecutablePath, [0x80, 0x90]);
        }

        if (includeUnityVersion)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.UnityVersionPath)!);
            File.WriteAllBytes(fixture.UnityVersionPath, [0xa0]);
        }

        return fixture;
    }

    public static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
