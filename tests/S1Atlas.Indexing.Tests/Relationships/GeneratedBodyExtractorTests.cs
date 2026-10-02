using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Relationships;
using Xunit;

namespace S1Atlas.Indexing.Tests.Relationships;

public sealed class GeneratedBodyExtractorTests
{
    [Fact]
    public void CreditsLambdaLocalAndStateMachineSourcesToDeclaringMethods()
    {
        var help = new ManagedMemberFacts("Help", ManagedMemberKind.Method, "Help", true, [], [], "System.Int32");
        var leafTarget = ManagedMemberIdentity.Render("Demo.Leaf", help);
        var foo = new ManagedMemberFacts("Foo", ManagedMemberKind.Method, "Foo", true, [], ["System.Int32"], "System.Int32");
        var lambda = new ManagedMemberFacts(
            "<Foo>b__0_0", ManagedMemberKind.Method, "lambda", true,
            [new ManagedReferenceFact(ManagedReferenceKind.Calls, leafTarget)], ["System.Int32"], "System.Int32");
        var local = new ManagedMemberFacts(
            "<Foo>g__Local|1_0", ManagedMemberKind.Method, "local", true,
            [new ManagedReferenceFact(ManagedReferenceKind.Calls, leafTarget)], ["System.Int32"], "System.Int32");
        var async = new ManagedMemberFacts("Async", ManagedMemberKind.Method, "Async", true, [], ["System.Int32"], "System.Threading.Tasks.Task`1<System.Int32>");
        var moveNext = new ManagedMemberFacts(
            "MoveNext", ManagedMemberKind.Method, "MoveNext", true,
            [new ManagedReferenceFact(ManagedReferenceKind.Calls, leafTarget)], [], "System.Void");
        var input = new ManagedDecompilation(
            "fixture.dll",
            string.Empty,
            [
                new ManagedTypeFacts("Demo.Work", "Demo", "Work", "Demo.Base", [], [foo, local, async]),
                new ManagedTypeFacts("Demo.Work+<>c", "", "<>c", null, [], [lambda]),
                new ManagedTypeFacts("Demo.Work+<Async>d__1", "", "<Async>d__1", null, [], [moveNext]),
                new ManagedTypeFacts("Demo.Base", "Demo", "Base", null, [], []),
                new ManagedTypeFacts("Demo.Leaf", "Demo", "Leaf", null, [], [help])
            ]);

        var result = new RelationshipExtractor().Extract(input, CodebaseKind.ScheduleI, CodeChannel.Installed);

        var fooKey = Key("Demo.Work", foo, SymbolKind.Method);
        var lambdaKey = Key("Demo.Work+<>c", lambda, SymbolKind.Method);
        var localKey = Key("Demo.Work", local, SymbolKind.Method);
        var asyncKey = Key("Demo.Work", async, SymbolKind.Method);
        var moveNextKey = Key("Demo.Work+<Async>d__1", moveNext, SymbolKind.Method);

        var lambdaCall = Assert.Single(result, fact => fact.GeneratedSourceKey == lambdaKey);
        Assert.Equal(fooKey, lambdaCall.SourceKey);
        Assert.Equal("in lambda", lambdaCall.GeneratedDetail);
        Assert.Equal(RelationshipKind.Calls, lambdaCall.Kind);

        var localCall = Assert.Single(result, fact => fact.GeneratedSourceKey == localKey);
        Assert.Equal(fooKey, localCall.SourceKey);
        Assert.Equal("in local function Local", localCall.GeneratedDetail);

        var asyncCall = Assert.Single(result, fact => fact.GeneratedSourceKey == moveNextKey);
        Assert.Equal(asyncKey, asyncCall.SourceKey);
        Assert.Equal("in async state machine", asyncCall.GeneratedDetail);

        var inherits = Assert.Single(result, fact => fact.Kind == RelationshipKind.Inherits);
        Assert.Null(inherits.GeneratedSourceKey);
        Assert.Null(inherits.GeneratedDetail);
    }

    [Fact]
    public void KeepsUnmappedSourcesRawWithReason()
    {
        var help = new ManagedMemberFacts("Help", ManagedMemberKind.Method, "Help", true, [], [], "System.Int32");
        var leafTarget = ManagedMemberIdentity.Render("Demo.Leaf", help);
        var first = new ManagedMemberFacts("Foo", ManagedMemberKind.Method, "Foo", true, [], ["System.Int32"], "System.Int32");
        var second = new ManagedMemberFacts("Foo", ManagedMemberKind.Method, "Foo", true, [], ["System.String"], "System.String");
        var lambda = new ManagedMemberFacts(
            "<Foo>b__0_0", ManagedMemberKind.Method, "lambda", true,
            [new ManagedReferenceFact(ManagedReferenceKind.Calls, leafTarget)], ["System.Int32"], "System.Int32");
        var input = new ManagedDecompilation(
            "fixture.dll",
            string.Empty,
            [
                new ManagedTypeFacts("Demo.Multi", "Demo", "Multi", null, [], [first, second]),
                new ManagedTypeFacts("Demo.Multi+<>c", "", "<>c", null, [], [lambda]),
                new ManagedTypeFacts("Demo.Leaf", "Demo", "Leaf", null, [], [help])
            ]);

        var result = new RelationshipExtractor().Extract(input, CodebaseKind.ScheduleI, CodeChannel.Installed);

        var lambdaKey = Key("Demo.Multi+<>c", lambda, SymbolKind.Method);
        var call = Assert.Single(result, fact => fact.Kind == RelationshipKind.Calls);
        Assert.Equal(lambdaKey, call.SourceKey);
        Assert.Null(call.GeneratedSourceKey);
        Assert.Equal("unmapped: ambiguous overloads sharing 'Foo'", call.GeneratedDetail);
    }

    private static string Key(string typeFullName, ManagedMemberFacts member, SymbolKind kind) =>
        SymbolIdentity.Create(
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            kind,
            ManagedMemberIdentity.Render(typeFullName, member)).CanonicalKey;
}
