using System.CommandLine;
using S1Atlas.Application.Readiness;
using S1Atlas.Cli.Output;
using S1Atlas.Cli.Performance;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Workflow;
using S1Atlas.Indexing.Scene;
using S1Atlas.Storage.Sqlite;

namespace S1Atlas.Cli.Commands;

internal static class IndexCommand
{
    public static Command Create(
        IndexingWorkflow workflow,
        ApiIndexingWorkflow apiWorkflow,
        SceneIndexWorkflow sceneWorkflow,
        IAtlasRepository repository,
        string dataRoot,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var forceOption = new Option<bool>("--force") { Description = "Rebuild a completed index as a new candidate." };
        var sceneOption = new Option<bool>("--scene") { Description = "Build the verified Unity scene intelligence index." };
        var buildOption = new Option<string?>("--build") { Description = "A completed build ID for scene indexing." };
        var codebaseOption = new Option<string?>("--codebase") { Description = "schedule-i, s1api, or s1mapi." };
        var channelOption = new Option<string?>("--channel") { Description = "installed, release, or preview." };
        var commitOption = new Option<string?>("--commit") { Description = "An exact cached upstream commit SHA." };
        var interopPathOption = new Option<string?>("--interop-path") { Description = "A generated Il2CppInterop Assembly-CSharp.dll or its directory." };
        var performanceOption = new Option<bool>("--performance")
        {
            Description = "Write performance diagnostics JSON to standard error."
        };
        var jsonOption = CommandOutput.CreateJsonOption();
        var command = new Command("index", CliExamples.With("Build the installed Schedule I source and symbol index.", "s1atlas index --force"));
        command.Options.Add(forceOption);
        command.Options.Add(sceneOption);
        command.Options.Add(buildOption);
        command.Options.Add(codebaseOption);
        command.Options.Add(channelOption);
        command.Options.Add(commitOption);
        command.Options.Add(interopPathOption);
        command.Options.Add(performanceOption);
        command.Options.Add(jsonOption);
        command.Validators.Add(result =>
        {
            try
            {
                var scene = CliValidation.GetValue(result, sceneOption);
                var build = CliValidation.GetValue(result, buildOption);
                var codebase = CliValidation.GetValue(result, codebaseOption);
                var channel = CliValidation.GetValue(result, channelOption);
                var commit = CliValidation.GetValue(result, commitOption);
                var interopPath = CliValidation.GetValue(result, interopPathOption);
                if (interopPath is not null &&
                    (scene || build is not null || codebase is not null || channel is not null || commit is not null))
                {
                    throw new CliValidationException(
                        "index",
                        "InvalidOptionCombination",
                        "--interop-path is valid only for the default installed Schedule I code index.");
                }
                if (scene && (codebase is not null || channel is not null || commit is not null))
                {
                    throw new CliValidationException(
                        "index",
                        "InvalidOptionCombination",
                        "Scene indexing accepts --build and --force; --codebase, --channel, and --commit are code-index options.");
                }
                if (!scene && build is not null)
                {
                    throw new CliValidationException(
                        "index",
                        "InvalidOptionCombination",
                        "--build is valid only with --scene.");
                }
                if (codebase is null && channel is null && commit is null)
                    return;
                if (!TryParseApiCodebase(codebase, out _) || !TryParseApiChannel(channel, out _))
                {
                    throw new CliValidationException(
                        "index",
                        "InvalidCodebaseChannel",
                        "API indexing requires --codebase s1api or s1mapi and --channel installed, release, or preview.");
                }
                if (commit is null &&
                    TryParseApiChannel(channel, out var apiChannel) &&
                    apiChannel is CodeChannel.Release or CodeChannel.Preview)
                {
                    throw new CliValidationException(
                        "index",
                        "InvalidCommit",
                        "Release and Preview indexing require --commit <40-character cached SHA>.");
                }
            }
            catch (InvalidOperationException)
            {
                // A framework binding failure; the framework reports it.
            }
        });
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("index", parseResult.GetValue(jsonOption), output, error);
            var performance = parseResult.GetValue(performanceOption)
                ? new PerformanceMeasurement("index", dataRoot)
                : null;
            return Execute(
                workflow,
                apiWorkflow,
                sceneWorkflow,
                repository,
                new IndexCommandOptions(
                    parseResult.GetValue(forceOption),
                    parseResult.GetValue(sceneOption),
                    parseResult.GetValue(buildOption),
                    parseResult.GetValue(codebaseOption),
                    parseResult.GetValue(channelOption),
                    parseResult.GetValue(commitOption),
                    parseResult.GetValue(interopPathOption)),
                commandOutput,
                performance,
                cancellationToken);
        });
        return command;
    }

    internal static int Execute(
        IndexingWorkflow workflow,
        ApiIndexingWorkflow apiWorkflow,
        SceneIndexWorkflow sceneWorkflow,
        IAtlasRepository repository,
        IndexCommandOptions options,
        CommandOutput commandOutput,
        PerformanceMeasurement? performance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(apiWorkflow);
        ArgumentNullException.ThrowIfNull(sceneWorkflow);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(commandOutput);
        return CommandExecution.Run(
            () =>
            {
                using (performance?.Measure("repository.initialize"))
                {
                    repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
                }

                using var workflowPhase = performance?.Measure("index.workflow");
                var sceneIndexRequested = options.Scene;
                var requestedBuild = options.Build;
                var requestedCodebase = options.Codebase;
                var requestedChannel = options.Channel;
                var requestedCommit = options.Commit;
                var requestedInteropPath = options.InteropPath;

                if (sceneIndexRequested)
                {
                    var sceneBuildId = requestedBuild ?? repository.GetCurrentSnapshotAsync(cancellationToken).GetAwaiter().GetResult()?.Build.BuildId;
                    if (sceneBuildId is null)
                        return commandOutput.Failure(1, "NoEnvironmentSnapshot", "No current environment snapshot is available.", hint: ReadinessFixCommands.Scan);
                    SceneIndexWorkflowResult sceneResult;
                    try
                    {
                        sceneResult = sceneWorkflow.RunScheduleOneAsync(sceneBuildId, options.Force, cancellationToken).GetAwaiter().GetResult();
                    }
                    catch (InvalidDataException exception)
                    {
                        return commandOutput.Failure(1, "SceneInputIntegrityFailure", exception.Message);
                    }
                    catch (SceneIndexFailureException exception)
                    {
                        return commandOutput.Failure(1, exception.Status.ToString(), exception.Message);
                    }
                    if (performance is not null)
                    {
                        performance.SetCounter("scene.containers", sceneResult.ContainerCount);
                        performance.SetCounter("scene.documents", sceneResult.SceneCount);
                        performance.SetCounter("scene.gameObjects", sceneResult.GameObjectCount);
                        performance.SetCounter("scene.components", sceneResult.ComponentCount);
                        performance.SetCounter("scene.references", sceneResult.ReferenceCount);
                        performance.SetCounter("index.reused", sceneResult.Reused ? 1 : 0);
                    }
                    return WriteSceneResult(commandOutput, sceneResult);
                }
                var snapshot = repository.GetCurrentSnapshotAsync(cancellationToken).GetAwaiter().GetResult();
                IndexingWorkflowResult result;
                string codebase;
                string channel;
                if (requestedCodebase is null && requestedChannel is null && requestedCommit is null)
                {
                    if (snapshot is null)
                        return commandOutput.Failure(1, "NoEnvironmentSnapshot", "No current environment snapshot is available.", hint: ReadinessFixCommands.Scan);
                    result = workflow.RunScheduleOneAsync(
                        snapshot.Build.BuildId,
                        options.Force,
                        cancellationToken,
                        requestedInteropPath).GetAwaiter().GetResult();
                    codebase = "ScheduleI";
                    channel = "Installed";
                }
                else
                {
                    TryParseApiCodebase(requestedCodebase, out var apiCodebase);
                    TryParseApiChannel(requestedChannel, out var apiChannel);
                    if (apiChannel is CodeChannel.Release or CodeChannel.Preview)
                    {
                        try
                        {
                            result = apiWorkflow.RunCachedSourceAsync(
                                apiCodebase,
                                apiChannel,
                                requestedCommit!,
                                options.Force,
                                cancellationToken).GetAwaiter().GetResult();
                        }
                        catch (ArgumentException exception)
                        {
                            return commandOutput.Failure(1, "InvalidCommit", exception.Message);
                        }
                        catch (FileNotFoundException exception)
                        {
                            return commandOutput.Failure(1, "UpstreamUnavailable", exception.Message);
                        }
                        catch (InvalidDataException exception)
                        {
                            return commandOutput.Failure(1, "UpstreamUnavailable", exception.Message);
                        }
                        codebase = apiCodebase.ToString();
                        channel = apiChannel.ToString();
                    }
                    else
                    {
                        if (snapshot is null)
                            return commandOutput.Failure(1, "NoEnvironmentSnapshot", "No current environment snapshot is available.", hint: ReadinessFixCommands.Scan);
                        var dependencyKind = apiCodebase == CodebaseKind.S1Api
                            ? DependencyKind.S1Api
                            : DependencyKind.S1Mapi;
                        var dependency = snapshot.Dependencies.SingleOrDefault(item => item.Kind == dependencyKind);
                        if (dependency is null || !dependency.IsInstalled || string.IsNullOrWhiteSpace(dependency.Path))
                        {
                            return commandOutput.Failure(
                                1,
                                "InstalledDependencyMissing",
                                $"The installed {apiCodebase} dependency is not present in the current environment snapshot.");
                        }

                        result = apiWorkflow.RunInstalledAsync(
                            apiCodebase,
                            EnvironmentSnapshotId.Create(snapshot),
                            dependency.Path,
                            options.Force,
                            cancellationToken).GetAwaiter().GetResult();
                        codebase = apiCodebase.ToString();
                        channel = apiChannel.ToString();
                    }
                }
                var data = new IndexOutput(
                    codebase,
                    channel,
                    channel is "Release" or "Preview"
                        ? requestedCommit ?? string.Empty
                        : snapshot?.Build.BuildId ?? string.Empty,
                    result.IndexId,
                    result.Reused,
                    result.SymbolCount,
                    result.SourceFileCount,
                    result.RelationshipCount,
                    result.Warnings);
                if (performance is not null)
                {
                    performance.SetCounter("index.symbols", result.SymbolCount);
                    performance.SetCounter("index.sourceFiles", result.SourceFileCount);
                    performance.SetCounter("index.relationships", result.RelationshipCount);
                    performance.SetCounter("index.reused", result.Reused ? 1 : 0);
                }
                return commandOutput.Success(
                    data,
                    writer => writer.WriteLine(
                        $"{codebase} {channel} | {result.IndexId} | " +
                        (result.Reused ? "reused" : "rebuilt") +
                        $" | symbols {result.SymbolCount} | source files {result.SourceFileCount} | relationships {result.RelationshipCount}"));
            },
                commandOutput,
                cancellationToken,
                performance);
    }

    internal static int WriteSceneResult(CommandOutput commandOutput, SceneIndexWorkflowResult sceneResult)
    {
        ArgumentNullException.ThrowIfNull(commandOutput);
        ArgumentNullException.ThrowIfNull(sceneResult);
        var sceneData = new SceneIndexOutput(
            sceneResult.SceneSnapshotId,
            sceneResult.BuildId,
            sceneResult.CodeIndexId,
            sceneResult.ParserId,
            sceneResult.ParserVersion,
            sceneResult.Reused,
            sceneResult.ContainerCount,
            sceneResult.SceneCount,
            sceneResult.GameObjectCount,
            sceneResult.TransformCount,
            sceneResult.ComponentCount,
            sceneResult.ReferenceCount,
            sceneResult.RecoveryCounts ?? new Dictionary<string, int>(),
            sceneResult.Warnings ?? [],
            sceneResult.TypeTreeSource,
            sceneResult.ScriptLayoutSource);
        return commandOutput.Success(sceneData, writer =>
        {
            writer.WriteLine(
                $"scene {sceneData.SceneSnapshotId} | build {sceneData.BuildId} | code index {sceneData.CodeIndexId} | " +
                $"parser {sceneData.ParserId} {sceneData.ParserVersion} | type tree {sceneData.TypeTreeSource ?? "unknown"} | {(sceneData.Reused ? "reused" : "rebuilt")} | " +
                $"containers {sceneData.ContainerCount} | documents {sceneData.DocumentCount} | objects {sceneData.GameObjectCount} | " +
                $"transforms {sceneData.TransformCount} | components {sceneData.ComponentCount} | references {sceneData.ReferenceCount}");
            writer.WriteLine("Script layouts: " + (sceneData.ScriptLayoutSource ?? "unknown"));
            writer.WriteLine("Recovery: " + string.Join(", ", sceneData.RecoveryCounts.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Key + "=" + item.Value)));
            foreach (var warning in sceneData.Warnings) writer.WriteLine("Warning: " + warning);
        });
    }

    private static bool TryParseApiCodebase(string? value, out CodebaseKind codebase)
    {
        codebase = value?.ToLowerInvariant() switch
        {
            "s1api" => CodebaseKind.S1Api,
            "s1mapi" => CodebaseKind.S1MApi,
            _ => default
        };
        return codebase is CodebaseKind.S1Api or CodebaseKind.S1MApi;
    }

    private static bool TryParseApiChannel(string? value, out CodeChannel channel)
    {
        channel = value?.ToLowerInvariant() switch
        {
            "installed" => CodeChannel.Installed,
            "release" => CodeChannel.Release,
            "preview" => CodeChannel.Preview,
            _ => default
        };
        return value is not null &&
            (string.Equals(value, "installed", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "release", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "preview", StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed record IndexCommandOptions(
    bool Force = false,
    bool Scene = false,
    string? Build = null,
    string? Codebase = null,
    string? Channel = null,
    string? Commit = null,
    string? InteropPath = null);
