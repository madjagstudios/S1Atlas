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

        var refTypeManager = new RefTypeManager();
        refTypeManager.FromTypeTree(assetsFile.Metadata);
        var objects = assetsFile.AssetInfos.Select(info =>
        {
            var classId = info.GetTypeId(assetsFile);
            var kind = Classify(classId);
            var baseField = ReadSupportedBaseField(
                assetsFile,
                info,
                kind,
                refTypeManager,
                container.RelativePath);
            var references = baseField is null
                ? []
                : CollectReferences(baseField).ToArray();
            return new ParsedSceneObject(
                info.PathId,
                classId,
                info.GetAbsoluteByteOffset(assetsFile),
                info.ByteSize,
                kind,
                references,
                kind == ParsedSceneObjectKind.GameObject && baseField is not null
                    ? ReadGameObject(baseField)
                    : null,
                kind == ParsedSceneObjectKind.Transform && baseField is not null
                    ? ReadTransform(baseField)
                    : null,
                kind == ParsedSceneObjectKind.MonoBehaviour && baseField is not null
                    ? ReadMonoBehaviour(baseField)
                    : null,
                kind == ParsedSceneObjectKind.MonoScript && baseField is not null
                    ? ReadMonoScript(baseField)
                    : null,
                kind == ParsedSceneObjectKind.BuildSettings && baseField is not null
                    ? ReadBuildSettings(baseField)
                    : null);
        }).ToArray();
        var externals = assetsFile.Metadata.Externals
            .Select((external, index) => new ParsedSceneExternalReference(
                index + 1,
                external.PathName ?? string.Empty,
                external.OriginalPathName ?? string.Empty))
            .ToArray();

        return new ParsedSceneContainer(
            container.RelativePath,
            container.PrimaryPath,
            container.SidecarPaths.ToArray(),
            container.Sha256,
            assetsFile.Metadata.UnityVersion,
            serializedFileVersion,
            objects,
            externals,
            objects.Any(item => item.Kind == ParsedSceneObjectKind.PrefabEvidence));
    }

    private static AssetTypeValueField? ReadSupportedBaseField(
        AssetsFile assetsFile,
        AssetFileInfo info,
        ParsedSceneObjectKind kind,
        RefTypeManager refTypeManager,
        string relativePath)
    {
        if (kind is not (ParsedSceneObjectKind.GameObject or
            ParsedSceneObjectKind.Transform or
            ParsedSceneObjectKind.MonoBehaviour or
            ParsedSceneObjectKind.MonoScript or
            ParsedSceneObjectKind.BuildSettings) ||
            !assetsFile.Metadata.TypeTreeEnabled)
        {
            return null;
        }

        var typeTree = assetsFile.Metadata.TypeTreeTypes[info.TypeIdOrIndex];
        if (typeTree.IsStrippedType ||
            typeTree.TypeBlob is null ||
            typeTree.Nodes.Count == 0)
        {
            return null;
        }

        try
        {
            var template = new AssetTypeTemplateField();
            template.FromTypeTree(typeTree);
            return template.MakeValue(
                assetsFile.Reader,
                info.GetAbsoluteByteOffset(assetsFile),
                refTypeManager);
        }
        catch (Exception exception) when (exception is not InvalidDataException)
        {
            throw new InvalidDataException(
                $"SerializedFile '{relativePath}' object {info.PathId} could not be decoded from its embedded type tree.",
                exception);
        }
    }

    private static ParsedGameObjectData ReadGameObject(AssetTypeValueField field)
    {
        var components = field["m_Component.Array"]
            .Select(item => ReadPointer(item["component"]))
            .ToArray();
        return new ParsedGameObjectData(
            field["m_Name"].AsString,
            field["m_Layer"].AsUInt,
            field["m_Tag"].AsUShort,
            field["m_IsActive"].AsBool,
            components);
    }

    private static ParsedTransformData ReadTransform(AssetTypeValueField field) =>
        new(
            ReadPointer(field["m_GameObject"]),
            ReadPointer(field["m_Father"]),
            field["m_Children.Array"].Select(ReadPointer).ToArray(),
            ReadVector3(field["m_LocalPosition"]),
            ReadQuaternion(field["m_LocalRotation"]),
            ReadVector3(field["m_LocalScale"]),
            field["m_RootOrder"].AsInt);

    private static ParsedMonoBehaviourData ReadMonoBehaviour(AssetTypeValueField field) =>
        new(
            ReadPointer(field["m_GameObject"]),
            ReadPointer(field["m_Script"]),
            field["m_Enabled"].AsByte != 0);

    private static ParsedMonoScriptData ReadMonoScript(AssetTypeValueField field) =>
        new(
            field["m_AssemblyName"].AsString,
            field["m_Namespace"].AsString,
            field["m_ClassName"].AsString);

    private static ParsedBuildSettingsData ReadBuildSettings(AssetTypeValueField field) =>
        new(field["scenes.Array"].Select(item => item.AsString).ToArray());

    private static ParsedSceneVector3 ReadVector3(AssetTypeValueField field) =>
        new(field["x"].AsFloat, field["y"].AsFloat, field["z"].AsFloat);

    private static ParsedSceneQuaternion ReadQuaternion(AssetTypeValueField field) =>
        new(
            field["x"].AsFloat,
            field["y"].AsFloat,
            field["z"].AsFloat,
            field["w"].AsFloat);

    private static ParsedScenePPtr ReadPointer(AssetTypeValueField field)
    {
        var pointer = AssetPPtr.FromField(field);
        return new ParsedScenePPtr(pointer.FileId, pointer.PathId);
    }

    private static IEnumerable<ParsedSceneReference> CollectReferences(
        AssetTypeValueField root)
    {
        foreach (var child in root.Children)
        {
            foreach (var reference in CollectReferences(child, child.FieldName))
            {
                yield return reference;
            }
        }
    }

    private static IEnumerable<ParsedSceneReference> CollectReferences(
        AssetTypeValueField field,
        string path)
    {
        if (field.TypeName.StartsWith("PPtr<", StringComparison.Ordinal))
        {
            yield return new ParsedSceneReference(path, field.TypeName, ReadPointer(field));
            yield break;
        }

        if (field.TemplateField.IsArray)
        {
            for (var index = 0; index < field.Children.Count; index++)
            {
                foreach (var reference in CollectReferences(
                             field.Children[index],
                             $"{path}[{index}]"))
                {
                    yield return reference;
                }
            }

            yield break;
        }

        foreach (var child in field.Children)
        {
            foreach (var reference in CollectReferences(
                         child,
                         $"{path}.{child.FieldName}"))
            {
                yield return reference;
            }
        }
    }

    private static ParsedSceneObjectKind Classify(int classId) => classId switch
    {
        (int)AssetClassID.GameObject => ParsedSceneObjectKind.GameObject,
        (int)AssetClassID.Transform => ParsedSceneObjectKind.Transform,
        (int)AssetClassID.MonoBehaviour => ParsedSceneObjectKind.MonoBehaviour,
        (int)AssetClassID.MonoScript => ParsedSceneObjectKind.MonoScript,
        (int)AssetClassID.BuildSettings => ParsedSceneObjectKind.BuildSettings,
        (int)AssetClassID.PrefabInstance or
            (int)AssetClassID.Prefab => ParsedSceneObjectKind.PrefabEvidence,
        _ => ParsedSceneObjectKind.Other
    };
}
