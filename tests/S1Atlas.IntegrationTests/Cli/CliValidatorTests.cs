using System.Text.Json;
using Xunit;

namespace S1Atlas.IntegrationTests.Cli;

public sealed class CliValidatorTests
{
    [Theory]
    [InlineData("search", "Alpha", "--codebase bogus", "InvalidOptionCombination", "Codebase must be schedule-i, s1api, or s1mapi.", false)]
    [InlineData("search", "Alpha", "--channel bogus", "InvalidOptionCombination", "Channel must be installed, release, preview, or all.", false)]
    [InlineData("search", "Alpha", "--scope bogus", "InvalidOptionCombination", "Scope must be game, reference, or all.", false)]
    [InlineData("search", "Alpha", "--scope game --collection qol", "InvalidOptionCombination", "--collection is valid only for reference or all scope.", false)]
    [InlineData("search", "Alpha", "--scope reference", "InvalidOptionCombination", "--scope reference and --scope all require --collection.", false)]
    [InlineData("search", "Alpha", "--scope reference --collection qol --codebase s1api", "InvalidOptionCombination", "Reference scopes require --codebase schedule-i.", false)]
    [InlineData("search", "Alpha", "--scope reference --collection qol --channel all", "InvalidOptionCombination", "Reference scopes require --channel installed.", false)]
    [InlineData("search", "Alpha", "--build build-1 --codebase s1api", "InvalidOptionCombination", "--build is only valid with --codebase schedule-i and --channel installed or all.", true)]
    [InlineData("callable", "Alpha", "--channel release", "InvalidOptionCombination", "callable queries require --codebase schedule-i and --channel installed.", true)]
    [InlineData("field-refs", "Alpha", "--readers --writers", "InvalidOptionCombination", "--readers and --writers are mutually exclusive.", true)]
    [InlineData("field-refs", "Alpha", "--build build-1 --codebase s1api", "InvalidOptionCombination", "--build is only valid with --codebase schedule-i and --channel installed for game scope, or with --scope reference/all.", true)]
    [InlineData("source", "Alpha", "--full-type --file", "InvalidOptionCombination", "--full-type cannot be combined with --file or --output.", true)]
    [InlineData("source", "Alpha", "--build build-1 --codebase s1api", "InvalidOptionCombination", "--build is only valid with --codebase schedule-i and --channel installed.", true)]
    [InlineData("overrides", "Alpha", "--build build-1 --codebase s1api", "InvalidOptionCombination", "--build is only valid with --codebase schedule-i and --channel installed for game scope, or with --scope reference/all.", true)]
    [InlineData("investigate-seam", "Alpha", "--question Why --codebase bogus", "InvalidOptionCombination", "Codebase must be schedule-i, s1api, or s1mapi.", false)]
    public async Task Query_option_rules_fail_with_the_stable_json_triple(
        string command,
        string query,
        string extra,
        string expectedCode,
        string expectedMessage,
        bool exactMessage)
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var args = new List<string> { command, query };
        args.AddRange(extra.Split(' '));
        args.Add("--json");
        var result = atlas.Run([.. args]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(command, root.GetProperty("command").GetString());
        Assert.Equal(1, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(expectedCode, root.GetProperty("error").GetProperty("code").GetString());
        var message = root.GetProperty("error").GetProperty("message").GetString();
        if (exactMessage)
            Assert.Equal(expectedMessage, message);
        else
            Assert.Contains(expectedMessage, message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("search", "Alpha", "--codebase bogus", "Codebase must be schedule-i, s1api, or s1mapi.")]
    [InlineData("field-refs", "Alpha", "--readers --writers", "--readers and --writers are mutually exclusive.")]
    public async Task Query_option_rules_fail_on_stderr_for_human_output(
        string command,
        string query,
        string extra,
        string expectedMessage)
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var args = new List<string> { command, query };
        args.AddRange(extra.Split(' '));
        var result = atlas.Run([.. args]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains(expectedMessage, result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Code:    InvalidOptionCombination", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("search", "Alpha", "--limit 0 --codebase bogus", "InvalidLimit")]
    [InlineData("overridden-by", "Alpha", "--depth 0 --codebase bogus", "InvalidDepth")]
    [InlineData("source", "Alpha", "--context -1 --codebase bogus", "InvalidContext")]
    public async Task Earlier_value_checks_still_win_over_option_rules(
        string command,
        string query,
        string extra,
        string expectedCode)
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var args = new List<string> { command, query };
        args.AddRange(extra.Split(' '));
        args.Add("--json");
        var result = atlas.Run([.. args]);

        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(expectedCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Blank_selector_still_wins_over_option_rules()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run("investigate-seam", "", "--question", "Why", "--codebase", "bogus", "--json");

        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("InvalidSelector", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Mistyped_option_values_keep_the_framework_error()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run("search", "Alpha", "--limit", "abc");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Cannot parse argument 'abc' for option '--limit'", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Code:", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("schemaVersion", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_required_question_keeps_the_framework_error()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run("investigate-seam", "Alpha", "--codebase", "bogus");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--question", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Code:", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("schemaVersion", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_wins_over_failing_option_rules()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run("search", "Alpha", "--codebase", "bogus", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Examples:", result.StandardOutput, StringComparison.Ordinal);
    }
}
