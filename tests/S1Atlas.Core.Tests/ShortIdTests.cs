using S1Atlas.Core;
using Xunit;

namespace S1Atlas.Core.Tests;

public sealed class ShortIdTests
{
    [Fact]
    public void TryParsePrefix_accepts_8_to_63_hex_and_lowercases()
    {
        Assert.True(ShortId.TryParsePrefix("abcdef12", out var lower));
        Assert.Equal("abcdef12", lower);

        Assert.True(ShortId.TryParsePrefix("ABCDEF12", out var folded));
        Assert.Equal("abcdef12", folded);

        Assert.True(ShortId.TryParsePrefix(new string('a', 63), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc1234")]
    [InlineData("zzzzzzzz")]
    [InlineData("abcdef12 ")]
    public void TryParsePrefix_rejects_non_prefixes(string? input)
    {
        Assert.False(ShortId.TryParsePrefix(input, out _));
    }

    [Fact]
    public void TryParsePrefix_rejects_full_length_ids()
    {
        Assert.False(ShortId.TryParsePrefix(new string('a', 64), out _));
    }

    [Fact]
    public void TryParseFullId_accepts_exact_hex_case_insensitively()
    {
        Assert.True(ShortId.TryParseFullId(new string('a', 64), 64, out var lower));
        Assert.Equal(new string('a', 64), lower);

        Assert.True(ShortId.TryParseFullId(new string('B', 32), 32, out var folded));
        Assert.Equal(new string('b', 32), folded);
    }

    [Theory]
    [InlineData(null, 64)]
    [InlineData("", 64)]
    [InlineData("zzzzzzzz", 8)]
    [InlineData("abcdef12", 64)]
    public void TryParseFullId_rejects_mismatches(string? input, int length)
    {
        Assert.False(ShortId.TryParseFullId(input, length, out _));
    }

    [Fact]
    public void MatchPrefix_prefers_exact_case_insensitive_hits()
    {
        var match = ShortId.MatchPrefix(["abcdef1234", "abcdef5678"], "ABCDEF1234");

        Assert.Equal(ShortIdMatchKind.Resolved, match.Kind);
        Assert.Equal("abcdef1234", match.Id);
    }

    [Fact]
    public void MatchPrefix_resolves_unique_prefixes()
    {
        var match = ShortId.MatchPrefix(["abcdef1234", "1234567890"], "abcdef12");

        Assert.Equal(ShortIdMatchKind.Resolved, match.Kind);
        Assert.Equal("abcdef1234", match.Id);
    }

    [Fact]
    public void MatchPrefix_reports_ambiguity_with_capped_ordered_matches_and_total()
    {
        var ids = Enumerable.Range(0, 12)
            .Select(index => "abcdef00" + index.ToString("x2", System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        var match = ShortId.MatchPrefix(ids, "abcdef00");

        Assert.Equal(ShortIdMatchKind.Ambiguous, match.Kind);
        Assert.Null(match.Id);
        Assert.Equal(12, match.TotalCount);
        Assert.Equal(10, match.Shown.Count);
        Assert.Equal(ids.Order(StringComparer.Ordinal).Take(10), match.Shown);
    }

    [Fact]
    public void MatchPrefix_distinguishes_no_match_from_not_a_prefix()
    {
        Assert.Equal(
            ShortIdMatchKind.NotFound,
            ShortId.MatchPrefix(["abcdef1234"], "12345678").Kind);
        Assert.Equal(
            ShortIdMatchKind.NotAPrefix,
            ShortId.MatchPrefix(["abcdef1234"], "zzzzzzzz").Kind);
        Assert.Equal(
            ShortIdMatchKind.NotAPrefix,
            ShortId.MatchPrefix(["abcdef1234"], "abc").Kind);
    }

    [Fact]
    public void MatchPrefix_rejects_blank_input()
    {
        Assert.Throws<ArgumentException>(() => ShortId.MatchPrefix(["abcdef1234"], "   "));
    }

    [Fact]
    public void Display_truncates_to_twelve_characters()
    {
        Assert.Equal("abcdef123456", ShortId.Display("abcdef1234567890"));
        Assert.Equal("abc", ShortId.Display("abc"));
    }

    [Fact]
    public void FormatMatchList_joins_described_ids_and_reports_truncation()
    {
        var truncated = ShortId.MatchPrefix(
            Enumerable.Range(0, 12).Select(index => "abcdef00" + index.ToString("x2") + new string('f', 52)),
            "abcdef00");

        Assert.Equal(
            "abcdef0000ff, abcdef0001ff, abcdef0002ff, abcdef0003ff, abcdef0004ff, " +
            "abcdef0005ff, abcdef0006ff, abcdef0007ff, abcdef0008ff, abcdef0009ff " +
            "(showing 10 of 12)",
            ShortId.FormatMatchList(truncated, ShortId.Display));

        var exact = ShortId.MatchPrefix(["abcdef0010", "abcdef0011"], "abcdef001");

        Assert.Equal(ShortIdMatchKind.Ambiguous, exact.Kind);
        Assert.Equal(
            "abcdef0010 <10>, abcdef0011 <11>",
            ShortId.FormatMatchList(exact, id => $"{ShortId.Display(id)} <{id[8..10]}>"));
    }
}
