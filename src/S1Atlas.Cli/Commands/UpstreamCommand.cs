using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Upstream;

namespace S1Atlas.Cli.Commands;

internal static class UpstreamCommand
{
    public static Command Create(string dataRoot, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var command = new Command("upstream", "Inspect and manually synchronize upstream API snapshots.");
        command.Subcommands.Add(UpstreamStatusCommand.Create(dataRoot, output, error, cancellationToken));
        command.Subcommands.Add(UpstreamSyncCommand.Create(dataRoot, output, error, cancellationToken));
        return command;
    }
}
