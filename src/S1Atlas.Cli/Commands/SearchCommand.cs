using System.CommandLine;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Cli.Output;
using S1Atlas.Application.Authority;

namespace S1Atlas.Cli.Commands;

internal static class SearchCommand
{
    public static Command Create(IndexQueryService service, InstalledBuildAuthorityResolver authorityResolver, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        IndexQueryCommandFactory.Create("search", service, authorityResolver, repository, output, error, cancellationToken, async (query, options, ct) =>
        {
            var result = await service.SearchAsync(query, options, ct);
            return new IndexQueryOutput(
                result.Results,
                [],
                [],
                result.TotalCount,
                result.ReturnedCount,
                result.ResolutionStatus is { } status
                    ? new SymbolResolutionResult(status, null, [])
                    : null);
        }, async (query, run, limit, ct) =>
        {
            var result = await service.SearchInIndexAsync(run, CodebaseKind.ScheduleI, CodeChannel.Installed, query, limit, null, ct);
            return new IndexQueryOutput(result.Results, [], [], result.TotalCount, result.ReturnedCount,
                result.TotalCount == 0
                    ? new SymbolResolutionResult(SymbolResolutionStatus.NotFound, null, [])
                    : null);
        });
}
