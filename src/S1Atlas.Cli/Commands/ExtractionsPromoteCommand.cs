using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.History;

namespace S1Atlas.Cli.Commands;

internal static class ExtractionsPromoteCommand
{
    public static Command Create(
        ExtractionHistoryService historyService,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var idArgument = new Argument<string>("extraction-id")
        {
            Description = "The 64-character validated extraction ID to make preferred."
        };
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command(
            "promote",
            "Explicitly make a validated extraction the preferred output for its build.");
        command.Arguments.Add(idArgument);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(
                "extractions promote",
                parseResult.GetValue(jsonOption),
                output,
                error);
            var id = parseResult.GetRequiredValue(idArgument);
            return CommandExecution.Run(
                () =>
                {
                    if (IsLowerHex(id, 32))
                    {
                        return commandOutput.Failure(
                            1,
                            "AttemptNotPromotable",
                            $"Manual promotion requires a 64-character validated extraction ID; " +
                            $"'{id}' is a 32-character attempt ID.");
                    }

                    if (!IsLowerHex(id, 64))
                    {
                        return commandOutput.Failure(
                            1,
                            "InvalidExtractionId",
                            $"'{id}' is not a 64-character lower-case hexadecimal extraction ID.");
                    }

                    repository.InitializeAsync(cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    var outcome = historyService.PromoteAsync(id, cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    var data = new ExtractionPromoteOutput(
                        outcome.Extraction.ExtractionId,
                        outcome.Extraction.BuildId,
                        outcome.Outcome.ToString(),
                        outcome.ToolTrustLevel.ToString(),
                        Preferred: true,
                        outcome.WasAlreadyPreferred,
                        outcome.Revalidated);
                    return commandOutput.Success(data, writer => WriteHuman(writer, data));
                },
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    private static void WriteHuman(TextWriter writer, ExtractionPromoteOutput data)
    {
        if (data.AlreadyPreferred)
        {
            writer.WriteLine("Extraction is already the preferred output; no change was made.");
        }
        else
        {
            writer.WriteLine("Extraction promoted to preferred output.");
        }

        writer.WriteLine($"Extraction:  {data.ExtractionId}");
        writer.WriteLine($"Build:       {data.BuildId}");
        writer.WriteLine($"Trust:       {data.ToolTrustLevel}");
        writer.WriteLine($"Validation:  {data.ValidationOutcome}");
        writer.WriteLine($"Preferred:   {(data.Preferred ? "yes" : "no")}");
        writer.WriteLine($"Revalidated: {(data.Revalidated ? "yes" : "no")}");
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length &&
        value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
