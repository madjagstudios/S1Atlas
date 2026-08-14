using System.CommandLine;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Cli.Output;

namespace S1Atlas.Cli.Commands;

internal static class SearchCommand
{
    public static Command Create(IndexQueryService service, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        IndexQueryCommandFactory.Create("search", service, repository, output, error, cancellationToken, async (query, options, ct) =>
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
        });
}
