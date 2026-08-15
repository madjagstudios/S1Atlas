using System.Security.Cryptography;
using S1Atlas.Extraction.Scene;

namespace S1Atlas.Extraction.Tests.Scene;

internal static class SerializedFileFixtureBuilder
{
    private const string UnityVersion = "2022.3.62f1";
    private const int SerializedFileVersion = 22;

    public static SerializedFileFixture CreateSceneGraph()
    {
        var fixture = SerializedFileFixture.Create();
        fixture.Add(
            "Fixture_Data/level0",
            SanitizedSerializedFileFixture.CreateBytes(
                "sanitized-scene-graph",
                prefabClassId: null,
                includeTypeTree: true));
        fixture.Add(
            "Fixture_Data/sharedassets0.assets",
            SanitizedSerializedFileFixture.CreateBytes(
                "sanitized-external-targets",
                prefabClassId: null,
                includeTypeTree: true));
        return fixture;
    }

    public static SerializedFileFixture CreatePrefabEvidence()
    {
        var fixture = SerializedFileFixture.Create();
        fixture.Add(
            "Fixture_Data/sharedassets0.assets",
            SanitizedSerializedFileFixture.CreateBytes(
                "sanitized-prefab-evidence",
                prefabClassId: 1001,
                includeTypeTree: true));
        return fixture;
    }

    internal sealed class SerializedFileFixture : IDisposable
    {
        private readonly List<VerifiedSceneContainer> _containers = [];
        private readonly List<string> _paths = [];

        private SerializedFileFixture(string rootPath)
        {
            RootPath = rootPath;
        }

        public string RootPath { get; }
        public IReadOnlyList<VerifiedSceneContainer> VerifiedContainers => _containers;
        public IReadOnlyList<string> SerializedFilePaths => _paths;

        public static SerializedFileFixture Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "s1atlas-serialized-fixtures",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new SerializedFileFixture(root);
        }

        public void Add(string relativePath, byte[] bytes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            ArgumentNullException.ThrowIfNull(bytes);
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var path = Path.Combine(RootPath, normalized);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            _paths.Add(path);
            _containers.Add(new VerifiedSceneContainer(
                relativePath,
                path,
                [],
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.LongLength,
                UnityVersion,
                SerializedFileVersion,
                "[]"));
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
    }
}
