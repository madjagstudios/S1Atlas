using System.CommandLine;
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
        var command = new Command(
            "env",
            "Show the current game and modding dependency environment.");
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
                    error.WriteLine("No indexed builds. Run 's1atlas scan' first.");
                    return 1;
                }

                output.WriteLine($"Build: {snapshot.Build.BuildId}");
                output.WriteLine(
                    $"Executable version: {snapshot.Installation.ExecutableVersion ?? "unknown"}");
                output.WriteLine(
                    $"Steam app ID: {snapshot.Installation.SteamAppId ?? "unknown"}");
                output.WriteLine(
                    $"Steam build ID: {snapshot.Installation.SteamBuildId ?? "unknown"}");
                output.WriteLine(
                    $"Installation root: {snapshot.Installation.InstallationRoot ?? "unknown"}");
                output.WriteLine(
                    $"GameAssembly: {snapshot.Installation.GameAssemblyPath ?? "unknown"}");
                output.WriteLine(
                    $"Global metadata: {snapshot.Installation.GlobalMetadataPath ?? "unknown"}");
                foreach (var dependency in snapshot.Dependencies)
                {
                    output.WriteLine(DependencyDisplay.Format(dependency));
                }

                return 0;
            },
            error,
            cancellationToken));

        return command;
    }
}
