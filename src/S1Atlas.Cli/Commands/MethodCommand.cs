using System.CommandLine;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Cli.Output;

namespace S1Atlas.Cli.Commands;

internal static class MethodCommand
{
    public static Command Create(IndexQueryService service, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        IndexQueryCommandFactory.Create("method", service, repository, output, error, cancellationToken, async (query, options, ct) => new IndexQueryOutput(await service.FindAsync(query, SymbolKind.Method, options, ct), [], []));
}
