using S1Atlas.Core.Indexing;

namespace S1Atlas.Cli.Output;

internal sealed record IndexQueryOutput(
    IReadOnlyList<SymbolQueryResult> Symbols,
    IReadOnlyList<RelationshipQueryResult> Relationships,
    IReadOnlyList<SourceQueryResult> Sources,
    int? TotalCount = null,
    int? ReturnedCount = null,
    SymbolResolutionResult? Resolution = null,
    BodyRecoveryStatus? BodyRecoveryStatus = null,
    bool? CallerCompletenessBoundedByTargetResolution = null,
    string? CompletenessNotice = null)
{
    public IReadOnlyList<SymbolQueryResult> Results => Symbols;
}

internal sealed record IndexQueryFailureData(
    IReadOnlyList<SymbolQueryResult> Candidates);
