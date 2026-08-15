using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Diffing;

namespace S1Atlas.Cli.Commands;

internal static class DiffCommand
{
    public static Command Create(
        IndexDiffService service,
        Func<CancellationToken, Task> initialize,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var codebaseOption = new Option<string?>("--codebase") { Description = "schedule-i, s1api, or s1mapi." };
        var fromOption = new Option<string?>("--from") { Description = "A build, snapshot, index, or channel selector." };
        var toOption = new Option<string?>("--to") { Description = "A build, snapshot, index, or channel selector." };
        var symbolOption = new Option<string?>("--symbol") { Description = "Show one exact symbol diff by ID, key, name, or signature." };
        var kindOption = new Option<string?>("--kind") { Description = "Filter by one change kind." };
        var allOption = new Option<bool>("--all") { Description = "Include unchanged symbols and standalone unavailable-body classifications." };
        var limitOption = new Option<int>("--limit")
        {
            Description = "Maximum number of changes to return.",
            DefaultValueFactory = _ => 50
        };
        var jsonOption = CommandOutput.CreateJsonOption();
        var command = new Command("diff", "Compare two completed code indexes without mutating indexed data.");
        command.Options.Add(codebaseOption);
        command.Options.Add(fromOption);
        command.Options.Add(toOption);
        command.Options.Add(symbolOption);
        command.Options.Add(kindOption);
        command.Options.Add(allOption);
        command.Options.Add(limitOption);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("diff", parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(
                () =>
                {
                    if (parseResult.GetValue(limitOption) <= 0)
                        return commandOutput.Failure(1, "InvalidLimit", "--limit must be greater than zero.");

                    if (!TryParseCodebase(parseResult.GetValue(codebaseOption), out var codebase))
                        return commandOutput.Failure(1, "InvalidCodebase", "Codebase must be schedule-i, s1api, or s1mapi.");

                    var fromSelector = parseResult.GetValue(fromOption);
                    var toSelector = parseResult.GetValue(toOption);
                    if (string.IsNullOrWhiteSpace(fromSelector) || string.IsNullOrWhiteSpace(toSelector))
                        return commandOutput.Failure(1, "MissingSelector", "diff requires both --from and --to selectors.");

                    if (!TryParseKind(parseResult.GetValue(kindOption), out var kind))
                        return commandOutput.Failure(1, "InvalidKind", "--kind must name a valid diff change kind.");

                    initialize(cancellationToken).GetAwaiter().GetResult();
                    var from = service.ResolveSelectorAsync(codebase, fromSelector, CodeChannel.Installed, cancellationToken).GetAwaiter().GetResult();
                    var to = service.ResolveSelectorAsync(codebase, toSelector, CodeChannel.Installed, cancellationToken).GetAwaiter().GetResult();
                    var result = service.CompareAsync(from.IndexId, to.IndexId, cancellationToken).GetAwaiter().GetResult();
                    var changes = result.Changes.AsEnumerable();
                    if (!parseResult.GetValue(allOption))
                        changes = changes.Where(change => change.IsMeaningfulChange);
                    if (kind is not null)
                        changes = changes.Where(change => change.Kinds.Contains(kind.Value));

                    var symbolSelector = parseResult.GetValue(symbolOption);
                    if (!string.IsNullOrWhiteSpace(symbolSelector))
                    {
                        var symbolMatches = changes.Where(change => Matches(change, symbolSelector)).ToArray();
                        if (symbolMatches.Length == 0)
                            return commandOutput.Failure(1, "SymbolNotFound", "No diff symbol matched the selector.", new DiffFailureData([]));
                        if (symbolMatches.Length > 1)
                            return commandOutput.Failure(1, "AmbiguousSymbol", "The diff symbol selector matched multiple changes.", new DiffFailureData(symbolMatches.Select(DiffOutputMapper.Map).ToArray()));
                        changes = symbolMatches;
                    }

                    var allChanges = changes.OrderBy(change => change.ComparisonKey, StringComparer.Ordinal).ThenBy(change => change.FromSymbolId, StringComparer.Ordinal).ToArray();
                    var returned = allChanges.Take(parseResult.GetValue(limitOption)).ToArray();
                    var data = DiffOutputMapper.Map(result, returned) with { TotalCount = allChanges.Length, ReturnedCount = returned.Length };
                    return commandOutput.Success(data, writer => WriteHuman(data, writer));
                },
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    private static bool TryParseCodebase(string? value, out CodebaseKind codebase)
    {
        codebase = (value ?? "schedule-i").ToLowerInvariant() switch
        {
            "schedule-i" => CodebaseKind.ScheduleI,
            "s1api" => CodebaseKind.S1Api,
            "s1mapi" => CodebaseKind.S1MApi,
            _ => default
        };
        return value is null || codebase is CodebaseKind.ScheduleI or CodebaseKind.S1Api or CodebaseKind.S1MApi;
    }

    private static bool TryParseKind(string? value, out SymbolChangeKind? kind)
    {
        if (value is null)
        {
            kind = null;
            return true;
        }
        if (Enum.TryParse<SymbolChangeKind>(value, true, out var parsed))
        {
            kind = parsed;
            return true;
        }
        kind = null;
        return false;
    }

    private static bool Matches(SymbolDiff change, string selector) =>
        string.Equals(change.FromSymbolId, selector, StringComparison.Ordinal) ||
        string.Equals(change.ToSymbolId, selector, StringComparison.Ordinal) ||
        string.Equals(change.ComparisonKey, selector, StringComparison.Ordinal) ||
        string.Equals(change.FromQualifiedName, selector, StringComparison.Ordinal) ||
        string.Equals(change.ToQualifiedName, selector, StringComparison.Ordinal) ||
        string.Equals(change.FromSignature, selector, StringComparison.Ordinal) ||
        string.Equals(change.ToSignature, selector, StringComparison.Ordinal);

    private static void WriteHuman(DiffOutput data, TextWriter writer)
    {
        writer.WriteLine($"From: {data.From.Codebase} {data.From.Channel} | {data.From.IndexId} | {data.From.SnapshotId}");
        writer.WriteLine($"To:   {data.To.Codebase} {data.To.Channel} | {data.To.IndexId} | {data.To.SnapshotId}");
        writer.WriteLine($"Found {data.TotalCount} changes. Showing {data.ReturnedCount}.");
        foreach (var change in data.Changes)
        {
            writer.WriteLine(
                $"{string.Join(",", change.Kinds)} | {change.ComparisonKey} | " +
                $"{change.FromSymbolId ?? "<none>"} -> {change.ToSymbolId ?? "<none>"}");
            foreach (var evidence in change.Evidence)
                writer.WriteLine($"  evidence: {evidence.Layer} | {evidence.From ?? "<none>"} -> {evidence.To ?? "<none>"}");
            foreach (var relationship in change.Relationships)
                writer.WriteLine($"  relationship: {relationship.Change} {relationship.Kind} | {relationship.Evidence}");
        }
    }
}
