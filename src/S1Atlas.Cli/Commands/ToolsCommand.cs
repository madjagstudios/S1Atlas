using System.CommandLine;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Tools;

namespace S1Atlas.Cli.Commands;

internal static class ToolsCommand
{
    public static Command Create(
        ManagedToolService service,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var command = new Command(
            "tools",
            "Inspect and explicitly install repository-pinned tools.");
        command.Subcommands.Add(ToolsStatusCommand.Create(
            service,
            repository,
            output,
            error,
            cancellationToken));
        command.Subcommands.Add(ToolsInstallCommand.Create(
            service,
            repository,
            output,
            error,
            cancellationToken));
        return command;
    }
}
