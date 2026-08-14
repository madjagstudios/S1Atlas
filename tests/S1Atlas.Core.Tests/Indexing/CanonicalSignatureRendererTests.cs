using S1Atlas.Core.Indexing;
using Xunit;

namespace S1Atlas.Core.Tests.Indexing;

public sealed class CanonicalSignatureRendererTests
{
    [Fact]
    public void Primitive_aliases_and_framework_names_render_identically()
    {
        Assert.Equal(
            CanonicalSignatureRenderer.RenderType("int"),
            CanonicalSignatureRenderer.RenderType("System.Int32"));
    }

    [Fact]
    public void Overloads_generic_arity_and_ref_modifiers_are_significant()
    {
        var first = CanonicalSignatureRenderer.RenderMethod(
            "Demo.Widget", "Run", "void", ["int"], genericArity: 1);
        var second = CanonicalSignatureRenderer.RenderMethod(
            "Demo.Widget", "Run", "void", ["ref int"], genericArity: 1);

        Assert.NotEqual(first, second);
        Assert.Contains("`1", first);
    }

    [Fact]
    public void Complex_type_shapes_are_normalized()
    {
        Assert.Equal(
            "System.Nullable<System.Int32>[]*&",
            CanonicalSignatureRenderer.RenderType("ref int?[]*"));
        Assert.Equal(
            "System.ValueTuple<System.Int32,System.String>",
            CanonicalSignatureRenderer.RenderType("(int, string)"));
    }

    [Fact]
    public void Symbol_identity_rejects_unsupported_codebase_channels()
    {
        Assert.Throws<ArgumentException>(() =>
            SymbolIdentity.Create(CodebaseKind.ScheduleI, CodeChannel.Preview, SymbolKind.Type, "Demo.Widget"));
    }
}
