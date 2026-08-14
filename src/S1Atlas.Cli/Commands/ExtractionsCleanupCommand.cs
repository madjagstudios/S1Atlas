using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Extraction;
using S1Atlas.Extraction.Cleanup;

namespace S1Atlas.Cli.Commands;

internal static class ExtractionsCleanupCommand
{
    public static Command Create(
        ExtractionCleanupService cleanupService,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cleanupService);

        var olderThanOption = new Option<string?>("--older-than")
        {
            Description =
                "Retention window as a positive lower-case integer plus m, h, or d " +
                "(default 30d, maximum 36500d). Items are eligible only when strictly older."
        };
        var applyOption = new Option<bool>("--apply")
        {
            Description =
                "Delete the eligible items. Without this flag cleanup only previews."
        };
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command(
            "cleanup",
            "Preview or delete only proven Atlas-owned, age-eligible failure and " +
            "staging data. Preview is the default.");
        command.Options.Add(olderThanOption);
        command.Options.Add(applyOption);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(
                "extractions cleanup",
                parseResult.GetValue(jsonOption),
                output,
                error);
            var durationText = parseResult.GetValue(olderThanOption);
            var apply = parseResult.GetValue(applyOption);
            return CommandExecution.Run(
                () => Execute(cleanupService, commandOutput, durationText, apply, cancellationToken),
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    private static int Execute(
        ExtractionCleanupService cleanupService,
        CommandOutput commandOutput,
        string? durationText,
        bool apply,
        CancellationToken cancellationToken)
    {
        TimeSpan olderThan;
        try
        {
            olderThan = durationText is null
                ? CleanupDurationParser.Default
                : CleanupDurationParser.Parse(durationText);
        }
        catch (FormatException exception)
        {
            return commandOutput.Failure(1, "InvalidCleanupDuration", exception.Message);
        }

        var normalized = durationText ?? CleanupDurationParser.DefaultText;
        try
        {
            if (apply)
            {
                var result = cleanupService.ApplyAsync(olderThan, cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                var data = ToApplyOutput(result, normalized);
                var exitCode = result.HasOperationalProblems ? 1 : 0;
                return commandOutput.Complete(exitCode, data, writer => WriteApplyHuman(writer, data));
            }

            var plan = cleanupService.PreviewAsync(olderThan, cancellationToken)
                .GetAwaiter()
                .GetResult();
            var previewData = ToPreviewOutput(plan, normalized);
            return commandOutput.Success(previewData, writer => WritePreviewHuman(writer, previewData));
        }
        catch (ExtractionCleanupActiveException exception)
        {
            return commandOutput.Failure(1, "ExtractionActive", exception.Message);
        }
    }

    private static ExtractionCleanupOutput ToPreviewOutput(
        ExtractionCleanupPlan plan,
        string olderThan) =>
        new(
            Applied: false,
            olderThan,
            plan.CutoffUtc.ToString("O"),
            plan.EligibleFileCount,
            plan.EligibleByteCount,
            plan.EligibleItems.Select(ToItemOutput).ToArray(),
            plan.BlockedItems.Select(ToBlockedOutput).ToArray(),
            DeletedItems: [],
            Failures: []);

    private static ExtractionCleanupOutput ToApplyOutput(
        ExtractionCleanupResult result,
        string olderThan) =>
        new(
            Applied: true,
            olderThan,
            result.Plan.CutoffUtc.ToString("O"),
            result.Plan.EligibleFileCount,
            result.Plan.EligibleByteCount,
            result.Plan.EligibleItems.Select(ToItemOutput).ToArray(),
            result.Plan.BlockedItems.Select(ToBlockedOutput).ToArray(),
            result.DeletedItems.Select(ToItemOutput).ToArray(),
            result.Failures.Select(ToFailureOutput).ToArray());

    private static ExtractionCleanupItemOutput ToItemOutput(ExtractionCleanupItem item) =>
        new(
            item.Kind.ToString(),
            item.Id,
            item.BuildId,
            item.AttemptId,
            item.DisplayPath,
            item.ControllingTimestampUtc.ToString("O"),
            item.FileCount,
            item.ByteCount);

    private static ExtractionCleanupBlockedOutput ToBlockedOutput(
        ExtractionCleanupBlockedItem item) =>
        new(item.Kind.ToString(), item.Id, item.DisplayPath, item.Code, item.Message);

    private static ExtractionCleanupFailureOutput ToFailureOutput(
        ExtractionCleanupFailure failure) =>
        new(failure.Kind.ToString(), failure.Id, failure.Code, failure.Message);

    private static void WritePreviewHuman(TextWriter writer, ExtractionCleanupOutput data)
    {
        writer.WriteLine($"Cleanup preview (older than {data.OlderThan}).");
        writer.WriteLine($"  Cutoff (UTC): {data.CutoffUtc}");
        WriteCategoryCounts(writer, data.EligibleItems);
        writer.WriteLine(
            $"  Eligible:     {data.EligibleItems.Count} item(s), " +
            $"{data.EligibleFileCount} file(s), {data.EligibleByteCount} byte(s).");
        writer.WriteLine($"  Blocked:      {data.BlockedItems.Count} item(s).");
        WriteBlocked(writer, data.BlockedItems);
        writer.WriteLine("No files were deleted.");
        writer.WriteLine("Re-run with --apply to delete the eligible items.");
    }

    private static void WriteApplyHuman(TextWriter writer, ExtractionCleanupOutput data)
    {
        var deletedBytes = data.DeletedItems.Sum(item => item.ByteCount);
        var deletedFiles = data.DeletedItems.Sum(item => item.FileCount);
        writer.WriteLine($"Cleanup applied (older than {data.OlderThan}).");
        writer.WriteLine($"  Cutoff (UTC): {data.CutoffUtc}");
        writer.WriteLine(
            $"  Deleted:      {data.DeletedItems.Count} item(s), " +
            $"{deletedFiles} file(s), {deletedBytes} byte(s).");
        writer.WriteLine($"  Blocked:      {data.BlockedItems.Count} item(s).");
        WriteBlocked(writer, data.BlockedItems);
        if (data.Failures.Count > 0)
        {
            writer.WriteLine($"  Failures:     {data.Failures.Count} item(s).");
            foreach (var failure in data.Failures)
            {
                writer.WriteLine($"    - {failure.Kind} {failure.Id}: {failure.Code}");
            }
        }

        if (data.BlockedItems.Count > 0 || data.Failures.Count > 0)
        {
            writer.WriteLine(
                "Some items were preserved; re-run cleanup after resolving them.");
        }
    }

    private static void WriteCategoryCounts(
        TextWriter writer,
        IReadOnlyList<ExtractionCleanupItemOutput> items)
    {
        foreach (var group in items
            .GroupBy(item => item.Kind)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            writer.WriteLine($"    {group.Key}: {group.Count()}");
        }
    }

    private static void WriteBlocked(
        TextWriter writer,
        IReadOnlyList<ExtractionCleanupBlockedOutput> blocked)
    {
        foreach (var item in blocked)
        {
            writer.WriteLine($"    - {item.Kind} {item.Id}: {item.Code}");
        }
    }
}
