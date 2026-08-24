using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Cli.Performance;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Discovery;

namespace S1Atlas.Cli.Commands;

internal static class ScanCommand
{
    public static Command Create(
        EnvironmentDiscoveryService discovery,
        IAtlasRepository repository,
        string atlasVersion,
        string dataRoot,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var gamePathOption = new Option<DirectoryInfo?>("--game-path")
        {
            Description = "Override the Schedule I installation directory."
        };
        var performanceOption = new Option<bool>("--performance")
        {
            Description = "Write performance diagnostics JSON to standard error."
        };

        var command = new Command(
            "scan",
            "Discover the local Schedule I environment and save a build snapshot.");
        command.Options.Add(gamePathOption);
        command.Options.Add(performanceOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(
                "scan",
                json: false,
                output,
                error);
            var performance = parseResult.GetValue(performanceOption)
                ? new PerformanceMeasurement("scan", dataRoot)
                : null;
            return CommandExecution.Run(
                () =>
                {
                    using (performance?.Measure("repository.initialize"))
                    {
                        repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
                    }

                    var gamePath = parseResult.GetValue(gamePathOption)?.FullName;
                    EnvironmentSnapshot? snapshot;
                    using (performance?.Measure("environment.discovery"))
                    {
                        snapshot = discovery
                            .DiscoverAsync(gamePath, atlasVersion, cancellationToken)
                            .GetAwaiter()
                            .GetResult();
                    }

                    if (snapshot is null)
                    {
                        return commandOutput.Failure(
                            1,
                            "InstallationNotFound",
                            "Schedule I installation could not be found or is missing required IL2CPP files.");
                    }

                    using (performance?.Measure("snapshot.persisted"))
                    {
                        repository
                            .SaveSnapshotAsync(snapshot, cancellationToken)
                            .GetAwaiter()
                            .GetResult();
                    }

                    if (performance is not null)
                    {
                        performance.SetCounter("dependencies.total", snapshot.Dependencies.Count);
                        performance.SetCounter(
                            "dependencies.installed",
                            snapshot.Dependencies.Count(dependency => dependency.IsInstalled));
                        SetFileSizeCounter(
                            performance,
                            "inputs.gameAssembly.bytes",
                            snapshot.Installation.GameAssemblyPath);
                        SetFileSizeCounter(
                            performance,
                            "inputs.globalMetadata.bytes",
                            snapshot.Installation.GlobalMetadataPath);
                    }

                    return commandOutput.Success(
                        snapshot.Build.BuildId,
                        writer =>
                        {
                            writer.WriteLine(
                                $"Indexed Schedule I build {snapshot.Build.BuildId}");
                            writer.WriteLine(
                                $"Executable version: {snapshot.Installation.ExecutableVersion ?? "unknown"}");
                            writer.WriteLine(
                                $"Steam app ID: {snapshot.Installation.SteamAppId ?? "unknown"}");
                            writer.WriteLine(
                                $"Steam build ID: {snapshot.Installation.SteamBuildId ?? "unknown"}");
                            foreach (var dependency in snapshot.Dependencies)
                            {
                                writer.WriteLine(DependencyDisplay.Format(dependency));
                            }
                        });
                },
                commandOutput,
                cancellationToken,
                performance);
        });

        return command;
    }

    private static void SetFileSizeCounter(
        PerformanceMeasurement performance,
        string counterName,
        string? path)
    {
        if (path is not null && File.Exists(path))
        {
            performance.SetCounter(counterName, new FileInfo(path).Length);
        }
    }
}
