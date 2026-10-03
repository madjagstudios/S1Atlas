using System.CommandLine;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;
using S1Atlas.Cli.Output;
using S1Atlas.Application.Authority;

namespace S1Atlas.Cli.Commands;

internal static class RefsCommand
{
    public static Command Create(IndexQueryService service, FederatedIndexQueryService federatedService, InstalledBuildAuthorityResolver authorityResolver, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        IndexQueryCommandFactory.Create("refs", CliExamples.With("List indexed references to a resolved symbol.", "s1atlas refs \"Demo.Widget::count\" --json"), service, authorityResolver, repository, output, error, cancellationToken,
        executeWithGenerated: async (query, options, ct, includeGenerated) =>
        {
            var result = options.Scope == IndexQueryScope.Game
                ? await service.RefsAsync(query, options, ct, includeGenerated: includeGenerated)
                : await federatedService.RefsAsync(query, options, ct, includeGenerated: includeGenerated);
            return IndexQueryCommandFactory.ToOutput(result);
        },
        executeInIndexWithGenerated: async (query, run, limit, ct, includeGenerated) => IndexQueryCommandFactory.ToOutput(await service.RefsInIndexAsync(run, CodebaseKind.ScheduleI, CodeChannel.Installed, query, limit, ct, includeGenerated: includeGenerated)),
        includeScopeOptions: true);
}
