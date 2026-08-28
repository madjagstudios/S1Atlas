using System.CommandLine;
using System.Diagnostics;
using S1Atlas.Application.Authority;
using S1Atlas.Cli.Output;
using S1Atlas.Core.ReferenceMods;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.ReferenceMods;
using S1Atlas.Indexing.Workflow;

namespace S1Atlas.Cli.Commands;

internal static class ReferenceIndexCommand
{
    public static Command Create(
        ReferenceModIndexWorkflow workflow,
        ReferenceModManifestLoader loader,
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository atlasRepository,
        IIndexRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var manifestArgument = new Argument<string>("manifest") { Description = "A local reference-mod collection manifest." };
        var forceOption = new Option<bool>("--force") { Description = "Rebuild a completed reference index as a new candidate." };
        var jsonOption = CommandOutput.CreateJsonOption();
        var command = new Command("index", "Build a local reference-mod index.");
        command.Arguments.Add(manifestArgument);
        command.Options.Add(forceOption);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("reference index", parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(
                () => Execute(
                    workflow,
                    loader,
                    authorityResolver,
                    atlasRepository,
                    repository,
                    parseResult.GetValue(manifestArgument)!,
                    parseResult.GetValue(forceOption),
                    commandOutput,
                    cancellationToken),
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    private static int Execute(
        ReferenceModIndexWorkflow workflow,
        ReferenceModManifestLoader loader,
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository atlasRepository,
        IIndexRepository repository,
        string manifestPath,
        bool force,
        CommandOutput commandOutput,
        CancellationToken cancellationToken)
    {
        atlasRepository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
        var phases = Stopwatch.StartNew();
        ReferenceCollectionDefinition manifest;
        try
        {
            manifest = loader.LoadAsync(manifestPath, cancellationToken).GetAwaiter().GetResult();
        }
        catch (InvalidDataException exception)
        {
            return commandOutput.Failure(1, "InvalidManifest", exception.Message);
        }
        var manifestMilliseconds = phases.ElapsedMilliseconds;

        var authority = authorityResolver.ResolveAsync(null, cancellationToken).GetAwaiter().GetResult();
        if (authority.Status != InstalledBuildAuthorityStatus.Resolved || authority.IndexRun is null || authority.ResolvedBuildId is null)
        {
            return commandOutput.Failure(
                1,
                authority.Status.ToString(),
                authority.Message ?? "The current Schedule I game index is unavailable.");
        }

        var collection = manifest with
        {
            BuildId = authority.ResolvedBuildId,
            GameIndexId = authority.IndexRun.IndexId
        };
        phases.Restart();
        IndexingWorkflowResult result;
        try
        {
            result = workflow.RunAsync(
                authority.ResolvedBuildId,
                collection,
                force,
                cancellationToken).GetAwaiter().GetResult();
        }
        catch (InvalidDataException exception)
        {
            return commandOutput.Failure(1, "ReferenceIndexFailure", exception.Message);
        }
        var data = new ReferenceIndexOutput(
            collection.CollectionId,
            collection.CollectionName,
            result.IndexId,
            result.SnapshotId,
            result.Reused,
            result.ReferenceModCount ?? 0,
            result.ReferenceDocumentCount ?? 0,
            result.ReferenceSymbolCount ?? result.SymbolCount,
            result.SourceFileCount,
            result.RelationshipCount,
            result.Warnings
                .Concat(collection.Mods
                    .Where(mod => string.Equals(mod.License, "unknown", StringComparison.OrdinalIgnoreCase))
                    .Select(mod => $"Mod '{mod.ModId}' declares an unknown license; provenance remains LocalOnly."))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            new ReferencePhaseTimings(manifestMilliseconds, result.InputHashMilliseconds
                ?? throw new InvalidOperationException("Reference indexing did not report input hash timing."), phases.ElapsedMilliseconds));
        return commandOutput.Success(data, writer =>
        {
            writer.WriteLine(
                $"reference {data.Collection} | {data.IndexId} | {(data.Reused ? "reused" : "rebuilt")} | " +
                $"mods {data.ModCount} | documents {data.DocumentCount} | symbols {data.SymbolCount} | " +
                $"source files {data.SourceFileCount} | relationships {data.RelationshipCount}");
            writer.WriteLine(
                $"Phases: manifest {data.Phases.ManifestValidationMilliseconds}ms, " +
                $"hash {data.Phases.InputHashMilliseconds}ms, " +
                $"workflow {data.Phases.IndexWorkflowMilliseconds}ms");
            foreach (var warning in data.Warnings) writer.WriteLine("Warning: " + warning);
        });
    }
}
