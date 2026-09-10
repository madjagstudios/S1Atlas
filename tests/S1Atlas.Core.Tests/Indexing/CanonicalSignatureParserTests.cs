using S1Atlas.Core.Indexing;
using Xunit;

namespace S1Atlas.Core.Tests.Indexing;

public sealed class CanonicalSignatureParserTests
{
    [Fact]
    public void ParseMethod_NoParameters_RoundTripsThroughRenderMethod()
    {
        var signature = CanonicalSignatureRenderer.RenderMethod("Foo.Bar", "DoWork", "void", []);

        var (declaringType, methodName, parameterTypes) = CanonicalSignatureParser.ParseMethod(signature);

        Assert.Equal("Foo.Bar", declaringType);
        Assert.Equal("DoWork", methodName);
        Assert.Empty(parameterTypes);
        Assert.Equal(
            signature,
            CanonicalSignatureRenderer.RenderMethod(declaringType, methodName, "void", parameterTypes));
    }

    [Fact]
    public void ParseMethod_MultipleParameters_RoundTripsThroughRenderMethod()
    {
        var signature = CanonicalSignatureRenderer.RenderMethod(
            "ScheduleOne.Economy.Customer", "EvaluateCounteroffer", "bool",
            ["ScheduleOne.Product.ProductDefinition", "int", "float"]);

        var (declaringType, methodName, parameterTypes) = CanonicalSignatureParser.ParseMethod(signature);

        Assert.Equal("ScheduleOne.Economy.Customer", declaringType);
        Assert.Equal("EvaluateCounteroffer", methodName);
        Assert.Equal(
            ["ScheduleOne.Product.ProductDefinition", "System.Int32", "System.Single"],
            parameterTypes);
        Assert.Equal(
            signature,
            CanonicalSignatureRenderer.RenderMethod(declaringType, methodName, "bool", parameterTypes));
    }

    [Fact]
    public void ParseMethod_GenericParameterType_DoesNotSplitOnTheGenericArgumentComma()
    {
        var signature = CanonicalSignatureRenderer.RenderMethod(
            "Foo.Bar", "Process", "void",
            ["System.Collections.Generic.Dictionary<System.String,System.Int32>", "bool"]);

        var (declaringType, methodName, parameterTypes) = CanonicalSignatureParser.ParseMethod(signature);

        Assert.Equal("Foo.Bar", declaringType);
        Assert.Equal("Process", methodName);
        Assert.Equal(
            ["System.Collections.Generic.Dictionary`2<System.String,System.Int32>", "System.Boolean"],
            parameterTypes);
        Assert.Equal(
            signature,
            CanonicalSignatureRenderer.RenderMethod(declaringType, methodName, "void", parameterTypes));
    }

    [Fact]
    public void ParseMethod_ArrayAndNestedType_RoundTripsThroughRenderMethod()
    {
        var signature = CanonicalSignatureRenderer.RenderMethod(
            "Foo.Bar", "Handle", "void",
            ["System.Int32[]", "Foo.Outer/Inner"]);

        var (declaringType, methodName, parameterTypes) = CanonicalSignatureParser.ParseMethod(signature);

        Assert.Equal("Foo.Bar", declaringType);
        Assert.Equal("Handle", methodName);
        Assert.Equal(["System.Int32[]", "Foo.Outer+Inner"], parameterTypes);
        Assert.Equal(
            signature,
            CanonicalSignatureRenderer.RenderMethod(declaringType, methodName, "void", parameterTypes));
    }

    [Fact]
    public void ParseMethod_GenericMethodArity_StripsTheArityMarkerFromTheName()
    {
        var signature = CanonicalSignatureRenderer.RenderMethod(
            "Foo.Bar", "Convert", "void", ["T1", "T2"], genericArity: 2);

        var (declaringType, methodName, parameterTypes) = CanonicalSignatureParser.ParseMethod(signature);

        Assert.Equal("Foo.Bar", declaringType);
        Assert.Equal("Convert", methodName);
        Assert.Equal(["T1", "T2"], parameterTypes);
    }

    [Fact]
    public void ParseMethod_MissingDeclaringTypeSeparator_Throws()
    {
        Assert.Throws<FormatException>(() => CanonicalSignatureParser.ParseMethod("NoSeparatorHere(int):void"));
    }

    [Fact]
    public void ParseMethod_MissingParameterList_Throws()
    {
        Assert.Throws<FormatException>(() => CanonicalSignatureParser.ParseMethod("Foo.Bar::Method:void"));
    }
}
