using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;

namespace S1Atlas.Cli.Commands;

internal static class StatusCommand
{
    public static Command Create(
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var jsonOption = CommandOutput.CreateJsonOption();
        var command = new Command(
            "status",
            "Show the current Atlas build status.");
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(
                "status",
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
                        var empty = new StatusOutput(
                            HasCurrentBuild: false,
                            BuildId: null,
                            ExecutableVersion: null,
                            SteamAppId: null,
                            SteamBuildId: null,
                            CapturedAtUtc: null,
                            InstalledDependencyCount: 0,
                            DependencyCount: 0);
                        return commandOutput.Success(
                            empty,
                            writer => writer.WriteLine(
                                "No indexed builds. Run 's1atlas scan'."));
                    }

                    var installedCount = snapshot.Dependencies.Count(
                        item => item.IsInstalled);
                    var data = new StatusOutput(
                        HasCurrentBuild: true,
                        BuildId: snapshot.Build.BuildId,
                        ExecutableVersion: snapshot.Installation.ExecutableVersion,
                        SteamAppId: snapshot.Installation.SteamAppId,
                        SteamBuildId: snapshot.Installation.SteamBuildId,
                        CapturedAtUtc: snapshot.CapturedAtUtc,
                        InstalledDependencyCount: installedCount,
                        DependencyCount: snapshot.Dependencies.Count);

                    return commandOutput.Success(
                        data,
                        writer =>
                        {
                            writer.WriteLine($"Current build: {snapshot.Build.BuildId}");
                            writer.WriteLine(
                                $"Executable version: {snapshot.Installation.ExecutableVersion ?? "unknown"}");
                            writer.WriteLine(
                                $"Steam app ID: {snapshot.Installation.SteamAppId ?? "unknown"}");
                            writer.WriteLine(
                                $"Steam build ID: {snapshot.Installation.SteamBuildId ?? "unknown"}");
                            writer.WriteLine($"Captured: {snapshot.CapturedAtUtc:O}");
                            writer.WriteLine(
                                $"Dependencies installed: {installedCount}/{snapshot.Dependencies.Count}");
                        });
                },
                commandOutput,
                cancellationToken);
        });

        return command;
    }
}
