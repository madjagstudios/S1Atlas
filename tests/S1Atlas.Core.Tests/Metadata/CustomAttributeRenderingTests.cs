using S1Atlas.Core.Metadata;
using Xunit;

namespace S1Atlas.Core.Tests.Metadata;

public sealed class CustomAttributeRenderingTests
{
    [Fact]
    public void Renders_null_as_null()
    {
        var value = new CustomAttributeValue(CustomAttributeValueKind.Null, null, null, null);

        Assert.Equal("null", CustomAttributeRendering.CanonicalValue(value));
    }

    [Fact]
    public void Renders_string_as_the_value()
    {
        var value = new CustomAttributeValue(CustomAttributeValueKind.String, "hello", "System.String", null);

        Assert.Equal("hello", CustomAttributeRendering.CanonicalValue(value));
    }

    [Theory]
    [InlineData(42, "System.Int32", "42")]
    [InlineData(-7, "System.Int32", "-7")]
    [InlineData(3000000000L, "System.Int64", "3000000000")]
    [InlineData((byte)255, "System.Byte", "255")]
    [InlineData(1.5, "System.Double", "1.5")]
    [InlineData('x', "System.Char", "x")]
    public void Renders_primitives_with_invariant_culture(object value, string typeName, string expected)
    {
        var attribute = new CustomAttributeValue(CustomAttributeValueKind.Primitive, value, typeName, null);

        Assert.Equal(expected, CustomAttributeRendering.CanonicalValue(attribute));
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void Renders_booleans_lowercased(bool value, string expected)
    {
        var attribute = new CustomAttributeValue(CustomAttributeValueKind.Primitive, value, "System.Boolean", null);

        Assert.Equal(expected, CustomAttributeRendering.CanonicalValue(attribute));
    }

    [Fact]
    public void Renders_type_as_the_type_name()
    {
        var value = new CustomAttributeValue(CustomAttributeValueKind.Type, null, "Game.Widget", null);

        Assert.Equal("Game.Widget", CustomAttributeRendering.CanonicalValue(value));
    }

    [Fact]
    public void Renders_enum_as_the_underlying_numeric_value()
    {
        var value = new CustomAttributeValue(CustomAttributeValueKind.Enum, 1, "Mod.SampleKind", null);

        Assert.Equal("1", CustomAttributeRendering.CanonicalValue(value));
    }

    [Fact]
    public void Renders_array_as_comma_joined_elements()
    {
        var value = new CustomAttributeValue(
            CustomAttributeValueKind.Array,
            null,
            "System.Object[]",
            [
                new(CustomAttributeValueKind.Primitive, 1, "System.Int32", null),
                new(CustomAttributeValueKind.String, "a", "System.String", null),
                new(CustomAttributeValueKind.Null, null, null, null),
            ]);

        Assert.Equal("1,a,null", CustomAttributeRendering.CanonicalValue(value));
    }

    [Fact]
    public void Renders_nested_arrays_recursively()
    {
        var value = new CustomAttributeValue(
            CustomAttributeValueKind.Array,
            null,
            "System.Object[][]",
            [
                new(
                    CustomAttributeValueKind.Array,
                    null,
                    "System.Object[]",
                    [
                        new(CustomAttributeValueKind.Primitive, 1, "System.Int32", null),
                        new(CustomAttributeValueKind.Primitive, 2, "System.Int32", null),
                    ]),
                new(CustomAttributeValueKind.Type, null, "Game.Widget", null),
            ]);

        Assert.Equal("1,2,Game.Widget", CustomAttributeRendering.CanonicalValue(value));
    }

    [Fact]
    public void Renders_empty_array_as_empty_string()
    {
        var value = new CustomAttributeValue(CustomAttributeValueKind.Array, null, "System.Int32[]", []);

        Assert.Equal(string.Empty, CustomAttributeRendering.CanonicalValue(value));
    }
}
