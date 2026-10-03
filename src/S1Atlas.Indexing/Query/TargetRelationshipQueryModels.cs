using System.Text.Json.Serialization;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Query;

public static class TargetRelationshipQueryNotices
{
    public const string CallSites = "Call-site results are evidence of recovered IL references and do not prove runtime behavior or execution order.";
    public const string FieldReferences = "Field-reference results are evidence of recovered IL references and do not prove runtime behavior, lifecycle ordering, or call order.";
    public const string RelationshipTotalUnavailable = "The complete relationship total is unavailable; returned rows are a lower bound.";
}

public enum FieldReferenceFilter
{
    All,
    Readers,
    Writers
}

public sealed record CallSiteQueryResult(
    RelationshipQueryPageResult Page,
    string CompletenessNotice,
    string? NextCursor = null,
    [property: JsonIgnore] bool HasMore = false)
{
    public int TotalCount => Page.TotalCount;
    public int ReturnedCount => Page.ReturnedCount;
    public IReadOnlyList<RelationshipQueryResult> Relationships => Page.Relationships;
}

public sealed record FieldReferenceQueryResult(
    SymbolResolutionResult Resolution,
    RelationshipQueryPageResult Page,
    string CompletenessNotice = TargetRelationshipQueryNotices.FieldReferences,
    string? NextCursor = null,
    [property: JsonIgnore] bool HasMore = false)
{
    public int TotalCount => Page.TotalCount;
    public int ReturnedCount => Page.ReturnedCount;
    public IReadOnlyList<RelationshipQueryResult> Relationships => Page.Relationships;
}
