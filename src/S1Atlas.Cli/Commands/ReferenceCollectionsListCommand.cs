using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;

namespace S1Atlas.Cli.Commands;

internal static class ReferenceCollectionsListCommand
{
    public static Command Create(
        IAtlasRepository atlasRepository,
        IIndexRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var jsonOption = CommandOutput.CreateJsonOption();
        var command = new Command("list", "List completed local reference-mod collections.");
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("reference collections list", parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(
                () => Execute(atlasRepository, repository, commandOutput, cancellationToken),
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    private static int Execute(
        IAtlasRepository atlasRepository,
        IIndexRepository repository,
        CommandOutput commandOutput,
        CancellationToken cancellationToken)
    {
        atlasRepository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();
        var items = new List<ReferenceCollectionListItem>();
        foreach (var run in repository.GetCompletedReferenceIndexesAsync(cancellationToken).GetAwaiter().GetResult())
        {
            var snapshot = repository.GetCodeSnapshotAsync(run.SnapshotId, cancellationToken).GetAwaiter().GetResult();
            if (snapshot is null) continue;
            var context = repository.GetReferenceIndexContextAsync(run.IndexId, cancellationToken).GetAwaiter().GetResult();
            if (context is null) continue;
            var mods = repository.GetCompletedReferenceModsAsync(run.IndexId, cancellationToken).GetAwaiter().GetResult()
                .OrderBy(mod => mod.ModId, StringComparer.Ordinal)
                .Select(mod => new ReferenceCollectionModOutput(
                    mod.ModId, mod.DisplayName, mod.Version, mod.License, mod.ContentSha256))
                .ToArray();
            items.Add(new ReferenceCollectionListItem(
                snapshot.SourceIdentity,
                run.IndexId,
                run.SnapshotId,
                context.BuildId,
                mods.Length,
                mods));
        }

        var unique = items
            .GroupBy(item => item.Collection, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Collection, StringComparer.Ordinal)
            .ToArray();
        var data = new ReferenceCollectionListOutput(unique);
        return commandOutput.Success(data, writer =>
        {
            foreach (var collection in data.Collections)
            {
                writer.WriteLine(
                    $"{collection.Collection} | {collection.IndexId} | build {collection.BuildId} | mods {collection.ModCount}");
                foreach (var mod in collection.Mods)
                    writer.WriteLine($"  {mod.ModId} | {mod.DisplayName} | {mod.Version} | {mod.Provenance}");
            }
        });
    }
}
