using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using S1Atlas.Application.Authority;
using S1Atlas.Application.Envelope;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;
using S1Atlas.Web.Api;
using S1Atlas.Web.Envelopes;
using S1Atlas.Web.Queries;
using S1Atlas.Web.Rendering;

namespace S1Atlas.Web.Endpoints;

internal static class StatusEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapMethods("/", ["GET", "HEAD"], LandingAsync);
        app.MapMethods("/api/status", ["GET", "HEAD"], ApiStatusAsync);
    }

    private static async Task<IResult> LandingAsync(ServeQueries queries, CancellationToken ct)
    {
        InstalledBuildAuthority authority;
        try
        {
            authority = await queries.ResolveAuthorityAsync(ct);
        }
        catch (AtlasStoreMissingException)
        {
            return ServeHttp.Html(LandingView.Render(new LandingModel(null, null, [])));
        }

        if (authority.Status != InstalledBuildAuthorityStatus.Resolved || authority.IndexRun is null)
        {
            return ServeHttp.Html(LandingView.Render(new LandingModel(authority, null, [])));
        }

        var model = await BuildLandingModelAsync(queries, authority, ct);
        return ServeHttp.Html(LandingView.Render(model));
    }

    private static async Task<IResult> ApiStatusAsync(ServeQueries queries, CancellationToken ct)
    {
        InstalledBuildAuthority authority;
        try
        {
            authority = await queries.ResolveAuthorityAsync(ct);
        }
        catch (AtlasStoreMissingException)
        {
            return Results.Json(
                ServeEnvelopes.StoreMissing<ServeStatusResult>(),
                ServeApiJson.Options);
        }

        if (authority.Status != InstalledBuildAuthorityStatus.Resolved || authority.IndexRun is null)
        {
            var data = new ServeStatusResult(
                authority.ResolvedBuildId, authority.Status, authority.Message ?? Html.AuthorityMessage(authority), []);
            return Results.Json(
                ToolEnvelope<ServeStatusResult>.Resolved(ServeEnvelopes.BuildFrom(authority), data),
                ServeApiJson.Options);
        }

        var model = await BuildLandingModelAsync(queries, authority, ct);
        var resolved = new ServeStatusResult(
            authority.ResolvedBuildId, authority.Status, null, [model.Game!, .. model.Apis]);
        return Results.Json(
            ToolEnvelope<ServeStatusResult>.Resolved(ServeEnvelopes.BuildFrom(authority), resolved),
            ServeApiJson.Options);
    }

    private static async Task<LandingModel> BuildLandingModelAsync(
        ServeQueries queries,
        InstalledBuildAuthority authority,
        CancellationToken ct)
    {
        var run = authority.IndexRun!;
        var game = new ServeIndexStatus(
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            run.IndexId,
            await queries.CountSymbolsAsync(run.IndexId, ct));
        var apis = new List<ServeIndexStatus>();
        var catalog = await queries.ListApiCatalogAsync(ct);
        foreach (var selection in catalog.Selections)
        {
            if (selection is { Availability: ApiIndexAvailability.Current, IndexId: { } indexId })
            {
                apis.Add(new ServeIndexStatus(
                    selection.Codebase,
                    selection.Channel,
                    indexId,
                    await queries.CountSymbolsAsync(indexId, ct)));
            }
        }

        return new LandingModel(authority, game, apis);
    }
}
