using System.CommandLine;
using Xunit;

namespace S1Atlas.IntegrationTests.Cli;

public sealed class CliOptionRuleTests
{
    [Theory]
    [InlineData("recover-native-body", "--symbol-id", "At least one is required.")]
    [InlineData("upstream sync", "--commit", "Required.")]
    [InlineData("index", "--commit", "Required with --channel release or preview.")]
    [InlineData("index", "--interop-path", "Valid only for the default installed Schedule I code index.")]
    [InlineData("index", "--build", "Valid only with --scene.")]
    [InlineData("index", "--scene", "Accepts --build and --force only.")]
    [InlineData("index", "--codebase", "Not with --scene.")]
    [InlineData("index", "--channel", "Not with --scene.")]
    [InlineData("field-refs", "--readers", "Mutually exclusive with --writers.")]
    [InlineData("field-refs", "--writers", "Mutually exclusive with --readers.")]
    [InlineData("source", "--full-type", "Cannot be combined with --file or --output.")]
    [InlineData("source", "--file", "Cannot be combined with --full-type.")]
    [InlineData("source", "--output", "Cannot be combined with --full-type.")]
    [InlineData("open", "--port", "1 to 65535.")]
    [InlineData("diff", "--limit", "Must be greater than zero.")]
    [InlineData("diff", "--codebase", "schedule-i, s1api, or s1mapi.")]
    [InlineData("diff", "--channel", "Only installed (default) is supported for diffing.")]
    [InlineData("index", "--codebase", "s1api or s1mapi.")]
    [InlineData("callable", "--codebase", "schedule-i only.")]
    [InlineData("callable", "--channel", "installed only.")]
    [InlineData("upstream status", "--codebase", "s1api or s1mapi.")]
    [InlineData("upstream sync", "--codebase", "s1api or s1mapi")]
    [InlineData("extract", "--input-snapshot", "Requires --retry")]
    [InlineData("extract", "--input-snapshot", "cannot be combined with --game-path or --snapshot-inputs.")]
    [InlineData("search", "--channel", "all is not valid with --scope reference or all.")]
    [InlineData("type", "--channel", "all is not valid with --scope reference or all.")]
    [InlineData("method", "--channel", "all is not valid with --scope reference or all.")]
    [InlineData("refs", "--channel", "all is not valid with --scope reference or all.")]
    [InlineData("callees", "--channel", "all is not valid with --scope reference or all.")]
    [InlineData("call-sites", "--channel", "all is not valid with --scope reference or all.")]
    [InlineData("callers", "--channel", "all is not valid with --scope reference or all.")]
    [InlineData("field-refs", "--channel", "all is not valid with --scope reference or all.")]
    [InlineData("overrides", "--channel", "all is not valid with --scope reference or all.")]
    [InlineData("overridden-by", "--channel", "all is not valid with --scope reference or all.")]
    [InlineData("derived", "--channel", "all is not valid with --scope reference or all.")]
    [InlineData("source", "--channel", "all is not valid with --scope reference or all.")]
    [InlineData("investigate-seam", "--channel", "all is not valid with --scope reference or all.")]
    [InlineData("search", "--scope", "Reference and all require --collection and --codebase schedule-i.")]
    [InlineData("type", "--scope", "Reference and all require --collection and --codebase schedule-i.")]
    [InlineData("method", "--scope", "Reference and all require --collection and --codebase schedule-i.")]
    [InlineData("refs", "--scope", "Reference and all require --collection and --codebase schedule-i.")]
    [InlineData("callees", "--scope", "Reference and all require --collection and --codebase schedule-i.")]
    [InlineData("call-sites", "--scope", "Reference and all require --collection and --codebase schedule-i.")]
    [InlineData("callers", "--scope", "Reference and all require --collection and --codebase schedule-i.")]
    [InlineData("field-refs", "--scope", "Reference and all require --collection and --codebase schedule-i.")]
    [InlineData("overrides", "--scope", "Reference and all require --collection and --codebase schedule-i.")]
    [InlineData("overridden-by", "--scope", "Reference and all require --collection and --codebase schedule-i.")]
    [InlineData("derived", "--scope", "Reference and all require --collection and --codebase schedule-i.")]
    [InlineData("source", "--scope", "Reference and all require --collection and --codebase schedule-i.")]
    [InlineData("investigate-seam", "--scope", "Reference and all require --collection and --codebase schedule-i.")]
    [InlineData("search", "--collection", "Valid only with --scope reference or all.")]
    [InlineData("type", "--collection", "Valid only with --scope reference or all.")]
    [InlineData("method", "--collection", "Valid only with --scope reference or all.")]
    [InlineData("refs", "--collection", "Valid only with --scope reference or all.")]
    [InlineData("callees", "--collection", "Valid only with --scope reference or all.")]
    [InlineData("call-sites", "--collection", "Valid only with --scope reference or all.")]
    [InlineData("callers", "--collection", "Valid only with --scope reference or all.")]
    [InlineData("field-refs", "--collection", "Valid only with --scope reference or all.")]
    [InlineData("overrides", "--collection", "Valid only with --scope reference or all.")]
    [InlineData("overridden-by", "--collection", "Valid only with --scope reference or all.")]
    [InlineData("derived", "--collection", "Valid only with --scope reference or all.")]
    [InlineData("source", "--collection", "Valid only with --scope reference or all.")]
    [InlineData("investigate-seam", "--collection", "Valid only with --scope reference or all.")]
    [InlineData("search", "--build", "Valid only with --codebase schedule-i and --channel installed.")]
    [InlineData("type", "--build", "Valid only with --codebase schedule-i and --channel installed.")]
    [InlineData("method", "--build", "Valid only with --codebase schedule-i and --channel installed.")]
    [InlineData("refs", "--build", "Valid only with --codebase schedule-i and --channel installed.")]
    [InlineData("callees", "--build", "Valid only with --codebase schedule-i and --channel installed.")]
    [InlineData("callers", "--build", "Valid only with --codebase schedule-i and --channel installed.")]
    [InlineData("callable", "--build", "Valid only with --codebase schedule-i and --channel installed.")]
    [InlineData("call-sites", "--build", "or with --scope reference/all.")]
    [InlineData("field-refs", "--build", "or with --scope reference/all.")]
    [InlineData("overrides", "--build", "or with --scope reference/all.")]
    [InlineData("overridden-by", "--build", "or with --scope reference/all.")]
    [InlineData("derived", "--build", "or with --scope reference/all.")]
    [InlineData("investigate-seam", "--build", "or with --scope reference/all.")]
    [InlineData("source", "--build", "Valid only with --codebase schedule-i and --channel installed.")]
    public void Option_help_names_its_parse_time_rule(string commandPath, string optionName, string fragment)
    {
        var root = CliConsistencyTests.BuildTree();
        Command current = root;
        foreach (var segment in commandPath.Split(' '))
        {
            current = current.Subcommands.FirstOrDefault(c => c.Name == segment)
                ?? throw new InvalidOperationException($"Command '{commandPath}' not found.");
        }

        var option = current.Options.FirstOrDefault(o => o.Name == optionName);
        Assert.NotNull(option);
        Assert.Contains(fragment, option.Description ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Required_question_is_marked_in_rendered_help()
    {
        await using var atlas = await SeamInvestigationCliAtlas.CreateBareAsync();

        var result = atlas.Run("investigate-seam", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--question", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("(REQUIRED)", result.StandardOutput, StringComparison.Ordinal);
    }
}
