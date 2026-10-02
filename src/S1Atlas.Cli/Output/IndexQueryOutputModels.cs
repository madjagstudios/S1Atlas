using S1Atlas.Core.Indexing;

namespace S1Atlas.Cli.Output;

internal sealed record IndexQueryOutput(
    IReadOnlyList<SymbolQueryResult> Symbols,
    IReadOnlyList<RelationshipQueryResult> Relationships,
    IReadOnlyList<SourceQueryResult> Sources,
    int? TotalCount = null,
    int? ReturnedCount = null,
    int? ExactCount = null,
    int? DerivedCount = null,
    SymbolResolutionResult? Resolution = null,
    BodyRecoveryStatus? BodyRecoveryStatus = null,
    bool? CallerCompletenessBoundedByTargetResolution = null,
    string? CompletenessNotice = null,
    CallableSurfaceResolutionResult? CallableSurface = null,
    IReadOnlyList<HierarchyNodeQueryResult>? HierarchyNodes = null)
{
    public IReadOnlyList<SymbolQueryResult> Results => Symbols;
}

internal sealed record IndexQueryFailureData(
    IReadOnlyList<SymbolQueryResult> Candidates);
