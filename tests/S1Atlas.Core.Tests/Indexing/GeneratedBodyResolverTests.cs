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

    [Fact]
    public void ResolveAll_OverloadedStateMachines_MapViaAttributes()
    {
        var first = Method(
            "Foo", "System.Threading.Tasks.Task`1<System.Int32>", ["System.Int32"], null,
            "A.B.C+<Foo>d__1", true);
        var second = Method(
            "Foo", "System.Threading.Tasks.Task`1<System.String>", ["System.String"], null,
            "A.B.C+<Foo>d__2", true);
        var firstMove = Method("MoveNext", "System.Void");
        var secondMove = Method("MoveNext", "System.Void");
        var decompilation = new ManagedDecompilation(
            "test.dll",
            string.Empty,
            [
                Type("A.B.C", first, second),
                Type("A.B.C+<Foo>d__1", firstMove),
                Type("A.B.C+<Foo>d__2", secondMove)
            ]);

        var mappings = GeneratedBodyResolver.ResolveAll(decompilation, CodebaseKind.ScheduleI, CodeChannel.Installed);

        var firstMapping = mappings[Key("A.B.C+<Foo>d__1", firstMove, SymbolKind.Method)];
        Assert.Equal(Key("A.B.C", first, SymbolKind.Method), firstMapping.DeclaringKey);
        Assert.Equal("in async state machine", firstMapping.Detail);
        Assert.True(firstMapping.IsAttributeBased);
        var secondMapping = mappings[Key("A.B.C+<Foo>d__2", secondMove, SymbolKind.Method)];
        Assert.Equal(Key("A.B.C", second, SymbolKind.Method), secondMapping.DeclaringKey);
        Assert.True(secondMapping.IsAttributeBased);
    }

    [Fact]
    public void ResolveAll_AsyncVoidStateMachine_UsesAttributeKind()
    {
        var foo = Method("Foo", "System.Void", ["System.Int32"], null, "A.B.C+<Foo>d__0", true);
        var moveNext = Method("MoveNext", "System.Void");
        var decompilation = new ManagedDecompilation(
            "test.dll",
            string.Empty,
            [Type("A.B.C", foo), Type("A.B.C+<Foo>d__0", moveNext)]);

        var mappings = GeneratedBodyResolver.ResolveAll(decompilation, CodebaseKind.ScheduleI, CodeChannel.Installed);

        var mapping = mappings[Key("A.B.C+<Foo>d__0", moveNext, SymbolKind.Method)];
        Assert.Equal("in async state machine", mapping.Detail);
        Assert.True(mapping.IsAttributeBased);
    }

    [Fact]
    public void ResolveAll_OverloadedDisplayLambdas_MapViaNewobjConfirmation()
    {
        var first = Method(
            "Foo", "System.Int32", ["System.Int32"],
            [new ManagedReferenceFact(ManagedReferenceKind.Constructs, "A.B.C+<>c__DisplayClass0_0::.ctor():System.Void")],
            null, false);
        var second = Method(
            "Foo", "System.String", ["System.String"],
            [new ManagedReferenceFact(ManagedReferenceKind.Constructs, "A.B.C+<>c__DisplayClass1_0::.ctor():System.Void")],
            null, false);
        var firstLambda = Method("<Foo>b__0", "System.Int32", "System.Int32");
        var secondLambda = Method("<Foo>b__0", "System.String", "System.String");
        var decompilation = new ManagedDecompilation(
            "test.dll",
            string.Empty,
            [
                Type("A.B.C", first, second),
                Type("A.B.C+<>c__DisplayClass0_0", firstLambda),
                Type("A.B.C+<>c__DisplayClass1_0", secondLambda)
            ]);

        var mappings = GeneratedBodyResolver.ResolveAll(decompilation, CodebaseKind.ScheduleI, CodeChannel.Installed);

        Assert.Equal(
            Key("A.B.C", first, SymbolKind.Method),
            mappings[Key("A.B.C+<>c__DisplayClass0_0", firstLambda, SymbolKind.Method)].DeclaringKey);
        Assert.Equal(
            Key("A.B.C", second, SymbolKind.Method),
            mappings[Key("A.B.C+<>c__DisplayClass1_0", secondLambda, SymbolKind.Method)].DeclaringKey);
    }

    [Fact]
    public void ResolveAll_OverloadedCachedLambdas_StayUnmappedWithoutConfirmation()
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
    public void ResolveAll_IteratorInGenericMethod_MapsWithIteratorDetail()
    {
        var each = Method("Each", "System.Collections.Generic.IEnumerable`1<T>", "T[]");
        var moveNext = Method("MoveNext", "System.Boolean");
        var decompilation = new ManagedDecompilation(
            "test.dll",
            string.Empty,
            [Type("A.B.Box`1", each), Type("A.B.Box`1+<Each>d__0", moveNext)]);

        var mappings = GeneratedBodyResolver.ResolveAll(decompilation, CodebaseKind.ScheduleI, CodeChannel.Installed);

        var mapping = mappings[Key("A.B.Box`1+<Each>d__0", moveNext, SymbolKind.Method)];
        Assert.Equal(Key("A.B.Box`1", each, SymbolKind.Method), mapping.DeclaringKey);
        Assert.Equal("in iterator state machine", mapping.Detail);
    }

    [Fact]
    public void MapSymbols_AttributeOnlyOverloads_StayUnmapped()
    {
        var first = new IndexSymbolRecord(
            "id-first", "snap", "key-first", "Method",
            "A.B.C::Foo(System.Int32):System.Threading.Tasks.Task`1<System.Int32>", "sig", false);
        var second = new IndexSymbolRecord(
            "id-second", "snap", "key-second", "Method",
            "A.B.C::Foo(System.String):System.Threading.Tasks.Task`1<System.String>", "sig", false);
        var moveNext = new IndexSymbolRecord(
            "id-move", "snap", "key-move", "Method",
            "A.B.C+<Foo>d__1::MoveNext():System.Void", "sig", false);

        var mappings = GeneratedBodyResolver.MapSymbols(
            [first, second, moveNext], CodebaseKind.ScheduleI, CodeChannel.Installed);

        var mapping = mappings["key-move"];
        Assert.Null(mapping.DeclaringKey);
        Assert.Equal("unmapped: ambiguous overloads sharing 'Foo'", mapping.Detail);
    }

    private static ManagedMemberFacts Method(string name, string returnType, params string[] parameterTypes) =>
        Method(name, returnType, parameterTypes, null, null, false);

    private static ManagedMemberFacts Method(
        string name,
        string returnType,
        string[]? parameterTypes,
        IReadOnlyList<ManagedReferenceFact>? references,
        string? stateMachineTypeName,
        bool isAsyncStateMachine) =>
        new(
            name,
            ManagedMemberKind.Method,
            "sig",
            true,
            references ?? [],
            parameterTypes,
            returnType,
            StateMachineTypeName: stateMachineTypeName,
            IsAsyncStateMachine: isAsyncStateMachine);

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
