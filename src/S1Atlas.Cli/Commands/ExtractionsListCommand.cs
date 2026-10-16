using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core;
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
            Description = "Restrict history to one build: a 64-character Atlas build ID or a unique short-ID prefix. Unknown or malformed build IDs are an error."
        };
        var includeFailedOption = new Option<bool>("--include-failed")
        {
            Description = "Also list failed, canceled, abandoned, and candidate attempts."
        };
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command(
            "list",
            CliExamples.With("List validated extractions newest first, optionally with failed attempts.", "s1atlas extractions list --include-failed"));
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
                    var requestedBuild = parseResult.GetValue(buildOption);
                    var builds = repository.ListBuildsAsync(cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    string? buildFilter;
                    if (string.IsNullOrWhiteSpace(requestedBuild))
                    {
                        buildFilter = null;
                    }
                    else
                    {
                        buildFilter = requestedBuild;
                        var match = ShortId.MatchPrefix(
                            builds.Select(build => build.BuildId),
                            requestedBuild);

                        string DescribeBuild(string candidateId)
                        {
                            var candidate = builds.First(entry => string.Equals(
                                entry.BuildId, candidateId, StringComparison.Ordinal));
                            return $"{ShortId.Display(candidateId)} (first seen {candidate.FirstSeenAtUtc:O})";
                        }

                        if (match.Kind == ShortIdMatchKind.Resolved && match.Id is not null)
                        {
                            buildFilter = match.Id;
                        }
                        else if (match.Kind == ShortIdMatchKind.Ambiguous)
                        {
                            return commandOutput.Failure(
                                1,
                                "AmbiguousBuildPrefix",
                                $"The build prefix '{requestedBuild}' matches {match.TotalCount} builds; " +
                                $"re-run with a full build ID or one of these short IDs: {ShortId.FormatMatchList(match, DescribeBuild)}.");
                        }
                        else
                        {
                            return commandOutput.Failure(
                                1,
                                "BuildNotFound",
                                $"Build '{Truncate(requestedBuild)}' not found.");
                        }
                    }

                    var items = historyService.ListAsync(
                            buildFilter,
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

    private static string Truncate(string id) =>
        id.Length > 16 ? id[..8] + "..." + id[^8..] : id;

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
