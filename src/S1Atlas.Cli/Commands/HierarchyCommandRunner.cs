using System.CommandLine;
using S1Atlas.Application.Authority;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Cli.Commands;

internal static class HierarchyCommandRunner
{
    public static Command Create(
        string name,
        string description,
        bool includeDepth,
        bool includeOffset,
        IndexQueryService service,
        FederatedIndexQueryService federatedService,
        ReferenceModQueryService referenceService,
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        Func<IndexQueryService, IndexRunRecord, string, int, int, int, CancellationToken, Task<HierarchyQueryResult>> executeInIndex,
        Func<IndexQueryService, string, IndexQueryOptions, int, int, CancellationToken, Task<HierarchyQueryResult>> execute,
        Func<FederatedIndexQueryService, string, IndexQueryOptions, int, int, CancellationToken, string?, Task<HierarchyQueryResult>> executeFederated)
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
        var depthOption = new Option<int>("--depth")
        {
            Description = "Maximum hierarchy depth to traverse.",
            DefaultValueFactory = _ => 10
        };
        var offsetOption = new Option<int>("--offset")
        {
            Description = "Number of hierarchy results to skip.",
            DefaultValueFactory = _ => 0
        };
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command(name, description);
        command.Arguments.Add(queryArgument);
        command.Options.Add(codebaseOption);
        command.Options.Add(channelOption);
        command.Options.Add(buildOption);
        command.Options.Add(limitOption);
        command.Options.Add(scopeOption);
        command.Options.Add(collectionOption);
        if (includeDepth)
            command.Options.Add(depthOption);
        if (includeOffset)
            command.Options.Add(offsetOption);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(name, parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(
                () =>
                {
                    var limit = parseResult.GetValue(limitOption);
                    if (limit <= 0)
                        return commandOutput.Failure(1, "InvalidLimit", "--limit must be greater than zero.");
                    var depth = includeDepth ? parseResult.GetValue(depthOption) : 10;
                    if (depth <= 0)
                        return commandOutput.Failure(1, "InvalidDepth", "--depth must be greater than zero.");
                    var offset = includeOffset ? parseResult.GetValue(offsetOption) : 0;
                    if (offset < 0)
                        return commandOutput.Failure(1, "InvalidOffset", "--offset must not be negative.");

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
                        referenceService,
                        options,
                        parseResult.GetValue(buildOption),
                        cancellationToken);
                    if (authority.ErrorCode is not null)
                        return commandOutput.Failure(1, authority.ErrorCode, authority.ErrorMessage!);

                    var query = parseResult.GetValue(queryArgument)!;
                    HierarchyQueryResult result;
                    if (authority.Run is not null)
                    {
                        result = executeInIndex(service, authority.Run, query, limit, depth, offset, cancellationToken).GetAwaiter().GetResult();
                    }
                    else if (options.Scope == IndexQueryScope.Game)
                    {
                        result = execute(service, query, options, depth, offset, cancellationToken).GetAwaiter().GetResult();
                    }
                    else
                    {
                        result = executeFederated(federatedService, query, options, depth, offset, cancellationToken, authority.ReferenceIndexId).GetAwaiter().GetResult();
                    }

                    return IndexQueryCommandFactory.Complete(commandOutput, IndexQueryCommandFactory.ToOutput(result));
                },
                commandOutput,
                cancellationToken);
        });
        return command;
    }
}
