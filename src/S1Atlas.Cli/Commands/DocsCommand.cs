using System.CommandLine;

namespace S1Atlas.Cli.Commands;

internal static class DocsCommand
{
    public static Command Create(string dataRoot, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var command = new Command("docs", "Generate the offline static human portal.");
        command.Subcommands.Add(DocsGenerateCommand.Create(dataRoot, output, error, cancellationToken));
        return command;
    }
}
