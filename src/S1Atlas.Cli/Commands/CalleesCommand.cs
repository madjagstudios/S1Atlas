using System.CommandLine;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;
using S1Atlas.Cli.Output;
using S1Atlas.Application.Authority;

namespace S1Atlas.Cli.Commands;

internal static class CalleesCommand
{
    public static Command Create(IndexQueryService service, InstalledBuildAuthorityResolver authorityResolver, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        IndexQueryCommandFactory.Create("callees", service, authorityResolver, repository, output, error, cancellationToken, async (query, options, ct) =>
        {
            var result = await service.CalleesAsync(query, options, ct);
            return new IndexQueryOutput(
                [],
                result.Relationships,
                [],
                Resolution: result.Resolution,
                BodyRecoveryStatus: result.BodyRecoveryStatus,
                CallerCompletenessBoundedByTargetResolution: result.CallerCompletenessBoundedByTargetResolution,
                CompletenessNotice: result.CompletenessNotice);
        }, async (query, run, limit, ct) => ToOutput(await service.CalleesInIndexAsync(run, CodebaseKind.ScheduleI, CodeChannel.Installed, query, limit, ct)));

    private static IndexQueryOutput ToOutput(RelationshipQuerySetResult result) => new([], result.Relationships, [],
        Resolution: result.Resolution, BodyRecoveryStatus: result.BodyRecoveryStatus,
        CallerCompletenessBoundedByTargetResolution: result.CallerCompletenessBoundedByTargetResolution,
        CompletenessNotice: result.CompletenessNotice);
}
