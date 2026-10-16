using S1Atlas.Core.Indexing;
using S1Atlas.Core.Metadata;
using S1Atlas.Indexing.Decompilation;
using Xunit;

namespace S1Atlas.Indexing.Tests.Decompilation;

public sealed class IlSpyAttributeFactsTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "harmony-fixture", "S1Atlas.HarmonyModFixture.dll");

    [Fact]
    public async Task Decompiled_facts_carry_attributes_on_all_six_target_kinds()
    {
        var result = await new IlSpyManagedDecompiler().DecompileAsync(FixturePath, CancellationToken.None);

        var type = Assert.Single(result.Types, candidate => candidate.FullName == "Mod.AttributeTargetsProbe");
        Assert.Equal("type", ProbeTag(type.Attributes ?? []));
        Assert.Equal("field", ProbeTag(FindMember(type, "MarkedField", ManagedMemberKind.Field).AttributesOrEmpty));
        Assert.Equal("property", ProbeTag(FindMember(type, "MarkedProperty", ManagedMemberKind.Property).AttributesOrEmpty));
        Assert.Equal("event", ProbeTag(FindMember(type, "MarkedEvent", ManagedMemberKind.Event).AttributesOrEmpty));
        var method = FindMember(type, "MarkedMethod", ManagedMemberKind.Method);
        Assert.Equal("method", ProbeTag(method.AttributesOrEmpty));
        var parameter = Assert.Single(method.ParameterAttributesOrEmpty);
        Assert.Equal(0, parameter.Index);
        Assert.Equal("markedParameter", parameter.Name);
        Assert.Equal("parameter", ProbeTag(parameter.Attributes));
    }

    [Fact]
    public async Task Decompiled_facts_exclude_injected_analysis_attributes()
    {
        var result = await new IlSpyManagedDecompiler().DecompileAsync(FixturePath, CancellationToken.None);

        var type = Assert.Single(result.Types, candidate => candidate.FullName == "Mod.AttributeTargetsProbe");
        var filtered = FindMember(type, "FilteredMethod", ManagedMemberKind.Method);
        var kept = Assert.Single(
            filtered.AttributesOrEmpty,
            attribute => attribute.AttributeTypeName == "Mod.ProbeMarkerAttribute");
        Assert.Equal("kept", kept.FixedArguments.Single().Value);
        foreach (var candidate in result.Types)
        {
            Assert.DoesNotContain(candidate.Attributes ?? [], attribute => IsInjected(attribute.AttributeTypeName));
            foreach (var member in candidate.Members)
            {
                Assert.DoesNotContain(member.AttributesOrEmpty, attribute => IsInjected(attribute.AttributeTypeName));
                Assert.DoesNotContain(
                    member.ParameterAttributesOrEmpty.SelectMany(parameter => parameter.Attributes),
                    attribute => IsInjected(attribute.AttributeTypeName));
            }
        }
    }

    [Fact]
    public async Task Members_without_attributes_leave_fact_fields_null()
    {
        var result = await new IlSpyManagedDecompiler().DecompileAsync(FixturePath, CancellationToken.None);

        var type = Assert.Single(result.Types, candidate => candidate.FullName == "Mod.AttributeTargetsProbe");
        var unmarked = FindMember(type, "UnmarkedMethod", ManagedMemberKind.Method);
        Assert.Null(unmarked.Attributes);
        Assert.Null(unmarked.ParameterAttributes);
    }

    // The marker is singled out because the compiler also emits genuine attributes
    // of its own (NullableContext/Nullable); the decompiler keeps those by design.
    private static string? ProbeTag(IEnumerable<DecodedCustomAttribute> attributes) =>
        attributes.Single(attribute => attribute.AttributeTypeName == "Mod.ProbeMarkerAttribute")
            .FixedArguments.Single().Value as string;

    private static bool IsInjected(string? attributeTypeName) =>
        attributeTypeName is not null &&
        attributeTypeName.StartsWith("Cpp2ILInjected.", StringComparison.Ordinal);

    private static ManagedMemberFacts FindMember(ManagedTypeFacts type, string name, ManagedMemberKind kind) =>
        type.Members.Single(member => member.Kind == kind && string.Equals(member.Name, name, StringComparison.Ordinal));
}
