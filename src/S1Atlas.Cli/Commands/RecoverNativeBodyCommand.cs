using System.CommandLine;
using S1Atlas.Application.Authority;
using S1Atlas.Cli.Output;
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
            Description = "A native symbol ID to recover; repeat for multiple IDs."
        };
        var traversalBudgetOption = new Option<int>("--traversal-budget")
        {
            Description = "Native evidence traversal budget (1-500).",
            DefaultValueFactory = _ => 100
        };
        var buildOption = new Option<string?>("--build-id")
        {
            Description = "Select a Schedule I Installed build ID; defaults to the current installed build."
        };
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command(
            "recover-native-body",
            "Recover native method bodies for the selected symbols and persist the result.");
        command.Options.Add(symbolIdOption);
        command.Options.Add(traversalBudgetOption);
        command.Options.Add(buildOption);
        command.Options.Add(jsonOption);
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
        if (symbolIds.Count == 0)
            return commandOutput.Failure(1, "MissingSymbolId", "At least one --symbol-id must be provided.");
        if (traversalBudget is < MinimumTraversalBudget or > MaximumTraversalBudget)
        {
            return commandOutput.Failure(
                1,
                "InvalidNativeTraversalBudget",
                $"--traversal-budget must be between {MinimumTraversalBudget} and {MaximumTraversalBudget}.");
        }

        atlasRepository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();

        var authority = authorityResolver.ResolveAsync(buildId, cancellationToken).GetAwaiter().GetResult();
        if (authority.Status != InstalledBuildAuthorityStatus.Resolved)
        {
            return commandOutput.Failure(
                1,
                authority.Status.ToString(),
                authority.Message ?? "The requested Schedule I build is unavailable.");
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
