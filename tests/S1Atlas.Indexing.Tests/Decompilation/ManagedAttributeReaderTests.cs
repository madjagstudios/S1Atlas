using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using S1Atlas.Core.Metadata;
using S1Atlas.Indexing.Decompilation;
using Xunit;

namespace S1Atlas.Indexing.Tests.Decompilation;

public sealed class ManagedAttributeReaderTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "harmony-fixture", "S1Atlas.HarmonyModFixture.dll");

    [Fact]
    public void Reads_attributes_on_all_six_target_kinds()
    {
        using var reader = OpenFixture();
        var type = FindType(reader, "Mod.AttributeTargetsProbe");

        var attributes = ManagedAttributeReader.ReadTypeAttributes(reader, type);

        Assert.Equal("type", ProbeTag(attributes.Type ?? []));
        Assert.Equal("field", ProbeTag(attributes.Fields[FindField(reader, type, "MarkedField")]));
        Assert.Equal("property", ProbeTag(attributes.Properties[FindProperty(reader, type, "MarkedProperty")]));
        Assert.Equal("event", ProbeTag(attributes.Events[FindEvent(reader, type, "MarkedEvent")]));
        var method = FindMethod(reader, type, "MarkedMethod");
        Assert.Equal("method", ProbeTag(attributes.Methods[method]));
        var parameter = Assert.Single(attributes.Parameters[method]);
        Assert.Equal(0, parameter.Index);
        Assert.Equal("markedParameter", parameter.Name);
        Assert.Equal("parameter", ProbeTag(parameter.Attributes));
    }

    [Fact]
    public void Drops_injected_analysis_attributes_but_keeps_genuine_ones()
    {
        using var reader = OpenFixture();
        var type = FindType(reader, "Mod.AttributeTargetsProbe");

        var attributes = ManagedAttributeReader.ReadTypeAttributes(reader, type);

        var kept = attributes.Methods[FindMethod(reader, type, "FilteredMethod")];
        var genuine = Assert.Single(kept);
        Assert.Equal("Mod.ProbeMarkerAttribute", genuine.AttributeTypeName);
        Assert.Equal("kept", Tag(genuine));
    }

    [Fact]
    public void Omits_members_without_attributes()
    {
        using var reader = OpenFixture();
        var type = FindType(reader, "Mod.AttributeTargetsProbe");

        var attributes = ManagedAttributeReader.ReadTypeAttributes(reader, type);

        Assert.DoesNotContain(FindMethod(reader, type, ".ctor"), attributes.Methods.Keys);
        Assert.DoesNotContain(FindMethod(reader, type, ".ctor"), attributes.Parameters.Keys);
    }

    // The marker is singled out because the compiler also emits genuine attributes
    // of its own (NullableContext/Nullable); the reader keeps those by design.
    private static string? ProbeTag(IEnumerable<DecodedCustomAttribute> attributes) =>
        attributes.Single(attribute => attribute.AttributeTypeName == "Mod.ProbeMarkerAttribute")
            .FixedArguments.Single().Value as string;

    private static string? Tag(DecodedCustomAttribute attribute) =>
        attribute.FixedArguments.Single().Value as string;

    private static TypeDefinitionHandle FindType(MetadataReader reader, string fullName)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            if (string.Equals(reader.GetString(definition.Namespace) + "." + reader.GetString(definition.Name), fullName, StringComparison.Ordinal))
                return handle;
        }

        throw new InvalidOperationException($"Type {fullName} not found.");
    }

    private static FieldDefinitionHandle FindField(MetadataReader reader, TypeDefinitionHandle type, string name)
    {
        foreach (var handle in reader.GetTypeDefinition(type).GetFields())
        {
            if (string.Equals(reader.GetString(reader.GetFieldDefinition(handle).Name), name, StringComparison.Ordinal))
                return handle;
        }

        throw new InvalidOperationException($"Field {name} not found.");
    }

    private static PropertyDefinitionHandle FindProperty(MetadataReader reader, TypeDefinitionHandle type, string name)
    {
        foreach (var handle in reader.GetTypeDefinition(type).GetProperties())
        {
            if (string.Equals(reader.GetString(reader.GetPropertyDefinition(handle).Name), name, StringComparison.Ordinal))
                return handle;
        }

        throw new InvalidOperationException($"Property {name} not found.");
    }

    private static EventDefinitionHandle FindEvent(MetadataReader reader, TypeDefinitionHandle type, string name)
    {
        foreach (var handle in reader.GetTypeDefinition(type).GetEvents())
        {
            if (string.Equals(reader.GetString(reader.GetEventDefinition(handle).Name), name, StringComparison.Ordinal))
                return handle;
        }

        throw new InvalidOperationException($"Event {name} not found.");
    }

    private static MethodDefinitionHandle FindMethod(MetadataReader reader, TypeDefinitionHandle type, string name)
    {
        foreach (var handle in reader.GetTypeDefinition(type).GetMethods())
        {
            if (string.Equals(reader.GetString(reader.GetMethodDefinition(handle).Name), name, StringComparison.Ordinal))
                return handle;
        }

        throw new InvalidOperationException($"Method {name} not found.");
    }

    private static FixtureReader OpenFixture()
    {
        var stream = new FileStream(FixturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = new PEReader(stream);
        return new FixtureReader(stream, reader, reader.GetMetadataReader());
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
