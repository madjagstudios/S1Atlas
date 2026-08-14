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
                    [new ManagedReferenceFact(ManagedReferenceKind.Calls, "Demo.Service::Do()")])])]);

        var result = new RelationshipExtractor().Extract(input, CodebaseKind.S1Api, CodeChannel.Release);

        Assert.Contains(result, relationship => relationship.Kind == RelationshipKind.Inherits && relationship.TargetText == "Demo.Base" && relationship.Evidence == RelationshipEvidence.Metadata);
        Assert.Contains(result, relationship => relationship.Kind == RelationshipKind.ImplementsInterface && relationship.TargetText == "Demo.IContract");
        var call = Assert.Single(result, relationship => relationship.Kind == RelationshipKind.Calls);
        Assert.Null(call.TargetKey);
        Assert.Equal("Demo.Service::Do()", call.TargetText);
        Assert.Equal(RelationshipEvidence.RecoveredIL, call.Evidence);
    }
}
