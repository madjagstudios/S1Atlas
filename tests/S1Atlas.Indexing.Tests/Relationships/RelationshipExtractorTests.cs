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
}
