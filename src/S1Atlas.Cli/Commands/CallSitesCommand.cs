using System.CommandLine;
using S1Atlas.Application.Authority;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Cli.Commands;

internal static class CallSitesCommand
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
        IndexQueryCommandFactory.Create(
            "callsites",
            service,
            authorityResolver,
            repository,
            output,
            error,
            cancellationToken,
            async (query, options, ct) => IndexQueryCommandFactory.ToOutput(
                options.Scope == IndexQueryScope.Game
                    ? await service.CallSitesAsync(query, options, ct)
                    : await federatedService.CallSitesAsync(query, options, ct)),
            async (query, run, limit, ct) => IndexQueryCommandFactory.ToOutput(
                await service.CallSitesInIndexAsync(
                    run,
                    CodebaseKind.ScheduleI,
                    CodeChannel.Installed,
                    query,
                    limit,
                    ct)),
            includeScopeOptions: true,
            referenceService: referenceService,
            executeWithReferenceIndex: async (query, options, referenceIndexId, ct) => IndexQueryCommandFactory.ToOutput(
                await federatedService.CallSitesAsync(query, options, ct, referenceIndexId)));
}
