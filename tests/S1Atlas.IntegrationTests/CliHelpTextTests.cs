using S1Atlas.Cli;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.IntegrationTests;

public sealed class CliHelpTextTests : IAsyncDisposable
{
    private const string SharedQueryDescription =
        "A symbol selector: full symbol ID, unique short-ID prefix, canonical key, signature, qualified name, or fuzzy text.";
    private const string SharedBuildDescription =
        "Select a Schedule I Installed build ID or unique short-ID prefix.";

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(), "s1atlas-cli-help-" + Guid.NewGuid().ToString("N"));

    public CliHelpTextTests()
    {
        Directory.CreateDirectory(_dataDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_dataDirectory);
    }

    public static TheoryData<string> SymbolQueryCommands => new()
    {
        "callers", "callees", "refs", "field-refs", "call-sites", "search", "type", "method",
        "callable", "source", "open", "investigate-seam", "derived", "overrides", "overridden-by"
    };

    [Theory]
    [MemberData(nameof(SymbolQueryCommands))]
    public void Symbol_query_commands_share_one_selector_help_text(string command)
    {
        var help = InvokeHelp(command);

        Assert.Contains(SharedQueryDescription, help, StringComparison.Ordinal);
    }

    public static TheoryData<string> InstalledBuildCommands => new()
    {
        "callers", "callees", "refs", "field-refs", "call-sites", "search", "type", "method",
        "callable", "source", "investigate-seam", "derived", "overrides", "overridden-by"
    };

    [Theory]
    [MemberData(nameof(InstalledBuildCommands))]
    public void Installed_build_options_share_one_prefix_capable_help_text(string command)
    {
        var help = InvokeHelp(command);

        Assert.Contains(SharedBuildDescription, help, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_build_option_advertises_prefixes()
    {
        Assert.Contains(
            "Select a known Atlas build ID or unique short-ID prefix.",
            InvokeHelp("extract"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Recover_options_advertise_prefixes()
    {
        var help = InvokeHelp("recover-native-body");

        Assert.Contains(
            "Select a Schedule I Installed build ID or unique short-ID prefix; defaults to the current installed build.",
            help,
            StringComparison.Ordinal);
        Assert.Contains(
            "A native symbol ID or unique short-ID prefix to recover; repeat for multiple IDs.",
            help,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_build_arguments_advertise_prefixes()
    {
        var help = InvokeHelp("diff");

        Assert.Contains(
            "Build ID or unique short-ID prefix for the baseline (before).",
            help,
            StringComparison.Ordinal);
        Assert.Contains(
            "Build ID or unique short-ID prefix for the target (after).",
            help,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Extractions_show_id_argument_advertises_prefixes()
    {
        var help = InvokeHelp("extractions", "show");

        Assert.Contains(
            "A 64-character extraction ID, 32-character attempt ID, or unique short-ID prefix.",
            help,
            StringComparison.Ordinal);
    }

    private string InvokeHelp(params string[] command)
    {
        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(
            [.. command, "--help"], output, error, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        return output.ToString();
    }
}
