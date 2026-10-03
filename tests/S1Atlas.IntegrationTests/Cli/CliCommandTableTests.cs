using System.CommandLine;
using Xunit;

namespace S1Atlas.IntegrationTests.Cli;

public sealed class CliCommandTableTests
{
    private const string BeginMarker = "<!-- cli-table:begin -->";
    private const string EndMarker = "<!-- cli-table:end -->";

    [Fact]
    public void CommandReference_MatchesGeneratedLeafTable()
    {
        var generated = GenerateTable(CliConsistencyTests.BuildTree());
        var usagePath = FindUsage();
        var text = File.ReadAllText(usagePath).Replace("\r\n", "\n");
        var begin = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, "USAGE.md has no cli-table block.");

        var committed = text[(begin + BeginMarker.Length)..end].Trim('\n');
        if (Environment.GetEnvironmentVariable("S1ATLAS_UPDATE_CLI_TABLE") == "1")
        {
            var updated = text[..(begin + BeginMarker.Length)] + "\n" + generated + "\n" + text[end..];
            File.WriteAllText(usagePath, updated);
            Assert.Fail("Regenerated the cli-table block; re-run tests.");
        }

        Assert.Equal(generated, committed);
    }

    internal static string GenerateTable(RootCommand root)
    {
        var rows = new List<string>();
        Walk(root, [], rows);
        rows.Sort(StringComparer.Ordinal);
        var lines = new List<string> { "| Command | Purpose |", "|---|---|" };
        lines.AddRange(rows);
        return string.Join("\n", lines);
    }

    private static void Walk(Command command, string[] path, List<string> rows)
    {
        var current = command is RootCommand ? path : [.. path, command.Name];
        if (command.Subcommands.Count == 0 && command is not RootCommand)
        {
            var signature = new List<string>(["s1atlas", .. current]);
            foreach (var argument in command.Arguments)
            {
                var rendered = "<" + argument.Name + ">";
                signature.Add(argument.Arity.MinimumNumberOfValues == 0 ? "[" + rendered + "]" : rendered);
            }
            foreach (var option in command.Options)
            {
                if (option.Name is "--help" or "--version")
                    continue;
                signature.Add(option.Required ? option.Name : $"[{option.Name}]");
            }
            var purpose = (command.Description ?? string.Empty).Split('\n')[0].Replace("|", "\\|");
            rows.Add($"| `{string.Join(" ", signature)}` | {purpose} |");
        }
        foreach (var subcommand in command.Subcommands)
            Walk(subcommand, current, rows);
    }

    private static string FindUsage()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "USAGE.md");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate docs/USAGE.md above the test binaries.");
    }
}
