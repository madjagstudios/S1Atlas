using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;

namespace S1Atlas.Cli.Commands;

internal static class BuildsCommand
{
    public static Command Create(
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var jsonOption = CommandOutput.CreateJsonOption();
        var command = new Command(
            "builds",
            "List all indexed Schedule I builds.");
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(
                "builds",
                parseResult.GetValue(jsonOption),
                output,
                error);
            return CommandExecution.Run(
                () =>
                {
                    repository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
                    var builds = repository
                        .ListBuildsAsync(cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    var data = new BuildsOutput(
                        builds
                            .Select(build => new BuildOutput(
                                build.BuildId,
                                build.FirstSeenAtUtc,
                                build.IsValid))
                            .ToArray());

                    return commandOutput.Success(
                        data,
                        writer =>
                        {
                            if (builds.Count == 0)
                            {
                                writer.WriteLine("No indexed builds.");
                                return;
                            }

                            foreach (var build in builds)
                            {
                                writer.WriteLine(
                                    $"{build.BuildId} | first seen {build.FirstSeenAtUtc:O} | " +
                                    (build.IsValid ? "valid" : "invalid"));
                            }
                        });
                },
                commandOutput,
                cancellationToken);
        });

        return command;
    }
}
