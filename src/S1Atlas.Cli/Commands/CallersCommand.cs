using System.CommandLine;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;
using S1Atlas.Cli.Output;
using S1Atlas.Application.Authority;

namespace S1Atlas.Cli.Commands;

internal static class CallersCommand
{
    public static Command Create(IndexQueryService service, FederatedIndexQueryService federatedService, InstalledBuildAuthorityResolver authorityResolver, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        IndexQueryCommandFactory.Create("callers", service, authorityResolver, repository, output, error, cancellationToken, async (query, options, ct) =>
        {
            var result = options.Scope == IndexQueryScope.Game
                ? await service.CallersAsync(query, options, ct)
                : await federatedService.CallersAsync(query, options, ct);
            return IndexQueryCommandFactory.ToOutput(result);
        }, async (query, run, limit, ct) => IndexQueryCommandFactory.ToOutput(await service.CallersInIndexAsync(run, CodebaseKind.ScheduleI, CodeChannel.Installed, query, limit, ct)),
        includeScopeOptions: true);
}
