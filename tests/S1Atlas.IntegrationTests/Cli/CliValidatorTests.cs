using System.Text.Json;
using S1Atlas.Cli.Commands;
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
    [InlineData("search", "Alpha", "--scope all", "InvalidOptionCombination", "--scope reference and --scope all require --collection.", false)]
    [InlineData("search", "Alpha", "--scope reference --collection qol --codebase s1api", "InvalidOptionCombination", "Reference scopes require --codebase schedule-i.", false)]
    [InlineData("search", "Alpha", "--scope reference --collection qol --channel all", "InvalidOptionCombination", "Reference scopes require --channel installed.", false)]
    [InlineData("search", "Alpha", "--build build-1 --codebase s1api", "InvalidOptionCombination", "--build is only valid with --codebase schedule-i and --channel installed.", true)]
    [InlineData("search", "Alpha", "--build build-1 --channel all", "InvalidOptionCombination", "--build is only valid with --codebase schedule-i and --channel installed.", true)]
    [InlineData("callable", "Alpha", "--channel release", "InvalidOptionCombination", "callable queries require --codebase schedule-i and --channel installed.", true)]
    [InlineData("callable", "Alpha", "--codebase s1api", "InvalidOptionCombination", "callable queries require --codebase schedule-i and --channel installed.", true)]
    [InlineData("field-refs", "Alpha", "--readers --writers", "InvalidOptionCombination", "--readers and --writers are mutually exclusive.", true)]
    [InlineData("field-refs", "Alpha", "--build build-1 --codebase s1api", "InvalidOptionCombination", "--build is only valid with --codebase schedule-i and --channel installed for game scope, or with --scope reference/all.", true)]
    [InlineData("source", "Alpha", "--full-type --file", "InvalidOptionCombination", "--full-type cannot be combined with --file or --output.", true)]
    [InlineData("source", "Alpha", "--full-type --output out.txt", "InvalidOptionCombination", "--full-type cannot be combined with --file or --output.", true)]
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
    [InlineData("s1api")]
    [InlineData("s1mapi")]
    public void ParseOptions_accepts_all_channels_for_reference_codebases(string codebase)
    {
        var options = IndexQueryCommandFactory.ParseOptions(codebase, "all");

        Assert.True(options.AllChannels);
    }

    [Fact]
    public async Task Channel_all_with_a_reference_codebase_passes_option_validation()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run("search", "Alpha", "--codebase", "s1api", "--channel", "all", "--json");

        // Whether the query succeeds depends on machine-global API index state;
        // option validation passes either way, so only the validation code is pinned.
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        if (!root.GetProperty("success").GetBoolean())
            Assert.NotEqual("InvalidOptionCombination", root.GetProperty("error").GetProperty("code").GetString());
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

    [Theory]
    [InlineData("recover-native-body", "recover-native-body --json", "MissingSymbolId", "At least one --symbol-id must be provided.", true)]
    [InlineData("extract", "extract --input-snapshot aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa --json", "InputSnapshotRequiresRetry", "An explicit --input-snapshot run requires --retry so it always runs a new process from the archived snapshot.", true)]
    [InlineData("extract", "extract --input-snapshot aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa --retry --game-path x --json", "InputSnapshotConflict", "The --input-snapshot option cannot be combined with --game-path or --snapshot-inputs.", true)]
    [InlineData("extract", "extract --input-snapshot zz --retry --json", "InvalidInputSnapshot", "The --input-snapshot value must be a 64-character lower-case hexadecimal snapshot ID.", true)]
    [InlineData("upstream status", "upstream status --codebase bogus --json", "InvalidCodebase", "Upstream codebase must be s1api or s1mapi.", false)]
    [InlineData("upstream sync", "upstream sync --codebase s1api --json", "CommitRequired", "upstream sync requires --commit so the cache is keyed by an exact commit SHA.", true)]
    [InlineData("upstream sync", "upstream sync --codebase bogus --json", "InvalidCodebase", "Upstream codebase must be s1api or s1mapi.", false)]
    [InlineData("index", "index --scene --codebase s1api --json", "InvalidOptionCombination", "Scene indexing accepts --build and --force; --codebase, --channel, and --commit are code-index options.", true)]
    [InlineData("index", "index --scene --channel installed --json", "InvalidOptionCombination", "Scene indexing accepts --build and --force; --codebase, --channel, and --commit are code-index options.", true)]
    [InlineData("index", "index --scene --commit deadbeef --json", "InvalidOptionCombination", "Scene indexing accepts --build and --force; --codebase, --channel, and --commit are code-index options.", true)]
    [InlineData("index", "index --build b --json", "InvalidOptionCombination", "--build is valid only with --scene.", true)]
    [InlineData("index", "index --interop-path p --codebase s1api --channel installed --json", "InvalidOptionCombination", "--interop-path is valid only for the default installed Schedule I code index.", true)]
    [InlineData("index", "index --codebase bogus --json", "InvalidCodebaseChannel", "API indexing requires --codebase s1api or s1mapi and --channel installed, release, or preview.", true)]
    [InlineData("index", "index --codebase s1api --channel release --json", "InvalidCommit", "Release and Preview indexing require --commit <40-character cached SHA>.", true)]
    [InlineData("diff", "diff a b --limit 0 --json", "InvalidLimit", "--limit must be greater than zero.", true)]
    [InlineData("diff", "diff a b --channel release --json", "UnsupportedChannel", "Build diffing requires installed-channel indexes. Release and preview channels are not supported in V1.", true)]
    [InlineData("diff", "diff a b --channel bogus --json", "InvalidChannel", "Channel must be installed. Release and preview are not supported for diffing.", true)]
    [InlineData("diff", "diff a b --channel all --json", "InvalidChannel", "Channel must be installed. Release and preview are not supported for diffing.", true)]
    [InlineData("diff", "diff a b --codebase bogus --json", "InvalidCodebase", "Codebase must be schedule-i, s1api, or s1mapi.", false)]
    public async Task Operational_option_rules_fail_with_the_stable_json_triple(
        string expectedCommand,
        string joinedArgs,
        string expectedCode,
        string expectedMessage,
        bool exactMessage)
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run(joinedArgs.Split(' '));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal(expectedCommand, root.GetProperty("command").GetString());
        Assert.Equal(1, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(expectedCode, root.GetProperty("error").GetProperty("code").GetString());
        var message = root.GetProperty("error").GetProperty("message").GetString();
        if (exactMessage)
            Assert.Equal(expectedMessage, message);
        else
            Assert.Contains(expectedMessage, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_rejects_an_invalid_codebase_on_stderr()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run("diff", "a", "b", "--codebase", "bogus");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("Codebase must be schedule-i, s1api, or s1mapi.", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Code:    InvalidCodebase", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    public async Task Open_rejects_an_out_of_range_port_on_stderr(string port)
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run("open", "Alpha", "--port", port);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains($"Invalid port '{port}'. Use 1 to 65535.", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Code:    InvalidPort", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("search --codebase bogus", "Required argument missing for command")]
    [InlineData("search Alpha --codebase bogus --bogus-flag", "Unrecognized command or argument '--bogus-flag'")]
    public async Task Framework_parse_errors_win_over_option_rules(string joinedArgs, string expectedFragment)
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run(joinedArgs.Split(' '));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expectedFragment, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("schemaVersion", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_wins_over_a_missing_required_option()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run("recover-native-body", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Examples:", result.StandardOutput, StringComparison.Ordinal);
    }
}
