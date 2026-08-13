using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.History;

namespace S1Atlas.Cli.Commands;

internal static class ExtractionsListCommand
{
    public static Command Create(
        ExtractionHistoryService historyService,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var buildOption = new Option<string?>("--build")
        {
            Description = "Restrict history to a known 64-character Atlas build ID."
        };
        var includeFailedOption = new Option<bool>("--include-failed")
        {
            Description = "Also list failed, canceled, abandoned, and candidate attempts."
        };
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command(
            "list",
            "List validated extractions newest first, optionally with failed attempts.");
        command.Options.Add(buildOption);
        command.Options.Add(includeFailedOption);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(
                "extractions list",
                parseResult.GetValue(jsonOption),
                output,
                error);
            return CommandExecution.Run(
                () =>
                {
                    repository.InitializeAsync(cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    var items = historyService.ListAsync(
                            parseResult.GetValue(buildOption),
                            parseResult.GetValue(includeFailedOption),
                            cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    var data = new ExtractionHistoryListOutput(
                        items.Select(ToOutput).ToArray());
                    return commandOutput.Success(data, writer => WriteHuman(writer, data));
                },
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    private static ExtractionHistoryEntryOutput ToOutput(ExtractionHistoryItem item) => new(
        item.Kind == ExtractionHistoryEntryKind.ValidatedExtraction ? "Extraction" : "Attempt",
        item.Id,
        item.BuildId,
        item.RecipeId,
        item.CreatedAtUtc.ToString("O"),
        item.Status,
        item.ToolTrustLevel?.ToString(),
        item.ValidationOutcome?.ToString(),
        item.Preferred,
        item.ResultExtractionId);

    private static void WriteHuman(TextWriter writer, ExtractionHistoryListOutput data)
    {
        if (data.Entries.Count == 0)
        {
            writer.WriteLine("No validated extractions.");
            return;
        }

        for (var index = 0; index < data.Entries.Count; index++)
        {
            if (index > 0)
            {
                writer.WriteLine();
            }

            var entry = data.Entries[index];
            writer.WriteLine($"{entry.Kind}: {entry.Id}");
            writer.WriteLine($"  Build:       {entry.BuildId}");
            writer.WriteLine($"  Created:     {entry.CreatedAtUtc}");
            writer.WriteLine($"  Status:      {entry.Status}");
            writer.WriteLine($"  Trust:       {entry.ToolTrustLevel ?? "unknown"}");
            if (entry.ValidationOutcome is not null)
            {
                writer.WriteLine($"  Validation:  {entry.ValidationOutcome}");
            }

            if (entry.Kind == "Extraction")
            {
                writer.WriteLine($"  Preferred:   {(entry.Preferred ? "yes" : "no")}");
            }
            else if (entry.ResultExtractionId is not null)
            {
                writer.WriteLine($"  Result:      {entry.ResultExtractionId}");
            }
        }
    }
}
