using System.CommandLine;
using S1Atlas.Application.Authority;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Cli.Commands;

internal static class CallersCommand
{
    public static Command Create(
        IndexQueryService service,
        FederatedIndexQueryService federatedService,
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var queryArgument = new Argument<string>("query") { Description = "A symbol, method, or type query." };
        var codebaseOption = new Option<string>("--codebase") { Description = "schedule-i, s1api, or s1mapi." };
        var channelOption = new Option<string>("--channel") { Description = "installed, release, preview, or all." };
        var buildOption = new Option<string?>("--build") { Description = "Select a Schedule I Installed build ID." };
        var limitOption = new Option<int>("--limit")
        {
            Description = "Maximum number of query results to return.",
            DefaultValueFactory = _ => 50
        };
        var scopeOption = new Option<string?>("--scope") { Description = "game, reference, or all." };
        var collectionOption = new Option<string?>("--collection") { Description = "A named or indexed reference collection." };
        var exactOption = new Option<bool>("--exact") { Description = "Return only exact (statically bound) callers; omit may-dispatch callers." };
        var includeGeneratedOption = IndexQueryCommandFactory.CreateIncludeGeneratedOption();
        var includeDelegatesOption = IndexQueryCommandFactory.CreateIncludeDelegatesOption();
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command(
            "callers",
            "Find callers of one resolved symbol, including may-dispatch callers reached through overrides and interface implementations.");
        command.Arguments.Add(queryArgument);
        command.Options.Add(codebaseOption);
        command.Options.Add(channelOption);
        command.Options.Add(buildOption);
        command.Options.Add(limitOption);
        command.Options.Add(scopeOption);
        command.Options.Add(collectionOption);
        command.Options.Add(exactOption);
        command.Options.Add(includeGeneratedOption);
        command.Options.Add(includeDelegatesOption);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("callers", parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(
                () =>
                {
                    var limit = parseResult.GetValue(limitOption);
                    if (limit <= 0)
                        return commandOutput.Failure(1, "InvalidLimit", "--limit must be greater than zero.");

                    repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
                    IndexQueryOptions options;
                    try
                    {
                        options = IndexQueryCommandFactory.ParseOptions(
                            parseResult.GetValue(codebaseOption),
                            parseResult.GetValue(channelOption),
                            limit,
                            parseResult.GetValue(scopeOption),
                            parseResult.GetValue(collectionOption));
                    }
                    catch (ArgumentException exception)
                    {
                        return commandOutput.Failure(1, "InvalidOptionCombination", exception.Message);
                    }

                    var authority = IndexQueryCommandFactory.ResolveExecutionAuthority(
                        authorityResolver,
                        null,
                        options,
                        parseResult.GetValue(buildOption),
                        cancellationToken);
                    if (authority.ErrorCode is not null)
                        return commandOutput.Failure(1, authority.ErrorCode, authority.ErrorMessage!);

                    var exact = parseResult.GetValue(exactOption);
                    var includeGenerated = parseResult.GetValue(includeGeneratedOption);
                    var includeDelegates = parseResult.GetValue(includeDelegatesOption);
                    var result = authority.Run is not null
                        ? service.CallersInIndexAsync(
                            authority.Run,
                            CodebaseKind.ScheduleI,
                            CodeChannel.Installed,
                            parseResult.GetValue(queryArgument)!,
                            limit,
                            cancellationToken,
                            exact,
                            includeGenerated: includeGenerated,
                            includeDelegates: includeDelegates).GetAwaiter().GetResult()
                        : options.Scope == IndexQueryScope.Game
                            ? service.CallersAsync(
                                parseResult.GetValue(queryArgument)!,
                                options,
                                cancellationToken,
                                exact,
                                includeGenerated: includeGenerated,
                                includeDelegates: includeDelegates).GetAwaiter().GetResult()
                            : federatedService.CallersAsync(
                                parseResult.GetValue(queryArgument)!,
                                options,
                                cancellationToken,
                                exact,
                                includeGenerated: includeGenerated,
                                includeDelegates: includeDelegates).GetAwaiter().GetResult();
                    return IndexQueryCommandFactory.Complete(
                        commandOutput,
                        IndexQueryCommandFactory.ToOutput(result),
                        parseResult.GetValue(queryArgument)!);
                },
                commandOutput,
                cancellationToken);
        });
        return command;
    }
}
