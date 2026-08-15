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
    PrefabEvidence
}

public sealed record ParsedSceneObject(
    long LocalFileId,
    int UnityClassId,
    long ByteOffset,
    long ByteCount,
    ParsedSceneObjectKind Kind);

public sealed record ParsedSceneExternalReference(
    int FileId,
    string PathName,
    string OriginalPathName);

public sealed record ParsedSceneContainer(
    string RelativePath,
    string UnityVersion,
    int SerializedFileVersion,
    IReadOnlyList<ParsedSceneObject> Objects,
    IReadOnlyList<ParsedSceneExternalReference> ExternalReferences,
    bool HasPrefabEvidence);

internal sealed record VerifiedSceneFile(
    string RelativePath,
    string FullPath,
    long ByteCount,
    DateTimeOffset LastWriteUtc,
    string Sha256);
