using S1Atlas.Core.Indexing;

namespace S1Atlas.Cli.Output;

internal sealed record IndexQueryOutput(
    IReadOnlyList<SymbolQueryResult> Symbols,
    IReadOnlyList<RelationshipQueryResult> Relationships,
    IReadOnlyList<SourceQueryResult> Sources,
    int? TotalCount = null,
    int? ReturnedCount = null)
{
    public IReadOnlyList<SymbolQueryResult> Results => Symbols;
}
