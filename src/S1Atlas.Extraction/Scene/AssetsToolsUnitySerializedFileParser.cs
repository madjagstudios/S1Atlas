using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace S1Atlas.Extraction.Scene;

public sealed class AssetsToolsUnitySerializedFileParser : IUnitySerializedFileParser
{
    public Task<IReadOnlyList<ParsedSceneContainer>> ParseAsync(
        IReadOnlyList<VerifiedSceneContainer> containers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(containers);
        cancellationToken.ThrowIfCancellationRequested();

        var parsed = new List<ParsedSceneContainer>(containers.Count);
        foreach (var container in containers)
        {
            ArgumentNullException.ThrowIfNull(container);
            cancellationToken.ThrowIfCancellationRequested();
            parsed.Add(ParseContainer(container));
        }

        return Task.FromResult<IReadOnlyList<ParsedSceneContainer>>(parsed.ToArray());
    }

    private static ParsedSceneContainer ParseContainer(VerifiedSceneContainer container)
    {
        using var stream = new FileStream(
            container.PrimaryPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        if (stream.Length != container.ByteCount)
        {
            throw new InvalidDataException(
                $"SerializedFile '{container.RelativePath}' no longer matches its verified size.");
        }

        var assetsFile = new AssetsFile();
        assetsFile.Read(stream);
        var serializedFileVersion = checked((int)assetsFile.Header.Version);
        if (!string.Equals(
                assetsFile.Metadata.UnityVersion,
                container.UnityVersion,
                StringComparison.Ordinal) ||
            serializedFileVersion != container.SerializedFileVersion)
        {
            throw new InvalidDataException(
                $"SerializedFile '{container.RelativePath}' header no longer matches its verified facts.");
        }

        var objects = assetsFile.AssetInfos.Select(info =>
        {
            var classId = info.GetTypeId(assetsFile);
            return new ParsedSceneObject(
                info.PathId,
                classId,
                info.GetAbsoluteByteOffset(assetsFile),
                info.ByteSize,
                Classify(classId));
        }).ToArray();
        var externals = assetsFile.Metadata.Externals
            .Select((external, index) => new ParsedSceneExternalReference(
                index + 1,
                external.PathName ?? string.Empty,
                external.OriginalPathName ?? string.Empty))
            .ToArray();

        return new ParsedSceneContainer(
            container.RelativePath,
            assetsFile.Metadata.UnityVersion,
            serializedFileVersion,
            objects,
            externals,
            objects.Any(item => item.Kind == ParsedSceneObjectKind.PrefabEvidence));
    }

    private static ParsedSceneObjectKind Classify(int classId) => classId switch
    {
        (int)AssetClassID.GameObject => ParsedSceneObjectKind.GameObject,
        (int)AssetClassID.Transform => ParsedSceneObjectKind.Transform,
        (int)AssetClassID.MonoBehaviour => ParsedSceneObjectKind.MonoBehaviour,
        (int)AssetClassID.MonoScript => ParsedSceneObjectKind.MonoScript,
        (int)AssetClassID.PrefabInstance or
            (int)AssetClassID.Prefab => ParsedSceneObjectKind.PrefabEvidence,
        _ => ParsedSceneObjectKind.Other
    };
}
