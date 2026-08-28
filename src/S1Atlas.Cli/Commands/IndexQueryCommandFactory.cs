using System.CommandLine;
using S1Atlas.Application.Authority;
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
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        Func<string, IndexQueryOptions, CancellationToken, Task<IndexQueryOutput>> execute,
        Func<string, IndexRunRecord, int, CancellationToken, Task<IndexQueryOutput>> executeInIndex,
        Func<IndexQueryOptions, string?>? validateOptions = null,
        bool includeScopeOptions = false,
        ReferenceModQueryService? referenceService = null,
        Func<string, IndexQueryOptions, string, CancellationToken, Task<IndexQueryOutput>>? executeWithReferenceIndex = null)
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
        var jsonOption = CommandOutput.CreateJsonOption();
        var scopeOption = new Option<string?>("--scope") { Description = "game, reference, or all." };
        var collectionOption = new Option<string?>("--collection") { Description = "A named or indexed reference collection." };
        var command = new Command(name, "Query the normalized code index.");
        command.Arguments.Add(queryArgument);
        command.Options.Add(codebaseOption);
        command.Options.Add(channelOption);
        command.Options.Add(buildOption);
        command.Options.Add(limitOption);
        if (includeScopeOptions)
        {
            command.Options.Add(scopeOption);
            command.Options.Add(collectionOption);
        }
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
                    IndexQueryOptions options;
                    try
                    {
                        options = ParseOptions(
                            parseResult.GetValue(codebaseOption),
                            parseResult.GetValue(channelOption),
                            limit,
                            includeScopeOptions ? parseResult.GetValue(scopeOption) : null,
                            includeScopeOptions ? parseResult.GetValue(collectionOption) : null);
                    }
                    catch (ArgumentException exception)
                    {
                        return commandOutput.Failure(1, "InvalidOptionCombination", exception.Message);
                    }
                    var optionError = validateOptions?.Invoke(options);
                    if (optionError is not null)
                        return commandOutput.Failure(1, "InvalidOptionCombination", optionError);
                    var buildId = parseResult.GetValue(buildOption);
                    var authority = ResolveExecutionAuthority(
                        authorityResolver,
                        referenceService,
                        options,
                        buildId,
                        cancellationToken);
                    if (authority.ErrorCode is not null)
                        return commandOutput.Failure(1, authority.ErrorCode, authority.ErrorMessage!);

                    IndexQueryOutput data;
                    if (authority.Run is not null)
                    {
                        data = executeInIndex(
                            parseResult.GetValue(queryArgument)!,
                            authority.Run,
                            limit,
                            cancellationToken).GetAwaiter().GetResult();
                    }
                    else
                    {
                        var query = parseResult.GetValue(queryArgument)!;
                        data = executeWithReferenceIndex is not null && authority.ReferenceIndexId is not null
                            ? executeWithReferenceIndex(query, options, authority.ReferenceIndexId, cancellationToken).GetAwaiter().GetResult()
                            : execute(query, options, cancellationToken).GetAwaiter().GetResult();
                    }
                    return Complete(commandOutput, data);
                },
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    internal static int Complete(CommandOutput commandOutput, IndexQueryOutput data)
    {
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
    }

    internal static IndexQueryOutput ToOutput(RelationshipQuerySetResult result) => new(
        [],
        result.Relationships,
        [],
        Resolution: result.Resolution,
        BodyRecoveryStatus: result.BodyRecoveryStatus,
        CallerCompletenessBoundedByTargetResolution: result.CallerCompletenessBoundedByTargetResolution,
        CompletenessNotice: result.CompletenessNotice);

    internal static IndexQueryOutput ToOutput(CallSiteQueryResult result) => new(
        [],
        result.Relationships,
        [],
        TotalCount: result.TotalCount,
        ReturnedCount: result.ReturnedCount,
        CompletenessNotice: result.CompletenessNotice);

    internal static IndexQueryOutput ToOutput(FieldReferenceQueryResult result) => new(
        [],
        result.Relationships,
        [],
        TotalCount: result.TotalCount,
        ReturnedCount: result.ReturnedCount,
        Resolution: result.Resolution,
        CompletenessNotice: result.CompletenessNotice);

    internal static void WriteHuman(IndexQueryOutput data, TextWriter writer)
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

        if (data.CallableSurface?.CallableSurface is { } callable)
        {
            writer.WriteLine($"Callable: {callable.Status} | {callable.Kind} | reflection required: {callable.RequiresReflection}");
            writer.WriteLine($"Game member: {callable.GameCanonicalKey}");
            writer.WriteLine($"Interop: {callable.InteropSignature ?? "unavailable"}");
            writer.WriteLine($"Evidence: {callable.Evidence}");
            writer.WriteLine($"Interop input trust: {callable.InteropInputTrust} (not cross-validated to the selected game build)");
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

    public static IndexQueryOptions ParseOptions(
        string? codebase,
        string? channel,
        int limit = 50,
        string? scope = null,
        string? collection = null)
    {
        var parsedCodebase = (codebase ?? "schedule-i").ToLowerInvariant() switch
        {
            "schedule-i" => CodebaseKind.ScheduleI,
            "s1api" => CodebaseKind.S1Api,
            "s1mapi" => CodebaseKind.S1MApi,
            _ => throw new ArgumentException("Codebase must be schedule-i, s1api, or s1mapi.", nameof(codebase))
        };
        var parsedChannel = (channel ?? "installed").ToLowerInvariant();
        var parsedScope = (scope ?? "game").ToLowerInvariant() switch
        {
            "game" => IndexQueryScope.Game,
            "reference" => IndexQueryScope.Reference,
            "all" => IndexQueryScope.All,
            _ => throw new ArgumentException("Scope must be game, reference, or all.", nameof(scope))
        };
        if (parsedScope == IndexQueryScope.Game && !string.IsNullOrWhiteSpace(collection))
            throw new ArgumentException("--collection is valid only for reference or all scope.", nameof(collection));
        if (parsedScope is IndexQueryScope.Reference or IndexQueryScope.All && string.IsNullOrWhiteSpace(collection))
            throw new ArgumentException("--scope reference and --scope all require --collection.", nameof(collection));
        if (parsedScope is IndexQueryScope.Reference or IndexQueryScope.All && parsedCodebase != CodebaseKind.ScheduleI)
            throw new ArgumentException("Reference scopes require --codebase schedule-i.", nameof(codebase));
        var effectiveCodebase = parsedScope == IndexQueryScope.Reference ? CodebaseKind.ReferenceMod : parsedCodebase;
        if (parsedChannel == "all")
        {
            if (parsedScope is IndexQueryScope.Reference or IndexQueryScope.All)
                throw new ArgumentException("Reference scopes require --channel installed.", nameof(channel));
            return new IndexQueryOptions(effectiveCodebase, null, true, limit, parsedScope);
        }
        return new IndexQueryOptions(effectiveCodebase, parsedChannel switch
        {
            "installed" => CodeChannel.Installed,
            "release" => CodeChannel.Release,
            "preview" => CodeChannel.Preview,
            _ => throw new ArgumentException("Channel must be installed, release, preview, or all.", nameof(channel))
        }, false, limit, parsedScope, collection?.Trim());
    }

    public static bool UsesInstalledScheduleIAuthority(IndexQueryOptions options) =>
        options.Codebase == CodebaseKind.ScheduleI &&
        options.Channel == CodeChannel.Installed &&
        options.Scope == IndexQueryScope.Game &&
        !options.AllChannels;

    internal static ExecutionAuthority ResolveExecutionAuthority(
        InstalledBuildAuthorityResolver authorityResolver,
        ReferenceModQueryService? referenceService,
        IndexQueryOptions options,
        string? buildId,
        CancellationToken cancellationToken)
    {
        if (UsesInstalledScheduleIAuthority(options))
        {
            var authority = authorityResolver.ResolveAsync(buildId, cancellationToken).GetAwaiter().GetResult();
            return authority.Status == InstalledBuildAuthorityStatus.Resolved
                ? new ExecutionAuthority(authority.IndexRun, null, null)
                : new ExecutionAuthority(null, authority.Status.ToString(), authority.Message ?? "The requested Schedule I build is unavailable.");
        }

        var allowsScopedAuthority = referenceService is not null &&
            options.Scope is IndexQueryScope.Reference or IndexQueryScope.All;
        if (!string.IsNullOrWhiteSpace(buildId) && !allowsScopedAuthority)
        {
            return new ExecutionAuthority(
                null,
                "InvalidOptionCombination",
                referenceService is null
                    ? "--build is only valid with --codebase schedule-i and --channel installed or all."
                    : "--build is only valid with --codebase schedule-i and --channel installed for game scope, or with --scope reference/all.");
        }

        if (!allowsScopedAuthority)
            return new ExecutionAuthority(null, null, null);

        var collection = referenceService!.GetCollectionAuthorityAsync(options.ReferenceCollection!, cancellationToken)
            .GetAwaiter()
            .GetResult();
        if (collection is null)
        {
            return new ExecutionAuthority(
                null,
                "NoCompletedIndex",
                "No completed reference collection exists for the requested scope.");
        }

        var baseAuthority = authorityResolver.ResolveAsync(collection.BuildId, cancellationToken).GetAwaiter().GetResult();
        if (baseAuthority.Status != InstalledBuildAuthorityStatus.Resolved)
        {
            return new ExecutionAuthority(
                null,
                baseAuthority.Status.ToString(),
                baseAuthority.Message ?? "The requested Schedule I build is unavailable.");
        }

        if (!string.IsNullOrWhiteSpace(buildId) &&
            !string.Equals(buildId, collection.BuildId, StringComparison.Ordinal))
        {
            return new ExecutionAuthority(
                null,
                "ReferenceCollectionBuildMismatch",
                "The requested build does not match the reference collection's recorded base build.");
        }

        if (!string.Equals(baseAuthority.IndexId, collection.BaseIndexId, StringComparison.Ordinal))
        {
            return new ExecutionAuthority(
                null,
                "ReferenceCollectionBaseIndexMismatch",
                "The reference collection's recorded base index is not the authoritative index for its build.");
        }

        return new ExecutionAuthority(null, null, null, collection.ReferenceIndexId);
    }

    internal readonly record struct ExecutionAuthority(
        IndexRunRecord? Run,
        string? ErrorCode,
        string? ErrorMessage,
        string? ReferenceIndexId = null);
}
