using System.CommandLine;
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
            repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
            var gamePath = parseResult.GetValue(gamePathOption)?.FullName;
            var snapshot = discovery
                .DiscoverAsync(gamePath, atlasVersion, cancellationToken)
                .GetAwaiter()
                .GetResult();

            if (snapshot is null)
            {
                error.WriteLine(
                    "Schedule I installation could not be found or is missing required IL2CPP files.");
                return 1;
            }

            repository
                .SaveSnapshotAsync(snapshot, cancellationToken)
                .GetAwaiter()
                .GetResult();

            output.WriteLine($"Indexed Schedule I build {snapshot.Build.BuildId}");
            output.WriteLine(
                $"Game version: {snapshot.Build.GameVersion ?? "unknown"}");
            foreach (var dependency in snapshot.Dependencies)
            {
                output.WriteLine(DependencyDisplay.Format(dependency));
            }

            return 0;
        });

        return command;
    }
}
