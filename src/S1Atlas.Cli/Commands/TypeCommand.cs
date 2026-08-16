using System.CommandLine;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Cli.Output;
using S1Atlas.Application.Authority;

namespace S1Atlas.Cli.Commands;

internal static class TypeCommand
{
    public static Command Create(IndexQueryService service, InstalledBuildAuthorityResolver authorityResolver, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        IndexQueryCommandFactory.Create("type", service, authorityResolver, repository, output, error, cancellationToken,
            async (query, options, ct) => new IndexQueryOutput(await service.FindAsync(query, SymbolKind.Type, options, ct), [], []),
            async (query, run, limit, ct) => new IndexQueryOutput(await service.FindInIndexAsync(run, CodebaseKind.ScheduleI, CodeChannel.Installed, query, SymbolKind.Type, limit, ct), [], []));
}
