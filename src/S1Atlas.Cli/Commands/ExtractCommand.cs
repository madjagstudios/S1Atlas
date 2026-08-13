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
        ExtractionOrchestrator orchestrator,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
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
            Description = "Run a new attempt even when prior attempt facts exist."
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
            "Run offline Cpp2IL extraction into a non-authoritative candidate.");
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
                    var result = orchestrator.RunAsync(
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
                    var attempt = result.Attempt;
                    var data = new ExtractionOutput(
                        attempt.AttemptId,
                        attempt.Status.ToString(),
                        attempt.BuildId,
                        attempt.RecipeId ?? throw InvalidResult("recipe ID"),
                        attempt.ToolInstanceId ?? throw InvalidResult("tool instance ID"),
                        result.ToolInstance.TrustLevel.ToString(),
                        result.InputSource.ToString(),
                        result.InputSnapshotId,
                        attempt.CandidateOutputPath ?? throw InvalidResult("candidate output path"),
                        attempt.StandardOutputPath,
                        attempt.StandardErrorPath,
                        result.ProcessWasRun,
                        result.IsAuthoritative,
                        ValidationOutcome: null);
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
        writer.WriteLine("Cpp2IL process completed under S1Atlas control.");
        writer.WriteLine($"Attempt:       {data.AttemptId}");
        writer.WriteLine($"Build:         {data.BuildId}");
        writer.WriteLine($"Tool trust:    {data.ToolTrustLevel}");
        writer.WriteLine($"Input source:  {data.InputSource}");
        writer.WriteLine($"Candidate:     {data.CandidateOutputPath}");
        writer.WriteLine();
        writer.WriteLine(
            "This Phase 3 output is unvalidated and is not available to downstream consumers.");
        writer.WriteLine(
            "Phase 4 validation and immutable promotion are still required.");
    }

    private static InvalidOperationException InvalidResult(string description) =>
        new($"A successful extraction did not include its {description}.");
}
