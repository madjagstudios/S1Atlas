using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Indexing.Upstream;

namespace S1Atlas.Cli.Commands;

internal static class UpstreamSyncCommand
{
    public static Command Create(string dataRoot, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var codebaseArgument = new Argument<string?>("codebase") { Description = "s1api or s1mapi." };
        var commitOption = new Option<string>("--commit") { Description = "The exact 40-character commit SHA to cache." };
        var jsonOption = CommandOutput.CreateJsonOption();
        var command = new Command("sync", "Fetch and cache one exact upstream commit.");
        command.Arguments.Add(codebaseArgument); command.Options.Add(commitOption); command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("upstream sync", parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(() =>
            {
                var codebase = UpstreamStatusCommand.ParseCodebase(parseResult.GetValue(codebaseArgument));
                var commit = parseResult.GetValue(commitOption);
                if (string.IsNullOrWhiteSpace(commit)) return commandOutput.Failure(1, "CommitRequired", "upstream sync requires --commit so the cache is keyed by an exact commit SHA.");
                var repository = codebase == S1Atlas.Core.Indexing.CodebaseKind.S1Api
                    ? new S1Atlas.Core.Indexing.UpstreamRepositoryConfiguration(codebase, "KaBooMa", "S1API")
                    : new S1Atlas.Core.Indexing.UpstreamRepositoryConfiguration(codebase, "ifBars", "S1MAPI");
                using var client = new HttpClient();
                var result = new UpstreamSyncService(new GitHubUpstreamClient(client), new UpstreamSnapshotCache(dataRoot)).SyncAsync(repository, commit, cancellationToken).GetAwaiter().GetResult();
                var data = new UpstreamSyncOutput(result.Codebase.ToString(), result.CommitSha, result.ReusedCache, result.Stale, result.FileCount, result.Warning);
                return commandOutput.Success(data, writer => writer.WriteLine($"{result.Codebase} {result.CommitSha} | files {result.FileCount} | {(result.Stale ? "stale cache" : "synced")}"));
            }, commandOutput, cancellationToken);
        });
        return command;
    }
}
