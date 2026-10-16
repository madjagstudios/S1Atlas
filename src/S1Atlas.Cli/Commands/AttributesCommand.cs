using System.CommandLine;
using S1Atlas.Application.Authority;
using S1Atlas.Application.Readiness;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Cli.Commands;

internal static class AttributesCommand
{
    public static Command Create(
        IndexQueryService service,
        ApiIndexQueryService apiService,
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var attributeArgument = new Argument<string>("attribute") { Description = "Full attribute type name, or the suffix after the last '.' (case-insensitive)." };
        var argOption = new Option<string[]>("--arg")
        {
            Description = "Argument filter: name=value matches a named argument, or a bare value matches any fixed argument. Repeat for multiple filters (all must match)."
        };
        var codebaseOption = new Option<string>("--codebase") { Description = "schedule-i, s1api, or s1mapi." };
        var channelOption = new Option<string>("--channel") { Description = "installed, release, preview, or all (every channel of the codebase)." };
        var buildOption = new Option<string?>("--build") { Description = IndexQueryCommandFactory.BuildOptionDescription };
        var limitOption = new Option<int>("--limit")
        {
            Description = "Maximum number of query results to return.",
            DefaultValueFactory = _ => 50
        };
        var includeGeneratedOption = IndexQueryCommandFactory.CreateIncludeGeneratedOption();
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command("attributes", CliExamples.With("Find indexed symbols carrying a custom attribute.", "s1atlas attributes \"FishNet.Object.ServerRpcAttribute\" --arg RequireOwnership=false"));
        command.Arguments.Add(attributeArgument);
        command.Options.Add(argOption);
        command.Options.Add(codebaseOption);
        command.Options.Add(channelOption);
        command.Options.Add(buildOption);
        command.Options.Add(limitOption);
        command.Options.Add(includeGeneratedOption);
        command.Options.Add(jsonOption);
        IndexQueryCommandFactory.AddOptionsValidator(
            command,
            "attributes",
            codebaseOption,
            channelOption,
            limitOption,
            scopeOption: null,
            collectionOption: null,
            buildOption,
            hasReferenceService: false,
            defersToAction: result => CliValidation.GetValue(result, limitOption) <= 0);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("attributes", parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(
                () =>
                {
                    var limit = parseResult.GetValue(limitOption);
                    if (limit <= 0)
                        return commandOutput.Failure(1, "InvalidLimit", "--limit must be greater than zero.");

                    repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
                    var options = IndexQueryCommandFactory.ParseOptions(
                        parseResult.GetValue(codebaseOption),
                        parseResult.GetValue(channelOption),
                        limit);

                    var attribute = parseResult.GetValue(attributeArgument)!;
                    if (string.IsNullOrWhiteSpace(attribute))
                        return commandOutput.Failure(1, "InvalidAttribute", "The attribute type name must not be blank.");
                    IReadOnlyList<string> args = parseResult.GetValue(argOption) ?? [];
                    var includeGenerated = parseResult.GetValue(includeGeneratedOption);

                    if (options.Codebase is CodebaseKind.S1Api or CodebaseKind.S1MApi)
                    {
                        var channels = options.AllChannels
                            ? (IReadOnlyList<CodeChannel>)[CodeChannel.Installed, CodeChannel.Release, CodeChannel.Preview]
                            : [options.Channel ?? CodeChannel.Installed];
                        var catalog = apiService.ListAsync(null, cancellationToken).GetAwaiter().GetResult();
                        var selections = channels
                            .Select(channel => catalog.Selections.Single(selection =>
                                selection.Codebase == options.Codebase && selection.Channel == channel))
                            .ToArray();
                        var current = selections
                            .Where(selection => selection.Availability == ApiIndexAvailability.Current)
                            .ToArray();
                        if (current.Length == 0)
                            return ApiSelectionFailure(commandOutput, selections[0]);
                        var totalCount = 0;
                        var candidates = new List<AttributeHitQueryResult>();
                        foreach (var selection in current)
                        {
                            var channelResult = apiService.FindByAttributeSelectedAsync(
                                selection,
                                attribute,
                                args,
                                limit,
                                cancellationToken,
                                includeGenerated).GetAwaiter().GetResult();
                            totalCount += channelResult.TotalCount;
                            candidates.AddRange(channelResult.Results);
                        }

                        var merged = candidates
                            .OrderBy(hit => hit.Symbol.QualifiedName, StringComparer.Ordinal)
                            .ThenBy(hit => hit.Symbol.Signature, StringComparer.Ordinal)
                            .ThenBy(hit => hit.Symbol.Channel, StringComparer.Ordinal)
                            .ThenBy(hit => hit.Symbol.SymbolId, StringComparer.Ordinal)
                            .Take(limit)
                            .ToArray();
                        return IndexQueryCommandFactory.Complete(
                            commandOutput,
                            IndexQueryCommandFactory.ToOutput(new AttributeSearchResult(totalCount, merged.Length, merged)),
                            attribute,
                            IndexQueryCommandFactory.ScopeIndexHint(options));
                    }

                    var authority = IndexQueryCommandFactory.ResolveExecutionAuthority(
                        authorityResolver,
                        referenceService: null,
                        options,
                        parseResult.GetValue(buildOption),
                        cancellationToken);
                    if (authority.ErrorCode is not null)
                        return commandOutput.Failure(1, authority.ErrorCode, authority.ErrorMessage!, hint: authority.BuildAuthority?.Hint);

                    var result = authority.Run is not null
                        ? service.FindByAttributeInIndexAsync(
                            authority.Run,
                            CodebaseKind.ScheduleI,
                            CodeChannel.Installed,
                            attribute,
                            args,
                            limit,
                            cancellationToken,
                            includeGenerated).GetAwaiter().GetResult()
                        : service.FindByAttributeAsync(
                            attribute,
                            args,
                            options,
                            cancellationToken,
                            includeGenerated).GetAwaiter().GetResult();
                    return IndexQueryCommandFactory.Complete(
                        commandOutput,
                        IndexQueryCommandFactory.ToOutput(result),
                        attribute,
                        IndexQueryCommandFactory.ScopeIndexHint(options));
                },
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    private static int ApiSelectionFailure(CommandOutput commandOutput, ApiIndexSelection selection)
    {
        ArgumentNullException.ThrowIfNull(commandOutput);
        ArgumentNullException.ThrowIfNull(selection);

        return selection.Availability switch
        {
            ApiIndexAvailability.Stale => commandOutput.Failure(
                1,
                "StaleApiIndex",
                selection.Message ?? "The installed API index does not match the selected Schedule I build."),
            ApiIndexAvailability.Unavailable when selection.IndexId is null && IsMissingApiIndex(selection) => commandOutput.Failure(
                1,
                "NoCompletedIndex",
                selection.Message ?? "No completed API index exists for the requested scope.",
                new IndexQueryFailureData([], [], null),
                hint: ReadinessFixCommands.HintForNoCompletedIndex(selection.Codebase, selection.Channel)),
            ApiIndexAvailability.Unavailable => commandOutput.Failure(
                1,
                "ApiIndexUnavailable",
                selection.Message ?? "The API index cannot be used with the selected authority."),
            ApiIndexAvailability.Ambiguous => commandOutput.Failure(
                1,
                "AmbiguousApiIndex",
                selection.Message ?? "The API index selection is ambiguous."),
            _ => throw new InvalidOperationException("A current API selection is not a failure.")
        };
    }

    private static bool IsMissingApiIndex(ApiIndexSelection selection) =>
        selection.Message?.StartsWith("No completed", StringComparison.Ordinal) == true;
}
