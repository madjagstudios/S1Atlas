using System.CommandLine;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;
using S1Atlas.Cli.Output;
using S1Atlas.Application.Authority;

namespace S1Atlas.Cli.Commands;

internal static class CalleesCommand
{
    public static Command Create(IndexQueryService service, FederatedIndexQueryService federatedService, InstalledBuildAuthorityResolver authorityResolver, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        IndexQueryCommandFactory.Create("callees", service, authorityResolver, repository, output, error, cancellationToken,
        executeWithGenerated: async (query, options, ct, includeGenerated) =>
        {
            var result = options.Scope == IndexQueryScope.Game
                ? await service.CalleesAsync(query, options, ct, includeGenerated: includeGenerated)
                : await federatedService.CalleesAsync(query, options, ct, includeGenerated: includeGenerated);
            return new IndexQueryOutput(
                [],
                result.Relationships,
                [],
                Resolution: result.Resolution,
                BodyRecoveryStatus: result.BodyRecoveryStatus,
                CallerCompletenessBoundedByTargetResolution: result.CallerCompletenessBoundedByTargetResolution,
                CompletenessNotice: result.CompletenessNotice);
        },
        executeInIndexWithGenerated: async (query, run, limit, ct, includeGenerated) => ToOutput(await service.CalleesInIndexAsync(run, CodebaseKind.ScheduleI, CodeChannel.Installed, query, limit, ct, includeGenerated: includeGenerated)),
        includeScopeOptions: true);

    private static IndexQueryOutput ToOutput(RelationshipQuerySetResult result) => new([], result.Relationships, [],
        Resolution: result.Resolution, BodyRecoveryStatus: result.BodyRecoveryStatus,
        CallerCompletenessBoundedByTargetResolution: result.CallerCompletenessBoundedByTargetResolution,
        CompletenessNotice: result.CompletenessNotice);
}
