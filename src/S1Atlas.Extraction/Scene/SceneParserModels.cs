using S1Atlas.Core.Scenes;

namespace S1Atlas.Extraction.Scene;

public sealed record SceneContainerDeclaration(
    string RelativePath,
    IReadOnlyList<string> SidecarRelativePaths);

public sealed record VerifiedSceneContainer(
    string RelativePath,
    string PrimaryPath,
    IReadOnlyList<string> SidecarPaths,
    string Sha256,
    long ByteCount,
    string UnityVersion,
    int SerializedFileVersion,
    string SidecarManifest);

public sealed class VerifiedSceneInput
{
    internal VerifiedSceneInput(
        string installRoot,
        IReadOnlyList<VerifiedSceneContainer> containers,
        string manifestDigest,
        IReadOnlyList<VerifiedSceneFile> files)
    {
        InstallRoot = installRoot;
        Containers = containers;
        ManifestDigest = manifestDigest;
        Files = files;
    }

    public string InstallRoot { get; }
    public IReadOnlyList<VerifiedSceneContainer> Containers { get; }
    public string ManifestDigest { get; }
    internal IReadOnlyList<VerifiedSceneFile> Files { get; }
}

public enum ParsedSceneObjectKind
{
    Other,
    GameObject,
    Transform,
    MonoBehaviour,
    MonoScript,
    BuildSettings,
    PrefabEvidence
}

public readonly record struct ParsedScenePPtr(int FileId, long LocalFileId);

public readonly record struct ParsedSceneVector3(float X, float Y, float Z);

public readonly record struct ParsedSceneQuaternion(float X, float Y, float Z, float W);

public sealed record ParsedSceneReference(
    string FieldPath,
    string DeclaredType,
    ParsedScenePPtr Target);

public sealed record ParsedGameObjectData(
    string Name,
    uint Layer,
    ushort Tag,
    bool IsActive,
    IReadOnlyList<ParsedScenePPtr> Components);

public sealed record ParsedTransformData(
    ParsedScenePPtr GameObject,
    ParsedScenePPtr ParentTransform,
    IReadOnlyList<ParsedScenePPtr> Children,
    ParsedSceneVector3 LocalPosition,
    ParsedSceneQuaternion LocalRotation,
    ParsedSceneVector3 LocalScale,
    int RootOrder);

public sealed record ParsedMonoBehaviourData(
    ParsedScenePPtr GameObject,
    ParsedScenePPtr Script,
    bool Enabled,
    string Name = "");

public sealed record ParsedMonoScriptData(
    string AssemblyName,
    string Namespace,
    string ClassName);

public sealed record ParsedBuildSettingsData(
    IReadOnlyList<string> ScenePaths);

public sealed record ParsedSceneObject(
    long LocalFileId,
    int UnityClassId,
    long ByteOffset,
    long ByteCount,
    ParsedSceneObjectKind Kind,
    IReadOnlyList<ParsedSceneReference> References,
    ParsedGameObjectData? GameObject,
    ParsedTransformData? Transform,
    ParsedMonoBehaviourData? MonoBehaviour,
    ParsedMonoScriptData? MonoScript,
    ParsedBuildSettingsData? BuildSettings,
    ParsedScriptFields? ScriptFields = null);

public sealed record ParsedSceneExternalReference(
    int FileId,
    string PathName,
    string OriginalPathName);

public sealed record ParsedSceneContainer(
    string RelativePath,
    string PrimaryPath,
    IReadOnlyList<string> SidecarPaths,
    string Sha256,
    string UnityVersion,
    int SerializedFileVersion,
    IReadOnlyList<ParsedSceneObject> Objects,
    IReadOnlyList<ParsedSceneExternalReference> ExternalReferences,
    bool HasPrefabEvidence,
    bool TypeTreeEmbedded = true,
    ParsedTypeTreeSource? TypeTreeSource = null)
{
    /// <summary>
    /// The type-tree source that decoded this container's supported objects. Falls back to the
    /// embedded flag when the parser did not record an explicit source.
    /// </summary>
    public ParsedTypeTreeSource DecodeSource =>
        TypeTreeSource ?? (TypeTreeEmbedded ? ParsedTypeTreeSource.Embedded : ParsedTypeTreeSource.Unavailable);
}

internal sealed record VerifiedSceneFile(
    string RelativePath,
    string FullPath,
    long ByteCount,
    DateTimeOffset LastWriteUtc,
    string Sha256);

/// <summary>
/// Where reconstructed script assemblies with restored serialization attributes live, and a
/// stable identity for them that enters the scene snapshot identity.
/// </summary>
public sealed record SceneScriptLayoutSource(string ManagedAssembliesPath, string Identity)
{
    public string ManagedAssembliesPath { get; init; } = Require(ManagedAssembliesPath, nameof(ManagedAssembliesPath));
    public string Identity { get; init; } = Require(Identity, nameof(Identity));

    private static string Require(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }
}

/// <summary>
/// A MonoBehaviour's decoded game-script fields, or why they could not be decoded. Only parsers
/// given script layouts produce these.
/// </summary>
public sealed record ParsedScriptFields(
    SceneScriptFieldSetStatus Status,
    string? UnavailableReason,
    IReadOnlyList<SceneScriptField> Fields,
    bool Truncated)
{
    public static ParsedScriptFields Unavailable(string reason) =>
        new(SceneScriptFieldSetStatus.Unavailable, reason, [], false);
}
