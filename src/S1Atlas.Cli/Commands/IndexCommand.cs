using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Workflow;

namespace S1Atlas.Cli.Commands;

internal static class IndexCommand
{
    public static Command Create(
        IndexingWorkflow workflow,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var forceOption = new Option<bool>("--force") { Description = "Rebuild a completed index as a new candidate." };
        var jsonOption = CommandOutput.CreateJsonOption();
        var command = new Command("index", "Build the installed Schedule I source and symbol index.");
        command.Options.Add(forceOption);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("index", parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(
                () =>
                {
                    repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
                    var snapshot = repository.GetCurrentSnapshotAsync(cancellationToken).GetAwaiter().GetResult();
                    if (snapshot is null)
                        return commandOutput.Failure(1, "NoEnvironmentSnapshot", "No current environment snapshot is available.");

                    var result = workflow.RunScheduleOneAsync(
                        snapshot.Build.BuildId,
                        parseResult.GetValue(forceOption),
                        cancellationToken).GetAwaiter().GetResult();
                    var data = new IndexOutput(
                        "ScheduleI",
                        "Installed",
                        snapshot.Build.BuildId,
                        result.IndexId,
                        result.Reused,
                        result.SymbolCount,
                        result.SourceFileCount,
                        result.RelationshipCount,
                        result.Warnings);
                    return commandOutput.Success(
                        data,
                        writer => writer.WriteLine(
                            $"Schedule I Installed | {result.IndexId} | " +
                            (result.Reused ? "reused" : "rebuilt") +
                            $" | symbols {result.SymbolCount} | source files {result.SourceFileCount} | relationships {result.RelationshipCount}"));
                },
                commandOutput,
                cancellationToken);
        });
        return command;
    }
}
