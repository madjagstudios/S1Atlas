using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.ReferenceMods;

namespace S1Atlas.Cli.Commands;

internal static class ReferenceCollectionsCommand
{
    public static Command Create(
        ReferenceModManifestLoader loader,
        ReferenceModFileSelector selector,
        ReferenceModInputHasher hasher,
        IAtlasRepository atlasRepository,
        IIndexRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var command = new Command("collections", "Validate manifests and list completed local reference collections.");
        command.Subcommands.Add(ReferenceCollectionsValidateCommand.Create(
            loader, selector, hasher, output, error, cancellationToken));
        command.Subcommands.Add(ReferenceCollectionsListCommand.Create(
            atlasRepository, repository, output, error, cancellationToken));
        return command;
    }
}
