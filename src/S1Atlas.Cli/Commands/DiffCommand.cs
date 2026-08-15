using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Diff;

namespace S1Atlas.Cli.Commands;

internal static class DiffCommand
{
    public static Command Create(
        BuildDiffService diffService,
        IIndexRepository indexRepository,
        IExtractionRepository extractionRepository,
        IValidatedExtractionRepository validatedExtractionRepository,
        IAtlasRepository atlasRepository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var idAArgument = new Argument<string>("id-a") { Description = "Build ID for the baseline (before)." };
        var idBArgument = new Argument<string>("id-b") { Description = "Build ID for the target (after)." };
        var codebaseOption = new Option<string>("--codebase") { Description = "schedule-i, s1api, or s1mapi." };
        var channelOption = new Option<string>("--channel") { Description = "installed (default). Release and preview are not supported." };
        var kindOption = new Option<string>("--kind") { Description = "Filter by symbol kind: type, method, constructor, field, property, event." };
        var limitOption = new Option<int>("--limit") { Description = "Maximum changed symbols to list.", DefaultValueFactory = _ => 50 };
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command("diff", "Compare two indexed builds and report per-symbol changes.");
        command.Arguments.Add(idAArgument);
        command.Arguments.Add(idBArgument);
        command.Options.Add(codebaseOption);
        command.Options.Add(channelOption);
        command.Options.Add(kindOption);
        command.Options.Add(limitOption);
        command.Options.Add(jsonOption);

        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("diff", parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(
                () =>
                {
                    var idA = parseResult.GetValue(idAArgument)!;
                    var idB = parseResult.GetValue(idBArgument)!;
                    var limit = parseResult.GetValue(limitOption);
                    var kindFilter = parseResult.GetValue(kindOption);

                    if (limit <= 0)
                        return commandOutput.Failure(1, "InvalidLimit", "--limit must be greater than zero.");

                    var codebase = IndexQueryCommandFactory.ParseOptions(
                        parseResult.GetValue(codebaseOption), null).Codebase;

                    var channelRaw = (parseResult.GetValue(channelOption) ?? "installed").ToLowerInvariant();
                    if (channelRaw is "release" or "preview")
                        return commandOutput.Failure(1, "UnsupportedChannel",
                            "Build diffing requires installed-channel indexes. Release and preview channels are not supported in V1.");

                    if (channelRaw != "installed")
                        return commandOutput.Failure(1, "InvalidChannel",
                            "Channel must be installed. Release and preview are not supported for diffing.");

                    var channel = CodeChannel.Installed;

                    atlasRepository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();

                    var indexIdA = ResolveIndexId(
                        idA, codebase, channel, indexRepository, extractionRepository, validatedExtractionRepository, cancellationToken);
                    var indexIdB = ResolveIndexId(
                        idB, codebase, channel, indexRepository, extractionRepository, validatedExtractionRepository, cancellationToken);

                    if (string.Equals(indexIdA, indexIdB, StringComparison.Ordinal))
                        return commandOutput.Failure(1, "SameIndex",
                            "Both build identifiers resolve to the same index. Provide two different builds.");

                    var result = diffService.DiffAsync(indexIdA, indexIdB, codebase.ToString(), channel.ToString(), kindFilter, cancellationToken)
                        .GetAwaiter().GetResult();

                    var totalChanged = result.Changes.Count;
                    var limitedChanges = limit < result.Changes.Count
                        ? result.Changes.Take(limit).ToArray()
                        : result.Changes;

                    var data = new DiffOutputData(
                        idA, idB,
                        result.IndexIdA, result.IndexIdB,
                        result.Codebase, result.Channel,
                        result.TotalSymbolsA, result.TotalSymbolsB,
                        new DiffOutputCounts(
                            result.CountsByClassification.GetValueOrDefault(DiffClassification.Added),
                            result.CountsByClassification.GetValueOrDefault(DiffClassification.Removed),
                            result.CountsByClassification.GetValueOrDefault(DiffClassification.MethodBodyChanged),
                            result.CountsByClassification.GetValueOrDefault(DiffClassification.RelationshipsChanged),
                            result.CountsByClassification.GetValueOrDefault(DiffClassification.Unchanged)),
                        totalChanged,
                        limitedChanges.Count,
                        limitedChanges.Select(c => new DiffOutputChange(
                            c.CanonicalKey, c.QualifiedName, c.Kind,
                            c.Classification.ToString(), c.SignatureBefore, c.SignatureAfter)).ToArray());

                    return commandOutput.Success(data, writer => WriteHuman(writer, data, idA, idB));
                },
                commandOutput,
                cancellationToken);
        });

        return command;
    }

    private static string ResolveIndexId(
        string buildId,
        CodebaseKind codebase,
        CodeChannel channel,
        IIndexRepository indexRepository,
        IExtractionRepository extractionRepository,
        IValidatedExtractionRepository validatedExtractionRepository,
        CancellationToken ct)
    {
        var build = extractionRepository.GetBuildAsync(buildId, ct).GetAwaiter().GetResult();
        if (build is null)
            throw new InvalidOperationException($"Build '{Truncate(buildId)}' not found.");

        IndexRunRecord? index;
        if (codebase == CodebaseKind.ScheduleI)
        {
            var preferred = validatedExtractionRepository.GetPreferredExtractionAsync(buildId, ct)
                .GetAwaiter().GetResult();
            if (preferred is null)
                throw new InvalidOperationException($"No preferred validated extraction for build '{Truncate(buildId)}'.");

            index = indexRepository.GetLatestCompletedIndexBySourceIdentityAsync(
                codebase, channel, preferred.ExtractionId, ct).GetAwaiter().GetResult();
        }
        else
        {
            index = indexRepository.GetLatestCompletedIndexForBuildAsync(
                codebase, channel, buildId, ct).GetAwaiter().GetResult();
        }

        if (index is null)
            throw new InvalidOperationException($"No completed index for build '{Truncate(buildId)}' ({codebase}/{channel}).");

        return index.IndexId;
    }

    private static void WriteHuman(TextWriter writer, DiffOutputData data, string idA, string idB)
    {
        writer.WriteLine($"Build diff: {Truncate(idA)} (before) → {Truncate(idB)} (after)");
        writer.WriteLine($"Codebase: {data.Codebase}  Channel: {data.Channel}");
        writer.WriteLine();
        writer.WriteLine($"  Added:                {data.Counts.Added,6:N0}");
        writer.WriteLine($"  Removed:              {data.Counts.Removed,6:N0}");
        writer.WriteLine($"  Method body changed:  {data.Counts.MethodBodyChanged,6:N0}");
        writer.WriteLine($"  Relationships changed:{data.Counts.RelationshipsChanged,6:N0}");
        writer.WriteLine($"  Unchanged:            {data.Counts.Unchanged,6:N0}");
        writer.WriteLine($"  ─────────────────────────");
        writer.WriteLine($"  Total (before):       {data.TotalSymbolsA,6:N0}");
        writer.WriteLine($"  Total (after):        {data.TotalSymbolsB,6:N0}");

        if (data.TotalChanged > 0)
        {
            writer.WriteLine();
            writer.WriteLine($"Changed symbols ({data.ReturnedCount} of {data.TotalChanged}):");
            writer.WriteLine();
            foreach (var change in data.Changes)
            {
                var tag = change.Classification switch
                {
                    "Added" => "[Added]     ",
                    "Removed" => "[Removed]   ",
                    "MethodBodyChanged" => "[BodyChange]",
                    "RelationshipsChanged" => "[RelChange] ",
                    _ => "[?]         "
                };
                writer.WriteLine($"  {tag} {change.Kind,-12} {change.QualifiedName}");
            }
        }
    }

    private static string Truncate(string id) =>
        id.Length > 16 ? id[..8] + "..." + id[^8..] : id;
}
