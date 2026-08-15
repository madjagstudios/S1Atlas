using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using S1Atlas.Extraction.Scene;
using Xunit;

namespace S1Atlas.Extraction.Tests.Scene;

public sealed class AssetsToolsUnitySerializedFileParserTests
{
    [Fact]
    public async Task ParseAsync_Unity2022Fixture_MapsHeaderAndSupportedObjectTableClassIds()
    {
        using var fixture = SanitizedSerializedFileFixture.Create();
        var parser = new AssetsToolsUnitySerializedFileParser();

        var containers = await parser.ParseAsync(
        [
            fixture.VerifiedContainer
        ], TestContext.Current.CancellationToken);

        var container = Assert.Single(containers);
        Assert.Equal("Schedule I_Data/level0", container.RelativePath);
        Assert.Equal("2022.3.62f1", container.UnityVersion);
        Assert.Equal(22, container.SerializedFileVersion);
        Assert.Collection(
            container.Objects,
            item => AssertObject(item, 101, 1, ParsedSceneObjectKind.GameObject),
            item => AssertObject(item, 102, 4, ParsedSceneObjectKind.Transform),
            item => AssertObject(item, 103, 114, ParsedSceneObjectKind.MonoBehaviour),
            item => AssertObject(item, 104, 115, ParsedSceneObjectKind.MonoScript));
        Assert.False(container.HasPrefabEvidence);
    }

    [Fact]
    public async Task ParseAsync_Unity2022Fixture_MapsExternalFileReferences()
    {
        using var fixture = SanitizedSerializedFileFixture.Create();
        var parser = new AssetsToolsUnitySerializedFileParser();

        var container = Assert.Single(await parser.ParseAsync(
            [fixture.VerifiedContainer],
            TestContext.Current.CancellationToken));

        var external = Assert.Single(container.ExternalReferences);
        Assert.Equal(1, external.FileId);
        Assert.Equal("archive:/CAB-sanitized/CAB-sanitized", external.PathName);
        Assert.Equal(
            "archive:/CAB-sanitized/CAB-sanitized",
            external.OriginalPathName);
    }

    [Fact]
    public async Task ParseAsync_PrefabMarkerWithoutPrefabClassId_DoesNotClaimPrefabEvidence()
    {
        using var fixture = SanitizedSerializedFileFixture.Create(
            objectPayloadMarker: "Prefab PrefabInstance");
        var parser = new AssetsToolsUnitySerializedFileParser();

        var container = Assert.Single(await parser.ParseAsync(
            [fixture.VerifiedContainer],
            TestContext.Current.CancellationToken));

        Assert.False(container.HasPrefabEvidence);
        Assert.DoesNotContain(
            container.Objects,
            item => item.Kind == ParsedSceneObjectKind.PrefabEvidence);
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(1001480554)]
    public async Task ParseAsync_PrefabClassId_RecordsPrefabEvidence(int prefabClassId)
    {
        using var fixture = SanitizedSerializedFileFixture.Create(
            prefabClassId: prefabClassId);
        var parser = new AssetsToolsUnitySerializedFileParser();

        var container = Assert.Single(await parser.ParseAsync(
            [fixture.VerifiedContainer],
            TestContext.Current.CancellationToken));

        Assert.True(container.HasPrefabEvidence);
        var prefab = Assert.Single(
            container.Objects,
            item => item.Kind == ParsedSceneObjectKind.PrefabEvidence);
        Assert.Equal(prefabClassId, prefab.UnityClassId);
    }

    [Fact]
    public void ExtractionPublicApi_DoesNotExposeAssetsToolsTypes()
    {
        var extractionAssembly = typeof(AssetsToolsUnitySerializedFileParser).Assembly;
        var leakedTypes = extractionAssembly.ExportedTypes
            .SelectMany(GetPublicApiTypes)
            .Where(type => type.Namespace?.StartsWith("AssetsTools.NET", StringComparison.Ordinal) == true)
            .Select(type => type.FullName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(leakedTypes);
    }

    private static void AssertObject(
        ParsedSceneObject item,
        long localFileId,
        int classId,
        ParsedSceneObjectKind kind)
    {
        Assert.Equal(localFileId, item.LocalFileId);
        Assert.Equal(classId, item.UnityClassId);
        Assert.Equal(kind, item.Kind);
        Assert.True(item.ByteCount > 0);
    }

    private static IEnumerable<Type> GetPublicApiTypes(Type type)
    {
        yield return type;
        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            switch (member)
            {
                case MethodInfo method:
                    yield return method.ReturnType;
                    foreach (var parameter in method.GetParameters())
                    {
                        yield return parameter.ParameterType;
                    }
                    break;
                case PropertyInfo property:
                    yield return property.PropertyType;
                    break;
                case FieldInfo field:
                    yield return field.FieldType;
                    break;
            }
        }
    }
}

internal sealed class SanitizedSerializedFileFixture : IDisposable
{
    private const string UnityVersion = "2022.3.62f1";
    private const uint SerializedFileVersion = 22;

    private SanitizedSerializedFileFixture(string rootPath, string primaryPath)
    {
        RootPath = rootPath;
        PrimaryPath = primaryPath;
        var bytes = File.ReadAllBytes(primaryPath);
        VerifiedContainer = new VerifiedSceneContainer(
            "Schedule I_Data/level0",
            primaryPath,
            [],
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.LongLength,
            UnityVersion,
            (int)SerializedFileVersion,
            "[]");
    }

    public string RootPath { get; }
    public string PrimaryPath { get; }
    public VerifiedSceneContainer VerifiedContainer { get; }

    public static SanitizedSerializedFileFixture Create(
        string objectPayloadMarker = "sanitized-object-data",
        int? prefabClassId = null)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "s1atlas-scene-parser-tests",
            Guid.NewGuid().ToString("N"));
        var primary = Path.Combine(root, "Schedule I_Data", "level0");
        Directory.CreateDirectory(Path.GetDirectoryName(primary)!);
        File.WriteAllBytes(primary, CreateBytes(objectPayloadMarker, prefabClassId));
        return new SanitizedSerializedFileFixture(root, primary);
    }

    private static byte[] CreateBytes(string objectPayloadMarker, int? prefabClassId)
    {
        var classIds = new List<int> { 1, 4, 114, 115 };
        if (prefabClassId is not null)
        {
            classIds.Add(prefabClassId.Value);
        }

        var types = classIds.Select(classId => new TypeTreeType
        {
            TypeId = classId,
            ScriptTypeIndex = ushort.MaxValue,
            ScriptIdHash = new Hash128(new byte[16]),
            TypeHash = new Hash128(new byte[16]),
            ExtTypeHash = new Hash128(new byte[16]),
            TypeDependencies = []
        }).ToList();
        var infos = classIds.Select((_, index) =>
        {
            var info = new AssetFileInfo
            {
                PathId = 101 + index,
                TypeIdOrIndex = index
            };
            info.SetNewData(Encoding.UTF8.GetBytes($"{objectPayloadMarker}-{index}"));
            return info;
        }).ToList();
        var assetsFile = new AssetsFile
        {
            Header = new AssetsFileHeader
            {
                Version = SerializedFileVersion,
                Endianness = false
            },
            Metadata = new AssetsFileMetadata
            {
                UnityVersion = UnityVersion,
                TargetPlatform = 19,
                TypeTreeEnabled = false,
                TypeTreeTypes = types,
                AssetInfos = infos,
                ScriptTypes = [],
                Externals =
                [
                    new AssetsFileExternal
                    {
                        VirtualAssetPathName = string.Empty,
                        Type = AssetsFileExternalType.Serialized,
                        PathName = "archive:/CAB-sanitized/CAB-sanitized",
                        OriginalPathName = "archive:/CAB-sanitized/CAB-sanitized"
                    }
                ],
                RefTypes = [],
                UserInformation = string.Empty
            }
        };

        using var stream = new MemoryStream();
        using (var writer = new AssetsFileWriter(stream))
        {
            assetsFile.Write(writer);
        }

        return stream.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
