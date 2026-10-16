using S1Atlas.Core.Metadata;
using Xunit;

namespace S1Atlas.Core.Tests.Metadata;

public sealed class CustomAttributeJsonTests
{
    [Fact]
    public void Round_trip_preserves_every_value_kind()
    {
        var attribute = new DecodedCustomAttribute(
            "Mod.FixtureCaseAttribute",
            [
                new(CustomAttributeValueKind.String, "decoder", "System.String", null),
                new(CustomAttributeValueKind.Primitive, 3, "System.Int32", null),
                new(CustomAttributeValueKind.Primitive, true, "System.Boolean", null),
                new(CustomAttributeValueKind.Primitive, 1.5, "System.Double", null),
                new(CustomAttributeValueKind.Primitive, 'x', "System.Char", null),
                new(CustomAttributeValueKind.Primitive, (byte)255, "System.Byte", null),
                new(CustomAttributeValueKind.Primitive, (sbyte)-8, "System.SByte", null),
                new(CustomAttributeValueKind.Primitive, (short)-12, "System.Int16", null),
                new(CustomAttributeValueKind.Primitive, 4000000000U, "System.UInt32", null),
                new(CustomAttributeValueKind.Primitive, 3000000000L, "System.Int64", null),
                new(CustomAttributeValueKind.Primitive, 2.5f, "System.Single", null),
                new(CustomAttributeValueKind.Null, null, null, null),
                new(CustomAttributeValueKind.Type, null, "Game.Widget", null),
                new(CustomAttributeValueKind.Enum, 1, "Mod.SampleKind", null),
                new(CustomAttributeValueKind.Enum, 3000000000L, "Mod.BigKind", null),
                new(
                    CustomAttributeValueKind.Array,
                    null,
                    "System.Type[]",
                    [
                        new(CustomAttributeValueKind.Type, null, "System.Int32", null),
                        new(CustomAttributeValueKind.Type, null, "System.String", null),
                    ]),
                new(CustomAttributeValueKind.Array, null, "System.Int32[]", []),
            ],
            [
                new("Count", new(CustomAttributeValueKind.Primitive, 3, "System.Int32", null)),
                new("Label", new(CustomAttributeValueKind.String, "hi", "System.String", null)),
                new("Kind", new(CustomAttributeValueKind.Enum, 1, "Mod.SampleKind", null)),
                new("Target", new(CustomAttributeValueKind.Type, null, "Game.Widget", null)),
                new("Missing", new(CustomAttributeValueKind.Null, null, null, null)),
                new(
                    "Flags",
                    new(
                        CustomAttributeValueKind.Array,
                        null,
                        "System.Boolean[]",
                        [
                            new(CustomAttributeValueKind.Primitive, true, "System.Boolean", null),
                            new(CustomAttributeValueKind.Primitive, false, "System.Boolean", null),
                        ])),
            ]);

        var restored = CustomAttributeJson.FromJson(CustomAttributeJson.ToJson(attribute));

        // Record equality is reference equality on the argument lists, so compare structurally.
        AssertStructurallyEqual(attribute, restored);
        Assert.Equal(CustomAttributeJson.ToJson(attribute), CustomAttributeJson.ToJson(restored));
    }

    [Fact]
    public void Round_trip_preserves_null_type_name_and_empty_arguments()
    {
        var attribute = new DecodedCustomAttribute(null, [], []);

        var restored = CustomAttributeJson.FromJson(CustomAttributeJson.ToJson(attribute));

        AssertStructurallyEqual(attribute, restored);
        Assert.Equal("""{"type":null,"fixed":[],"named":[]}""", CustomAttributeJson.ToJson(attribute));
    }

    [Fact]
    public void Round_trip_preserves_nonfinite_singles()
    {
        var attribute = new DecodedCustomAttribute(
            "Mod.Probe",
            [
                new(CustomAttributeValueKind.Primitive, float.NaN, "System.Single", null),
                new(CustomAttributeValueKind.Primitive, float.PositiveInfinity, "System.Single", null),
                new(CustomAttributeValueKind.Primitive, float.NegativeInfinity, "System.Single", null),
            ],
            []);

        var json = CustomAttributeJson.ToJson(attribute);

        Assert.Equal(
            """{"type":"Mod.Probe","fixed":[{"kind":"Primitive","type":"System.Single","value":"NaN"},{"kind":"Primitive","type":"System.Single","value":"Infinity"},{"kind":"Primitive","type":"System.Single","value":"-Infinity"}],"named":[]}""",
            json);
        var restored = CustomAttributeJson.FromJson(json);
        Assert.Equal(3, restored.FixedArguments.Count);
        Assert.True(restored.FixedArguments[0].Value is float nan && float.IsNaN(nan));
        Assert.True(restored.FixedArguments[1].Value is float positive && float.IsPositiveInfinity(positive));
        Assert.True(restored.FixedArguments[2].Value is float negative && float.IsNegativeInfinity(negative));
        Assert.Equal(json, CustomAttributeJson.ToJson(restored));
    }

    [Fact]
    public void Round_trip_preserves_nonfinite_doubles()
    {
        var attribute = new DecodedCustomAttribute(
            "Mod.Probe",
            [
                new(CustomAttributeValueKind.Primitive, double.NaN, "System.Double", null),
                new(CustomAttributeValueKind.Primitive, double.PositiveInfinity, "System.Double", null),
                new(CustomAttributeValueKind.Primitive, double.NegativeInfinity, "System.Double", null),
            ],
            []);

        var json = CustomAttributeJson.ToJson(attribute);

        Assert.Equal(
            """{"type":"Mod.Probe","fixed":[{"kind":"Primitive","type":"System.Double","value":"NaN"},{"kind":"Primitive","type":"System.Double","value":"Infinity"},{"kind":"Primitive","type":"System.Double","value":"-Infinity"}],"named":[]}""",
            json);
        var restored = CustomAttributeJson.FromJson(json);
        Assert.Equal(3, restored.FixedArguments.Count);
        Assert.True(restored.FixedArguments[0].Value is double nan && double.IsNaN(nan));
        Assert.True(restored.FixedArguments[1].Value is double positive && double.IsPositiveInfinity(positive));
        Assert.True(restored.FixedArguments[2].Value is double negative && double.IsNegativeInfinity(negative));
        Assert.Equal(json, CustomAttributeJson.ToJson(restored));
    }

    [Fact]
    public void Reads_plus_infinity_alias_for_singles_and_doubles()
    {
        var single = CustomAttributeJson.FromJson(
            """{"type":"Mod.Probe","fixed":[{"kind":"Primitive","type":"System.Single","value":"+Infinity"}],"named":[]}""");
        var floating = CustomAttributeJson.FromJson(
            """{"type":"Mod.Probe","fixed":[{"kind":"Primitive","type":"System.Double","value":"+Infinity"}],"named":[]}""");

        Assert.True(single.FixedArguments[0].Value is float positiveSingle && float.IsPositiveInfinity(positiveSingle));
        Assert.True(floating.FixedArguments[0].Value is double positiveDouble && double.IsPositiveInfinity(positiveDouble));
    }

    [Fact]
    public void Round_trip_preserves_every_char_code_unit_including_surrogates()
    {
        var attribute = new DecodedCustomAttribute(
            "Mod.Probe",
            [
                new(CustomAttributeValueKind.Primitive, 'x', "System.Char", null),
                new(CustomAttributeValueKind.Primitive, '\0', "System.Char", null),
                new(CustomAttributeValueKind.Primitive, '\uFFFD', "System.Char", null),
                new(CustomAttributeValueKind.Primitive, '\uD800', "System.Char", null),
                new(CustomAttributeValueKind.Primitive, '\uDBFF', "System.Char", null),
                new(CustomAttributeValueKind.Primitive, '\uDC00', "System.Char", null),
                new(CustomAttributeValueKind.Primitive, '\uDFFF', "System.Char", null),
            ],
            []);

        var json = CustomAttributeJson.ToJson(attribute);
        var restored = CustomAttributeJson.FromJson(json);

        AssertStructurallyEqual(attribute, restored);
        Assert.Equal(json, CustomAttributeJson.ToJson(restored));
    }

    [Fact]
    public void Reads_legacy_single_character_string_for_char()
    {
        var restored = CustomAttributeJson.FromJson(
            """{"type":"Mod.Probe","fixed":[{"kind":"Primitive","type":"System.Char","value":"x"}],"named":[]}""");

        Assert.True(restored.FixedArguments[0].Value is char c && c == 'x');
    }

    [Fact]
    public void Writes_stable_key_ordering()
    {
        var attribute = new DecodedCustomAttribute(
            "Mod.Probe",
            [new(CustomAttributeValueKind.String, "hi", "System.String", null)],
            [new("Count", new(CustomAttributeValueKind.Primitive, 3, "System.Int32", null))]);

        Assert.Equal(
            """{"type":"Mod.Probe","fixed":[{"kind":"String","type":"System.String","value":"hi"}],"named":[{"name":"Count","kind":"Primitive","type":"System.Int32","value":3}]}""",
            CustomAttributeJson.ToJson(attribute));
    }

    [Fact]
    public void Writes_nested_arrays_recursively()
    {
        var attribute = new DecodedCustomAttribute(
            "Mod.Probe",
            [
                new(
                    CustomAttributeValueKind.Array,
                    null,
                    "System.Object[][]",
                    [
                        new(
                            CustomAttributeValueKind.Array,
                            null,
                            "System.Object[]",
                            [new(CustomAttributeValueKind.Primitive, 1, "System.Int32", null)]),
                    ]),
            ],
            []);

        var restored = CustomAttributeJson.FromJson(CustomAttributeJson.ToJson(attribute));

        AssertStructurallyEqual(attribute, restored);
    }

    private static void AssertStructurallyEqual(DecodedCustomAttribute expected, DecodedCustomAttribute actual)
    {
        Assert.Equal(expected.AttributeTypeName, actual.AttributeTypeName);
        Assert.Equal(expected.FixedArguments.Count, actual.FixedArguments.Count);
        Assert.Equal(expected.NamedArguments.Count, actual.NamedArguments.Count);
        foreach (var (first, second) in expected.FixedArguments.Zip(actual.FixedArguments))
            AssertValuesEqual(first, second);
        foreach (var (first, second) in expected.NamedArguments.Zip(actual.NamedArguments))
        {
            Assert.Equal(first.Name, second.Name);
            AssertValuesEqual(first.Value, second.Value);
        }
    }

    private static void AssertValuesEqual(CustomAttributeValue expected, CustomAttributeValue actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.TypeName, actual.TypeName);
        // Boxed values compare by exact runtime type: int 1 differs from long 1.
        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(expected.Elements is null, actual.Elements is null);
        if (expected.Elements is not null && actual.Elements is not null)
        {
            Assert.Equal(expected.Elements.Count, actual.Elements.Count);
            foreach (var (first, second) in expected.Elements.Zip(actual.Elements))
                AssertValuesEqual(first, second);
        }
    }
}
