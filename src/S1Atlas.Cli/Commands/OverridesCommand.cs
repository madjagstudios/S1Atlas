using System.CommandLine;
using S1Atlas.Application.Authority;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Cli.Commands;

internal static class OverridesCommand
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
            "overrides",
            CliExamples.With("Show the base and interface slots a method fills, up to the root.", "s1atlas overrides \"Demo.Widget::Run()\" --json"),
            false,
            false,
            service,
            federatedService,
            referenceService,
            authorityResolver,
            repository,
            output,
            error,
            cancellationToken,
            (current, run, query, limit, _, _, ct) => current.OverridesInIndexAsync(run, CodebaseKind.ScheduleI, CodeChannel.Installed, query, limit, ct),
            (current, query, options, _, _, ct) => current.OverridesAsync(query, options, ct),
            (federated, query, options, _, _, ct, referenceIndexId) => federated.OverridesAsync(query, options, ct, referenceIndexId));
}
