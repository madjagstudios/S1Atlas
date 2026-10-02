using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Relationships;
using Xunit;

namespace S1Atlas.Indexing.Tests.Relationships;

public sealed class OverrideGraphResolverTests
{
    private static readonly CodebaseKind Codebase = CodebaseKind.ScheduleI;
    private static readonly CodeChannel Channel = CodeChannel.Installed;

    [Fact]
    public void Override_EmitsEdgeToImmediateBaseSlotOnly()
    {
        var facts = Resolve(
            Type("N.Base", "System.Object", members: [Method("Foo", isVirtual: true, isNewSlot: true)]),
            Type("N.Derived", "N.Base", members: [Method("Foo", isVirtual: true)]),
            Type("N.Grandchild", "N.Derived", members: [Method("Foo", isVirtual: true)]));

        Assert.Equal(2, facts.Count);
        Assert.Contains(facts, fact =>
            fact.Kind == RelationshipKind.Overrides
            && fact.Evidence == RelationshipEvidence.Metadata
            && fact.SourceKey == MemberKey("N.Grandchild::Foo():System.Int32")
            && fact.TargetText == "N.Derived::Foo():System.Int32"
            && fact.TargetKey == MemberKey("N.Derived::Foo():System.Int32"));
        Assert.Contains(facts, fact =>
            fact.Kind == RelationshipKind.Overrides
            && fact.SourceKey == MemberKey("N.Derived::Foo():System.Int32")
            && fact.TargetText == "N.Base::Foo():System.Int32"
            && fact.TargetKey == MemberKey("N.Base::Foo():System.Int32"));
    }

    [Fact]
    public void RootVirtual_EmitsNoEdge()
    {
        var facts = Resolve(
            Type("N.Base", "System.Object", members: [Method("Foo", isVirtual: true, isNewSlot: true)]));

        Assert.Empty(facts);
    }

    [Fact]
    public void NewSlotVirtual_EmitsNoEdge()
    {
        var facts = Resolve(
            Type("N.Base", "System.Object", members: [Method("Foo", isVirtual: true, isNewSlot: true)]),
            Type("N.Hider", "N.Base", members: [Method("Foo", isVirtual: true, isNewSlot: true)]));

        Assert.Empty(facts);
    }

    [Fact]
    public void NonVirtual_EmitsNoEdge()
    {
        var facts = Resolve(
            Type("N.Base", "System.Object", members: [Method("Bar")]),
            Type("N.Hider", "N.Base", members: [Method("Bar")]));

        Assert.Empty(facts);
    }

    [Fact]
    public void AbstractOverride_EmitsEdge()
    {
        var facts = Resolve(
            Type("N.Abstract", "System.Object", members: [Method("Compute", parameterTypes: ["System.Int32"], isVirtual: true, isNewSlot: true)]),
            Type("N.Concrete", "N.Abstract", members: [Method("Compute", parameterTypes: ["System.Int32"], isVirtual: true)]));

        var fact = Assert.Single(facts);
        Assert.Equal(RelationshipKind.Overrides, fact.Kind);
        Assert.Equal(RelationshipEvidence.Metadata, fact.Evidence);
        Assert.Equal(MemberKey("N.Concrete::Compute(System.Int32):System.Int32"), fact.SourceKey);
        Assert.Equal("N.Abstract::Compute(System.Int32):System.Int32", fact.TargetText);
        Assert.Equal(MemberKey("N.Abstract::Compute(System.Int32):System.Int32"), fact.TargetKey);
    }

    [Fact]
    public void ExplicitImplementation_EmitsImplementsMethod()
    {
        var facts = Resolve(
            Type("N.IFoo", members: [Method("Serve")], isInterface: true),
            Type(
                "N.Explicit",
                "System.Object",
                interfaces: ["N.IFoo"],
                members: [Method("N.IFoo.Serve", methodImplDeclarations: ["N.IFoo::Serve():System.Int32"])]));

        var fact = Assert.Single(facts);
        Assert.Equal(RelationshipKind.ImplementsMethod, fact.Kind);
        Assert.Equal(RelationshipEvidence.Metadata, fact.Evidence);
        Assert.Equal(MemberKey("N.Explicit::N.IFoo.Serve():System.Int32"), fact.SourceKey);
        Assert.Equal("N.IFoo::Serve():System.Int32", fact.TargetText);
        Assert.Equal(MemberKey("N.IFoo::Serve():System.Int32"), fact.TargetKey);
    }

    [Fact]
    public void ImplicitImplementation_EmitsEdge()
    {
        var facts = Resolve(
            Type("N.IFoo", members: [Method("Serve")], isInterface: true),
            Type("N.Implicit", "System.Object", interfaces: ["N.IFoo"], members: [Method("Serve")]));

        var fact = Assert.Single(facts);
        Assert.Equal(RelationshipKind.ImplementsMethod, fact.Kind);
        Assert.Equal(MemberKey("N.Implicit::Serve():System.Int32"), fact.SourceKey);
        Assert.Equal("N.IFoo::Serve():System.Int32", fact.TargetText);
    }

    [Fact]
    public void InheritedImplicitImplementation_EmitsEdgeFromBaseMember()
    {
        var facts = Resolve(
            Type("N.IWorker", members: [Method("Work")], isInterface: true),
            Type("N.ImplBase", "System.Object", members: [Method("Work")]),
            Type("N.ImplDerived", "N.ImplBase", interfaces: ["N.IWorker"]));

        var fact = Assert.Single(facts);
        Assert.Equal(RelationshipKind.ImplementsMethod, fact.Kind);
        Assert.Equal(MemberKey("N.ImplBase::Work():System.Int32"), fact.SourceKey);
        Assert.Equal("N.IWorker::Work():System.Int32", fact.TargetText);
        Assert.Equal(MemberKey("N.IWorker::Work():System.Int32"), fact.TargetKey);
    }

    [Fact]
    public void GenericBase_EmitsEdgeToOpenDefinition()
    {
        var facts = Resolve(
            Type("N.GenericBase`1", "System.Object", members: [Method("Describe", returnType: "System.String", isVirtual: true, isNewSlot: true)]),
            Type("N.GenericDerived", "N.GenericBase`1<System.Int32>", members: [Method("Describe", returnType: "System.String", isVirtual: true)]));

        var fact = Assert.Single(facts);
        Assert.Equal(RelationshipKind.Overrides, fact.Kind);
        Assert.Equal(MemberKey("N.GenericDerived::Describe():System.String"), fact.SourceKey);
        Assert.Equal("N.GenericBase`1::Describe():System.String", fact.TargetText);
        Assert.Equal(MemberKey("N.GenericBase`1::Describe():System.String"), fact.TargetKey);
    }

    [Fact]
    public void ExternalBase_EmitsEdgeWithTargetText()
    {
        var facts = Resolve(
            Type("N.ExternalToString", "System.Object", members: [Method("ToString", returnType: "System.String", isVirtual: true)]));

        var fact = Assert.Single(facts);
        Assert.Equal(RelationshipKind.Overrides, fact.Kind);
        Assert.Equal(MemberKey("N.ExternalToString::ToString():System.String"), fact.SourceKey);
        Assert.Equal("System.Object::ToString():System.String", fact.TargetText);
        Assert.Null(fact.TargetKey);
    }

    [Fact]
    public void InterfaceType_SkipsMatching()
    {
        var facts = Resolve(
            Type("N.IBase", members: [Method("M")], isInterface: true),
            Type("N.ISub", interfaces: ["N.IBase"], members: [Method("M")], isInterface: true));

        Assert.Empty(facts);
    }

    [Fact]
    public void MethodImplToBaseClass_EmitsOverrides()
    {
        var facts = Resolve(
            Type("N.Base", "System.Object", members: [Method("M", isVirtual: true, isNewSlot: true)]),
            Type("N.Derived", "N.Base", members: [Method("MImpl", methodImplDeclarations: ["N.Base::M():System.Int32"])]));

        var fact = Assert.Single(facts);
        Assert.Equal(RelationshipKind.Overrides, fact.Kind);
        Assert.Equal(MemberKey("N.Derived::MImpl():System.Int32"), fact.SourceKey);
        Assert.Equal("N.Base::M():System.Int32", fact.TargetText);
    }

    [Fact]
    public void PropertyAccessorOverride_EmitsEdge()
    {
        var facts = Resolve(
            Type("N.PropBase", "System.Object", members: [Method("get_Label", returnType: "System.String", isVirtual: true, isNewSlot: true)]),
            Type("N.PropDerived", "N.PropBase", members: [Method("get_Label", returnType: "System.String", isVirtual: true)]));

        var fact = Assert.Single(facts);
        Assert.Equal(RelationshipKind.Overrides, fact.Kind);
        Assert.Equal("N.PropBase::get_Label():System.String", fact.TargetText);
    }

    private static IReadOnlyList<RelationshipFact> Resolve(params ManagedTypeFacts[] types)
    {
        var knownMembers = types
            .SelectMany(type => type.Members.Select(member => ManagedMemberIdentity.Render(type.FullName, member)))
            .GroupBy(name => name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => SymbolIdentity.Create(Codebase, Channel, SymbolKind.Method, group.Key).CanonicalKey,
                StringComparer.Ordinal);
        return new OverrideGraphResolver().Resolve(types, Codebase, Channel, knownMembers);
    }

    private static string MemberKey(string memberName) =>
        SymbolIdentity.Create(Codebase, Channel, SymbolKind.Method, memberName).CanonicalKey;

    private static ManagedMemberFacts Method(
        string name,
        string returnType = "System.Int32",
        string[]? parameterTypes = null,
        bool isVirtual = false,
        bool isNewSlot = false,
        string[]? methodImplDeclarations = null) =>
        new(
            name,
            ManagedMemberKind.Method,
            $"{name}()",
            false,
            [],
            ParameterTypes: parameterTypes,
            ReturnType: returnType,
            IsVirtual: isVirtual,
            IsNewSlot: isNewSlot,
            MethodImplDeclarations: methodImplDeclarations);

    private static ManagedTypeFacts Type(
        string fullName,
        string? baseType = null,
        string[]? interfaces = null,
        ManagedMemberFacts[]? members = null,
        bool isInterface = false) =>
        new(
            fullName,
            string.Empty,
            fullName.Split('.').Last(),
            baseType,
            interfaces ?? [],
            members ?? [],
            IsInterface: isInterface);
}
