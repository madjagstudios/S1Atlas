using S1Atlas.Core.Indexing;
using Xunit;

namespace S1Atlas.Core.Tests.Indexing;

public sealed class SymbolResolutionModelTests
{
    [Fact]
    public void ShortId_defaults_to_null_and_round_trips()
    {
        var symbol = Symbol("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        Assert.Null(symbol.ShortId);

        var withShortId = symbol with { ShortId = "0123456789ab" };

        Assert.Equal("0123456789ab", withShortId.ShortId);
    }

    [Fact]
    public void Suggestions_default_to_empty_and_null_normalizes_to_empty()
    {
        var @default = new SymbolResolutionResult(SymbolResolutionStatus.NotFound, null, []);
        var explicitNull = new SymbolResolutionResult(SymbolResolutionStatus.NotFound, null, [], null);

        Assert.NotNull(@default.Suggestions);
        Assert.Empty(@default.Suggestions);
        Assert.NotNull(explicitNull.Suggestions);
        Assert.Empty(explicitNull.Suggestions);
    }

    [Fact]
    public void Suggestions_preserve_explicit_entries()
    {
        var suggestion = Symbol("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");
        var result = new SymbolResolutionResult(SymbolResolutionStatus.NotFound, null, [], [suggestion]);

        Assert.Equal([suggestion.SymbolId], result.Suggestions.Select(item => item.SymbolId));
    }

    [Fact]
    public void TotalCandidateCount_defaults_to_null_and_round_trips()
    {
        var @default = new SymbolResolutionResult(SymbolResolutionStatus.Ambiguous, null, []);

        Assert.Null(@default.TotalCandidateCount);

        var withTotal = @default with { TotalCandidateCount = 12 };

        Assert.Equal(12, withTotal.TotalCandidateCount);
    }

    [Fact]
    public void Kinded_result_forwards_suggestions_and_total_to_base()
    {
        var mismatch = Symbol("1111111111111111111111111111111111111111111111111111111111111111");
        var suggestion = Symbol("2222222222222222222222222222222222222222222222222222222222222222");
        var result = new KindedSymbolResolutionResult(
            SymbolResolutionStatus.NotFound,
            null,
            [],
            mismatch,
            [suggestion],
            7);

        Assert.Equal([suggestion.SymbolId], result.Suggestions.Select(item => item.SymbolId));
        Assert.Equal(7, result.TotalCandidateCount);
        Assert.Equal(mismatch.SymbolId, result.KindMismatch.SymbolId);
    }

    private static SymbolQueryResult Symbol(string symbolId) =>
        new(
            "index-a",
            "ScheduleI",
            "Installed",
            symbolId,
            "Method",
            "Demo.Source.Run",
            "System.Void Demo.Source::Run()",
            false);
}
