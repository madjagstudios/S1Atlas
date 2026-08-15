using System.Security.Cryptography;
using System.Text;
using S1Atlas.Extraction.Scene;

namespace S1Atlas.Extraction.Tests.Scene;

internal sealed class SanitizedSerializedFileFixture : IDisposable
{
    private const string UnityVersion = "2022.3.62f1";
    private const int SerializedFileVersion = 22;

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
            SerializedFileVersion,
            "[]");
    }

    public string RootPath { get; }
    public string PrimaryPath { get; }
    public VerifiedSceneContainer VerifiedContainer { get; }

    public static SanitizedSerializedFileFixture Create(
        string objectPayloadMarker = "sanitized-object-data",
        int? prefabClassId = null,
        bool includeTypeTree = true)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "s1atlas-scene-parser-tests",
            Guid.NewGuid().ToString("N"));
        var primary = Path.Combine(root, "Schedule I_Data", "level0");
        Directory.CreateDirectory(Path.GetDirectoryName(primary)!);
        File.WriteAllBytes(primary, CreateBytes(objectPayloadMarker, prefabClassId, includeTypeTree));
        return new SanitizedSerializedFileFixture(root, primary);
    }

    internal static byte[] CreateBytes(
        string userInformation,
        int? prefabClassId,
        bool includeTypeTree)
    {
        var types = new List<FixtureType>
        {
            GameObjectType(),
            TransformType(),
            MonoBehaviourType(),
            MonoScriptType(),
            BuiltInComponentType(),
            BuildSettingsType()
        };
        if (prefabClassId is not null)
        {
            types.Add(new FixtureType(prefabClassId.Value, "Prefab", [
                Node(0, "Prefab", "Base"),
                .. PPtrNodes(1, "PPtr<GameObject>", "m_RootGameObject")
            ]));
        }

        var objects = new List<FixtureObject>
        {
            new(101, 0, GameObjectPayload("Sanitized Root", [PPtr(0, 102), PPtr(0, 103), PPtr(0, 107)], 7, 3, true)),
            new(102, 1, TransformPayload(101, [106], 0, 0)),
            new(103, 2, MonoBehaviourPayload(101, 104, PPtr(0, 101), PPtr(1, 101), PPtr(2, 999))),
            new(104, 3, MonoScriptPayload("SceneGraphBehaviour", "Fixture.Namespace", "Assembly-CSharp.dll")),
            new(105, 0, GameObjectPayload("Sanitized Child", [PPtr(0, 106)], 8, 4, true)),
            new(106, 1, TransformPayload(105, [], 102, 1)),
            new(107, 4, new byte[4]),
            new(108, 5, BuildSettingsPayload([
                "Assets/Scenes/SanitizedBootstrap.unity",
                "Assets/Scenes/SanitizedWorld.unity",
                "Assets/Scenes/SanitizedInterior.unity"
            ]))
        };
        if (prefabClassId is not null)
        {
            objects.Add(new FixtureObject(109, types.Count - 1, Payload(writer =>
                WritePPtr(writer, PPtr(0, 101)))));
        }

        using var dataStream = new MemoryStream();
        using (var dataWriter = new BinaryWriter(dataStream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var item in objects)
            {
                Align(dataWriter, 8);
                item.ByteOffset = dataWriter.BaseStream.Position;
                dataWriter.Write(item.Payload);
            }
        }

        using var metadataStream = new MemoryStream();
        using (var writer = new BinaryWriter(metadataStream, Encoding.UTF8, leaveOpen: true))
        {
            WriteNullTerminated(writer, UnityVersion);
            writer.Write(19u);
            writer.Write(includeTypeTree);
            writer.Write(types.Count);
            foreach (var type in types)
            {
                WriteType(writer, type, includeTypeTree);
            }

            writer.Write(objects.Count);
            Align(writer, 4);
            foreach (var item in objects)
            {
                Align(writer, 4);
                writer.Write(item.PathId);
                writer.Write(item.ByteOffset);
                writer.Write(item.Payload.Length);
                writer.Write(item.TypeIndex);
            }

            writer.Write(0); // script types
            writer.Write(2); // externals
            WriteExternal(writer, "archive:/CAB-fixture/sharedassets0.assets");
            WriteExternal(writer, "archive:/CAB-fixture/missing.assets");
            writer.Write(0); // reference types
            WriteNullTerminated(writer, userInformation);
        }

        var metadata = metadataStream.ToArray();
        var data = dataStream.ToArray();
        const int headerSize = 48;
        var dataOffset = Align(headerSize + metadata.Length, 16);
        var fileSize = dataOffset + data.Length;

        using var result = new MemoryStream(fileSize);
        using (var writer = new BinaryWriter(result, Encoding.UTF8, leaveOpen: true))
        {
            WriteBigEndian(writer, 0u);
            WriteBigEndian(writer, 0u);
            WriteBigEndian(writer, (uint)SerializedFileVersion);
            WriteBigEndian(writer, 0u);
            writer.Write(false);
            writer.Write(new byte[3]);
            WriteBigEndian(writer, (uint)metadata.Length);
            WriteBigEndian(writer, (long)fileSize);
            WriteBigEndian(writer, (long)dataOffset);
            writer.Write(new byte[8]);
            writer.Write(metadata);
            while (writer.BaseStream.Position < dataOffset)
            {
                writer.Write((byte)0);
            }

            writer.Write(data);
        }

        return result.ToArray();
    }

    private static FixtureType GameObjectType() => new(1, "GameObject",
    [
        Node(0, "GameObject", "Base"),
        Node(1, "vector", "m_Component"),
        Node(2, "Array", "Array", isArray: true),
        Node(3, "int", "size"),
        Node(3, "ComponentPair", "data"),
        Node(4, "PPtr<Component>", "component"),
        Node(5, "int", "m_FileID"),
        Node(5, "SInt64", "m_PathID"),
        Node(1, "unsigned int", "m_Layer"),
        Node(1, "string", "m_Name"),
        Node(2, "Array", "Array", isArray: true),
        Node(3, "int", "size"),
        Node(3, "char", "data"),
        Node(1, "UInt16", "m_Tag"),
        Node(1, "bool", "m_IsActive", aligned: true)
    ]);

    private static FixtureType TransformType() => new(4, "Transform",
    [
        Node(0, "Transform", "Base"),
        .. PPtrNodes(1, "PPtr<GameObject>", "m_GameObject"),
        Node(1, "Quaternionf", "m_LocalRotation"),
        Node(2, "float", "x"), Node(2, "float", "y"),
        Node(2, "float", "z"), Node(2, "float", "w"),
        Node(1, "Vector3f", "m_LocalPosition"),
        Node(2, "float", "x"), Node(2, "float", "y"), Node(2, "float", "z"),
        Node(1, "Vector3f", "m_LocalScale"),
        Node(2, "float", "x"), Node(2, "float", "y"), Node(2, "float", "z"),
        Node(1, "vector", "m_Children"),
        Node(2, "Array", "Array", isArray: true),
        Node(3, "int", "size"),
        Node(3, "PPtr<Transform>", "data"),
        Node(4, "int", "m_FileID"), Node(4, "SInt64", "m_PathID"),
        .. PPtrNodes(1, "PPtr<Transform>", "m_Father"),
        Node(1, "int", "m_RootOrder")
    ]);

    private static FixtureType MonoBehaviourType() => new(114, "MonoBehaviour",
    [
        Node(0, "MonoBehaviour", "Base"),
        .. PPtrNodes(1, "PPtr<GameObject>", "m_GameObject"),
        Node(1, "UInt8", "m_Enabled", aligned: true),
        .. PPtrNodes(1, "PPtr<MonoScript>", "m_Script"),
        Node(1, "string", "m_Name"),
        Node(2, "Array", "Array", isArray: true),
        Node(3, "int", "size"), Node(3, "char", "data"),
        .. PPtrNodes(1, "PPtr<GameObject>", "m_LocalTarget"),
        .. PPtrNodes(1, "PPtr<GameObject>", "m_ExternalTarget"),
        .. PPtrNodes(1, "PPtr<GameObject>", "m_MissingTarget")
    ], ScriptTypeIndex: 0);

    private static FixtureType MonoScriptType() => new(115, "MonoScript",
    [
        Node(0, "MonoScript", "Base"),
        Node(1, "string", "m_Name"),
        Node(2, "Array", "Array", isArray: true),
        Node(3, "int", "size"), Node(3, "char", "data"),
        Node(1, "int", "m_ExecutionOrder"),
        Node(1, "string", "m_ClassName"),
        Node(2, "Array", "Array", isArray: true),
        Node(3, "int", "size"), Node(3, "char", "data"),
        Node(1, "string", "m_Namespace"),
        Node(2, "Array", "Array", isArray: true),
        Node(3, "int", "size"), Node(3, "char", "data"),
        Node(1, "string", "m_AssemblyName"),
        Node(2, "Array", "Array", isArray: true),
        Node(3, "int", "size"), Node(3, "char", "data")
    ]);

    private static FixtureType BuiltInComponentType() => new(23, "MeshRenderer",
    [
        Node(0, "MeshRenderer", "Base")
    ]);

    private static FixtureType BuildSettingsType() => new(141, "BuildSettings",
    [
        Node(0, "BuildSettings", "Base"),
        Node(1, "vector", "scenes"),
        Node(2, "Array", "Array", isArray: true),
        Node(3, "int", "size"),
        Node(3, "string", "data"),
        Node(4, "Array", "Array", isArray: true),
        Node(5, "int", "size"), Node(5, "char", "data")
    ]);

    private static FixtureNode[] PPtrNodes(byte level, string type, string name) =>
    [
        Node(level, type, name),
        Node((byte)(level + 1), "int", "m_FileID"),
        Node((byte)(level + 1), "SInt64", "m_PathID")
    ];

    private static byte[] GameObjectPayload(
        string name,
        IReadOnlyList<(int FileId, long PathId)> components,
        uint layer,
        ushort tag,
        bool active) => Payload(writer =>
    {
        writer.Write(components.Count);
        foreach (var component in components)
        {
            WritePPtr(writer, component);
        }

        writer.Write(layer);
        WriteString(writer, name);
        writer.Write(tag);
        writer.Write(active);
        Align(writer, 4);
    });

    private static byte[] TransformPayload(
        long gameObject,
        IReadOnlyList<long> children,
        long parent,
        int rootOrder) => Payload(writer =>
    {
        WritePPtr(writer, PPtr(0, gameObject));
        writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
        writer.Write(1.25f); writer.Write(2.5f); writer.Write(3.75f);
        writer.Write(1f); writer.Write(1f); writer.Write(1f);
        writer.Write(children.Count);
        foreach (var child in children)
        {
            WritePPtr(writer, PPtr(0, child));
        }

        WritePPtr(writer, PPtr(0, parent));
        writer.Write(rootOrder);
    });

    private static byte[] MonoBehaviourPayload(
        long gameObject,
        long script,
        (int FileId, long PathId) localTarget,
        (int FileId, long PathId) externalTarget,
        (int FileId, long PathId) missingTarget) => Payload(writer =>
    {
        WritePPtr(writer, PPtr(0, gameObject));
        writer.Write((byte)1);
        Align(writer, 4);
        WritePPtr(writer, PPtr(0, script));
        WriteString(writer, "Sanitized Component Instance");
        WritePPtr(writer, localTarget);
        WritePPtr(writer, externalTarget);
        WritePPtr(writer, missingTarget);
    });

    private static byte[] MonoScriptPayload(
        string className,
        string @namespace,
        string assemblyName) => Payload(writer =>
    {
        WriteString(writer, "SanitizedComponent");
        writer.Write(0);
        WriteString(writer, className);
        WriteString(writer, @namespace);
        WriteString(writer, assemblyName);
    });

    private static byte[] BuildSettingsPayload(IReadOnlyList<string> scenePaths) =>
        Payload(writer =>
        {
            writer.Write(scenePaths.Count);
            foreach (var scenePath in scenePaths)
            {
                WriteString(writer, scenePath);
            }
        });

    private static byte[] Payload(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            write(writer);
        }

        return stream.ToArray();
    }

    private static (int FileId, long PathId) PPtr(int fileId, long pathId) =>
        (fileId, pathId);

    private static void WritePPtr(BinaryWriter writer, (int FileId, long PathId) pointer)
    {
        writer.Write(pointer.FileId);
        writer.Write(pointer.PathId);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
        Align(writer, 4);
    }

    private static FixtureNode Node(
        byte level,
        string type,
        string name,
        bool isArray = false,
        bool aligned = false) =>
        new(level, type, name, isArray, aligned);

    private static void WriteType(
        BinaryWriter writer,
        FixtureType type,
        bool includeTypeTree)
    {
        writer.Write(type.ClassId);
        writer.Write(false);
        writer.Write(type.ScriptTypeIndex);
        if (type.ClassId == 114)
        {
            writer.Write(new byte[16]);
        }

        writer.Write(new byte[16]);
        if (!includeTypeTree)
        {
            return;
        }

        var stringBuffer = BuildStringBuffer(type.Nodes, out var offsets);
        writer.Write(type.Nodes.Count);
        writer.Write(stringBuffer.Length);
        for (var index = 0; index < type.Nodes.Count; index++)
        {
            var node = type.Nodes[index];
            writer.Write((ushort)1);
            writer.Write(node.Level);
            writer.Write(node.IsArray ? (byte)1 : (byte)0);
            writer.Write(offsets[node.Type]);
            writer.Write(offsets[node.Name]);
            writer.Write(-1);
            writer.Write((uint)index);
            writer.Write(node.Aligned ? 0x4000u : 0u);
            writer.Write(0ul);
        }

        writer.Write(stringBuffer);
        writer.Write(0); // type dependencies
    }

    private static byte[] BuildStringBuffer(
        IReadOnlyList<FixtureNode> nodes,
        out Dictionary<string, uint> offsets)
    {
        offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        using var stream = new MemoryStream();
        foreach (var value in nodes.SelectMany(node => new[] { node.Type, node.Name }))
        {
            if (offsets.ContainsKey(value))
            {
                continue;
            }

            offsets.Add(value, checked((uint)stream.Position));
            stream.Write(Encoding.UTF8.GetBytes(value));
            stream.WriteByte(0);
        }

        return stream.ToArray();
    }

    private static int Align(int value, int alignment) =>
        (value + alignment - 1) / alignment * alignment;

    private static void Align(BinaryWriter writer, int alignment)
    {
        while (writer.BaseStream.Position % alignment != 0)
        {
            writer.Write((byte)0);
        }
    }

    private static void WriteNullTerminated(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }

    private static void WriteExternal(BinaryWriter writer, string path)
    {
        WriteNullTerminated(writer, string.Empty);
        writer.Write(new byte[16]);
        writer.Write(0);
        WriteNullTerminated(writer, path);
    }

    private static void WriteBigEndian(BinaryWriter writer, uint value) =>
        writer.Write(BitConverter.GetBytes(value).Reverse().ToArray());

    private static void WriteBigEndian(BinaryWriter writer, long value) =>
        writer.Write(BitConverter.GetBytes(value).Reverse().ToArray());

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }

    private sealed record FixtureType(
        int ClassId,
        string Name,
        IReadOnlyList<FixtureNode> Nodes,
        ushort ScriptTypeIndex = ushort.MaxValue);

    private sealed record FixtureNode(
        byte Level,
        string Type,
        string Name,
        bool IsArray,
        bool Aligned);

    private sealed class FixtureObject(
        long pathId,
        int typeIndex,
        byte[] payload)
    {
        public long PathId { get; } = pathId;
        public int TypeIndex { get; } = typeIndex;
        public byte[] Payload { get; } = payload;
        public long ByteOffset { get; set; }
    }
}
