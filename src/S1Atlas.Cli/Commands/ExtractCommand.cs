using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Cli.Performance;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction;

namespace S1Atlas.Cli.Commands;

internal static class ExtractCommand
{
    private const string DefaultProfileId =
        "cpp2il-reconstructed-assemblies-v2";

    public static Command Create(
        ValidatedExtractionWorkflow workflow,
        IAtlasRepository repository,
        string dataRoot,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(repository);

        var buildOption = new Option<string?>("--build")
        {
            Description = "Select a known Atlas build ID or unique short-ID prefix."
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
        var inputSnapshotOption = new Option<string?>("--input-snapshot")
        {
            Description =
                "Run Cpp2IL from an archived input snapshot (64-character lower-case hex) " +
                "instead of live input. Requires --retry and cannot be combined with " +
                "--game-path or --snapshot-inputs."
        };
        var keepFailedArtifactsOption = new Option<bool>("--keep-failed-artifacts")
        {
            Description = "Retain failed partial output inside the attempt quarantine."
        };
        var performanceOption = new Option<bool>("--performance")
        {
            Description = "Write performance diagnostics JSON to standard error."
        };
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command(
            "extract",
            CliExamples.With("Extract, validate, and promote an authoritative reconstructed assembly set.", "s1atlas extract --retry"));
        command.Options.Add(buildOption);
        command.Options.Add(gamePathOption);
        command.Options.Add(cpp2IlPathOption);
        command.Options.Add(profileOption);
        command.Options.Add(retryOption);
        command.Options.Add(snapshotInputsOption);
        command.Options.Add(inputSnapshotOption);
        command.Options.Add(keepFailedArtifactsOption);
        command.Options.Add(performanceOption);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(
                "extract",
                parseResult.GetValue(jsonOption),
                output,
                error);
            var performance = parseResult.GetValue(performanceOption)
                ? new PerformanceMeasurement("extract", dataRoot)
                : null;
            return Execute(
                workflow,
                new ExtractionOptions(
                    parseResult.GetValue(buildOption),
                    parseResult.GetValue(gamePathOption),
                    parseResult.GetValue(cpp2IlPathOption),
                    parseResult.GetValue(profileOption) ?? DefaultProfileId,
                    parseResult.GetValue(retryOption),
                    parseResult.GetValue(snapshotInputsOption),
                    parseResult.GetValue(keepFailedArtifactsOption),
                    parseResult.GetValue(inputSnapshotOption)),
                commandOutput,
                performance,
                cancellationToken);
        });

        return command;
    }

    internal static ExtractionOptions DefaultOptions =>
        new(null, null, null, DefaultProfileId, false, false, false, null);

    internal static int Execute(
        ValidatedExtractionWorkflow workflow,
        ExtractionOptions options,
        CommandOutput commandOutput,
        PerformanceMeasurement? performance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(commandOutput);
        return CommandExecution.Run(
            () =>
            {
                var validationFailure = ValidateInputSnapshotOption(
                    options.InputSnapshotId,
                    options.GamePath,
                    options.Retry,
                    options.SnapshotInputs,
                    commandOutput);
                if (validationFailure is int failureExitCode)
                {
                    return failureExitCode;
                }

                ExtractionWorkflowResult result;
                using (performance?.Measure("extraction.workflow"))
                {
                    result = workflow.RunAsync(options, cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                }

                if (performance is not null)
                {
                    performance.SetCounter("process.wasRun", result.ProcessWasRun ? 1 : 0);
                    performance.SetCounter("validation.wasRun", result.ValidationWasRun ? 1 : 0);
                    performance.SetCounter("extraction.reused", result.ReusedExistingExtraction ? 1 : 0);
                    performance.SetCounter("extraction.authoritative", result.IsAuthoritative ? 1 : 0);
                    performance.SetCounter(
                        "inputSnapshot.replayVerified",
                        result.InputSnapshotReplayVerified ? 1 : 0);
                }

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
                        attemptId: result.AttemptId,
                        stage: result.ValidationOutcome.ToString());
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
                    result.IsAuthoritative,
                    result.InputSource?.ToString(),
                    result.InputSnapshotId,
                    result.InputSnapshotReplayVerified);
                return commandOutput.Success(
                    data,
                    writer => WriteHuman(writer, data));
            },
            commandOutput,
            cancellationToken,
            performance);
    }

    private static int? ValidateInputSnapshotOption(
        string? inputSnapshot,
        string? gamePath,
        bool retry,
        bool snapshotInputs,
        CommandOutput commandOutput)
    {
        if (inputSnapshot is null)
        {
            return null;
        }

        if (!IsLowerSha256(inputSnapshot))
        {
            return commandOutput.Failure(
                1,
                "InvalidInputSnapshot",
                "The --input-snapshot value must be a 64-character lower-case hexadecimal " +
                "snapshot ID.");
        }

        if (!retry)
        {
            return commandOutput.Failure(
                1,
                "InputSnapshotRequiresRetry",
                "An explicit --input-snapshot run requires --retry so it always runs a new " +
                "process from the archived snapshot.");
        }

        if (!string.IsNullOrWhiteSpace(gamePath) || snapshotInputs)
        {
            return commandOutput.Failure(
                1,
                "InputSnapshotConflict",
                "The --input-snapshot option cannot be combined with --game-path or " +
                "--snapshot-inputs.");
        }

        return null;
    }

    private static bool IsLowerSha256(string value) =>
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

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
        writer.WriteLine($"Input source:  {data.InputSource ?? "unknown"}");
        if (data.InputSnapshotId is not null)
        {
            writer.WriteLine(
                $"Input snapshot: {data.InputSnapshotId} " +
                $"(replay-verified: {(data.InputSnapshotReplayVerified ? "yes" : "no")})");
        }
    }
}
