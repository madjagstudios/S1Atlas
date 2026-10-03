using System.CommandLine;
using S1Atlas.Application.Authority;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Cli.Commands;

internal static class DerivedCommand
{
    public static Command Create(
        IndexQueryService service,
        FederatedIndexQueryService federatedService,
        ReferenceModQueryService referenceService,
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) =>
        HierarchyCommandRunner.Create(
            "derived",
            CliExamples.With("Show the subclasses and implementers of a type, transitively.", "s1atlas derived \"Demo.Widget\" --json"),
            true,
            true,
            service,
            federatedService,
            referenceService,
            authorityResolver,
            repository,
            output,
            error,
            cancellationToken,
            (current, run, query, limit, depth, offset, ct) => current.DerivedInIndexAsync(run, CodebaseKind.ScheduleI, CodeChannel.Installed, query, limit, depth, offset, ct),
            (current, query, options, depth, offset, ct) => current.DerivedAsync(query, options, depth, offset, ct),
            (federated, query, options, depth, offset, ct, referenceIndexId) => federated.DerivedAsync(query, options, depth, offset, ct, referenceIndexId));
}
