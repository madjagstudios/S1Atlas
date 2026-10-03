using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;

namespace S1Atlas.Core.Metadata;

public enum CustomAttributeValueKind
{
    Null,
    Primitive,
    String,
    Type,
    Enum,
    Array
}

public sealed record CustomAttributeValue(
    CustomAttributeValueKind Kind,
    object? Value,
    string? TypeName,
    IReadOnlyList<CustomAttributeValue>? Elements);

public sealed record CustomAttributeNamedArgument(string Name, CustomAttributeValue Value);

public sealed record DecodedCustomAttribute(
    string? AttributeTypeName,
    IReadOnlyList<CustomAttributeValue> FixedArguments,
    IReadOnlyList<CustomAttributeNamedArgument> NamedArguments);

/// <summary>
/// Shared custom-attribute decoder over System.Reflection.Metadata. Decodes fixed and
/// named arguments into typed values: primitives, strings, typeof references as type
/// names, enums as their type name plus underlying value, and single-dimensional
/// arrays. Enum arguments whose type is defined in another assembly decode with the
/// default Int32 backing, since the referenced assembly is not resolved.
/// </summary>
public static class CustomAttributeDecoder
{
    public static DecodedCustomAttribute Decode(MetadataReader reader, CustomAttributeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var provider = new AttributeTypeProvider(reader);
        var value = reader.GetCustomAttribute(handle).DecodeValue(provider);
        return new DecodedCustomAttribute(
            AttributeTypeName(reader, handle),
            value.FixedArguments.Select(argument => provider.Convert(argument.Type, argument.Value)).ToArray(),
            value.NamedArguments
                .Select(argument => new CustomAttributeNamedArgument(argument.Name ?? string.Empty, provider.Convert(argument.Type, argument.Value)))
                .ToArray());
    }

    public static string? AttributeTypeName(MetadataReader reader, CustomAttributeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var constructor = reader.GetCustomAttribute(handle).Constructor;
        return constructor.Kind switch
        {
            HandleKind.MemberReference =>
                DeclaringTypeName(reader, reader.GetMemberReference((MemberReferenceHandle)constructor).Parent),
            HandleKind.MethodDefinition =>
                DeclaringTypeName(reader, reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()),
            _ => null,
        };
    }

    /// <summary>
    /// Renders a serialized type name ("Namespace.Type+Nested[[args]], Assembly") in
    /// canonical form: assembly stripped, generic arguments in angle brackets.
    /// </summary>
    public static string ParseSerializedTypeName(string serializedName)
    {
        ArgumentNullException.ThrowIfNull(serializedName);

        var split = TopLevelComma(serializedName);
        return RenderTypeName(split < 0 ? serializedName.Trim() : serializedName[..split].Trim());
    }

    private static string? DeclaringTypeName(MetadataReader reader, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => TypeReferenceName(reader, (TypeReferenceHandle)parent),
        HandleKind.TypeDefinition => TypeDefinitionName(reader, (TypeDefinitionHandle)parent),
        _ => null,
    };

    internal static string TypeReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Namespace) is { Length: > 0 }ns
            ? ns + "." + reader.GetString(reference.Name)
            : reader.GetString(reference.Name);
        return reference.ResolutionScope.Kind == HandleKind.TypeReference
            ? TypeReferenceName(reader, (TypeReferenceHandle)reference.ResolutionScope) + "+" + reader.GetString(reference.Name)
            : name;
    }

    internal static string TypeDefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var names = NestedTypeNames.GetValue(reader, static key => BuildNestedTypeNames(key));
        return names.TryGetValue(handle, out var name) ? name : reader.GetString(reader.GetTypeDefinition(handle).Name);
    }

    private static readonly ConditionalWeakTable<MetadataReader, Dictionary<TypeDefinitionHandle, string>> NestedTypeNames = new();

    private static Dictionary<TypeDefinitionHandle, string> BuildNestedTypeNames(MetadataReader reader)
    {
        var names = new Dictionary<TypeDefinitionHandle, string>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            if (definition.Attributes.HasFlag(System.Reflection.TypeAttributes.NestedPublic) ||
                definition.Attributes.HasFlag(System.Reflection.TypeAttributes.NestedPrivate) ||
                definition.Attributes.HasFlag(System.Reflection.TypeAttributes.NestedFamily) ||
                definition.Attributes.HasFlag(System.Reflection.TypeAttributes.NestedAssembly) ||
                definition.Attributes.HasFlag(System.Reflection.TypeAttributes.NestedFamANDAssem) ||
                definition.Attributes.HasFlag(System.Reflection.TypeAttributes.NestedFamORAssem))
            {
                continue;
            }

            var ns = reader.GetString(definition.Namespace);
            AddNestedTypeNames(reader, names, handle, ns.Length == 0 ? reader.GetString(definition.Name) : ns + "." + reader.GetString(definition.Name));
        }

        return names;
    }

    private static void AddNestedTypeNames(
        MetadataReader reader,
        Dictionary<TypeDefinitionHandle, string> names,
        TypeDefinitionHandle handle,
        string fullName)
    {
        names[handle] = fullName;
        foreach (var nested in reader.GetTypeDefinition(handle).GetNestedTypes())
            AddNestedTypeNames(reader, names, nested, fullName + "+" + reader.GetString(reader.GetTypeDefinition(nested).Name));
    }

    private static int TopLevelComma(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '[') depth++;
            else if (text[i] is ']') depth--;
            else if (text[i] == ',' && depth == 0) return i;
        }

        return -1;
    }

    private static string RenderTypeName(string type)
    {
        var suffix = string.Empty;
        while (type.EndsWith("[]", StringComparison.Ordinal) || type.EndsWith("&", StringComparison.Ordinal) || type.EndsWith("*", StringComparison.Ordinal))
        {
            if (type.EndsWith("[]", StringComparison.Ordinal))
            {
                suffix = "[]" + suffix;
                type = type[..^2];
            }
            else
            {
                suffix = type[^1..] + suffix;
                type = type[..^1];
            }
        }

        var arguments = type.IndexOf('[', StringComparison.Ordinal);
        if (arguments < 0)
            return type + suffix;

        var name = type[..arguments];
        var parsed = new List<string>();
        var depth = 0;
        var current = new System.Text.StringBuilder();
        foreach (var c in type[(arguments + 1)..^1])
        {
            if (c == '[') depth++;
            if (c == ']') depth--;
            if (c == ',' && depth == 0)
            {
                parsed.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        parsed.Add(current.ToString());
        return name + "<" + string.Join(",", parsed.Select(argument => ParseSerializedTypeName(argument.Trim('[', ']', ' ')))) + ">" + suffix;
    }

    private sealed class AttributeTypeProvider : ICustomAttributeTypeProvider<string>, ISignatureTypeProvider<string, object?>
    {
        private static readonly IReadOnlyDictionary<PrimitiveTypeCode, string> PrimitiveNames =
            new Dictionary<PrimitiveTypeCode, string>
            {
                [PrimitiveTypeCode.Void] = "System.Void",
                [PrimitiveTypeCode.Boolean] = "System.Boolean",
                [PrimitiveTypeCode.Char] = "System.Char",
                [PrimitiveTypeCode.SByte] = "System.SByte",
                [PrimitiveTypeCode.Byte] = "System.Byte",
                [PrimitiveTypeCode.Int16] = "System.Int16",
                [PrimitiveTypeCode.UInt16] = "System.UInt16",
                [PrimitiveTypeCode.Int32] = "System.Int32",
                [PrimitiveTypeCode.UInt32] = "System.UInt32",
                [PrimitiveTypeCode.Int64] = "System.Int64",
                [PrimitiveTypeCode.UInt64] = "System.UInt64",
                [PrimitiveTypeCode.Single] = "System.Single",
                [PrimitiveTypeCode.Double] = "System.Double",
                [PrimitiveTypeCode.String] = "System.String",
                [PrimitiveTypeCode.Object] = "System.Object",
                [PrimitiveTypeCode.TypedReference] = "System.TypedReference",
                [PrimitiveTypeCode.IntPtr] = "System.IntPtr",
                [PrimitiveTypeCode.UIntPtr] = "System.UIntPtr",
            };

        private static readonly HashSet<string> PrimitiveTypeNames = new(PrimitiveNames.Values, StringComparer.Ordinal);

        private readonly MetadataReader _reader;
        private readonly Dictionary<string, PrimitiveTypeCode> _enumBacking = new(StringComparer.Ordinal);

        public AttributeTypeProvider(MetadataReader reader)
        {
            _reader = reader;
        }

        public CustomAttributeValue Convert(string type, object? value)
        {
            if (value is null)
                return new CustomAttributeValue(CustomAttributeValueKind.Null, null, null, null);
            if (value is ImmutableArray<CustomAttributeTypedArgument<string>> items)
                return new CustomAttributeValue(
                    CustomAttributeValueKind.Array,
                    null,
                    type,
                    items.Select(item => Convert(item.Type, item.Value)).ToArray());
            if (IsSystemType(type))
                return new CustomAttributeValue(CustomAttributeValueKind.Type, null, (string)value, null);
            if (string.Equals(type, "System.String", StringComparison.Ordinal))
                return new CustomAttributeValue(CustomAttributeValueKind.String, value, type, null);
            if (PrimitiveTypeNames.Contains(type))
                return new CustomAttributeValue(CustomAttributeValueKind.Primitive, value, type, null);

            return new CustomAttributeValue(CustomAttributeValueKind.Enum, value, type, null);
        }

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) =>
            PrimitiveNames.TryGetValue(typeCode, out var name)
                ? name
                : throw new InvalidOperationException($"Unsupported attribute primitive {typeCode}.");

        public string GetSystemType() => "System.Type";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var name = TypeDefinitionName(reader, handle);
            if (IsEnumDefinition(reader, handle))
                _enumBacking.TryAdd(name, EnumBackingCode(reader, handle));
            return name;
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            TypeReferenceName(reader, handle);

        public string GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public PrimitiveTypeCode GetUnderlyingEnumType(string type) =>
            _enumBacking.TryGetValue(type, out var code) ? code : PrimitiveTypeCode.Int32;

        public bool IsSystemType(string type) => string.Equals(type, "System.Type", StringComparison.Ordinal);

        public string GetTypeFromSerializedName(string name) => ParseSerializedTypeName(name);

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType + "<" + string.Join(",", typeArguments) + ">";

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

        public string GetModifiedType(string modifierType, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => elementType + "*";

        private static bool IsEnumDefinition(MetadataReader reader, TypeDefinitionHandle handle)
        {
            var baseType = reader.GetTypeDefinition(handle).BaseType;
            return baseType.Kind == HandleKind.TypeReference &&
                string.Equals(reader.GetString(reader.GetTypeReference((TypeReferenceHandle)baseType).Name), "Enum", StringComparison.Ordinal) &&
                string.Equals(reader.GetString(reader.GetTypeReference((TypeReferenceHandle)baseType).Namespace), "System", StringComparison.Ordinal);
        }

        private static PrimitiveTypeCode EnumBackingCode(MetadataReader reader, TypeDefinitionHandle handle)
        {
            foreach (var fieldHandle in reader.GetTypeDefinition(handle).GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if (!string.Equals(reader.GetString(field.Name), "value__", StringComparison.Ordinal))
                    continue;
                var blob = reader.GetBlobBytes(field.Signature);
                return blob.Length >= 2 ? (PrimitiveTypeCode)blob[1] : PrimitiveTypeCode.Int32;
            }

            return PrimitiveTypeCode.Int32;
        }
    }
}
