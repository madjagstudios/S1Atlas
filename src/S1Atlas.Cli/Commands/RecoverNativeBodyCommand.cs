using System.CommandLine;
using S1Atlas.Application.Authority;
using S1Atlas.Cli.Output;
using S1Atlas.Core;
using S1Atlas.Core.Storage;
using S1Atlas.NativeRecovery;

namespace S1Atlas.Cli.Commands;

internal static class RecoverNativeBodyCommand
{
    private const int MinimumTraversalBudget = 1;
    private const int MaximumTraversalBudget = 500;

    public static Command Create(
        NativeRecoveryComposition composition,
        NativeRecoveryExecutionContextFactory contextFactory,
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository atlasRepository,
        IIndexRepository indexRepository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var symbolIdOption = new Option<string[]>("--symbol-id")
        {
            Description = "A native symbol ID or unique short-ID prefix to recover; repeat for multiple IDs."
        };
        var traversalBudgetOption = new Option<int>("--native-traversal-budget")
        {
            Description = "Native evidence traversal budget (1-500).",
            DefaultValueFactory = _ => 100
        };
        var buildOption = new Option<string?>("--build")
        {
            Description = "Select a Schedule I Installed build ID or unique short-ID prefix; defaults to the current installed build."
        };
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command(
            "recover-native-body",
            CliExamples.With("Recover native method bodies for the selected symbols and persist the result.", "s1atlas recover-native-body --symbol-id <id>"));
        command.Options.Add(symbolIdOption);
        command.Options.Add(traversalBudgetOption);
        command.Options.Add(buildOption);
        command.Options.Add(jsonOption);
        command.Validators.Add(result =>
        {
            try
            {
                if ((CliValidation.GetValue(result, symbolIdOption) ?? []).Length == 0)
                    throw new CliValidationException("recover-native-body", "MissingSymbolId", "At least one --symbol-id must be provided.");
            }
            catch (InvalidOperationException)
            {
                // A framework binding failure; the framework reports it.
            }
        });
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(
                "recover-native-body",
                parseResult.GetValue(jsonOption),
                output,
                error);
            return CommandExecution.Run(
                () => Execute(
                    composition,
                    contextFactory,
                    authorityResolver,
                    atlasRepository,
                    indexRepository,
                    parseResult.GetValue(symbolIdOption) ?? [],
                    parseResult.GetValue(traversalBudgetOption),
                    parseResult.GetValue(buildOption),
                    commandOutput,
                    cancellationToken),
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    private static int Execute(
        NativeRecoveryComposition composition,
        NativeRecoveryExecutionContextFactory contextFactory,
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository atlasRepository,
        IIndexRepository indexRepository,
        IReadOnlyList<string> symbolIds,
        int traversalBudget,
        string? buildId,
        CommandOutput commandOutput,
        CancellationToken cancellationToken)
    {
        if (traversalBudget is < MinimumTraversalBudget or > MaximumTraversalBudget)
        {
            return commandOutput.Failure(
                1,
                "InvalidNativeTraversalBudget",
                $"--native-traversal-budget must be between {MinimumTraversalBudget} and {MaximumTraversalBudget}.");
        }

        atlasRepository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();

        var authority = authorityResolver.ResolveAsync(buildId, cancellationToken).GetAwaiter().GetResult();
        if (authority.Status != InstalledBuildAuthorityStatus.Resolved)
        {
            return commandOutput.Failure(
                1,
                authority.Status.ToString(),
                authority.Message ?? "The requested Schedule I build is unavailable.",
                hint: authority.Hint);
        }

        if (authority.IndexId is not null)
        {
            var resolution = ResolveSymbolIdsAsync(
                    indexRepository, authority.IndexId, symbolIds, cancellationToken)
                .GetAwaiter()
                .GetResult();
            if (resolution.AmbiguousMessage is not null)
            {
                return commandOutput.Failure(1, "AmbiguousSymbol", resolution.AmbiguousMessage);
            }

            symbolIds = resolution.Resolved;
        }

        var installation = composition.LocateInstallationAsync(cancellationToken).GetAwaiter().GetResult();
        if (installation is null)
        {
            return commandOutput.Failure(
                1,
                "InstallationNotFound",
                "Schedule I installation could not be found or is missing required IL2CPP files.");
        }

        var libraryPins = composition.LoadLibraryPins();
        var contextResult = contextFactory.CreateAsync(
                authority,
                installation.GameAssemblyPath,
                libraryPins,
                cancellationToken)
            .GetAwaiter()
            .GetResult();
        if (contextResult.Status != NativeRecoveryExecutionContextStatus.Ready || contextResult.Context is null)
        {
            return commandOutput.Failure(
                1,
                contextResult.Status.ToString(),
                contextResult.Reason ?? "The native recovery execution context could not be prepared.");
        }

        var workflow = composition.BuildWorkflowAsync(cancellationToken).GetAwaiter().GetResult();
        if (workflow is null)
        {
            return commandOutput.Failure(
                1,
                "InstallationNotFound",
                "Schedule I installation could not be found or is missing required IL2CPP files.");
        }

        var executionContext = contextResult.Context;
        var request = new NativeRecoveryRequest(
            executionContext.CurrentBuildId,
            executionContext.CurrentIndexId,
            executionContext.CurrentGameAssemblySha256,
            symbolIds,
            traversalBudget);

        var record = workflow
            .RecoverAsync(request, executionContext, cancellationToken)
            .GetAwaiter()
            .GetResult();

        try
        {
            indexRepository
                .RequireNativeRecoveryRepository<NativeRecoveryRecord, NativeRecoveryRequest>()
                .SaveNativeRecoveryAsync(record, cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
        catch (InvalidOperationException exception)
        {
            return commandOutput.Failure(1, "NativeRecoveryPersistenceFailed", exception.Message);
        }

        var data = new NativeRecoveryCliOutput(
            record.RecoveryId,
            record.Status.ToString(),
            record.IsComplete,
            record.ToolName,
            record.ToolVersion,
            record.ToolSha256,
            record.OutputSha256,
            record.MappingEvidence,
            record.Edges
                .Select(edge => new NativeRecoveryEdgeOutput(
                    edge.EdgeId,
                    edge.SourceMethodPointer,
                    edge.TargetMethodPointer,
                    edge.TargetText,
                    edge.Kind,
                    edge.Evidence,
                    edge.IsComplete))
                .ToArray(),
            record.FieldAccesses,
            record.FailureMessage);

        return commandOutput.Success(data, writer => WriteHuman(record, writer));
    }

    /// <summary>
    /// Resolves unique short-ID prefixes to full symbol IDs against the resolved
    /// index. Full IDs, non-hex input, and unmatched prefixes pass through to the
    /// provider unchanged; an ambiguous prefix returns a failure message listing
    /// the matches instead of resolved IDs.
    /// </summary>
    internal static async Task<SymbolIdResolution> ResolveSymbolIdsAsync(
        IIndexRepository indexRepository,
        string indexId,
        IReadOnlyList<string> symbolIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexRepository);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentNullException.ThrowIfNull(symbolIds);

        var resolved = new string[symbolIds.Count];
        for (var i = 0; i < symbolIds.Count; i++)
        {
            var id = symbolIds[i];
            if (!ShortId.TryParsePrefix(id, out var prefix))
            {
                resolved[i] = id;
                continue;
            }

            var matches = await indexRepository.GetCompletedSymbolsByIdPrefixAsync(
                indexId, prefix, ShortId.MaxShownMatches + 1, cancellationToken);
            if (matches.Count == 0)
            {
                resolved[i] = id;
                continue;
            }

            if (matches.Count == 1)
            {
                resolved[i] = matches[0].SymbolId;
                continue;
            }

            var total = matches.Count > ShortId.MaxShownMatches
                ? await indexRepository.CountCompletedSymbolsByIdPrefixAsync(
                    indexId, prefix, cancellationToken)
                : matches.Count;
            var shown = matches
                .Take(ShortId.MaxShownMatches)
                .Select(record => record.SymbolId)
                .ToArray();
            var combined = new ShortIdMatch(ShortIdMatchKind.Ambiguous, null, shown, total);
            var signatures = matches.ToDictionary(
                record => record.SymbolId,
                record => record.Signature,
                StringComparer.Ordinal);

            string DescribeSymbol(string candidateId) =>
                $"{ShortId.Display(candidateId)} ({signatures[candidateId]})";

            return new SymbolIdResolution(
                symbolIds,
                $"The --symbol-id prefix '{id}' matches {total} symbols; " +
                $"re-run with a full symbol ID or one of these short IDs: {ShortId.FormatMatchList(combined, DescribeSymbol)}.");
        }

        return new SymbolIdResolution(resolved, null);
    }

    internal sealed record SymbolIdResolution(
        IReadOnlyList<string> Resolved,
        string? AmbiguousMessage);

    private static void WriteHuman(NativeRecoveryRecord record, TextWriter writer)
    {
        writer.WriteLine($"Status:         {record.Status}");
        writer.WriteLine($"Complete:       {record.IsComplete}");
        writer.WriteLine($"Recovery ID:    {record.RecoveryId}");
        writer.WriteLine($"Tool:           {record.ToolName} {record.ToolVersion}");
        writer.WriteLine($"Mapping notes:  {record.MappingEvidence.Count}");
        writer.WriteLine($"Edges:          {record.Edges.Count}");
        writer.WriteLine($"Field accesses: {record.FieldAccesses.Count}");
        if (record.FailureMessage is not null)
            writer.WriteLine($"Failure:        {record.FailureMessage}");
    }
}
