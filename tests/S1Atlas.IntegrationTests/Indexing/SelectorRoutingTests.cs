using Xunit;

namespace S1Atlas.IntegrationTests.Indexing;

// Walks every symbol-taking command (search excluded: it owns list-shaped
// output and its own tests) to prove ambiguous selectors render the shared
// numbered candidate table and unknown selectors render near matches.
public sealed class SelectorRoutingTests
{
    public static TheoryData<string, string, string, string[]> RoutingCommands => new()
    {
        { "callers", "Dup", "Widjet", [] },
        { "callees", "Dup", "Widjet", [] },
        { "refs", "Dup", "Widjet", [] },
        { "fieldrefs", "Value", "Valiu", [] },
        { "overrides", "Dup", "Widjet", [] },
        { "overridden-by", "Dup", "Widjet", [] },
        { "derived", "Dup", "Widjet", [] },
        { "callable", "Dup", "Widjet", [] },
        { "source", "Dup", "Widjet", [] },
        { "open", "Dup", "Widjet", [] },
        { "investigate_seam", "Dup", "Widjet", ["--question", "Which seam owns it?"] },
    };

    [Theory]
    [MemberData(nameof(RoutingCommands))]
    public async Task Ambiguous_selector_renders_the_shared_candidate_table(
        string command,
        string ambiguousQuery,
        string _,
        string[] extraArgs)
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateRoutingAsync();

        var result = atlas.Run([command, ambiguousQuery, .. extraArgs]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Found 2 candidates; showing 2.", result.StandardOutput, StringComparison.Ordinal);
        Assert.Matches(new System.Text.RegularExpressions.Regex(@"^1 \| ", System.Text.RegularExpressions.RegexOptions.Multiline), result.StandardOutput);
        Assert.Matches(new System.Text.RegularExpressions.Regex(@"^2 \| ", System.Text.RegularExpressions.RegexOptions.Multiline), result.StandardOutput);
        Assert.Contains("Hint: re-run with the exact signature or a short ID from the table.", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Code:    AmbiguousSymbol", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RoutingCommands))]
    public async Task Typo_selector_renders_nearest_matches(
        string command,
        string _,
        string typoQuery,
        string[] extraArgs)
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateRoutingAsync();

        var result = atlas.Run([command, typoQuery, .. extraArgs]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Found 0 matches.", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Nearest matches:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            $"Hint: no symbol matched '{typoQuery}'; check the spelling or run 's1atlas search \"{typoQuery}\".",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains("Code:    SymbolNotFound", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("type", "Gadget", "Game.Routing.Gadget")]
    [InlineData("method", "Widget", "Game.Routing.Widget")]
    public async Task List_commands_keep_list_shaped_success_output(
        string command,
        string query,
        string expectedRow)
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateRoutingAsync();

        var result = atlas.Run([command, query]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(expectedRow, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callsites_keeps_success_shaped_output()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateRoutingAsync();

        var result = atlas.Run(["callsites", "Game.Routing.Widget"]);

        Assert.Equal(0, result.ExitCode);
    }
}
