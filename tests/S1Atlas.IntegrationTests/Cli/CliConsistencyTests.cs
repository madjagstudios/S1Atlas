using System.CommandLine;
using System.Text.RegularExpressions;
using S1Atlas.Cli;
using Xunit;

namespace S1Atlas.IntegrationTests.Cli;

public sealed partial class CliConsistencyTests
{
    private static readonly Regex KebabCase = new("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.Compiled);

    [Fact]
    public void CommandTree_UsesKebabCaseCommandAndOptionNames()
    {
        var root = BuildTree();
        var violations = new List<string>();
        Walk(
            root,
            [],
            violations,
            checkLeaf: _ => { },
            checkCommand: (path, command) =>
            {
                if (!KebabCase.IsMatch(command.Name))
                    violations.Add($"{path}: command name '{command.Name}' is not kebab-case.");
            },
            checkOption: (path, option) =>
            {
                foreach (var alias in new[] { option.Name }.Concat(option.Aliases))
                {
                    if (!alias.StartsWith("--", StringComparison.Ordinal))
                        continue;
                    if (!KebabCase.IsMatch(alias[2..]))
                        violations.Add($"{path}: option name '{alias}' is not kebab-case.");
                }
            },
            checkArgument: (_, _) => { });

        Assert.Empty(violations);
    }

    [Fact]
    public void CommandTree_DescribesEveryCommandOptionAndArgument()
    {
        var root = BuildTree();
        var violations = new List<string>();
        Walk(
            root,
            [],
            violations,
            checkLeaf: _ => { },
            checkCommand: (path, command) =>
            {
                if (string.IsNullOrWhiteSpace(command.Description))
                    violations.Add($"{path}: command has no description.");
            },
            checkOption: (path, option) =>
            {
                if (string.IsNullOrWhiteSpace(option.Description))
                    violations.Add($"{path}: option '{option.Name}' has no description.");
            },
            checkArgument: (path, argument) =>
            {
                if (string.IsNullOrWhiteSpace(argument.Description))
                    violations.Add($"{path}: argument '<{argument.Name}>' has no description.");
            });

        Assert.Empty(violations);
    }

    [Fact]
    public void CommandTree_ShowsAnExampleOnEveryLeafCommand()
    {
        var root = BuildTree();
        var violations = new List<string>();
        var leafCount = 0;
        var leafPaths = new List<string>();
        Walk(
            root,
            [],
            violations,
            checkLeaf: path =>
            {
                leafCount++;
                leafPaths.Add(path);
            },
            checkCommand: (path, command) =>
            {
                if (command.Subcommands.Count != 0)
                    return;
                var lines = (command.Description ?? string.Empty).Split('\n');
                var marker = Array.FindIndex(lines, line => line.Trim().Equals("Examples:", StringComparison.Ordinal));
                if (marker < 0 || marker + 1 >= lines.Length || !lines[marker + 1].TrimStart().StartsWith("s1atlas ", StringComparison.Ordinal))
                    violations.Add($"{path}: leaf command shows no example.");
            },
            checkOption: (_, _) => { },
            checkArgument: (_, _) => { });

        Assert.True(leafCount >= 40, $"Expected at least 40 leaf commands, walked {leafCount}.");
        Assert.Contains("search", leafPaths);
        Assert.Contains("upstream sync", leafPaths);
        Assert.Contains("reference collections list", leafPaths);
        Assert.Empty(violations);
    }

    internal static RootCommand BuildTree()
    {
        using var fixture = new CliTreeFixture();
        var application = new CliApplication(
            fixture.DataDirectory,
            "0.1.0-test",
            fixture.ConfigurationDirectory,
            () => new HttpClient(new RejectingHandler()),
            TimeProvider.System,
            processExtractorFactory: null,
            isProcessAlive: _ => false);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = application.Invoke(
            ["--help"],
            output,
            error,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, exitCode);
        return application.LastBuiltRoot;
    }

    private static void Walk(
        Command command,
        string[] path,
        List<string> violations,
        Action<string> checkLeaf,
        Action<string, Command> checkCommand,
        Action<string, Option> checkOption,
        Action<string, Argument> checkArgument)
    {
        var current = command is RootCommand ? path : [.. path, command.Name];
        var label = current.Length == 0 ? "(root)" : string.Join(" ", current);
        if (command is not RootCommand)
            checkCommand(label, command);
        if (command.Subcommands.Count == 0 && command is not RootCommand)
            checkLeaf(label);
        foreach (var option in command.Options)
        {
            if (option.Name is "--help" or "--version")
                continue;
            checkOption(label, option);
        }
        foreach (var argument in command.Arguments)
            checkArgument(label, argument);
        foreach (var subcommand in command.Subcommands)
            Walk(subcommand, current, violations, checkLeaf, checkCommand, checkOption, checkArgument);
    }

    private sealed class CliTreeFixture : IDisposable
    {
        private readonly string _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-cli-tree-tests-{Guid.NewGuid():N}");

        public CliTreeFixture()
        {
            DataDirectory = Path.Combine(_temporaryDirectory, "data");
            ConfigurationDirectory = Path.Combine(_temporaryDirectory, "config");
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(ConfigurationDirectory);
        }

        public string DataDirectory { get; }

        public string ConfigurationDirectory { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_temporaryDirectory))
                    Directory.Delete(_temporaryDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Command-tree tests must not use the network.");
    }
}
