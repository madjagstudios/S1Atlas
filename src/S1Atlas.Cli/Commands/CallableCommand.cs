using System.CommandLine;
using S1Atlas.Application.Authority;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Cli.Commands;

internal static class CallableCommand
{
    public static Command Create(
        IndexQueryService service,
        InstalledBuildAuthorityResolver authorityResolver,
        IAtlasRepository repository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) =>
        IndexQueryCommandFactory.Create(
            "callable",
            service,
            authorityResolver,
            repository,
            output,
            error,
            cancellationToken,
            async (query, options, ct) =>
            {
                var result = await service.GetCallableSurfaceAsync(query, options, ct);
                return new IndexQueryOutput([], [], [], Resolution: result.Resolution, CallableSurface: result);
            },
            async (query, run, _, ct) =>
            {
                var result = await service.GetCallableSurfaceInIndexAsync(
                    run,
                    CodebaseKind.ScheduleI,
                    CodeChannel.Installed,
                    query,
                    ct);
                return new IndexQueryOutput([], [], [], Resolution: result.Resolution, CallableSurface: result);
            },
            options => options.Codebase == CodebaseKind.ScheduleI &&
                       options.Channel == CodeChannel.Installed &&
                       !options.AllChannels
                ? null
                : "callable queries require --codebase schedule-i and --channel installed.");
}
