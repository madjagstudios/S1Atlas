using System.CommandLine;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Cli.Output;
using S1Atlas.Application.Authority;

namespace S1Atlas.Cli.Commands;

internal static class MethodCommand
{
    public static Command Create(IndexQueryService service, FederatedIndexQueryService federatedService, InstalledBuildAuthorityResolver authorityResolver, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        IndexQueryCommandFactory.Create("method", CliExamples.With("Resolve and inspect indexed method definitions.", "s1atlas method \"Demo.Widget::Run()\" --json"), service, authorityResolver, repository, output, error, cancellationToken,
        executeWithGenerated: async (query, options, ct, includeGenerated) =>
        {
            if (options.Scope == IndexQueryScope.Game)
                return new IndexQueryOutput(await service.FindAsync(query, SymbolKind.Method, options, ct, includeGenerated), [], []);
            var result = await federatedService.SearchAsync(query, options, ct, SymbolKind.Method, includeGenerated);
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
            new IndexQueryOutput(await service.FindInIndexAsync(run, CodebaseKind.ScheduleI, CodeChannel.Installed, query, SymbolKind.Method, limit, ct, includeGenerated), [], []),
        includeScopeOptions: true);
}
