using System.CommandLine;
using S1Atlas.Application.Authority;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.ReferenceMods;
using S1Atlas.Indexing.Workflow;

namespace S1Atlas.Cli.Commands;

internal static class ReferenceCommand
{
    public static Command Create(
        ReferenceModIndexWorkflow workflow,
        ReferenceModManifestLoader loader,
        ReferenceModFileSelector selector,
        ReferenceModInputHasher hasher,
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository atlasRepository,
        IIndexRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var command = new Command("reference", "Index and inspect explicitly selected local reference mods.");
        command.Subcommands.Add(ReferenceIndexCommand.Create(
            workflow, loader, authorityResolver, atlasRepository, repository, output, error, cancellationToken));
        command.Subcommands.Add(ReferenceCollectionsCommand.Create(
            loader, selector, hasher, atlasRepository, repository, output, error, cancellationToken));
        return command;
    }
}
