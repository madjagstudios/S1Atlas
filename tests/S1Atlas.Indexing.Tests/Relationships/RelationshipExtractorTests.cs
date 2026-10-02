using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Relationships;
using Xunit;

namespace S1Atlas.Indexing.Tests.Relationships;

public sealed class RelationshipExtractorTests
{
    [Fact]
    public void Extracts_structural_and_recovered_il_edges_without_guessing_targets()
    {
        var input = new ManagedDecompilation(
            "fixture.dll",
            "class Derived {}",
            [new ManagedTypeFacts(
                "Demo.Derived", "Demo", "Derived", "Demo.Base", ["Demo.IContract"],
                [new ManagedMemberFacts(
                    "Run", ManagedMemberKind.Method, "Run(0)", true,
                    [new ManagedReferenceFact(ManagedReferenceKind.Calls, "Demo.Service::Do()")],
                    ["Demo.Argument"],
                    "Demo.Result"),
                 new ManagedMemberFacts("Field", ManagedMemberKind.Field, "Demo.Value Field", false, [], ValueType: "Demo.Value"),
                 new ManagedMemberFacts("Property", ManagedMemberKind.Property, "Demo.Value Property", false, [], ValueType: "Demo.Value"),
                 new ManagedMemberFacts("Event", ManagedMemberKind.Event, "Demo.Value Event", false, [], ValueType: "Demo.Value")]),
                new ManagedTypeFacts("Demo.Base", "Demo", "Base", null, [], []),
                new ManagedTypeFacts("Demo.IContract", "Demo", "IContract", null, [], []),
                new ManagedTypeFacts("Demo.Argument", "Demo", "Argument", null, [], []),
                new ManagedTypeFacts("Demo.Result", "Demo", "Result", null, [], []),
                new ManagedTypeFacts("Demo.Value", "Demo", "Value", null, [], []),
                new ManagedTypeFacts(
                    "Demo.Service", "Demo", "Service", null, [],
                    [new ManagedMemberFacts("Do", ManagedMemberKind.Method, "Do()", true, [])])]);

        var result = new RelationshipExtractor().Extract(input, CodebaseKind.S1Api, CodeChannel.Release);

        Assert.Contains(result, relationship => relationship.Kind == RelationshipKind.Inherits && relationship.TargetText == "Demo.Base" && relationship.TargetKey is not null && relationship.Evidence == RelationshipEvidence.Metadata);
        Assert.Contains(result, relationship => relationship.Kind == RelationshipKind.ImplementsInterface && relationship.TargetText == "Demo.IContract" && relationship.TargetKey is not null);
        Assert.Contains(result, relationship => relationship.Kind == RelationshipKind.FieldType && relationship.TargetText == "Demo.Value" && relationship.TargetKey is not null);
        Assert.Contains(result, relationship => relationship.Kind == RelationshipKind.PropertyType && relationship.TargetText == "Demo.Value" && relationship.TargetKey is not null);
        Assert.Contains(result, relationship => relationship.Kind == RelationshipKind.EventType && relationship.TargetText == "Demo.Value" && relationship.TargetKey is not null);
        Assert.Contains(result, relationship => relationship.Kind == RelationshipKind.ParameterType && relationship.TargetText == "Demo.Argument" && relationship.TargetKey is not null);
        Assert.Contains(result, relationship => relationship.Kind == RelationshipKind.ReturnType && relationship.TargetText == "Demo.Result" && relationship.TargetKey is not null);
        var call = Assert.Single(result, relationship => relationship.Kind == RelationshipKind.Calls);
        Assert.NotNull(call.TargetKey);
        Assert.Equal("Demo.Service::Do()", call.TargetText);
        Assert.Equal(RelationshipEvidence.RecoveredIL, call.Evidence);
    }

    [Fact]
    public void References_method_maps_to_references_method()
    {
        var input = new ManagedDecompilation(
            "fixture.dll",
            "",
            [new ManagedTypeFacts(
                "Demo.Owner", "Demo", "Owner", null, [],
                [new ManagedMemberFacts(
                    "Build", ManagedMemberKind.Method, "Build()", true,
                    [new ManagedReferenceFact(ManagedReferenceKind.ReferencesMethod, "Demo.Target::Run()")])]),
            new ManagedTypeFacts(
                "Demo.Target", "Demo", "Target", null, [],
                [new ManagedMemberFacts("Run", ManagedMemberKind.Method, "Run()", true, [])])]);

        var result = new RelationshipExtractor().Extract(input, CodebaseKind.S1Api, CodeChannel.Release);

        var reference = Assert.Single(result, relationship => relationship.Kind == RelationshipKind.ReferencesMethod);
        Assert.NotNull(reference.TargetKey);
        Assert.Equal("Demo.Target::Run()", reference.TargetText);
        Assert.Equal(RelationshipEvidence.RecoveredIL, reference.Evidence);
    }

    [Fact]
    public void Takes_field_address_maps_and_preserves_evidence()
    {
        var input = new ManagedDecompilation(
            "fixture.dll",
            "",
            [new ManagedTypeFacts(
                "Demo.Owner", "Demo", "Owner", null, [],
                [new ManagedMemberFacts(
                    "Bump", ManagedMemberKind.Method, "Bump()", true,
                    [new ManagedReferenceFact(
                        ManagedReferenceKind.TakesFieldAddress,
                        "Demo.Widget::Count",
                        RelationshipEvidence.Metadata)])])]);

        var result = new RelationshipExtractor().Extract(input, CodebaseKind.S1Api, CodeChannel.Release);

        var address = Assert.Single(result, relationship => relationship.Kind == RelationshipKind.TakesFieldAddress);
        Assert.Equal("Demo.Widget::Count", address.TargetText);
        Assert.Equal(RelationshipEvidence.Metadata, address.Evidence);
    }
}
