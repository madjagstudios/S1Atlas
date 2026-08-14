using System.CommandLine;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Cli.Output;

namespace S1Atlas.Cli.Commands;

internal static class CallersCommand
{
    public static Command Create(IndexQueryService service, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        IndexQueryCommandFactory.Create("callers", service, repository, output, error, cancellationToken, async (query, options, ct) => new IndexQueryOutput([], await service.RelationshipsAsync(query, true, options, ct), []));
}
