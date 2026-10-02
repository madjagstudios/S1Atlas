using System.CommandLine;
using S1Atlas.Application.Authority;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Cli.Commands;

internal static class OverriddenByCommand
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
            "overridden-by",
            "Show the methods that override or implement a method, transitively.",
            true,
            false,
            service,
            federatedService,
            referenceService,
            authorityResolver,
            repository,
            output,
            error,
            cancellationToken,
            (current, run, query, limit, depth, _, ct) => current.OverriddenByInIndexAsync(run, CodebaseKind.ScheduleI, CodeChannel.Installed, query, limit, depth, ct),
            (current, query, options, depth, _, ct) => current.OverriddenByAsync(query, options, depth, ct),
            (federated, query, options, depth, _, ct, referenceIndexId) => federated.OverriddenByAsync(query, options, depth, ct, referenceIndexId));
}
