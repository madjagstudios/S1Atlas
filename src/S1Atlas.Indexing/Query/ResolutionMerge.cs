using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Query;

internal static class ResolutionMerge
{
    internal const int MaxSuggestions = 5;

    internal static int? CombineTotals(params (SymbolResolutionResult Result, int Contributed)[] sides)
    {
        var total = 0;
        foreach (var (result, contributed) in sides)
        {
            if (result.Status == SymbolResolutionStatus.Ambiguous)
            {
                if (result.TotalCandidateCount is null)
                    return null;
                total += result.TotalCandidateCount.Value;
            }
            else
            {
                total += contributed;
            }
        }

        return total;
    }
}
