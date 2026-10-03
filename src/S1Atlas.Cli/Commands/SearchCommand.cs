using System.CommandLine;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Cli.Output;
using S1Atlas.Application.Authority;

namespace S1Atlas.Cli.Commands;

internal static class SearchCommand
{
    public static Command Create(IndexQueryService service, FederatedIndexQueryService federatedService, InstalledBuildAuthorityResolver authorityResolver, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        IndexQueryCommandFactory.Create("search", "Query the normalized code index across symbols, types, and methods.", service, authorityResolver, repository, output, error, cancellationToken,
        executeWithGenerated: async (query, options, ct, includeGenerated) =>
        {
            var result = options.Scope == IndexQueryScope.Game
                ? await service.SearchAsync(query, options, ct, includeGenerated: includeGenerated)
                : await federatedService.SearchAsync(query, options, ct, includeGenerated: includeGenerated);
            return new IndexQueryOutput(
                result.Results,
                [],
                [],
                result.TotalCount,
                result.ReturnedCount,
                Resolution: result.ResolutionStatus is { } status
                    ? new SymbolResolutionResult(status, null, [])
                    : null,
                SearchNotice: IndexQueryCommandFactory.RewordSearchNotice(result.SearchNotice));
        },
        executeInIndexWithGenerated: async (query, run, limit, ct, includeGenerated) =>
        {
            var result = await service.SearchInIndexAsync(run, CodebaseKind.ScheduleI, CodeChannel.Installed, query, limit, null, ct, includeGenerated: includeGenerated);
            return new IndexQueryOutput(result.Results, [], [], result.TotalCount, result.ReturnedCount,
                Resolution: result.TotalCount == 0
                    ? new SymbolResolutionResult(SymbolResolutionStatus.NotFound, null, [])
                    : null,
                SearchNotice: IndexQueryCommandFactory.RewordSearchNotice(result.SearchNotice));
        }, includeScopeOptions: true);
}
