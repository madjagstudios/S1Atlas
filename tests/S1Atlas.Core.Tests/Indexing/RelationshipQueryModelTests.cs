using S1Atlas.Core.Indexing;
using Xunit;

namespace S1Atlas.Core.Tests.Indexing;

public sealed class RelationshipQueryModelTests
{
    [Fact]
    public void Relationship_target_text_match_mode_exposes_exact_and_prefix_values()
    {
        Assert.Equal(["Exact", "Prefix"], Enum.GetNames<RelationshipTargetTextMatchMode>());
    }

    [Fact]
    public void Relationship_query_page_result_rejects_invalid_counts_and_preserves_exact_rows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RelationshipQueryPageResult(-1, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RelationshipQueryPageResult(0, -1, []));
        Assert.Throws<ArgumentException>(() => new RelationshipQueryPageResult(3, 1, []));
        Assert.Throws<ArgumentException>(() => new RelationshipQueryPageResult(1, 2, [Relationship("relationship-a")]));
        Assert.Throws<ArgumentNullException>(() => new RelationshipQueryPageResult(0, 0, null!));

        var result = new RelationshipQueryPageResult(
            3,
            2,
            [Relationship("relationship-a"), Relationship("relationship-b")]);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.ReturnedCount);
        Assert.Equal(["relationship-a", "relationship-b"], result.Relationships.Select(item => item.RelationshipId));
    }

    private static RelationshipQueryResult Relationship(string id) =>
        new(
            id,
            "Calls",
            "IL:call",
            "Outgoing",
            new RelationshipEndpointQueryResult("source", "Demo.Source.Run", "System.Void Demo.Source::Run()", null, true, "game"),
            new RelationshipEndpointQueryResult(null, null, null, "UnityEngine.Debug::Log(System.String)", false));
}
