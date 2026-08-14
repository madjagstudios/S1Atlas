using System.CommandLine;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Cli.Output;

namespace S1Atlas.Cli.Commands;

internal static class RefsCommand
{
    public static Command Create(IndexQueryService service, IAtlasRepository repository, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        IndexQueryCommandFactory.Create("refs", service, repository, output, error, cancellationToken, async (query, options, ct) =>
        {
            var result = await service.RefsAsync(query, options, ct);
            return new IndexQueryOutput(
                [],
                result.Relationships,
                [],
                Resolution: result.Resolution,
                BodyRecoveryStatus: result.BodyRecoveryStatus,
                CallerCompletenessBoundedByTargetResolution: result.CallerCompletenessBoundedByTargetResolution,
                CompletenessNotice: result.CompletenessNotice);
        });
}
