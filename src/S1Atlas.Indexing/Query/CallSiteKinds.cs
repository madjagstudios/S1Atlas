using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Query;

internal static class CallSiteKinds
{
    public static readonly IReadOnlyList<string> Names = ["Calls", "CallsVirtual"];

    public static IReadOnlyList<IndexRelationshipRecord> MergeAndTake(
        IEnumerable<IndexRelationshipRecord> edges,
        int limit,
        int offset = 0) =>
        edges
            .OrderBy(edge => edge.TargetText ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(edge => edge.RelationshipId, StringComparer.Ordinal)
            .Skip(offset)
            .Take(limit)
            .ToArray();
}
