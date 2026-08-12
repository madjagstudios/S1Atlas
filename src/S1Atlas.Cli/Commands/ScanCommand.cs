using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Discovery;

namespace S1Atlas.Cli.Commands;

internal static class ScanCommand
{
    public static Command Create(
        EnvironmentDiscoveryService discovery,
        IAtlasRepository repository,
        string atlasVersion,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var gamePathOption = new Option<DirectoryInfo?>("--game-path")
        {
            Description = "Override the Schedule I installation directory."
        };

        var command = new Command(
            "scan",
            "Discover the local Schedule I environment and save a build snapshot.");
        command.Options.Add(gamePathOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(
                "scan",
                json: false,
                output,
                error);
            return CommandExecution.Run(
                () =>
                {
                    repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
                    var gamePath = parseResult.GetValue(gamePathOption)?.FullName;
                    var snapshot = discovery
                        .DiscoverAsync(gamePath, atlasVersion, cancellationToken)
                        .GetAwaiter()
                        .GetResult();

                    if (snapshot is null)
                    {
                        return commandOutput.Failure(
                            1,
                            "InstallationNotFound",
                            "Schedule I installation could not be found or is missing required IL2CPP files.");
                    }

                    repository
                        .SaveSnapshotAsync(snapshot, cancellationToken)
                        .GetAwaiter()
                        .GetResult();

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
                cancellationToken);
        });

        return command;
    }
}
