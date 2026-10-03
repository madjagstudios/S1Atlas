using System.CommandLine;
using S1Atlas.Application.Authority;
using S1Atlas.Application.Readiness;
using S1Atlas.Cli.Output;
using S1Atlas.Core;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Cli.Commands;

internal static class IndexQueryCommandFactory
{
    internal const string QueryArgumentDescription =
        "A symbol selector: full symbol ID, unique short-ID prefix, canonical key, signature, qualified name, or fuzzy text.";
    internal const string BuildOptionDescription =
        "Select a Schedule I Installed build ID or unique short-ID prefix.";

    public static Command Create(
        string name,
        IndexQueryService service,
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        Func<string, IndexQueryOptions, CancellationToken, Task<IndexQueryOutput>>? execute = null,
        Func<string, IndexRunRecord, int, CancellationToken, Task<IndexQueryOutput>>? executeInIndex = null,
        Func<IndexQueryOptions, string?>? validateOptions = null,
        bool includeScopeOptions = false,
        ReferenceModQueryService? referenceService = null,
        Func<string, IndexQueryOptions, string, CancellationToken, Task<IndexQueryOutput>>? executeWithReferenceIndex = null,
        Func<string, IndexQueryOptions, CancellationToken, bool, Task<IndexQueryOutput>>? executeWithGenerated = null,
        Func<string, IndexRunRecord, int, CancellationToken, bool, Task<IndexQueryOutput>>? executeInIndexWithGenerated = null,
        Func<string, IndexQueryOptions, CancellationToken, bool, bool, Task<IndexQueryOutput>>? executeWithGeneratedAndDelegates = null,
        Func<string, IndexRunRecord, int, CancellationToken, bool, bool, Task<IndexQueryOutput>>? executeInIndexWithGeneratedAndDelegates = null)
    {
        var classic = execute is not null && executeInIndex is not null;
        var withGenerated = executeWithGenerated is not null && executeInIndexWithGenerated is not null;
        var withDelegates = executeWithGeneratedAndDelegates is not null && executeInIndexWithGeneratedAndDelegates is not null;
        if ((classic ? 1 : 0) + (withGenerated ? 1 : 0) + (withDelegates ? 1 : 0) != 1)
            throw new ArgumentException(
                "Provide either execute/executeInIndex, executeWithGenerated/executeInIndexWithGenerated, or executeWithGeneratedAndDelegates/executeInIndexWithGeneratedAndDelegates.",
                nameof(execute));

        var queryArgument = new Argument<string>("query") { Description = QueryArgumentDescription };
        var codebaseOption = new Option<string>("--codebase") { Description = "schedule-i, s1api, or s1mapi." };
        var channelOption = new Option<string>("--channel") { Description = "installed, release, preview, or all." };
        var buildOption = new Option<string?>("--build") { Description = BuildOptionDescription };
        var limitOption = new Option<int>("--limit")
        {
            Description = "Maximum number of query results to return.",
            DefaultValueFactory = _ => 50
        };
        var jsonOption = CommandOutput.CreateJsonOption();
        var scopeOption = new Option<string?>("--scope") { Description = "game, reference, or all." };
        var collectionOption = new Option<string?>("--collection") { Description = "A named or indexed reference collection." };
        var includeGeneratedOption = CreateIncludeGeneratedOption();
        var includeDelegatesOption = CreateIncludeDelegatesOption();
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
        if (withGenerated || withDelegates)
            command.Options.Add(includeGeneratedOption);
        if (withDelegates)
            command.Options.Add(includeDelegatesOption);
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
                        return commandOutput.Failure(1, authority.ErrorCode, authority.ErrorMessage!, hint: authority.BuildAuthority?.Hint);

                    var includeGenerated = (withGenerated || withDelegates) && parseResult.GetValue(includeGeneratedOption);
                    var includeDelegates = withDelegates && parseResult.GetValue(includeDelegatesOption);
                    IndexQueryOutput data;
                    if (authority.Run is not null)
                    {
                        data = executeInIndexWithGeneratedAndDelegates is not null
                            ? executeInIndexWithGeneratedAndDelegates(
                                parseResult.GetValue(queryArgument)!,
                                authority.Run,
                                limit,
                                cancellationToken,
                                includeGenerated,
                                includeDelegates).GetAwaiter().GetResult()
                            : executeInIndexWithGenerated is not null
                            ? executeInIndexWithGenerated(
                                parseResult.GetValue(queryArgument)!,
                                authority.Run,
                                limit,
                                cancellationToken,
                                includeGenerated).GetAwaiter().GetResult()
                            : executeInIndex!(
                                parseResult.GetValue(queryArgument)!,
                                authority.Run,
                                limit,
                                cancellationToken).GetAwaiter().GetResult();
                    }
                    else
                    {
                        var query = parseResult.GetValue(queryArgument)!;
                        data = executeWithGeneratedAndDelegates is not null
                            ? executeWithGeneratedAndDelegates(query, options, cancellationToken, includeGenerated, includeDelegates).GetAwaiter().GetResult()
                            : executeWithGenerated is not null
                            ? executeWithGenerated(query, options, cancellationToken, includeGenerated).GetAwaiter().GetResult()
                            : executeWithReferenceIndex is not null && authority.ReferenceIndexId is not null
                                ? executeWithReferenceIndex(query, options, authority.ReferenceIndexId, cancellationToken).GetAwaiter().GetResult()
                                : execute!(query, options, cancellationToken).GetAwaiter().GetResult();
                    }
                    return Complete(commandOutput, data, parseResult.GetValue(queryArgument)!, ScopeIndexHint(options));
                },
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    internal static int Complete(
        CommandOutput commandOutput,
        IndexQueryOutput data,
        string selector,
        string? indexHint = null)
    {
        var failure = FailureForResolution(commandOutput, data.Resolution, selector, indexHint);
        if (failure is not null)
        {
            return failure.Value;
        }

        return commandOutput.Success(data, writer => WriteHuman(data, writer));
    }

    internal static string? ScopeIndexHint(IndexQueryOptions options) =>
        ReadinessFixCommands.HintForNoCompletedIndex(options.Codebase, options.Channel);

    /// <summary>
    /// Shared selector-failure presenter for every symbol-taking command. Ambiguous
    /// resolutions render the numbered candidate table (stdout) with the exact total,
    /// or an at-least count when the candidate pool was truncated, plus the narrowest
    /// disambiguation hint; unknown selectors render up to 5 near-match suggestions
    /// with a search hint. Returns null when the resolution carries no failure so
    /// callers continue on their success path.
    /// </summary>
    internal static int? FailureForResolution(
        CommandOutput commandOutput,
        SymbolResolutionResult? resolution,
        string selector,
        string? indexHint = null)
    {
        if (resolution is { Status: SymbolResolutionStatus.Ambiguous } ambiguous)
        {
            return commandOutput.FailureWithData(
                "AmbiguousSymbol",
                "The symbol selector matched multiple candidates. Re-run with a full symbol ID or a unique short-ID prefix.",
                new IndexQueryFailureData(
                    ambiguous.Candidates,
                    ambiguous.Suggestions,
                    ambiguous.TotalCandidateCount),
                writer => WriteCandidates(writer, ambiguous.Candidates, ambiguous.TotalCandidateCount));
        }

        if (resolution is { Status: SymbolResolutionStatus.NoCompletedIndex })
        {
            return commandOutput.Failure(
                1,
                "NoCompletedIndex",
                "No completed index exists for the requested codebase and channel.",
                new IndexQueryFailureData([], [], null),
                hint: indexHint);
        }

        if (resolution is { Status: SymbolResolutionStatus.NotFound } notFound)
        {
            return commandOutput.FailureWithData(
                "SymbolNotFound",
                "No indexed symbol matched the selector.",
                new IndexQueryFailureData([], notFound.Suggestions, null),
                writer => WriteSuggestions(writer, notFound.Suggestions, selector));
        }

        return null;
    }

    internal const int MaxHumanCandidates = 10;

    internal static void WriteCandidates(
        TextWriter writer,
        IReadOnlyList<SymbolQueryResult> candidates,
        int? total)
    {
        var shown = candidates.Take(MaxHumanCandidates).ToArray();
        writer.WriteLine(total is null
            ? $"Found at least {candidates.Count} candidates; showing {shown.Length}."
            : $"Found {total} candidates; showing {shown.Length}.");
        for (var i = 0; i < shown.Length; i++)
        {
            WriteCandidateRow(writer, i + 1, shown[i]);
        }

        writer.WriteLine(NarrowestHint(candidates));
    }

    internal static void WriteSuggestions(
        TextWriter writer,
        IReadOnlyList<SymbolQueryResult> suggestions,
        string selector)
    {
        writer.WriteLine("Found 0 matches.");
        if (suggestions.Count == 0)
        {
            return;
        }

        writer.WriteLine("Nearest matches:");
        for (var i = 0; i < suggestions.Count; i++)
        {
            WriteCandidateRow(writer, i + 1, suggestions[i]);
        }

        var display = DisplaySelector(selector);
        writer.WriteLine(
            $"Hint: no symbol matched '{display}'; check the spelling or run 's1atlas search \"{display}\"'.");
    }

    internal static void WriteCandidateRow(TextWriter writer, int number, SymbolQueryResult candidate) =>
        writer.WriteLine(
            $"{number} | {candidate.Kind} | {candidate.QualifiedName} | {candidate.Signature} | " +
            $"{candidate.ShortId ?? ShortId.Display(candidate.SymbolId)} | {candidate.Codebase}");

    /// <summary>
    /// Narrowest working disambiguation: a shared signature leaves the short ID as
    /// the only distinguisher, otherwise the exact signature works. No symbol-taking
    /// command accepts a --kind filter, so kinds never appear here.
    /// </summary>
    internal static string NarrowestHint(IReadOnlyList<SymbolQueryResult> candidates) =>
        candidates.Select(candidate => candidate.Signature).Distinct(StringComparer.Ordinal).Count() <= 1
            ? "Hint: re-run with a short ID from the table."
            : "Hint: re-run with the exact signature or a short ID from the table.";

    /// <summary>
    /// Sanitizes a selector echoed into hints: first line only, trimmed, capped at
    /// 80 characters, double quotes folded so the search hint stays runnable.
    /// </summary>
    internal static string DisplaySelector(string selector)
    {
        var line = selector.Split('\n', '\r')[0].Trim();
        if (line.Length == 0)
        {
            return "(blank)";
        }

        if (line.Length > 80)
        {
            line = line[..80] + "...";
        }

        return line.Replace('"', '\'');
    }

    internal static IndexQueryOutput ToOutput(RelationshipQuerySetResult result) => new(
        [],
        result.Relationships,
        [],
        TotalCount: result.TotalCount,
        ReturnedCount: result.Relationships.Count,
        ExactCount: result.ExactCount,
        DerivedCount: result.DerivedCount,
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

    internal static IndexQueryOutput ToOutput(HierarchyQueryResult result) => new(
        [],
        result.Nodes.Select(node => node.Edge).ToArray(),
        [],
        TotalCount: result.TotalCount,
        ReturnedCount: result.ReturnedCount,
        Resolution: result.Resolution,
        HierarchyNodes: result.Nodes);

    internal static Option<bool> CreateIncludeGeneratedOption() =>
        new("--include-generated")
        {
            Description = "Include compiler-generated members; show their raw sources instead of credited ones."
        };

    internal static Option<bool> CreateIncludeDelegatesOption() =>
        new("--include-delegates")
        {
            Description = "Include delegate-creation references; they are labeled and never counted as calls."
        };

    internal static string? RewordSearchNotice(string? notice) =>
        notice?.Replace("includeGenerated", "--include-generated", StringComparison.Ordinal);

    internal static void WriteHuman(IndexQueryOutput data, TextWriter writer)
    {
        if (data.TotalCount is int totalCount && data.ReturnedCount is int returnedCount)
            writer.WriteLine($"Found {totalCount} matches. Showing {returnedCount}.");

        if (!string.IsNullOrWhiteSpace(data.SearchNotice))
            writer.WriteLine(data.SearchNotice);

        if (data.ExactCount is int exactCount && data.DerivedCount is int derivedCount)
            writer.WriteLine($"Exact {exactCount}. Derived {derivedCount}.");

        foreach (var symbol in data.Symbols)
            writer.WriteLine($"{symbol.Channel} | {symbol.Kind} | {symbol.QualifiedName} | {symbol.Signature} | {symbol.ShortId ?? ShortId.Display(symbol.SymbolId)}");

        if (data.HierarchyNodes is { } hierarchyNodes)
        {
            foreach (var node in hierarchyNodes)
            {
                var relationship = node.Edge;
                writer.WriteLine(
                    $"depth {node.Depth} | {(node.IsDirect ? "direct" : "transitive")} | {relationship.Kind} | {relationship.Direction} | " +
                    $"{FormatEndpoint(relationship.Source)} -> {FormatEndpoint(relationship.Target)} | evidence: {relationship.Evidence}");
            }
        }
        else
        {
            foreach (var relationship in data.Relationships)
            {
                var source = FormatEndpoint(relationship.Source);
                if (relationship.GeneratedDetail is not null)
                    source += $" ({relationship.GeneratedDetail})";
                var kind = relationship.Label is null ? relationship.Kind : $"{relationship.Kind} ({relationship.Label})";
                var line =
                    $"{relationship.RelationshipId} | {kind} | {relationship.Direction} | " +
                    $"{source} -> {FormatEndpoint(relationship.Target)} | evidence: {relationship.Evidence}";
                if (relationship.IsDerived)
                    line += $" | DERIVED {string.Join("; ", relationship.Routes ?? [])}";
                writer.WriteLine(line);
            }
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
                ? new ExecutionAuthority(authority.IndexRun, null, null, BuildAuthority: authority)
                : new ExecutionAuthority(null, authority.Status.ToString(), authority.Message ?? "The requested Schedule I build is unavailable.", BuildAuthority: authority);
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
                baseAuthority.Message ?? "The requested Schedule I build is unavailable.",
                BuildAuthority: baseAuthority);
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

        return new ExecutionAuthority(
            null,
            null,
            null,
            collection.ReferenceIndexId,
            baseAuthority);
    }

    internal readonly record struct ExecutionAuthority(
        IndexRunRecord? Run,
        string? ErrorCode,
        string? ErrorMessage,
        string? ReferenceIndexId = null,
        InstalledBuildAuthority? BuildAuthority = null);
}
