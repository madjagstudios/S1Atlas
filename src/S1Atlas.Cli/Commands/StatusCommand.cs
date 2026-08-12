using System.CommandLine;
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
        var command = new Command(
            "status",
            "Show the current Atlas build status.");
        command.SetAction(_ => CommandExecution.Run(
            () =>
            {
                repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
                var snapshot = repository
                    .GetCurrentSnapshotAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();

                if (snapshot is null)
                {
                    output.WriteLine("No indexed builds. Run 's1atlas scan'.");
                    return 0;
                }

                var installedCount = snapshot.Dependencies.Count(item => item.IsInstalled);
                output.WriteLine($"Current build: {snapshot.Build.BuildId}");
                output.WriteLine(
                    $"Executable version: {snapshot.Installation.ExecutableVersion ?? "unknown"}");
                output.WriteLine(
                    $"Steam app ID: {snapshot.Installation.SteamAppId ?? "unknown"}");
                output.WriteLine(
                    $"Steam build ID: {snapshot.Installation.SteamBuildId ?? "unknown"}");
                output.WriteLine($"Captured: {snapshot.CapturedAtUtc:O}");
                output.WriteLine(
                    $"Dependencies installed: {installedCount}/{snapshot.Dependencies.Count}");
                return 0;
            },
            error,
            cancellationToken));

        return command;
    }
}
