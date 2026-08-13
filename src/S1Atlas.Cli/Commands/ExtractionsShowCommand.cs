using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.History;

namespace S1Atlas.Cli.Commands;

internal static class ExtractionsShowCommand
{
    public static Command Create(
        ExtractionHistoryService historyService,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "A 64-character extraction ID or a 32-character attempt ID."
        };
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command(
            "show",
            "Show a validated extraction (full integrity) or an attempt's facts.");
        command.Arguments.Add(idArgument);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(
                "extractions show",
                parseResult.GetValue(jsonOption),
                output,
                error);
            var id = parseResult.GetRequiredValue(idArgument);
            return CommandExecution.Run(
                () =>
                {
                    repository.InitializeAsync(cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    var detail = historyService.ShowAsync(id, cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    if (detail is null)
                    {
                        return commandOutput.Failure(
                            1,
                            "HistoryEntryNotFound",
                            $"No validated extraction or attempt exists for ID '{id}'.");
                    }

                    var data = ToOutput(detail);
                    return commandOutput.Success(data, writer => WriteHuman(writer, data));
                },
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    private static ExtractionShowOutput ToOutput(ExtractionHistoryDetail detail)
    {
        if (detail.Kind == ExtractionHistoryEntryKind.ValidatedExtraction)
        {
            var extraction = detail.Extraction!;
            return new ExtractionShowOutput(
                "Extraction",
                new ExtractionDetailOutput(
                    extraction.ExtractionId,
                    extraction.RecipeId,
                    extraction.BuildId,
                    extraction.ToolInstanceId,
                    extraction.SourceAttemptId,
                    extraction.ProfileId,
                    extraction.ProfileVersion,
                    extraction.ProfileDigest,
                    extraction.ArtifactManifestDigest,
                    extraction.RootPath,
                    extraction.CreatedAtUtc.ToString("O"),
                    extraction.TrustLevel.ToString(),
                    extraction.InitialValidationOutcome.ToString(),
                    detail.Preferred,
                    detail.IntegrityVerified,
                    ToOutput(extraction.Statistics)),
                Attempt: null);
        }

        var attempt = detail.Attempt!;
        return new ExtractionShowOutput(
            "Attempt",
            Extraction: null,
            new AttemptDetailOutput(
                attempt.AttemptId,
                attempt.BuildId,
                attempt.RecipeId,
                attempt.Status.ToString(),
                attempt.ToolInstanceId,
                detail.AttemptToolTrustLevel?.ToString(),
                attempt.CreatedAtUtc.ToString("O"),
                attempt.StartedAtUtc?.ToString("O"),
                attempt.CompletedAtUtc?.ToString("O"),
                attempt.ValidationSourceExtractionId,
                attempt.ResultExtractionId,
                attempt.FailureStage?.ToString(),
                attempt.FailureCode?.ToString(),
                attempt.FailureMessage));
    }

    private static ExtractionStatisticsOutput ToOutput(ExtractionStatistics statistics) => new(
        statistics.ArtifactCount,
        statistics.LibraryCount,
        statistics.ManagedAssemblyCount,
        statistics.TypeDefinitionCount,
        statistics.MethodDefinitionCount,
        statistics.FieldDefinitionCount,
        statistics.PropertyDefinitionCount,
        statistics.EventDefinitionCount,
        statistics.TotalOutputBytes,
        statistics.TotalManagedBytes);

    private static void WriteHuman(TextWriter writer, ExtractionShowOutput data)
    {
        if (data.Extraction is { } extraction)
        {
            writer.WriteLine("Validated extraction");
            writer.WriteLine();
            writer.WriteLine($"Extraction:      {extraction.ExtractionId}");
            writer.WriteLine($"Build:           {extraction.BuildId}");
            writer.WriteLine($"Recipe:          {extraction.RecipeId}");
            writer.WriteLine($"Source attempt:  {extraction.SourceAttemptId}");
            writer.WriteLine($"Manifest digest: {extraction.ArtifactManifestDigest}");
            writer.WriteLine($"Root:            {extraction.RootPath}");
            writer.WriteLine($"Trust:           {extraction.ToolTrustLevel}");
            writer.WriteLine($"Validation:      {extraction.InitialValidationOutcome}");
            writer.WriteLine($"Preferred:       {(extraction.Preferred ? "yes" : "no")}");
            writer.WriteLine($"Integrity:       {(extraction.IntegrityVerified ? "verified" : "unverified")}");
            writer.WriteLine(
                $"Artifacts:       {extraction.Statistics.ArtifactCount} " +
                $"({extraction.Statistics.ManagedAssemblyCount} managed, " +
                $"{extraction.Statistics.TypeDefinitionCount} types, " +
                $"{extraction.Statistics.MethodDefinitionCount} methods)");
            return;
        }

        var attempt = data.Attempt!;
        writer.WriteLine("Extraction attempt");
        writer.WriteLine();
        writer.WriteLine($"Attempt:         {attempt.AttemptId}");
        writer.WriteLine($"Build:           {attempt.BuildId}");
        writer.WriteLine($"Status:          {attempt.Status}");
        writer.WriteLine($"Trust:           {attempt.ToolTrustLevel ?? "unknown"}");
        writer.WriteLine($"Created:         {attempt.CreatedAtUtc}");
        if (attempt.ValidationSourceExtractionId is not null)
        {
            writer.WriteLine($"Validation of:   {attempt.ValidationSourceExtractionId}");
        }

        if (attempt.ResultExtractionId is not null)
        {
            writer.WriteLine($"Result:          {attempt.ResultExtractionId}");
        }

        if (attempt.FailureCode is not null)
        {
            writer.WriteLine($"Failure stage:   {attempt.FailureStage}");
            writer.WriteLine($"Failure code:    {attempt.FailureCode}");
            if (attempt.FailureMessage is not null)
            {
                writer.WriteLine($"Failure detail:  {attempt.FailureMessage}");
            }
        }
    }
}
