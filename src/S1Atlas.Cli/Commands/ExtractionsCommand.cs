using System.CommandLine;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Cleanup;
using S1Atlas.Extraction.History;

namespace S1Atlas.Cli.Commands;

internal static class ExtractionsCommand
{
    public static Command Create(
        ExtractionHistoryService historyService,
        ExtractionCleanupService cleanupService,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(historyService);
        ArgumentNullException.ThrowIfNull(cleanupService);
        ArgumentNullException.ThrowIfNull(repository);

        var command = new Command(
            "extractions",
            "Inspect validated extraction history and manage preferred output.");
        command.Subcommands.Add(ExtractionsListCommand.Create(
            historyService, repository, output, error, cancellationToken));
        command.Subcommands.Add(ExtractionsShowCommand.Create(
            historyService, repository, output, error, cancellationToken));
        command.Subcommands.Add(ExtractionsPromoteCommand.Create(
            historyService, repository, output, error, cancellationToken));
        command.Subcommands.Add(ExtractionsCleanupCommand.Create(
            cleanupService, output, error, cancellationToken));
        return command;
    }
}
