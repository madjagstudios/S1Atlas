using System.Security.Cryptography;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using S1Atlas.Extraction.Scene;
using static S1Atlas.Extraction.Tests.Scene.SanitizedSerializedFileFixture;

namespace S1Atlas.Extraction.Tests.Scene;

/// <summary>
/// A stripped (TypeTreeEnabled=false) container whose MonoBehaviours carry game-script payloads
/// for the classes in S1Atlas.ScriptLayoutFixture. Path IDs:
/// 101 GameObject "Shop Owner"; 103 Shop; 110 Catalog (asset-level, m_GameObject null);
/// 112 UnattributedShop (payload has Price, class lacks [SerializeField]); 113 Inventory
/// (corrupt array length); 114 script class missing from the assembly; 104/111/115/116/117 MonoScripts.
/// </summary>
internal sealed class ScriptFieldSerializedFileFixture : IDisposable
{
    private const string FixtureUnityVersion = SanitizedSerializedFileFixture.UnityVersion;

    private ScriptFieldSerializedFileFixture(string rootPath, string primaryPath)
    {
        RootPath = rootPath;
        var bytes = File.ReadAllBytes(primaryPath);
        VerifiedContainer = new VerifiedSceneContainer(
            "Schedule I_Data/sharedassets0.assets",
            primaryPath,
            [],
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.LongLength,
            FixtureUnityVersion,
            SerializedFileVersion,
            "[]");
    }

    public string RootPath { get; }
    public VerifiedSceneContainer VerifiedContainer { get; }

    public static ScriptFieldSerializedFileFixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "s1atlas-script-field-tests", Guid.NewGuid().ToString("N"));
        var primary = Path.Combine(root, "Schedule I_Data", "sharedassets0.assets");
        Directory.CreateDirectory(Path.GetDirectoryName(primary)!);
        File.WriteAllBytes(primary, CreateBytes());
        return new ScriptFieldSerializedFileFixture(root, primary);
    }

    /// <summary>Class database with a header-only MonoBehaviour layout, as a real Unity dump has.</summary>
    internal static ClassDatabaseFile CreateClassDatabase()
    {
        var database = new ClassDatabaseFile
        {
            Header = new ClassDatabaseFileHeader
            {
                Magic = "CLDB",
                FileVersion = 1,
                Version = new AssetsTools.NET.Extra.UnityVersion(FixtureUnityVersion),
                CompressionType = ClassFileCompressionType.Uncompressed
            },
            StringTable = new ClassDatabaseStringTable { Strings = [] },
            CommonStringBufferIndices = [],
            Classes = []
        };
        foreach (var type in Types())
        {
            var root = BuildDatabaseNode(database.StringTable, type.Nodes, 0, out _);
            database.Classes.Add(new ClassDatabaseType
            {
                ClassId = type.ClassId,
                Name = database.StringTable.AddString(type.Name),
                BaseName = database.StringTable.AddString(string.Empty),
                Flags = ClassFileTypeFlags.HasReleaseRootNode,
                ReleaseRootNode = root,
                EditorRootNode = null
            });
        }

        return database;
    }

    private static List<FixtureType> Types() =>
    [
        GameObjectType(),
        new FixtureType(114, "MonoBehaviour",
        [
            Node(0, "MonoBehaviour", "Base"),
            .. PPtrNodes(1, "PPtr<GameObject>", "m_GameObject"),
            Node(1, "UInt8", "m_Enabled", aligned: true),
            .. PPtrNodes(1, "PPtr<MonoScript>", "m_Script"),
            Node(1, "string", "m_Name"),
            Node(2, "Array", "Array", isArray: true),
            Node(3, "int", "size"), Node(3, "char", "data")
        ], ScriptTypeIndex: 0),
        MonoScriptType()
    ];

    private static byte[] CreateBytes()
    {
        var objects = new List<FixtureObject>
        {
            new(101, 0, GameObjectPayload("Shop Owner", [PPtr(0, 103), PPtr(0, 112), PPtr(0, 113), PPtr(0, 114)], 0, 0, true)),
            new(103, 1, Behaviour(101, 104, "", writer =>
            {
                writer.Write(50000);            // Price ([SerializeField] private)
                writer.Write(100.5f);           // Wage
                WriteString(writer, "Docks");   // Label
                writer.Write(3);                // Stock.size
                writer.Write(1);
                writer.Write(2);
                writer.Write(3);
            })),
            new(104, 2, MonoScriptPayload("Shop", "Fixture.Game", "Assembly-CSharp.dll")),
            new(110, 1, Behaviour(0, 111, "SCD_Fixture", writer =>
            {
                writer.Write(100);              // MaxQuantity
                writer.Write(0.25f);            // Tint.r
                writer.Write(0.5f);             // Tint.g
                writer.Write(0.75f);            // Tint.b
                writer.Write(1f);               // Tint.a
            })),
            new(111, 2, MonoScriptPayload("Catalog", "Fixture.Game", "Assembly-CSharp.dll")),
            new(112, 1, Behaviour(101, 115, "", writer =>
            {
                writer.Write(50000);            // Price: present in the payload, absent from the layout
                writer.Write(100.5f);           // Wage
            })),
            new(115, 2, MonoScriptPayload("UnattributedShop", "Fixture.Game", "Assembly-CSharp.dll")),
            new(113, 1, Behaviour(101, 116, "", writer =>
            {
                writer.Write(int.MaxValue);     // corrupt Stock.size
                writer.Write(7);
            })),
            new(116, 2, MonoScriptPayload("Inventory", "Fixture.Game", "Assembly-CSharp.dll")),
            new(114, 1, Behaviour(101, 117, "", _ => { })),
            new(117, 2, MonoScriptPayload("DoesNotExist", "Fixture.Game", "Assembly-CSharp.dll"))
        };
        var types = Types();

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
            WriteNullTerminated(writer, FixtureUnityVersion);
            writer.Write(19u);
            writer.Write(false); // stripped type trees
            writer.Write(types.Count);
            foreach (var type in types)
                WriteType(writer, type, false);
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
            writer.Write(0); // externals
            writer.Write(0); // reference types
            WriteNullTerminated(writer, "script-field-fixture");
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
                writer.Write((byte)0);
            writer.Write(data);
        }

        return result.ToArray();
    }

    private static byte[] Behaviour(long gameObject, long script, string name, Action<BinaryWriter> writeFields) => Payload(writer =>
    {
        WritePPtr(writer, PPtr(0, gameObject));
        writer.Write((byte)1);
        Align(writer, 4);
        WritePPtr(writer, PPtr(0, script));
        WriteString(writer, name);
        writeFields(writer);
    });

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
            Directory.Delete(RootPath, recursive: true);
    }
}
