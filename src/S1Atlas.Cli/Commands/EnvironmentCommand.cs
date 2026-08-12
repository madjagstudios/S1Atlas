using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;

namespace S1Atlas.Cli.Commands;

internal static class EnvironmentCommand
{
    public static Command Create(
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var jsonOption = CommandOutput.CreateJsonOption();
        var command = new Command(
            "env",
            "Show the current game and modding dependency environment.");
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(
                "env",
                parseResult.GetValue(jsonOption),
                output,
                error);
            return CommandExecution.Run(
                () =>
                {
                    repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
                    var snapshot = repository
                        .GetCurrentSnapshotAsync(cancellationToken)
                        .GetAwaiter()
                        .GetResult();

                    if (snapshot is null)
                    {
                        return commandOutput.Failure(
                            1,
                            "NoIndexedBuilds",
                            "No indexed builds. Run 's1atlas scan' first.");
                    }

                    var dependencies = snapshot.Dependencies
                        .Select(dependency => new DependencyOutput(
                            dependency.Kind.ToString(),
                            dependency.Version,
                            dependency.Path,
                            dependency.IsInstalled))
                        .ToArray();
                    var data = new EnvironmentOutput(
                        snapshot.Build.BuildId,
                        snapshot.Installation.ExecutableVersion,
                        snapshot.Installation.SteamAppId,
                        snapshot.Installation.SteamBuildId,
                        snapshot.Installation.InstallationRoot,
                        snapshot.Installation.GameAssemblyPath,
                        snapshot.Installation.GlobalMetadataPath,
                        dependencies);

                    return commandOutput.Success(
                        data,
                        writer =>
                        {
                            writer.WriteLine($"Build: {snapshot.Build.BuildId}");
                            writer.WriteLine(
                                $"Executable version: {snapshot.Installation.ExecutableVersion ?? "unknown"}");
                            writer.WriteLine(
                                $"Steam app ID: {snapshot.Installation.SteamAppId ?? "unknown"}");
                            writer.WriteLine(
                                $"Steam build ID: {snapshot.Installation.SteamBuildId ?? "unknown"}");
                            writer.WriteLine(
                                $"Installation root: {snapshot.Installation.InstallationRoot ?? "unknown"}");
                            writer.WriteLine(
                                $"GameAssembly: {snapshot.Installation.GameAssemblyPath ?? "unknown"}");
                            writer.WriteLine(
                                $"Global metadata: {snapshot.Installation.GlobalMetadataPath ?? "unknown"}");
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
