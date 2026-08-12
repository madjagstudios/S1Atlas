using System.CommandLine;
using S1Atlas.Core.Storage;

namespace S1Atlas.Cli.Commands;

internal static class BuildsCommand
{
    public static Command Create(
        IAtlasRepository repository,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var command = new Command(
            "builds",
            "List all indexed Schedule I builds.");
        command.SetAction(_ =>
        {
            repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
            var builds = repository
                .ListBuildsAsync(cancellationToken)
                .GetAwaiter()
                .GetResult();

            if (builds.Count == 0)
            {
                output.WriteLine("No indexed builds.");
                return 0;
            }

            foreach (var build in builds)
            {
                output.WriteLine(
                    $"{build.BuildId} | {build.GameVersion ?? "unknown"} | {build.ScannedAtUtc:O}");
            }

            return 0;
        });

        return command;
    }
}
