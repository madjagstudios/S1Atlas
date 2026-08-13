using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction;

namespace S1Atlas.Cli.Commands;

internal static class ExtractCommand
{
    private const string DefaultProfileId =
        "cpp2il-reconstructed-assemblies-v1";

    public static Command Create(
        ValidatedExtractionWorkflow workflow,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(repository);

        var buildOption = new Option<string?>("--build")
        {
            Description = "Select a known 64-character Atlas build ID."
        };
        var gamePathOption = new Option<string?>("--game-path")
        {
            Description = "Override the Schedule I installation directory."
        };
        var cpp2IlPathOption = new Option<string?>("--cpp2il-path")
        {
            Description = "Use an explicitly trusted custom Cpp2IL executable."
        };
        var profileOption = new Option<string?>("--profile")
        {
            Description = $"Extraction profile ID (default: {DefaultProfileId})."
        };
        var retryOption = new Option<bool>("--retry")
        {
            Description = "Run a new Cpp2IL process even when reusable output exists."
        };
        var snapshotInputsOption = new Option<bool>("--snapshot-inputs")
        {
            Description = "Archive the verified live inputs for later review."
        };
        var keepFailedArtifactsOption = new Option<bool>("--keep-failed-artifacts")
        {
            Description = "Retain failed partial output inside the attempt quarantine."
        };
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command(
            "extract",
            "Extract, validate, and promote an authoritative reconstructed assembly set.");
        command.Options.Add(buildOption);
        command.Options.Add(gamePathOption);
        command.Options.Add(cpp2IlPathOption);
        command.Options.Add(profileOption);
        command.Options.Add(retryOption);
        command.Options.Add(snapshotInputsOption);
        command.Options.Add(keepFailedArtifactsOption);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(
                "extract",
                parseResult.GetValue(jsonOption),
                output,
                error);
            return CommandExecution.Run(
                () =>
                {
                    var result = workflow.RunAsync(
                            new ExtractionOptions(
                                parseResult.GetValue(buildOption),
                                parseResult.GetValue(gamePathOption),
                                parseResult.GetValue(cpp2IlPathOption),
                                parseResult.GetValue(profileOption) ?? DefaultProfileId,
                                parseResult.GetValue(retryOption),
                                parseResult.GetValue(snapshotInputsOption),
                                parseResult.GetValue(keepFailedArtifactsOption)),
                            cancellationToken)
                        .GetAwaiter()
                        .GetResult();

                    if (!result.IsAuthoritative)
                    {
                        // A candidate that ran the process but failed validation is not
                        // authoritative and never reaches downstream consumers; report it
                        // as an operational failure rather than a successful extraction.
                        return commandOutput.Failure(
                            1,
                            "ExtractionNotAuthoritative",
                            "The extraction did not produce authoritative validated output " +
                            $"(validation outcome: {result.ValidationOutcome}). See the attempt's " +
                            "validation report for the recorded issues.",
                            result.AttemptId,
                            result.ValidationOutcome.ToString());
                    }

                    var data = new ExtractionOutput(
                        result.AttemptId,
                        result.BuildId,
                        result.RecipeId,
                        result.ExtractionId,
                        result.ExtractionRoot,
                        result.ToolTrustLevel.ToString(),
                        result.ValidationOutcome.ToString(),
                        result.ProcessWasRun,
                        result.ValidationWasRun,
                        result.ReusedExistingExtraction,
                        result.IsPreferred,
                        result.IsAuthoritative);
                    return commandOutput.Success(
                        data,
                        writer => WriteHuman(writer, data));
                },
                commandOutput,
                cancellationToken);
        });

        return command;
    }

    private static void WriteHuman(TextWriter writer, ExtractionOutput data)
    {
        writer.WriteLine("Authoritative validated extraction is available.");
        writer.WriteLine($"Extraction:    {data.ExtractionId}");
        writer.WriteLine($"Root:          {data.ExtractionRoot}");
        writer.WriteLine($"Build:         {data.BuildId}");
        writer.WriteLine($"Tool trust:    {data.ToolTrustLevel}");
        writer.WriteLine($"Validation:    {data.ValidationOutcome}");
        writer.WriteLine($"Preferred:     {(data.Preferred ? "yes" : "no")}");
        writer.WriteLine(
            $"Process run:   {(data.ProcessWasRun ? "yes" : "no")}    " +
            $"Validation run: {(data.ValidationWasRun ? "yes" : "no")}    " +
            $"Reused: {(data.ReusedExistingExtraction ? "yes" : "no")}");
    }
}
