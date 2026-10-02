using S1Atlas.Cli.Commands;
using S1Atlas.Core.Indexing;
using Xunit;

namespace S1Atlas.IntegrationTests.Indexing;

public sealed class SelectorPresenterTests
{
    [Fact]
    public void NarrowestHint_shared_signature_leaves_only_the_short_id()
    {
        var candidates = new[]
        {
            Candidate("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Same.Sig()"),
            Candidate("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Same.Sig()"),
        };

        Assert.Equal(
            "Hint: re-run with a short ID from the table.",
            IndexQueryCommandFactory.NarrowestHint(candidates));
    }

    [Fact]
    public void NarrowestHint_differing_signatures_offer_the_signature_first()
    {
        var candidates = new[]
        {
            Candidate("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Same.Sig()"),
            Candidate("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Other.Sig()"),
        };

        Assert.Equal(
            "Hint: re-run with the exact signature or a short ID from the table.",
            IndexQueryCommandFactory.NarrowestHint(candidates));
    }

    [Fact]
    public void NarrowestHint_empty_candidates_do_not_throw()
    {
        Assert.Equal(
            "Hint: re-run with a short ID from the table.",
            IndexQueryCommandFactory.NarrowestHint([]));
    }

    [Theory]
    [InlineData("Demo.Target", "Demo.Target")]
    [InlineData("  Demo.Target  ", "Demo.Target")]
    [InlineData("first\nsecond", "first")]
    [InlineData("first\r\nsecond", "first")]
    [InlineData("say \"hi\"", "say 'hi'")]
    [InlineData("", "(blank)")]
    [InlineData("   ", "(blank)")]
    public void DisplaySelector_sanitizes_echoed_selectors(string input, string expected)
    {
        Assert.Equal(expected, IndexQueryCommandFactory.DisplaySelector(input));
    }

    [Fact]
    public void DisplaySelector_truncates_long_selectors()
    {
        var input = new string('x', 100);

        var display = IndexQueryCommandFactory.DisplaySelector(input);

        Assert.Equal(new string('x', 80) + "...", display);
    }

    [Fact]
    public void WriteCandidateRow_pins_the_ticket_column_order()
    {
        using var writer = new StringWriter();

        IndexQueryCommandFactory.WriteCandidateRow(
            writer,
            3,
            Candidate(
                "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
                "System.Void Ns.Type::Run()",
                qualifiedName: "Ns.Type.Run",
                kind: "Method",
                codebase: "ScheduleI",
                shortId: "abcdef123456"));

        Assert.Equal(
            "3 | Method | Ns.Type.Run | System.Void Ns.Type::Run() | abcdef123456 | ScheduleI" +
            Environment.NewLine,
            writer.ToString());
    }

    [Fact]
    public void WriteCandidateRow_falls_back_to_display_when_short_id_is_missing()
    {
        using var writer = new StringWriter();

        IndexQueryCommandFactory.WriteCandidateRow(
            writer,
            1,
            Candidate("abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890", "Sig()"));

        Assert.Contains("abcdef123456", writer.ToString(), StringComparison.Ordinal);
    }

    private static SymbolQueryResult Candidate(
        string symbolId,
        string signature,
        string qualifiedName = "Ns.Name",
        string kind = "Method",
        string codebase = "ScheduleI",
        string? shortId = null) =>
        new(
            IndexId: "index-1",
            Codebase: codebase,
            Channel: "Installed",
            SymbolId: symbolId,
            Kind: kind,
            QualifiedName: qualifiedName,
            Signature: signature,
            IsBestEffort: false,
            ShortId: shortId);
}
