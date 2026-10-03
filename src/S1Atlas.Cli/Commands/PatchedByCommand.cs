using System.CommandLine;
using S1Atlas.Application.Authority;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Cli.Commands;

internal static class PatchedByCommand
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
            "patched-by",
            CliExamples.With("Find reference-mod Harmony patches targeting one game method, including unresolved patches that name the method.", "s1atlas patched-by \"Demo.Widget::Run()\" --scope reference --collection my-mods"),
            service,
            authorityResolver,
            repository,
            output,
            error,
            cancellationToken,
            async (query, options, ct) => IndexQueryCommandFactory.ToOutput(
                options.Scope == IndexQueryScope.Game
                    ? await service.PatchesAsync(query, options, ct)
                    : await federatedService.PatchesAsync(query, options, ct)),
            async (query, run, limit, ct) => IndexQueryCommandFactory.ToOutput(
                await service.PatchesInIndexAsync(
                    run,
                    CodebaseKind.ScheduleI,
                    CodeChannel.Installed,
                    query,
                    ct)),
            includeScopeOptions: true,
            referenceService: referenceService,
            executeWithReferenceIndex: async (query, options, referenceIndexId, ct) => IndexQueryCommandFactory.ToOutput(
                await federatedService.PatchesAsync(query, options, ct, referenceIndexId: referenceIndexId)));
}
