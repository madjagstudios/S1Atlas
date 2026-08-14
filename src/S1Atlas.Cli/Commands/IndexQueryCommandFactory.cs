using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Cli.Commands;

internal static class IndexQueryCommandFactory
{
    public static Command Create(
        string name,
        IndexQueryService service,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        Func<string, IndexQueryOptions, CancellationToken, Task<IndexQueryOutput>> execute)
    {
        var queryArgument = new Argument<string>("query") { Description = "A symbol, method, or type query." };
        var codebaseOption = new Option<string>("--codebase") { Description = "schedule-i, s1api, or s1mapi." };
        var channelOption = new Option<string>("--channel") { Description = "installed, release, preview, or all." };
        var limitOption = new Option<int>("--limit")
        {
            Description = "Maximum number of query results to return.",
            DefaultValueFactory = _ => 50
        };
        var jsonOption = CommandOutput.CreateJsonOption();
        var command = new Command(name, "Query the normalized code index.");
        command.Arguments.Add(queryArgument);
        command.Options.Add(codebaseOption);
        command.Options.Add(channelOption);
        command.Options.Add(limitOption);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(name, parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(
                () =>
                {
                    var limit = parseResult.GetValue(limitOption);
                    if (limit <= 0)
                        return commandOutput.Failure(
                            1,
                            "InvalidLimit",
                            "--limit must be greater than zero.");

                    repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
                    var options = ParseOptions(
                        parseResult.GetValue(codebaseOption),
                        parseResult.GetValue(channelOption),
                        limit);
                    var data = execute(parseResult.GetValue(queryArgument)!, options, cancellationToken).GetAwaiter().GetResult();
                    if (data.Resolution is { Status: SymbolResolutionStatus.Ambiguous } ambiguous)
                    {
                        return commandOutput.Failure(
                            1,
                            "AmbiguousSymbol",
                            "The symbol selector matched multiple candidates. Use an exact symbol ID or signature.",
                            new IndexQueryFailureData(ambiguous.Candidates));
                    }

                    if (data.Resolution is { Status: SymbolResolutionStatus.NoCompletedIndex })
                    {
                        return commandOutput.Failure(
                            1,
                            "NoCompletedIndex",
                            "No completed index exists for the requested codebase and channel.",
                            new IndexQueryFailureData([]));
                    }

                    if (data.Resolution is { Status: SymbolResolutionStatus.NotFound })
                    {
                        return commandOutput.Failure(
                            1,
                            "SymbolNotFound",
                            "No indexed symbol matched the selector.",
                            new IndexQueryFailureData([]));
                    }

                    return commandOutput.Success(data, writer => WriteHuman(data, writer));
                },
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    private static void WriteHuman(IndexQueryOutput data, TextWriter writer)
    {
        if (data.TotalCount is int totalCount && data.ReturnedCount is int returnedCount)
            writer.WriteLine($"Found {totalCount} matches. Showing {returnedCount}.");

        foreach (var symbol in data.Symbols)
            writer.WriteLine($"{symbol.Channel} | {symbol.Kind} | {symbol.QualifiedName} | {symbol.Signature} | {symbol.SymbolId}");

        foreach (var relationship in data.Relationships)
        {
            writer.WriteLine(
                $"{relationship.RelationshipId} | {relationship.Kind} | {relationship.Direction} | " +
                $"{FormatEndpoint(relationship.Source)} -> {FormatEndpoint(relationship.Target)} | evidence: {relationship.Evidence}");
        }

        if (!string.IsNullOrWhiteSpace(data.CompletenessNotice))
            writer.WriteLine($"Notice: {data.CompletenessNotice}");

        foreach (var source in data.Sources)
            writer.WriteLine($"{source.RelativePath} | {source.Provenance}");
    }

    private static string FormatEndpoint(RelationshipEndpointQueryResult endpoint)
    {
        if (endpoint.Resolved)
        {
            var readable = endpoint.QualifiedName ?? endpoint.Signature ?? endpoint.SymbolId ?? "<unknown>";
            var id = endpoint.SymbolId is null ? string.Empty : $" [{endpoint.SymbolId}]";
            var signature = endpoint.Signature is null || string.Equals(endpoint.Signature, readable, StringComparison.Ordinal)
                ? string.Empty
                : $" | {endpoint.Signature}";
            return readable + id + signature;
        }

        var raw = endpoint.RawText ?? "<unresolved>";
        return endpoint.SymbolId is null
            ? $"unresolved: {raw}"
            : $"unresolved: {raw} [{endpoint.SymbolId}]";
    }

    public static IndexQueryOptions ParseOptions(string? codebase, string? channel, int limit = 50)
    {
        var parsedCodebase = (codebase ?? "schedule-i").ToLowerInvariant() switch
        {
            "schedule-i" => CodebaseKind.ScheduleI,
            "s1api" => CodebaseKind.S1Api,
            "s1mapi" => CodebaseKind.S1MApi,
            _ => throw new ArgumentException("Codebase must be schedule-i, s1api, or s1mapi.", nameof(codebase))
        };
        var parsedChannel = (channel ?? "installed").ToLowerInvariant();
        if (parsedChannel == "all") return new IndexQueryOptions(parsedCodebase, null, true, limit);
        return new IndexQueryOptions(parsedCodebase, parsedChannel switch
        {
            "installed" => CodeChannel.Installed,
            "release" => CodeChannel.Release,
            "preview" => CodeChannel.Preview,
            _ => throw new ArgumentException("Channel must be installed, release, preview, or all.", nameof(channel))
        }, false, limit);
    }
}
