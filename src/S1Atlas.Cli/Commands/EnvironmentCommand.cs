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
                    $"Game version: {snapshot.Build.GameVersion ?? "unknown"}");
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
