using System.Reflection.Metadata;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Metadata;

namespace S1Atlas.Indexing.Decompilation;

/// <summary>
/// Reads attribute-declared Harmony patches from one type definition. Matches attributes
/// by full type name, so no Harmony package is needed. Class-level and method-level
/// HarmonyPatch details merge per-field with method-level winning; patch kinds come
/// from kind attributes first and the method-name convention second. TargetMethod and
/// TargetMethods mark runtime-computed targets, which are reported unresolved.
/// </summary>
public static class HarmonyPatchReader
{
    private const string HarmonyPatchName = "HarmonyLib.HarmonyPatch";
    private const string HarmonyTargetMethodName = "HarmonyLib.HarmonyTargetMethod";
    private const string HarmonyTargetMethodsName = "HarmonyLib.HarmonyTargetMethods";

    private static readonly (string AttributeName, string MethodName, HarmonyPatchKind Kind)[] Kinds =
    [
        ("HarmonyLib.HarmonyPrefix", "Prefix", HarmonyPatchKind.Prefix),
        ("HarmonyLib.HarmonyPostfix", "Postfix", HarmonyPatchKind.Postfix),
        ("HarmonyLib.HarmonyTranspiler", "Transpiler", HarmonyPatchKind.Transpiler),
        ("HarmonyLib.HarmonyFinalizer", "Finalizer", HarmonyPatchKind.Finalizer),
    ];

    private const int MethodTypeNormal = 0;
    private const int MethodTypeGetter = 1;
    private const int MethodTypeSetter = 2;
    private const int MethodTypeConstructor = 3;
    private const int MethodTypeStaticConstructor = 4;

    public static IReadOnlyDictionary<MethodDefinitionHandle, IReadOnlyList<ManagedPatchFact>> ReadTypePatches(
        MetadataReader reader,
        TypeDefinitionHandle type)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var definition = reader.GetTypeDefinition(type);
        var classInfos = ReadPatchInfos(reader, definition.GetCustomAttributes());
        var methods = definition.GetMethods()
            .Select(handle => (Handle: handle, Definition: reader.GetMethodDefinition(handle)))
            .ToArray();
        var hasRuntimeTarget = methods.Any(method =>
            IsRuntimeTargetName(reader.GetString(method.Definition.Name)) ||
            HasAttribute(reader, method.Definition.GetCustomAttributes(), HarmonyTargetMethodName, HarmonyTargetMethodsName));

        var patches = new Dictionary<MethodDefinitionHandle, IReadOnlyList<ManagedPatchFact>>();
        foreach (var method in methods)
        {
            var kind = ReadKind(reader, method.Definition);
            if (kind is null)
                continue;

            if (hasRuntimeTarget)
            {
                patches[method.Handle] = [new ManagedPatchFact(kind.Value, null, HarmonyPatchReasons.RuntimeComputedTarget, RelationshipEvidence.Metadata)];
                continue;
            }

            var merged = PatchTargetInfo.Merge(classInfos, ReadPatchInfos(reader, method.Definition.GetCustomAttributes()));
            patches[method.Handle] = [BuildFact(kind.Value, merged)];
        }

        return patches;
    }

    private static HarmonyPatchKind? ReadKind(MetadataReader reader, MethodDefinition method)
    {
        var attributes = method.GetCustomAttributes()
            .Select(handle => CustomAttributeDecoder.AttributeTypeName(reader, handle))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (attributeName, _, kind) in Kinds)
        {
            if (attributes.Contains(attributeName))
                return kind;
        }

        var name = reader.GetString(method.Name);
        foreach (var (_, methodName, kind) in Kinds)
        {
            if (string.Equals(name, methodName, StringComparison.Ordinal))
                return kind;
        }

        return null;
    }

    private static bool IsRuntimeTargetName(string name) =>
        string.Equals(name, "TargetMethod", StringComparison.Ordinal) ||
        string.Equals(name, "TargetMethods", StringComparison.Ordinal);

    private static bool HasAttribute(MetadataReader reader, CustomAttributeHandleCollection attributes, params string[] names)
    {
        foreach (var handle in attributes)
        {
            var attributeName = CustomAttributeDecoder.AttributeTypeName(reader, handle);
            if (attributeName is not null && names.Contains(attributeName, StringComparer.Ordinal))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<PatchTargetInfo> ReadPatchInfos(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        var infos = new List<PatchTargetInfo>();
        foreach (var handle in attributes)
        {
            if (!string.Equals(CustomAttributeDecoder.AttributeTypeName(reader, handle), HarmonyPatchName, StringComparison.Ordinal))
                continue;
            try
            {
                infos.Add(PatchTargetInfo.FromArguments(CustomAttributeDecoder.Decode(reader, handle).FixedArguments));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or BadImageFormatException)
            {
                continue;
            }
        }

        return infos;
    }

    private static ManagedPatchFact BuildFact(HarmonyPatchKind kind, PatchTargetInfo merged)
    {
        var type = merged.DeclaringType ?? (merged.DeclaringTypeName is null ? null : CustomAttributeDecoder.ParseSerializedTypeName(merged.DeclaringTypeName));
        var methodType = merged.MethodType ?? MethodTypeNormal;
        var parameters = merged.ArgumentTypes is null ? null : RenderParameters(merged.ArgumentTypes, merged.ArgumentVariations);

        if (type is null && merged.MethodName is null && methodType is MethodTypeNormal)
            return new ManagedPatchFact(kind, null, HarmonyPatchReasons.NoTargetSpecified, RelationshipEvidence.Metadata);
        if (type is null)
            return new ManagedPatchFact(kind, null, HarmonyPatchReasons.UnknownDeclaringType, RelationshipEvidence.Metadata);

        var member = methodType switch
        {
            MethodTypeNormal => merged.MethodName,
            MethodTypeGetter => merged.MethodName is null ? null : "get_" + merged.MethodName,
            MethodTypeSetter => merged.MethodName is null ? null : "set_" + merged.MethodName,
            MethodTypeConstructor => ".ctor",
            MethodTypeStaticConstructor => ".cctor",
            _ => null,
        };
        if (member is null && methodType is MethodTypeNormal or MethodTypeGetter or MethodTypeSetter)
            return new ManagedPatchFact(kind, null, HarmonyPatchReasons.UnknownMemberName, RelationshipEvidence.Metadata);
        if (member is null)
            return new ManagedPatchFact(
                kind,
                merged.MethodName is null ? null : type + "::" + merged.MethodName + parameters,
                HarmonyPatchReasons.UnsupportedMethodType,
                RelationshipEvidence.Metadata);

        return new ManagedPatchFact(kind, type + "::" + member + parameters, null, RelationshipEvidence.Metadata);
    }

    private static string RenderParameters(IReadOnlyList<string> argumentTypes, IReadOnlyList<int> variations)
    {
        var rendered = new List<string>(argumentTypes.Count);
        for (var i = 0; i < argumentTypes.Count; i++)
        {
            var variation = i < variations.Count ? variations[i] : 0;
            rendered.Add(variation switch
            {
                1 or 2 => argumentTypes[i] + "&",
                3 => argumentTypes[i] + "*",
                _ => argumentTypes[i],
            });
        }

        return "(" + string.Join(",", rendered) + ")";
    }

    private sealed record PatchTargetInfo(
        string? DeclaringType,
        string? DeclaringTypeName,
        string? MethodName,
        int? MethodType,
        IReadOnlyList<string>? ArgumentTypes,
        IReadOnlyList<int> ArgumentVariations)
    {
        public static PatchTargetInfo Empty { get; } = new(null, null, null, null, null, []);

        public static PatchTargetInfo Merge(IEnumerable<PatchTargetInfo> classInfos, IEnumerable<PatchTargetInfo> methodInfos)
        {
            var merged = Empty;
            foreach (var info in classInfos.Concat(methodInfos))
            {
                merged = new PatchTargetInfo(
                    info.DeclaringType ?? merged.DeclaringType,
                    info.DeclaringTypeName ?? merged.DeclaringTypeName,
                    info.MethodName ?? merged.MethodName,
                    info.MethodType ?? merged.MethodType,
                    info.ArgumentTypes ?? merged.ArgumentTypes,
                    info.ArgumentVariations.Count > 0 ? info.ArgumentVariations : merged.ArgumentVariations);
            }

            return merged;
        }

        public static PatchTargetInfo FromArguments(IReadOnlyList<CustomAttributeValue> fixedArguments)
        {
            var kinds = fixedArguments.Select(argument => argument.Kind).ToArray();
            return (fixedArguments.Count, kinds) switch
            {
                (0, _) => Empty,
                (1, [CustomAttributeValueKind.Type]) => Empty with { DeclaringType = fixedArguments[0].TypeName },
                (1, [CustomAttributeValueKind.String]) => Empty with { MethodName = (string?)fixedArguments[0].Value },
                (1, [CustomAttributeValueKind.Enum]) => Empty with { MethodType = EnumValue(fixedArguments[0]) },
                (1, [CustomAttributeValueKind.Array]) => Empty with { ArgumentTypes = TypeArray(fixedArguments[0]) },
                (2, [CustomAttributeValueKind.Type, CustomAttributeValueKind.Array]) =>
                    Empty with { DeclaringType = fixedArguments[0].TypeName, ArgumentTypes = TypeArray(fixedArguments[1]) },
                (2, [CustomAttributeValueKind.Type, CustomAttributeValueKind.String]) =>
                    Empty with { DeclaringType = fixedArguments[0].TypeName, MethodName = (string?)fixedArguments[1].Value },
                (2, [CustomAttributeValueKind.Type, CustomAttributeValueKind.Enum]) =>
                    Empty with { DeclaringType = fixedArguments[0].TypeName, MethodType = EnumValue(fixedArguments[1]) },
                (2, [CustomAttributeValueKind.String, CustomAttributeValueKind.Array]) =>
                    Empty with { MethodName = (string?)fixedArguments[0].Value, ArgumentTypes = TypeArray(fixedArguments[1]) },
                (2, [CustomAttributeValueKind.String, CustomAttributeValueKind.Enum]) =>
                    Empty with { MethodName = (string?)fixedArguments[0].Value, MethodType = EnumValue(fixedArguments[1]) },
                (2, [CustomAttributeValueKind.Enum, CustomAttributeValueKind.Array]) =>
                    Empty with { MethodType = EnumValue(fixedArguments[0]), ArgumentTypes = TypeArray(fixedArguments[1]) },
                (2, [CustomAttributeValueKind.String, CustomAttributeValueKind.String]) =>
                    Empty with { DeclaringTypeName = (string?)fixedArguments[0].Value, MethodName = (string?)fixedArguments[1].Value },
                (2, [CustomAttributeValueKind.Array, CustomAttributeValueKind.Array]) =>
                    Empty with { ArgumentTypes = TypeArray(fixedArguments[0]), ArgumentVariations = IntArray(fixedArguments[1]) },
                (3, [CustomAttributeValueKind.Type, CustomAttributeValueKind.String, CustomAttributeValueKind.Array]) =>
                    Empty with
                    {
                        DeclaringType = fixedArguments[0].TypeName,
                        MethodName = (string?)fixedArguments[1].Value,
                        ArgumentTypes = TypeArray(fixedArguments[2]),
                    },
                (3, [CustomAttributeValueKind.Type, CustomAttributeValueKind.Enum, CustomAttributeValueKind.Array]) =>
                    Empty with
                    {
                        DeclaringType = fixedArguments[0].TypeName,
                        MethodType = EnumValue(fixedArguments[1]),
                        ArgumentTypes = TypeArray(fixedArguments[2]),
                    },
                (3, [CustomAttributeValueKind.Type, CustomAttributeValueKind.String, CustomAttributeValueKind.Enum]) =>
                    Empty with
                    {
                        DeclaringType = fixedArguments[0].TypeName,
                        MethodName = (string?)fixedArguments[1].Value,
                        MethodType = EnumValue(fixedArguments[2]),
                    },
                (3, [CustomAttributeValueKind.String, CustomAttributeValueKind.Array, CustomAttributeValueKind.Array]) =>
                    Empty with
                    {
                        MethodName = (string?)fixedArguments[0].Value,
                        ArgumentTypes = TypeArray(fixedArguments[1]),
                        ArgumentVariations = IntArray(fixedArguments[2]),
                    },
                (3, [CustomAttributeValueKind.Enum, CustomAttributeValueKind.Array, CustomAttributeValueKind.Array]) =>
                    Empty with
                    {
                        MethodType = EnumValue(fixedArguments[0]),
                        ArgumentTypes = TypeArray(fixedArguments[1]),
                        ArgumentVariations = IntArray(fixedArguments[2]),
                    },
                (3, [CustomAttributeValueKind.String, CustomAttributeValueKind.String, CustomAttributeValueKind.Enum]) =>
                    Empty with
                    {
                        DeclaringTypeName = (string?)fixedArguments[0].Value,
                        MethodName = (string?)fixedArguments[1].Value,
                        MethodType = EnumValue(fixedArguments[2]),
                    },
                (4, [CustomAttributeValueKind.Type, CustomAttributeValueKind.String, CustomAttributeValueKind.Array, CustomAttributeValueKind.Array]) =>
                    Empty with
                    {
                        DeclaringType = fixedArguments[0].TypeName,
                        MethodName = (string?)fixedArguments[1].Value,
                        ArgumentTypes = TypeArray(fixedArguments[2]),
                        ArgumentVariations = IntArray(fixedArguments[3]),
                    },
                (4, [CustomAttributeValueKind.Type, CustomAttributeValueKind.Enum, CustomAttributeValueKind.Array, CustomAttributeValueKind.Array]) =>
                    Empty with
                    {
                        DeclaringType = fixedArguments[0].TypeName,
                        MethodType = EnumValue(fixedArguments[1]),
                        ArgumentTypes = TypeArray(fixedArguments[2]),
                        ArgumentVariations = IntArray(fixedArguments[3]),
                    },
                _ => Empty,
            };
        }

        private static int EnumValue(CustomAttributeValue value) =>
            value.Value is null ? 0 : Convert.ToInt32(value.Value);

        private static IReadOnlyList<string>? TypeArray(CustomAttributeValue value)
        {
            if (value.Elements is null)
                return null;
            var names = new List<string>(value.Elements.Count);
            foreach (var element in value.Elements)
            {
                if (element.Kind != CustomAttributeValueKind.Type || element.TypeName is null)
                    return null;
                names.Add(element.TypeName);
            }

            return names;
        }

        private static IReadOnlyList<int> IntArray(CustomAttributeValue value)
        {
            if (value.Elements is null)
                return [];
            var values = new List<int>(value.Elements.Count);
            foreach (var element in value.Elements)
                values.Add(element.Value is null ? 0 : Convert.ToInt32(element.Value));
            return values;
        }
    }
}
