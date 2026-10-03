using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using S1Atlas.Application.Authority;
using S1Atlas.Application.Envelope;
using S1Atlas.Application.Readiness;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;
using S1Atlas.Web.Api;
using S1Atlas.Web.Envelopes;
using S1Atlas.Web.Queries;
using S1Atlas.Web.Rendering;

namespace S1Atlas.Web.Endpoints;

internal static class BuildsEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapMethods("/builds", ["GET", "HEAD"], BuildsPageAsync);
        app.MapMethods("/builds/{buildId}", ["GET", "HEAD"], BuildPageAsync);
        app.MapMethods("/api/builds", ["GET", "HEAD"], ApiBuildsAsync);
        app.MapMethods("/api/builds/{buildId}", ["GET", "HEAD"], ApiBuildDetailAsync);
    }

    private static async Task<IResult> BuildsPageAsync(
        ServeQueries queries,
        CancellationToken ct)
    {
        try
        {
            var history = await queries.GetHistoryAsync(ct);
            var entries = history.Entries
                .OrderByDescending(entry => entry.Build.FirstSeenAtUtc)
                .ToArray();
            return ServeHttp.Html(BuildsView.RenderList(new BuildListModel(entries)));
        }
        catch (AtlasStoreMissingException)
        {
            return ServeHttp.Html(Html.Layout(
                "Builds",
                "<h1>Builds</h1><p>No Atlas data store was found.</p>"));
        }
    }

    private static async Task<IResult> BuildPageAsync(
        string buildId,
        ServeQueries queries,
        CancellationToken ct)
    {
        try
        {
            var history = await queries.GetHistoryAsync(ct);
            var entry = history.Entries.FirstOrDefault(candidate =>
                candidate.Build.BuildId.Equals(buildId, StringComparison.Ordinal));
            if (entry is null)
            {
                return ServeHttp.Html(Html.Layout(
                    "Unknown build",
                    $"<h1>Unknown build</h1><p>No indexed build has ID '{Html.Escape(buildId)}'.</p>"),
                    StatusCodes.Status404NotFound);
            }

            var current = await queries.GetCurrentSnapshotAsync(ct);
            var isCurrent = current is not null
                && current.Build.BuildId.Equals(buildId, StringComparison.Ordinal);
            int? gameCount = null;
            List<ServeIndexStatus> apiSurfaces = [];
            if (entry.IsNavigable && entry.Authority?.IndexRun is { } run)
            {
                gameCount = await queries.CountSymbolsAsync(run.IndexId, ct);
                apiSurfaces.AddRange(await ApiSurfacesAsync(queries, ct));
            }

            var diffs = history.AdjacentPairs
                .Where(pair => pair.Before.Build.BuildId.Equals(buildId, StringComparison.Ordinal)
                    || pair.After.Build.BuildId.Equals(buildId, StringComparison.Ordinal))
                .Select(pair => new ServeAdjacentDiff(pair.Before.Build.BuildId, pair.After.Build.BuildId))
                .ToArray();
            return ServeHttp.Html(BuildsView.RenderDetail(new BuildDetailModel(
                entry, isCurrent, gameCount, apiSurfaces, diffs)));
        }
        catch (AtlasStoreMissingException)
        {
            return ServeHttp.Html(Html.Layout(
                "Builds",
                "<h1>Builds</h1><p>No Atlas data store was found.</p>"));
        }
    }

    private static async Task<IResult> ApiBuildsAsync(
        ServeQueries queries,
        CancellationToken ct)
    {
        try
        {
            var current = await queries.GetCurrentSnapshotAsync(ct);
            var builds = await queries.ListBuildsAsync(ct);
            var items = new List<ServeBuildListItem>(builds.Count);
            foreach (var build in builds)
            {
                var preferred = await queries.ResolvePreferredExtractionAsync(build.BuildId, ct);
                var authority = await queries.ResolveAuthorityAsync(ct, build.BuildId);
                items.Add(new ServeBuildListItem(
                    build.BuildId,
                    build.FirstSeenAtUtc,
                    build.IsValid,
                    string.Equals(current?.Build.BuildId, build.BuildId, StringComparison.Ordinal),
                    preferred is not null
                        && string.Equals(preferred.Extraction.BuildId, build.BuildId, StringComparison.Ordinal),
                    authority.Status == InstalledBuildAuthorityStatus.Resolved));
            }

            return ServeHttp.Envelope(ToolEnvelope<ServeBuildListResult>.Resolved(
                null,
                new ServeBuildListResult(items),
                new ProvenanceEntry(ProvenanceClassification.Fact, "atlas-build-list", null, null, null),
                new ProvenanceEntry(ProvenanceClassification.Derived, "installed-build-availability", null, null, null)));
        }
        catch (AtlasStoreMissingException)
        {
            return ServeHttp.Envelope(ServeEnvelopes.StoreMissing<ServeBuildListResult>());
        }
    }

    private static async Task<IResult> ApiBuildDetailAsync(
        string buildId,
        ServeQueries queries,
        CancellationToken ct)
    {
        try
        {
            var history = await queries.GetHistoryAsync(ct);
            var entry = history.Entries.FirstOrDefault(candidate =>
                candidate.Build.BuildId.Equals(buildId, StringComparison.Ordinal));
            if (entry is null)
            {
                return ServeHttp.Envelope(ToolEnvelope<ServeBuildResult>.NotFound(
                    new BuildContext(buildId, null, null, null, "ScheduleI", "Installed", false),
                    new ToolError("BuildNotFound", "The requested build was not found.", ReadinessFixCommands.Builds),
                    new ProvenanceEntry(
                        ProvenanceClassification.Derived, "build-selection", buildId, null, null)));
            }

            var current = await queries.GetCurrentSnapshotAsync(ct);
            var authority = entry.Authority;
            var surfaces = new List<ServeIndexStatus>();
            if (entry.IsNavigable && authority?.IndexRun is { } run)
            {
                surfaces.Add(new ServeIndexStatus(
                    CodebaseKind.ScheduleI,
                    CodeChannel.Installed,
                    run.IndexId,
                    await queries.CountSymbolsAsync(run.IndexId, ct)));
                surfaces.AddRange(await ApiSurfacesAsync(queries, ct));
            }

            var diffs = history.AdjacentPairs
                .Where(pair => pair.Before.Build.BuildId.Equals(buildId, StringComparison.Ordinal)
                    || pair.After.Build.BuildId.Equals(buildId, StringComparison.Ordinal))
                .Select(pair => new ServeAdjacentDiff(pair.Before.Build.BuildId, pair.After.Build.BuildId))
                .ToArray();
            var data = new ServeBuildResult(
                entry.Build.BuildId,
                entry.Build.FirstSeenAtUtc,
                entry.Build.IsValid,
                current is not null && current.Build.BuildId.Equals(buildId, StringComparison.Ordinal),
                Html.BuildStatusLabel(entry.Status),
                entry.IsNavigable,
                authority?.Message,
                authority?.ExtractionId,
                authority?.IndexId,
                surfaces,
                diffs,
                current is not null && current.Build.BuildId.Equals(buildId, StringComparison.Ordinal));
            var build = authority is null
                ? new BuildContext(null, entry.Build.BuildId, null, null, "ScheduleI", "Installed", false)
                : ServeEnvelopes.BuildFrom(authority);
            return ServeHttp.Envelope(ToolEnvelope<ServeBuildResult>.Resolved(
                build,
                data,
                new ProvenanceEntry(
                    ProvenanceClassification.Fact,
                    "atlas-build-list",
                    entry.Build.BuildId,
                    authority?.ExtractionId,
                    authority?.IndexId)));
        }
        catch (AtlasStoreMissingException)
        {
            return ServeHttp.Envelope(ServeEnvelopes.StoreMissing<ServeBuildResult>());
        }
    }

    private static async Task<IReadOnlyList<ServeIndexStatus>> ApiSurfacesAsync(
        ServeQueries queries,
        CancellationToken ct)
    {
        var catalog = await queries.ListApiCatalogAsync(ct);
        var surfaces = new List<ServeIndexStatus>();
        foreach (var selection in catalog.Selections)
        {
            if (selection is { Availability: ApiIndexAvailability.Current, IndexId: { } indexId })
            {
                surfaces.Add(new ServeIndexStatus(
                    selection.Codebase,
                    selection.Channel,
                    indexId,
                    await queries.CountSymbolsAsync(indexId, ct)));
            }
        }

        return surfaces;
    }
}
