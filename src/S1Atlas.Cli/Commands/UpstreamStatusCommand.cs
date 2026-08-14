using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Upstream;

namespace S1Atlas.Cli.Commands;

internal static class UpstreamStatusCommand
{
    public static Command Create(string dataRoot, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var codebaseOption = new Option<string>("--codebase") { Description = "s1api or s1mapi." };
        var jsonOption = CommandOutput.CreateJsonOption();
        var command = new Command("status", "Show cached upstream status without network access.");
        command.Options.Add(codebaseOption); command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("upstream status", parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(() =>
            {
                var codebase = ParseCodebase(parseResult.GetValue(codebaseOption));
                var cache = new UpstreamSnapshotCache(dataRoot);
                var data = new UpstreamStatusOutput(codebase.ToString(), false, "24:00:00", false, null, UpstreamMatchKind.Unmatched.ToString(), "No commit was selected for the cached status check.");
                return commandOutput.Success(data, writer => writer.WriteLine($"{codebase}: no cached upstream commit selected (network not contacted)."));
            }, commandOutput, cancellationToken);
        });
        return command;
    }

    internal static CodebaseKind ParseCodebase(string? value) => (value ?? "s1api").ToLowerInvariant() switch
    {
        "s1api" => CodebaseKind.S1Api,
        "s1mapi" => CodebaseKind.S1MApi,
        _ => throw new ArgumentException("Upstream codebase must be s1api or s1mapi.", nameof(value))
    };
}
