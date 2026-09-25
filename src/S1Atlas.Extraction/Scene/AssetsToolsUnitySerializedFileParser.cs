using System.Security.Cryptography;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace S1Atlas.Extraction.Scene;

public sealed partial class AssetsToolsUnitySerializedFileParser : IUnitySerializedFileParser
{
    private readonly IClassDatabaseSource? _classDatabase;

    public AssetsToolsUnitySerializedFileParser()
        : this((IClassDatabaseSource?)null)
    {
    }

    /// <summary>
    /// Creates a parser that falls back to the pinned class package for containers whose
    /// SerializedFile type trees were stripped (<c>TypeTreeEnabled=false</c>).
    /// </summary>
    public AssetsToolsUnitySerializedFileParser(ClassPackageSource? classDatabase)
        : this((IClassDatabaseSource?)classDatabase)
    {
    }

    internal AssetsToolsUnitySerializedFileParser(IClassDatabaseSource? classDatabase)
    {
        _classDatabase = classDatabase;
    }

    public UnityClassDatabaseDescriptor? ClassDatabase => _classDatabase?.Descriptor;

    public Task<IReadOnlyList<ParsedSceneContainer>> ParseAsync(
        IReadOnlyList<VerifiedSceneContainer> containers,
        CancellationToken cancellationToken) =>
        Task.FromResult(ParseContainers(containers, cancellationToken));

    private IReadOnlyList<ParsedSceneContainer> ParseContainers(
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

        return parsed.ToArray();
    }

    private ParsedSceneContainer ParseContainer(VerifiedSceneContainer container)
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
        var typeTreeSource = ParsedTypeTreeSource.Embedded;
        ClassDatabaseResolution? classDatabase = null;
        if (!assetsFile.Metadata.TypeTreeEnabled)
        {
            classDatabase = _classDatabase?.Resolve(assetsFile.Metadata.UnityVersion);
            typeTreeSource = classDatabase is null
                ? ParsedTypeTreeSource.Unavailable
                : ParsedTypeTreeSource.FromClassDatabase(
                    _classDatabase!.Descriptor,
                    classDatabase.ResolvedUnityVersion,
                    classDatabase.ExactVersionMatch);
        }

        var objects = assetsFile.AssetInfos.Select(info =>
        {
            var classId = info.GetTypeId(assetsFile);
            var kind = Classify(classId);
            var baseField = ReadSupportedBaseField(
                assetsFile,
                info,
                classId,
                kind,
                refTypeManager,
                classDatabase,
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
            objects.Any(item => item.Kind == ParsedSceneObjectKind.PrefabEvidence),
            assetsFile.Metadata.TypeTreeEnabled,
            typeTreeSource);
    }

    private static AssetTypeValueField? ReadSupportedBaseField(
        AssetsFile assetsFile,
        AssetFileInfo info,
        int classId,
        ParsedSceneObjectKind kind,
        RefTypeManager refTypeManager,
        ClassDatabaseResolution? classDatabase,
        string relativePath)
    {
        if (kind is not (ParsedSceneObjectKind.GameObject or
            ParsedSceneObjectKind.Transform or
            ParsedSceneObjectKind.MonoBehaviour or
            ParsedSceneObjectKind.MonoScript or
            ParsedSceneObjectKind.BuildSettings))
        {
            return null;
        }

        if (!assetsFile.Metadata.TypeTreeEnabled)
        {
            return classDatabase is null
                ? null
                : ReadFromClassDatabase(assetsFile, info, classId, refTypeManager, classDatabase, relativePath);
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

    // Only the built-in engine classes above are decoded from the class database: their
    // release layouts are what the package records. The release root node is used because a
    // player build serializes release layouts; editor-only fields are never present.
    private static AssetTypeValueField? ReadFromClassDatabase(
        AssetsFile assetsFile,
        AssetFileInfo info,
        int classId,
        RefTypeManager refTypeManager,
        ClassDatabaseResolution classDatabase,
        string relativePath)
    {
        var databaseType = classDatabase.Database.FindAssetClassByID(classId);
        if (databaseType is null)
            return null;

        try
        {
            var template = new AssetTypeTemplateField();
            template.FromClassDatabase(classDatabase.Database, databaseType, preferEditor: false);
            return template.MakeValue(
                assetsFile.Reader,
                info.GetAbsoluteByteOffset(assetsFile),
                refTypeManager);
        }
        catch (Exception exception) when (exception is not InvalidDataException)
        {
            throw new InvalidDataException(
                $"SerializedFile '{relativePath}' object {info.PathId} could not be decoded with the class database layout for Unity {classDatabase.ResolvedUnityVersion}.",
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
            ReadOptionalInt(field["m_RootOrder"]));

    // Unity 2022.2 dropped Transform.m_RootOrder from the serialized layout; sibling order then
    // comes from the parent's m_Children order, so a missing field is reported as 0.
    private static int ReadOptionalInt(AssetTypeValueField field) =>
        field.IsDummy ? 0 : field.AsInt;

    private static ParsedMonoBehaviourData ReadMonoBehaviour(AssetTypeValueField field) =>
        new(
            ReadPointer(field["m_GameObject"]),
            ReadPointer(field["m_Script"]),
            field["m_Enabled"].AsByte != 0,
            field["m_Name"].IsDummy ? string.Empty : field["m_Name"].AsString);

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
        // RectTransform (UI) derives from Transform and serializes the same leading fields
        // (m_GameObject, rotation, position, scale, m_Children, m_Father); decoding it with its own
        // class layout and reading those fields keeps UI hierarchies in the graph instead of
        // leaving every canvas child with an unresolvable parent.
        (int)AssetClassID.Transform or
            (int)AssetClassID.RectTransform => ParsedSceneObjectKind.Transform,
        (int)AssetClassID.MonoBehaviour => ParsedSceneObjectKind.MonoBehaviour,
        (int)AssetClassID.MonoScript => ParsedSceneObjectKind.MonoScript,
        (int)AssetClassID.BuildSettings => ParsedSceneObjectKind.BuildSettings,
        (int)AssetClassID.PrefabInstance or
            (int)AssetClassID.Prefab => ParsedSceneObjectKind.PrefabEvidence,
        _ => ParsedSceneObjectKind.Other
    };

    /// <summary>
    /// A class database resolved for one container's Unity version. Nested in the adapter so
    /// the AssetsTools.NET type stays confined to this file (see ParserIsolationTests).
    /// </summary>
    internal sealed record ClassDatabaseResolution(
        ClassDatabaseFile Database,
        string ResolvedUnityVersion,
        bool ExactVersionMatch);

    /// <summary>
    /// Supplies a class database for a container's Unity version. Internal, and nested, so the
    /// parser's public contract exposes no AssetsTools.NET type.
    /// </summary>
    internal interface IClassDatabaseSource
    {
        UnityClassDatabaseDescriptor Descriptor { get; }

        /// <summary>Returns null when the package is not installed or holds no usable dump.</summary>
        ClassDatabaseResolution? Resolve(string unityVersion);
    }

    /// <summary>
    /// Reads the pinned AssetsTools.NET class package (<c>classdata.tpk</c>) installed by
    /// <c>tools install unity-classdata</c>. The package is opened lazily, hashed against the
    /// pinned digest before any byte is interpreted, and never written. Resolution is offline.
    /// </summary>
    public sealed class ClassPackageSource : IClassDatabaseSource
    {
        private readonly string _packagePath;
        private readonly object _gate = new();
        private ClassPackageFile? _package;
        private bool _missing;

        public ClassPackageSource(string packagePath, UnityClassDatabaseDescriptor descriptor)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
            ArgumentNullException.ThrowIfNull(descriptor);
            _packagePath = Path.GetFullPath(packagePath);
            Descriptor = descriptor;
        }

        public UnityClassDatabaseDescriptor Descriptor { get; }

        public bool IsInstalled => File.Exists(_packagePath);

        ClassDatabaseResolution? IClassDatabaseSource.Resolve(string unityVersion)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(unityVersion);
            var package = LoadPackage();
            if (package is null)
                return null;

            UnityVersion requested;
            try
            {
                requested = new UnityVersion(unityVersion);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or IndexOutOfRangeException)
            {
                return null;
            }

            // The package resolves every class by version range, so any requested version at or
            // above the oldest dump yields a database. The provenance label must still say which
            // dump actually stood in: the newest one not newer than the container's version.
            var requestedKey = requested.ToUInt64();
            UnityVersion? resolved = null;
            var exact = false;
            foreach (var candidate in package.TpkTypeTree.Versions)
            {
                var key = candidate.ToUInt64();
                if (key == requestedKey)
                {
                    resolved = candidate;
                    exact = true;
                    break;
                }

                if (key < requestedKey && (resolved is null || key > resolved.ToUInt64()))
                    resolved = candidate;
            }

            if (resolved is null)
                return null;

            ClassDatabaseFile database;
            try
            {
                database = package.GetClassDatabase(requested);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new InvalidDataException(
                    $"Class database package '{Descriptor.PackageId} {Descriptor.PackageVersion}' could not produce a class database for Unity {unityVersion}.",
                    exception);
            }

            return new ClassDatabaseResolution(database, resolved.ToString(), exact);
        }

        private ClassPackageFile? LoadPackage()
        {
            lock (_gate)
            {
                if (_package is not null)
                    return _package;
                if (_missing)
                    return null;
                if (!File.Exists(_packagePath))
                {
                    _missing = true;
                    return null;
                }

                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(_packagePath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidDataException(
                        $"Class database package '{Descriptor.PackageId} {Descriptor.PackageVersion}' could not be read.",
                        exception);
                }

                var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(actual, Descriptor.PackageSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Class database package '{Descriptor.PackageId} {Descriptor.PackageVersion}' does not match its pinned SHA-256 " +
                        $"(expected {Descriptor.PackageSha256}, found {actual}); reinstall it with `tools install {UnityClassDatabasePin.ToolId} --repair`.");
                }

                try
                {
                    var package = new ClassPackageFile();
                    using var stream = new MemoryStream(bytes, writable: false);
                    package.Read(new AssetsFileReader(stream));
                    _package = package;
                    return package;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    throw new InvalidDataException(
                        $"Class database package '{Descriptor.PackageId} {Descriptor.PackageVersion}' is not a readable class package.",
                        exception);
                }
            }
        }
    }
}
