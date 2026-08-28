using System.CommandLine;
using S1Atlas.Application.Authority;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Cli.Commands;

internal static class FieldRefsCommand
{
    public static Command Create(
        IndexQueryService service,
        FederatedIndexQueryService federatedService,
        ReferenceModQueryService referenceService,
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var queryArgument = new Argument<string>("query") { Description = "A field selector." };
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
        var readersOption = new Option<bool>("--readers") { Description = "Return only field reads." };
        var writersOption = new Option<bool>("--writers") { Description = "Return only field writes." };
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command("fieldrefs", "Find field read/write relationships for one resolved symbol.");
        command.Arguments.Add(queryArgument);
        command.Options.Add(codebaseOption);
        command.Options.Add(channelOption);
        command.Options.Add(buildOption);
        command.Options.Add(limitOption);
        command.Options.Add(scopeOption);
        command.Options.Add(collectionOption);
        command.Options.Add(readersOption);
        command.Options.Add(writersOption);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("fieldrefs", parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(
                () =>
                {
                    var limit = parseResult.GetValue(limitOption);
                    if (limit <= 0)
                        return commandOutput.Failure(1, "InvalidLimit", "--limit must be greater than zero.");

                    if (parseResult.GetValue(readersOption) && parseResult.GetValue(writersOption))
                    {
                        return commandOutput.Failure(
                            1,
                            "InvalidOptionCombination",
                            "--readers and --writers are mutually exclusive.");
                    }

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

                    var filter = parseResult.GetValue(readersOption)
                        ? FieldReferenceFilter.Readers
                        : parseResult.GetValue(writersOption)
                            ? FieldReferenceFilter.Writers
                            : FieldReferenceFilter.All;

                    var result = authority.Run is not null
                        ? service.FieldReferencesInIndexAsync(
                            authority.Run,
                            CodebaseKind.ScheduleI,
                            CodeChannel.Installed,
                            parseResult.GetValue(queryArgument)!,
                            limit,
                            filter,
                            cancellationToken).GetAwaiter().GetResult()
                        : options.Scope == IndexQueryScope.Game
                            ? service.FieldReferencesAsync(
                                parseResult.GetValue(queryArgument)!,
                                options,
                                filter,
                                cancellationToken).GetAwaiter().GetResult()
                            : federatedService.FieldReferencesAsync(
                                parseResult.GetValue(queryArgument)!,
                                options,
                                filter,
                                cancellationToken,
                                authority.ReferenceIndexId).GetAwaiter().GetResult();
                    return IndexQueryCommandFactory.Complete(
                        commandOutput,
                        IndexQueryCommandFactory.ToOutput(result));
                },
                commandOutput,
                cancellationToken);
        });
        return command;
    }
}
