using System.Globalization;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using S1Atlas.Core.Scenes;

namespace S1Atlas.Extraction.Scene;

// Game-script field decoding. Stripped IL2CPP containers carry no script layouts, so each
// MonoBehaviour's layout is generated from the extraction's reconstructed assemblies (which must
// carry restored [SerializeField] attributes; the workflow checks that before passing layouts).
// A generated layout is trusted for an object only when reading it consumes exactly the object's
// byte count; anything else is recorded as unavailable with a reason, never as values.
public sealed partial class AssetsToolsUnitySerializedFileParser
{
    internal const int MaxScriptObjectBytes = 4 * 1024 * 1024;
    internal const int MaxScriptFields = 256;
    internal const int MaxScriptArrayElements = 32;
    internal const int MaxScriptStringLength = 512;

    // m_GameObject, m_Enabled, m_Script, m_Name precede every script's own fields.
    private const int MonoBehaviourHeaderFieldCount = 4;

    public Task<IReadOnlyList<ParsedSceneContainer>> ParseAsync(
        IReadOnlyList<VerifiedSceneContainer> containers,
        SceneScriptLayoutSource? scriptLayouts,
        CancellationToken cancellationToken)
    {
        var parsed = ParseContainers(containers, cancellationToken);
        return Task.FromResult(scriptLayouts is null
            ? parsed
            : DecodeScriptFields(containers, parsed, scriptLayouts, cancellationToken));
    }

    private IReadOnlyList<ParsedSceneContainer> DecodeScriptFields(
        IReadOnlyList<VerifiedSceneContainer> verified,
        IReadOnlyList<ParsedSceneContainer> parsed,
        SceneScriptLayoutSource scriptLayouts,
        CancellationToken cancellationToken)
    {
        var scripts = parsed
            .SelectMany(container => container.Objects
                .Where(item => item.MonoScript is not null)
                .Select(item => (Key: (container.RelativePath, item.LocalFileId), Script: item.MonoScript!)))
            .ToDictionary(pair => pair.Key, pair => pair.Script);
        var paths = parsed.Select(container => container.RelativePath).ToArray();
        var templates = new Dictionary<(string, string, string), AssetTypeTemplateField?>();

        // MonoCecilTempGenerator has Dispose() but is not IDisposable.
        var generator = new MonoCecilTempGenerator(scriptLayouts.ManagedAssembliesPath);
        try
        {
            var result = new ParsedSceneContainer[parsed.Count];
            for (var index = 0; index < parsed.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result[index] = parsed[index].Objects.Any(item => item.MonoBehaviour is not null)
                    ? DecodeContainerScriptFields(verified[index], parsed[index], scripts, paths, generator, templates)
                    : parsed[index];
            }

            return result;
        }
        finally
        {
            generator.Dispose();
        }
    }

    private ParsedSceneContainer DecodeContainerScriptFields(
        VerifiedSceneContainer source,
        ParsedSceneContainer container,
        IReadOnlyDictionary<(string, long), ParsedMonoScriptData> scripts,
        IReadOnlyList<string> paths,
        MonoCecilTempGenerator generator,
        Dictionary<(string, string, string), AssetTypeTemplateField?> templates)
    {
        using var stream = new FileStream(source.PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var assetsFile = new AssetsFile();
        assetsFile.Read(stream);

        // Embedded type trees already describe script fields; decoding those is out of scope.
        if (assetsFile.Metadata.TypeTreeEnabled)
            return container;
        var classDatabase = _classDatabase?.Resolve(assetsFile.Metadata.UnityVersion);
        var baseType = classDatabase?.Database.FindAssetClassByID((int)AssetClassID.MonoBehaviour);
        if (classDatabase is null || baseType is null)
            return container;

        var baseTemplate = new AssetTypeTemplateField();
        baseTemplate.FromClassDatabase(classDatabase.Database, baseType, preferEditor: false);
        var unityVersion = new UnityVersion(assetsFile.Metadata.UnityVersion);
        var refTypes = new RefTypeManager();
        refTypes.WithMonoTemplateGenerator(
            assetsFile.Metadata,
            generator,
            new Dictionary<AssetTypeReference, AssetTypeTemplateField>());
        var infos = assetsFile.AssetInfos.ToDictionary(info => info.PathId);

        var objects = container.Objects.Select(item =>
        {
            if (item.MonoBehaviour is null)
                return item;
            var script = ResolveScript(container, item.MonoBehaviour.Script, scripts, paths);
            if (script is null)
                return item with { ScriptFields = ParsedScriptFields.Unavailable("script-unresolved") };
            var template = TemplateFor(baseTemplate, script, unityVersion, generator, templates);
            if (template is null)
            {
                return item with
                {
                    ScriptFields = ParsedScriptFields.Unavailable($"script-type-not-found: {script.Namespace}.{script.ClassName}")
                };
            }

            return item with { ScriptFields = DecodeScriptObject(assetsFile, infos[item.LocalFileId], template, refTypes) };
        }).ToArray();
        return container with { Objects = objects };
    }

    // Same-container scripts use fileId 0; external ones resolve by file name against the verified
    // container set, the fallback the normalizer's pointer resolver also uses.
    private static ParsedMonoScriptData? ResolveScript(
        ParsedSceneContainer container,
        ParsedScenePPtr pointer,
        IReadOnlyDictionary<(string, long), ParsedMonoScriptData> scripts,
        IReadOnlyList<string> paths)
    {
        if (pointer.LocalFileId <= 0)
            return null;
        var path = container.RelativePath;
        if (pointer.FileId != 0)
        {
            var external = container.ExternalReferences.SingleOrDefault(item => item.FileId == pointer.FileId);
            if (external is null)
                return null;
            var names = new[] { external.PathName, external.OriginalPathName }
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => Path.GetFileName(name.Replace('\\', '/')))
                .ToHashSet(StringComparer.Ordinal);
            var matches = paths.Where(candidate => names.Contains(Path.GetFileName(candidate))).ToArray();
            if (matches.Length != 1)
                return null;
            path = matches[0];
        }

        return scripts.TryGetValue((path, pointer.LocalFileId), out var script) ? script : null;
    }

    private static AssetTypeTemplateField? TemplateFor(
        AssetTypeTemplateField baseTemplate,
        ParsedMonoScriptData script,
        UnityVersion unityVersion,
        MonoCecilTempGenerator generator,
        Dictionary<(string, string, string), AssetTypeTemplateField?> cache)
    {
        var key = (script.AssemblyName, script.Namespace, script.ClassName);
        if (cache.TryGetValue(key, out var cached))
            return cached;

        AssetTypeTemplateField? template;
        try
        {
            template = generator.GetTemplateField(
                baseTemplate.Clone(),
                script.AssemblyName,
                script.Namespace,
                script.ClassName,
                unityVersion);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The generator throws (NullReferenceException) instead of returning null when the
            // class or assembly is absent from the reconstruction.
            template = null;
        }

        cache[key] = template;
        return template;
    }

    private static ParsedScriptFields DecodeScriptObject(
        AssetsFile assetsFile,
        AssetFileInfo info,
        AssetTypeTemplateField template,
        RefTypeManager refTypes)
    {
        if (info.ByteSize > MaxScriptObjectBytes)
            return ParsedScriptFields.Unavailable($"object-too-large: {info.ByteSize} bytes");
        var size = checked((int)info.ByteSize);
        assetsFile.Reader.Position = info.GetAbsoluteByteOffset(assetsFile);
        var bytes = assetsFile.Reader.ReadBytes(size);
        if (bytes.Length != size)
            return ParsedScriptFields.Unavailable($"layout-mismatch: object data ends after {bytes.Length} of {size} bytes");

        try
        {
            // Walk first without allocating values: a misaligned layout reads garbage array
            // lengths, and inside the object's own slice those fail fast with end-of-stream
            // instead of allocating (an unguarded decode of a real build reached ~12 GB).
            using (var walkReader = SliceReader(bytes, assetsFile.Reader.BigEndian))
            {
                var iterator = new AssetTypeValueIterator(template, walkReader, refTypes);
                var maximumSteps = (long)size * 4 + 1024;
                for (var steps = 0L; iterator.ReadNext(); steps++)
                {
                    if (steps > maximumSteps)
                        return ParsedScriptFields.Unavailable("layout-mismatch: walk exceeded its step bound");
                }
            }

            using var valueReader = SliceReader(bytes, assetsFile.Reader.BigEndian);
            var value = template.MakeValue(valueReader, refTypes);
            if (valueReader.Position != size)
                return ParsedScriptFields.Unavailable($"layout-mismatch: consumed {valueReader.Position} of {size} bytes");
            return Flatten(value);
        }
        // Listed rather than caught wholesale so OutOfMemoryException and cancellation still surface.
        catch (Exception exception) when (exception is EndOfStreamException or IOException or InvalidDataException or
                                              ArgumentException or OverflowException or NullReferenceException or
                                              IndexOutOfRangeException or InvalidCastException or NotSupportedException or
                                              FormatException or KeyNotFoundException)
        {
            return ParsedScriptFields.Unavailable($"layout-mismatch: {exception.GetType().Name}");
        }
    }

    private static AssetsFileReader SliceReader(byte[] bytes, bool bigEndian) =>
        new(new MemoryStream(bytes, writable: false)) { BigEndian = bigEndian };

    private static ParsedScriptFields Flatten(AssetTypeValueField root)
    {
        var fields = new List<SceneScriptField>();
        var complete = true;
        foreach (var child in root.Children.Skip(MonoBehaviourHeaderFieldCount))
            complete &= AppendField(child, child.FieldName, fields);
        return new ParsedScriptFields(SceneScriptFieldSetStatus.Decoded, null, fields, Truncated: !complete);
    }

    // Returns false when a cap dropped data below this field.
    private static bool AppendField(AssetTypeValueField field, string path, List<SceneScriptField> fields)
    {
        if (fields.Count >= MaxScriptFields)
            return false;

        if (field.TypeName.StartsWith("PPtr<", StringComparison.Ordinal))
        {
            var pointer = ReadPointer(field);
            fields.Add(new SceneScriptField(path, field.TypeName, SceneScriptFieldValueKind.PPtr,
                string.Create(CultureInfo.InvariantCulture, $"{pointer.FileId}:{pointer.LocalFileId}")));
            return true;
        }

        if (field.TemplateField.IsArray)
        {
            if (field.Value?.ValueType == AssetValueType.ByteArray)
            {
                // Lower-case hex, bounded like strings; a longer array keeps its leading bytes.
                var bytes = field.AsByteArray;
                var kept = Math.Min(bytes.Length, MaxScriptStringLength / 2);
                fields.Add(new SceneScriptField(path, field.TypeName, SceneScriptFieldValueKind.Bytes,
                    Convert.ToHexString(bytes, 0, kept).ToLowerInvariant()));
                return kept == bytes.Length;
            }

            fields.Add(new SceneScriptField(path + ".size", "int", SceneScriptFieldValueKind.ArraySize,
                field.Children.Count.ToString(CultureInfo.InvariantCulture)));
            var complete = field.Children.Count <= MaxScriptArrayElements;
            for (var index = 0; index < Math.Min(field.Children.Count, MaxScriptArrayElements); index++)
                complete &= AppendField(field.Children[index], $"{path}[{index}]", fields);
            return complete;
        }

        if (field.Value is not null)
        {
            var (kind, text) = Scalar(field);
            var fits = text.Length <= MaxScriptStringLength;
            fields.Add(new SceneScriptField(path, field.TypeName, kind, fits ? text : text[..MaxScriptStringLength]));
            return fits;
        }

        var all = true;
        foreach (var child in field.Children)
            all &= AppendField(child, $"{path}.{child.FieldName}", fields);
        return all;
    }

    private static (SceneScriptFieldValueKind Kind, string Text) Scalar(AssetTypeValueField field) =>
        field.Value.ValueType switch
        {
            AssetValueType.String => (SceneScriptFieldValueKind.String, field.AsString),
            AssetValueType.Bool => (SceneScriptFieldValueKind.Boolean, field.AsBool ? "true" : "false"),
            AssetValueType.Float => (SceneScriptFieldValueKind.Float, field.AsFloat.ToString("R", CultureInfo.InvariantCulture)),
            AssetValueType.Double => (SceneScriptFieldValueKind.Float, field.AsDouble.ToString("R", CultureInfo.InvariantCulture)),
            AssetValueType.Int64 => (SceneScriptFieldValueKind.Integer, field.AsLong.ToString(CultureInfo.InvariantCulture)),
            AssetValueType.UInt64 => (SceneScriptFieldValueKind.Integer, field.AsULong.ToString(CultureInfo.InvariantCulture)),
            AssetValueType.UInt32 or AssetValueType.UInt16 or AssetValueType.UInt8 =>
                (SceneScriptFieldValueKind.Integer, field.AsUInt.ToString(CultureInfo.InvariantCulture)),
            _ => (SceneScriptFieldValueKind.Integer, field.AsInt.ToString(CultureInfo.InvariantCulture))
        };
}
