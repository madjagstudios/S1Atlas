using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using S1Atlas.Core.Metadata;
using Xunit;

namespace S1Atlas.Indexing.Tests.Decompilation;

public sealed class CustomAttributeDecoderTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "harmony-fixture", "S1Atlas.HarmonyModFixture.dll");

    [Fact]
    public void Decodes_constructor_and_named_arguments()
    {
        using var reader = OpenFixture();
        var attribute = FindClassAttribute(reader, "Mod.DecoderProbe", "FixtureCaseAttribute");

        var decoded = CustomAttributeDecoder.Decode(reader, attribute);

        Assert.Equal("Mod.FixtureCaseAttribute", decoded.AttributeTypeName);
        var name = Assert.Single(decoded.FixedArguments);
        Assert.Equal(CustomAttributeValueKind.String, name.Kind);
        Assert.Equal("decoder", name.Value);
        Assert.Equal(5, decoded.NamedArguments.Count);
        var kind = decoded.NamedArguments.Single(argument => argument.Name == "Kind").Value;
        Assert.Equal(CustomAttributeValueKind.Enum, kind.Kind);
        Assert.Equal("Mod.SampleKind", kind.TypeName);
        Assert.Equal(1, kind.Value);
        var target = decoded.NamedArguments.Single(argument => argument.Name == "Target").Value;
        Assert.Equal(CustomAttributeValueKind.Type, target.Kind);
        Assert.Equal("Game.Widget", target.TypeName);
        var types = decoded.NamedArguments.Single(argument => argument.Name == "Types").Value;
        Assert.Equal(CustomAttributeValueKind.Array, types.Kind);
        Assert.Equal(
            ["System.Int32", "System.String"],
            types.Elements!.Select(element => element.TypeName!).ToArray());
        var count = decoded.NamedArguments.Single(argument => argument.Name == "Count").Value;
        Assert.Equal(CustomAttributeValueKind.Primitive, count.Kind);
        Assert.Equal(3, count.Value);
        var targets = decoded.NamedArguments.Single(argument => argument.Name == "Targets").Value;
        Assert.Equal(CustomAttributeValueKind.Enum, targets.Kind);
        Assert.Equal("System.AttributeTargets", targets.TypeName);
        Assert.Equal(4, targets.Value);
    }

    [Fact]
    public void Decodes_harmony_type_and_name_arguments()
    {
        using var reader = OpenFixture();
        var attribute = FindClassAttribute(reader, "Mod.RunPatch", "HarmonyPatch");

        var decoded = CustomAttributeDecoder.Decode(reader, attribute);

        Assert.Equal("HarmonyLib.HarmonyPatch", decoded.AttributeTypeName);
        Assert.Equal(2, decoded.FixedArguments.Count);
        Assert.Equal(CustomAttributeValueKind.Type, decoded.FixedArguments[0].Kind);
        Assert.Equal("Game.Widget", decoded.FixedArguments[0].TypeName);
        Assert.Equal(CustomAttributeValueKind.String, decoded.FixedArguments[1].Kind);
        Assert.Equal("Run", decoded.FixedArguments[1].Value);
        Assert.Empty(decoded.NamedArguments);
    }

    [Fact]
    public void Decodes_type_and_enum_arrays()
    {
        using var reader = OpenFixture();
        var attribute = FindClassAttribute(reader, "Mod.RefOverloadPatch", "HarmonyPatch");

        var decoded = CustomAttributeDecoder.Decode(reader, attribute);

        Assert.Equal(4, decoded.FixedArguments.Count);
        Assert.Equal("Game.Widget", decoded.FixedArguments[0].TypeName);
        Assert.Equal("Compute", decoded.FixedArguments[1].Value);
        var argumentTypes = decoded.FixedArguments[2];
        Assert.Equal(CustomAttributeValueKind.Array, argumentTypes.Kind);
        Assert.Equal("System.Int32", Assert.Single(argumentTypes.Elements!).TypeName);
        var variations = decoded.FixedArguments[3];
        Assert.Equal(CustomAttributeValueKind.Array, variations.Kind);
        var variation = Assert.Single(variations.Elements!);
        Assert.Equal(CustomAttributeValueKind.Enum, variation.Kind);
        Assert.Equal("HarmonyLib.ArgumentType", variation.TypeName);
        Assert.Equal(1, variation.Value);
    }

    [Fact]
    public void Decodes_method_type_enum_argument()
    {
        using var reader = OpenFixture();
        var attribute = FindClassAttribute(reader, "Mod.StaticCtorPatch", "HarmonyPatch");

        var decoded = CustomAttributeDecoder.Decode(reader, attribute);

        Assert.Equal(2, decoded.FixedArguments.Count);
        var methodType = decoded.FixedArguments[1];
        Assert.Equal(CustomAttributeValueKind.Enum, methodType.Kind);
        Assert.Equal("HarmonyLib.MethodType", methodType.TypeName);
        Assert.Equal(4, methodType.Value);
    }

    [Fact]
    public void Decodes_nested_and_interop_type_references()
    {
        using var reader = OpenFixture();

        var nested = CustomAttributeDecoder.Decode(reader, FindClassAttribute(reader, "Mod.ConventionPatch", "HarmonyPatch"));
        Assert.Equal("Game.Widget+Nested", nested.FixedArguments[0].TypeName);

        var interop = CustomAttributeDecoder.Decode(reader, FindClassAttribute(reader, "Mod.InteropPrefixPatch", "HarmonyPatch"));
        Assert.Equal("Il2CppGame.Widget", interop.FixedArguments[0].TypeName);
    }

    [Theory]
    [InlineData("System.Int32, System.Private.CoreLib", "System.Int32")]
    [InlineData("Game.Widget, S1Atlas.HarmonyGameFixture", "Game.Widget")]
    [InlineData("System.Collections.Generic.List`1[[System.Int32, System.Private.CoreLib]], System.Private.CoreLib", "System.Collections.Generic.List`1<System.Int32>")]
    [InlineData("Game.Widget+Nested, S1Atlas.HarmonyGameFixture", "Game.Widget+Nested")]
    public void Parses_serialized_type_names(string serialized, string expected)
    {
        Assert.Equal(expected, CustomAttributeDecoder.ParseSerializedTypeName(serialized));
    }

    private static FixtureReader OpenFixture()
    {
        var stream = new FileStream(FixturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = new PEReader(stream);
        return new FixtureReader(stream, reader, reader.GetMetadataReader());
    }

    private static CustomAttributeHandle FindClassAttribute(MetadataReader reader, string typeName, string attributeName)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            var fullName = reader.GetString(definition.Namespace) + "." + reader.GetString(definition.Name);
            if (!string.Equals(fullName, typeName, StringComparison.Ordinal))
                continue;
            foreach (var attributeHandle in definition.GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(attributeHandle);
                if (AttributeNameOf(reader, attribute) == attributeName)
                    return attributeHandle;
            }
        }

        throw new InvalidOperationException($"Attribute {attributeName} not found on {typeName}.");
    }

    private static string? AttributeNameOf(MetadataReader reader, CustomAttribute attribute)
    {
        EntityHandle parent = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            _ => default,
        };
        return parent.Kind switch
        {
            HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)parent).Name),
            HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
            _ => null,
        };
    }

    private sealed class FixtureReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly PEReader _reader;

        public FixtureReader(FileStream stream, PEReader reader, MetadataReader metadata)
        {
            _stream = stream;
            _reader = reader;
            Metadata = metadata;
        }

        public MetadataReader Metadata { get; }

        public void Dispose()
        {
            _reader.Dispose();
            _stream.Dispose();
        }

        public static implicit operator MetadataReader(FixtureReader reader) => reader.Metadata;
    }
}
