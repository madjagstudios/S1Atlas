using System.CommandLine;
using S1Atlas.Cli.Output;
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
        var jsonOption = CommandOutput.CreateJsonOption();
        var command = new Command("index", "Build the installed Schedule I source and symbol index.");
        command.Options.Add(forceOption);
        command.Options.Add(sceneOption);
        command.Options.Add(buildOption);
        command.Options.Add(codebaseOption);
        command.Options.Add(channelOption);
        command.Options.Add(commitOption);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("index", parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(
                () =>
                {
                    repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
                    var sceneIndexRequested = parseResult.GetValue(sceneOption);
                    var requestedBuild = parseResult.GetValue(buildOption);
                    var requestedCodebase = parseResult.GetValue(codebaseOption);
                    var requestedChannel = parseResult.GetValue(channelOption);
                    var requestedCommit = parseResult.GetValue(commitOption);
                    if (sceneIndexRequested && (requestedCodebase is not null || requestedChannel is not null || requestedCommit is not null))
                    {
                        return commandOutput.Failure(
                            1,
                            "InvalidOptionCombination",
                            "Scene indexing accepts --build and --force; --codebase, --channel, and --commit are code-index options.");
                    }
                    if (!sceneIndexRequested && requestedBuild is not null)
                    {
                        return commandOutput.Failure(
                            1,
                            "InvalidOptionCombination",
                            "--build is valid only with --scene.");
                    }

                    if (sceneIndexRequested)
                    {
                        var sceneBuildId = requestedBuild ?? repository.GetCurrentSnapshotAsync(cancellationToken).GetAwaiter().GetResult()?.Build.BuildId;
                        if (sceneBuildId is null)
                            return commandOutput.Failure(1, "NoEnvironmentSnapshot", "No current environment snapshot is available.");
                        SceneIndexWorkflowResult sceneResult;
                        try
                        {
                            sceneResult = sceneWorkflow.RunScheduleOneAsync(sceneBuildId, parseResult.GetValue(forceOption), cancellationToken).GetAwaiter().GetResult();
                        }
                        catch (InvalidDataException exception)
                        {
                            return commandOutput.Failure(1, "SceneInputIntegrityFailure", exception.Message);
                        }
                        catch (SceneIndexFailureException exception)
                        {
                            return commandOutput.Failure(1, exception.Status.ToString(), exception.Message);
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
                            return commandOutput.Failure(1, "NoEnvironmentSnapshot", "No current environment snapshot is available.");
                        result = workflow.RunScheduleOneAsync(
                            snapshot.Build.BuildId,
                            parseResult.GetValue(forceOption),
                            cancellationToken).GetAwaiter().GetResult();
                        codebase = "ScheduleI";
                        channel = "Installed";
                    }
                    else
                    {
                        if (!TryParseApiCodebase(requestedCodebase, out var apiCodebase) ||
                            !TryParseApiChannel(requestedChannel, out var apiChannel))
                        {
                            return commandOutput.Failure(
                                1,
                                "InvalidCodebaseChannel",
                                "API indexing requires --codebase s1api or s1mapi and --channel installed, release, or preview.");
                        }
                        if (apiChannel is CodeChannel.Release or CodeChannel.Preview)
                        {
                            if (requestedCommit is null)
                                return commandOutput.Failure(1, "InvalidCommit", "Release and Preview indexing require --commit <40-character cached SHA>.");
                            try
                            {
                                result = apiWorkflow.RunCachedSourceAsync(
                                    apiCodebase,
                                    apiChannel,
                                    requestedCommit,
                                    parseResult.GetValue(forceOption),
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
                                return commandOutput.Failure(1, "NoEnvironmentSnapshot", "No current environment snapshot is available.");
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
                                parseResult.GetValue(forceOption),
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
                    return commandOutput.Success(
                        data,
                        writer => writer.WriteLine(
                            $"{codebase} {channel} | {result.IndexId} | " +
                            (result.Reused ? "reused" : "rebuilt") +
                            $" | symbols {result.SymbolCount} | source files {result.SourceFileCount} | relationships {result.RelationshipCount}"));
                },
                commandOutput,
                cancellationToken);
        });
        return command;
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
            sceneResult.Warnings ?? []);
        return commandOutput.Success(sceneData, writer =>
        {
            writer.WriteLine(
                $"scene {sceneData.SceneSnapshotId} | build {sceneData.BuildId} | code index {sceneData.CodeIndexId} | " +
                $"parser {sceneData.ParserId} {sceneData.ParserVersion} | {(sceneData.Reused ? "reused" : "rebuilt")} | " +
                $"containers {sceneData.ContainerCount} | documents {sceneData.DocumentCount} | objects {sceneData.GameObjectCount} | " +
                $"transforms {sceneData.TransformCount} | components {sceneData.ComponentCount} | references {sceneData.ReferenceCount}");
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
