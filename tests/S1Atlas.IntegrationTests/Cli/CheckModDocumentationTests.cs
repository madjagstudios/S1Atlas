using S1Atlas.Cli.Commands;
using Xunit;

namespace S1Atlas.IntegrationTests.Cli;

public sealed class CheckModDocumentationTests
{
    [Fact]
    public void CheckMod_has_help_example_and_parse_time_validation()
    {
        var root = CliConsistencyTests.BuildTree();
        var command = Assert.Single(root.Subcommands, command => command.Name == "check-mod");
        Assert.Contains("s1atlas check-mod", command.Description, StringComparison.Ordinal);
        var exception = Assert.Throws<CliValidationException>(() => root.Parse(["check-mod", " ", "--json"]));
        Assert.Equal("check-mod", exception.Command);
        Assert.Equal("InvalidModPath", exception.Code);
        var emptyBuild = Assert.Throws<CliValidationException>(() => root.Parse(["check-mod", "Demo.dll", "--from", " "]));
        Assert.Equal("InvalidBuild", emptyBuild.Code);
    }

    [Fact]
    public void Usage_documents_check_mod_and_all_cli_exit_codes()
    {
        var usage = File.ReadAllText(FindUsage());
        Assert.Contains("## Check a mod after an update", usage, StringComparison.Ordinal);
        Assert.Contains("## Exit codes", usage, StringComparison.Ordinal);
        Assert.Contains("| `0` | Success |", usage, StringComparison.Ordinal);
        Assert.Contains("| `1` | Error, including invalid arguments |", usage, StringComparison.Ordinal);
        Assert.Contains("| `2` | Cancelled |", usage, StringComparison.Ordinal);
        Assert.Contains("| `3` | `check-mod` found breaking changes |", usage, StringComparison.Ordinal);
    }

    private static string FindUsage()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "docs", "USAGE.md");
            if (File.Exists(path)) return path;
        }
        throw new InvalidOperationException("USAGE.md not found.");
    }
}
