using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using Xunit;

namespace S1Atlas.Core.Tests.Indexing;

public sealed class GeneratedBodyResolverTests
{
    [Theory]
    [InlineData("A.B.C+<>c::<Foo>b__0_0(System.Int32):System.Int32", true)]
    [InlineData("A.B.C+<>c__DisplayClass1_0::<Foo>b__0(System.Int32):System.Int32", true)]
    [InlineData("A.B.C+<Foo>d__3::MoveNext():System.Void", true)]
    [InlineData("A.B.C::<Foo>g__Bar|1_0():System.Void", true)]
    [InlineData("A.B.C+<>c__DisplayClass1_0::System.Int32 offset", true)]
    [InlineData("A.B.C+<Foo>d__3::System.Int32 <>1__state", true)]
    [InlineData("mod/A.B.C+<>c::<Foo>b__0_0():System.Void", true)]
    [InlineData("<PrivateImplementationDetails>::System.Int32 hash", true)]
    [InlineData("A.B.C::Foo(System.Int32):System.Int32", false)]
    [InlineData("A.B.C::Foo():System.Collections.Generic.List`1<System.Int32>", false)]
    [InlineData("A.B.C::get_Prop():System.Int32", false)]
    [InlineData("A.B.C", false)]
    public void IsGeneratedSymbol_ClassifiesNames(string qualifiedName, bool expected)
    {
        Assert.Equal(expected, GeneratedBodyResolver.IsGeneratedSymbol(qualifiedName));
    }

    [Fact]
    public void ResolveAll_CachedLambda_MapsToDeclaringMethod()
    {
        var foo = Method("Foo", "System.Int32", "System.Int32");
        var lambda = Method("<Foo>b__0_0", "System.Int32", "System.Int32");
        var decompilation = new ManagedDecompilation(
            "test.dll",
            string.Empty,
            [Type("A.B.C", foo), Type("A.B.C+<>c", lambda)]);

        var mappings = GeneratedBodyResolver.ResolveAll(decompilation, CodebaseKind.ScheduleI, CodeChannel.Installed);

        var mapping = mappings[Key("A.B.C+<>c", lambda, SymbolKind.Method)];
        Assert.Equal(Key("A.B.C", foo, SymbolKind.Method), mapping.DeclaringKey);
        Assert.Equal("in lambda", mapping.Detail);
        Assert.False(mapping.IsAttributeBased);
    }

    [Fact]
    public void ResolveAll_DisplayClassLambda_MapsToDeclaringMethod()
    {
        var foo = Method("Foo", "System.Int32", "System.Int32");
        var lambda = Method("<Foo>b__0", "System.Int32", "System.Int32");
        var decompilation = new ManagedDecompilation(
            "test.dll",
            string.Empty,
            [Type("A.B.C", foo), Type("A.B.C+<>c__DisplayClass1_0", lambda)]);

        var mappings = GeneratedBodyResolver.ResolveAll(decompilation, CodebaseKind.ScheduleI, CodeChannel.Installed);

        var mapping = mappings[Key("A.B.C+<>c__DisplayClass1_0", lambda, SymbolKind.Method)];
        Assert.Equal(Key("A.B.C", foo, SymbolKind.Method), mapping.DeclaringKey);
        Assert.Equal("in lambda", mapping.Detail);
    }

    [Fact]
    public void ResolveAll_AsyncMoveNext_MapsWithAsyncDetail()
    {
        var foo = Method("Foo", "System.Threading.Tasks.Task`1<System.Int32>", "System.Int32");
        var moveNext = Method("MoveNext", "System.Void");
        var decompilation = new ManagedDecompilation(
            "test.dll",
            string.Empty,
            [Type("A.B.C", foo), Type("A.B.C+<Foo>d__3", moveNext)]);

        var mappings = GeneratedBodyResolver.ResolveAll(decompilation, CodebaseKind.ScheduleI, CodeChannel.Installed);

        var mapping = mappings[Key("A.B.C+<Foo>d__3", moveNext, SymbolKind.Method)];
        Assert.Equal(Key("A.B.C", foo, SymbolKind.Method), mapping.DeclaringKey);
        Assert.Equal("in async state machine", mapping.Detail);
    }

    [Fact]
    public void ResolveAll_IteratorMoveNext_MapsWithIteratorDetail()
    {
        var foo = Method("Foo", "System.Collections.Generic.IEnumerable`1<System.Int32>", "System.Int32");
        var moveNext = Method("MoveNext", "System.Boolean");
        var decompilation = new ManagedDecompilation(
            "test.dll",
            string.Empty,
            [Type("A.B.C", foo), Type("A.B.C+<Foo>d__2", moveNext)]);

        var mappings = GeneratedBodyResolver.ResolveAll(decompilation, CodebaseKind.ScheduleI, CodeChannel.Installed);

        var mapping = mappings[Key("A.B.C+<Foo>d__2", moveNext, SymbolKind.Method)];
        Assert.Equal(Key("A.B.C", foo, SymbolKind.Method), mapping.DeclaringKey);
        Assert.Equal("in iterator state machine", mapping.Detail);
    }

    [Fact]
    public void ResolveAll_LocalFunction_MapsWithLocalName()
    {
        var foo = Method("Foo", "System.Int32", "System.Int32");
        var local = Method("<Foo>g__Bar|1_0", "System.Int32", "System.Int32");
        var decompilation = new ManagedDecompilation(
            "test.dll",
            string.Empty,
            [Type("A.B.C", foo, local)]);

        var mappings = GeneratedBodyResolver.ResolveAll(decompilation, CodebaseKind.ScheduleI, CodeChannel.Installed);

        var mapping = mappings[Key("A.B.C", local, SymbolKind.Method)];
        Assert.Equal(Key("A.B.C", foo, SymbolKind.Method), mapping.DeclaringKey);
        Assert.Equal("in local function Bar", mapping.Detail);
    }

    [Fact]
    public void ResolveAll_NestedLambdaInLocalFunction_ChainsDetailsOutermostFirst()
    {
        var foo = Method("Foo", "System.Int32", "System.Int32");
        var outer = Method("<Foo>g__Outer|1_0", "System.Int32", "System.Int32");
        var lambda = Method("<Outer>b__0_0", "System.Int32", "System.Int32");
        var decompilation = new ManagedDecompilation(
            "test.dll",
            string.Empty,
            [Type("A.B.C", foo, outer), Type("A.B.C+<>c", lambda)]);

        var mappings = GeneratedBodyResolver.ResolveAll(decompilation, CodebaseKind.ScheduleI, CodeChannel.Installed);

        var mapping = mappings[Key("A.B.C+<>c", lambda, SymbolKind.Method)];
        Assert.Equal(Key("A.B.C", foo, SymbolKind.Method), mapping.DeclaringKey);
        Assert.Equal("in local function Outer, in lambda", mapping.Detail);
    }

    [Fact]
    public void ResolveAll_OverloadsSharingName_StayUnmappedWithReason()
    {
        var first = Method("Foo", "System.Int32", "System.Int32");
        var second = Method("Foo", "System.String", "System.String");
        var lambda = Method("<Foo>b__0_0", "System.Int32", "System.Int32");
        var decompilation = new ManagedDecompilation(
            "test.dll",
            string.Empty,
            [Type("A.B.C", first, second), Type("A.B.C+<>c", lambda)]);

        var mappings = GeneratedBodyResolver.ResolveAll(decompilation, CodebaseKind.ScheduleI, CodeChannel.Installed);

        var mapping = mappings[Key("A.B.C+<>c", lambda, SymbolKind.Method)];
        Assert.Null(mapping.DeclaringKey);
        Assert.Equal("unmapped: ambiguous overloads sharing 'Foo'", mapping.Detail);
    }

    [Fact]
    public void ResolveAll_MissingDeclaringMethod_StaysUnmappedWithReason()
    {
        var lambda = Method("<Foo>b__0_0", "System.Int32", "System.Int32");
        var decompilation = new ManagedDecompilation(
            "test.dll",
            string.Empty,
            [Type("A.B.C+<>c", lambda)]);

        var mappings = GeneratedBodyResolver.ResolveAll(decompilation, CodebaseKind.ScheduleI, CodeChannel.Installed);

        var mapping = mappings[Key("A.B.C+<>c", lambda, SymbolKind.Method)];
        Assert.Null(mapping.DeclaringKey);
        Assert.Equal("unmapped: declaring method 'Foo' is not indexed", mapping.Detail);
    }

    [Fact]
    public void ResolveAll_FieldsHaveNoMappingButAnchorlessMembersStayUnmapped()
    {
        var field = Field("offset", "System.Int32");
        var ctor = new ManagedMemberFacts(".cctor", ManagedMemberKind.Constructor, "sig", true, []);
        var decompilation = new ManagedDecompilation(
            "test.dll",
            string.Empty,
            [Type("A.B.C+<>c__DisplayClass1_0", field), Type("A.B.C+<>c", ctor)]);

        var mappings = GeneratedBodyResolver.ResolveAll(decompilation, CodebaseKind.ScheduleI, CodeChannel.Installed);

        Assert.DoesNotContain(Key("A.B.C+<>c__DisplayClass1_0", field, SymbolKind.Field), mappings);
        var ctorMapping = mappings[Key("A.B.C+<>c", ctor, SymbolKind.Constructor)];
        Assert.Null(ctorMapping.DeclaringKey);
        Assert.Equal("unmapped: declaring method '.cctor' is not indexed", ctorMapping.Detail);
    }

    [Fact]
    public void MapSymbols_AgreesWithResolveAllOnNamingCases()
    {
        var foo = new IndexSymbolRecord(
            "id-foo", "snap", "key-foo", "Method",
            "A.B.C::Foo(System.Int32):System.Int32", "sig", false);
        var lambda = new IndexSymbolRecord(
            "id-lambda", "snap", "key-lambda", "Method",
            "A.B.C+<>c::<Foo>b__0_0(System.Int32):System.Int32", "sig", false);
        var moveNext = new IndexSymbolRecord(
            "id-move", "snap", "key-move", "Method",
            "A.B.C+<Bar>d__0::MoveNext():System.Void", "sig", false);

        var mappings = GeneratedBodyResolver.MapSymbols(
            [foo, lambda, moveNext], CodebaseKind.ScheduleI, CodeChannel.Installed);

        Assert.Equal("key-foo", mappings["key-lambda"].DeclaringKey);
        Assert.Equal("in lambda", mappings["key-lambda"].Detail);
        var unmapped = mappings["key-move"];
        Assert.Null(unmapped.DeclaringKey);
        Assert.Equal("unmapped: declaring method 'Bar' is not indexed", unmapped.Detail);
    }

    private static ManagedMemberFacts Method(string name, string returnType, params string[] parameterTypes) =>
        new(name, ManagedMemberKind.Method, "sig", true, [], parameterTypes, returnType);

    private static ManagedMemberFacts Field(string name, string valueType) =>
        new(name, ManagedMemberKind.Field, valueType + " " + name, false, [], ValueType: valueType);

    private static ManagedTypeFacts Type(string fullName, params ManagedMemberFacts[] members)
    {
        var nesting = fullName.LastIndexOf('+');
        var outer = nesting < 0 ? fullName : fullName[..nesting];
        var lastDot = outer.LastIndexOf('.');
        var @namespace = lastDot < 0 ? string.Empty : outer[..lastDot];
        var name = nesting < 0 ? outer[(lastDot + 1)..] : fullName[(nesting + 1)..];
        return new ManagedTypeFacts(fullName, @namespace, name, null, [], members);
    }

    private static string Key(string typeFullName, ManagedMemberFacts member, SymbolKind kind) =>
        SymbolIdentity.Create(
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            kind,
            ManagedMemberIdentity.Render(typeFullName, member)).CanonicalKey;
}
