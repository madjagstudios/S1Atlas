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
                    return commandOutput.Success(data, writer =>
                    {
                        foreach (var symbol in data.Symbols)
                            writer.WriteLine($"{symbol.Channel} | {symbol.Kind} | {symbol.QualifiedName} | {symbol.Signature} | {symbol.SymbolId}");
                        foreach (var relationship in data.Relationships)
                            writer.WriteLine($"{relationship.Kind} | {relationship.Source.SymbolId} -> {relationship.Target.SymbolId ?? relationship.Target.RawText}");
                        foreach (var source in data.Sources)
                            writer.WriteLine($"{source.RelativePath} | {source.Provenance}");
                    });
                },
                commandOutput,
                cancellationToken);
        });
        return command;
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
