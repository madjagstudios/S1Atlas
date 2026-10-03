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
        IndexQueryCommandFactory.Create("callees", CliExamples.With("List indexed callees of a resolved method.", "s1atlas callees \"Demo.Widget::Run()\" --json"), service, authorityResolver, repository, output, error, cancellationToken,
        executeWithGeneratedAndDelegates: async (query, options, ct, includeGenerated, includeDelegates) =>
        {
            var result = options.Scope == IndexQueryScope.Game
                ? await service.CalleesAsync(query, options, ct, includeGenerated: includeGenerated, includeDelegates: includeDelegates)
                : await federatedService.CalleesAsync(query, options, ct, includeGenerated: includeGenerated, includeDelegates: includeDelegates);
            return new IndexQueryOutput(
                [],
                result.Relationships,
                [],
                Resolution: result.Resolution,
                BodyRecoveryStatus: result.BodyRecoveryStatus,
                CallerCompletenessBoundedByTargetResolution: result.CallerCompletenessBoundedByTargetResolution,
                CompletenessNotice: result.CompletenessNotice);
        },
        executeInIndexWithGeneratedAndDelegates: async (query, run, limit, ct, includeGenerated, includeDelegates) => ToOutput(await service.CalleesInIndexAsync(run, CodebaseKind.ScheduleI, CodeChannel.Installed, query, limit, ct, includeGenerated: includeGenerated, includeDelegates: includeDelegates)),
        includeScopeOptions: true);

    private static IndexQueryOutput ToOutput(RelationshipQuerySetResult result) => new([], result.Relationships, [],
        Resolution: result.Resolution, BodyRecoveryStatus: result.BodyRecoveryStatus,
        CallerCompletenessBoundedByTargetResolution: result.CallerCompletenessBoundedByTargetResolution,
        CompletenessNotice: result.CompletenessNotice);
}
