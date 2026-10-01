using System.Text;
using S1Atlas.Application.Authority;
using S1Atlas.Core.Indexing;
using S1Atlas.Web.Api;

namespace S1Atlas.Web.Rendering;

internal sealed record BuildListModel(
    IReadOnlyList<InstalledBuildHistoryEntry> Entries);

internal sealed record BuildDetailModel(
    InstalledBuildHistoryEntry Entry,
    bool IsCurrent,
    int? GameSymbolCount,
    IReadOnlyList<ServeIndexStatus> ApiSurfaces,
    IReadOnlyList<ServeAdjacentDiff> AdjacentDiffs);

internal static class BuildsView
{
    internal static string RenderList(BuildListModel model)
    {
        var body = new StringBuilder();
        body.Append(Html.PageTitle("Builds"));
        if (model.Entries.Count == 0)
        {
            body.Append("<p>No indexed builds.</p>");
            return Html.Layout("Builds", body.ToString());
        }

        foreach (var entry in model.Entries)
        {
            var facts = $"{Html.Escape(Html.BuildStatusLabel(entry.Status))} (first seen {Html.Escape(entry.Build.FirstSeenAtUtc.ToString("O"))})";
            body.Append(entry.IsNavigable
                ? $"<p><a href=\"/builds/{Html.UrlSegment(entry.Build.BuildId)}\">{Html.Escape(entry.Build.BuildId)}</a> — {facts}</p>"
                : $"<p>{Html.Escape(entry.Build.BuildId)} — {facts} (not navigable)</p>");
        }

        return Html.Layout("Builds", body.ToString());
    }

    internal static string RenderDetail(BuildDetailModel model)
    {
        var entry = model.Entry;
        var buildId = entry.Build.BuildId;
        var body = new StringBuilder();
        body.Append(Html.PageTitle(buildId));
        body.Append("<ul>");
        body.Append($"<li>FACT: status {Html.Escape(Html.BuildStatusLabel(entry.Status))}.</li>");
        body.Append($"<li>FACT: first seen {Html.Escape(entry.Build.FirstSeenAtUtc.ToString("O"))}.</li>");
        body.Append(entry.Build.IsValid
            ? "<li>FACT: build snapshot valid.</li>"
            : "<li>FACT: build snapshot invalid.</li>");
        body.Append(entry.Authority?.ExtractionId is null
            ? "<li>Preferred extraction: none yet.</li>"
            : $"<li>Preferred extraction: <code>{Html.Escape(entry.Authority?.ExtractionId)}</code>.</li>");
        body.Append(entry.Authority?.IndexId is null
            ? "<li>Completed index: none yet.</li>"
            : $"<li>Completed index: <code>{Html.Escape(entry.Authority?.IndexId)}</code>.</li>");
        body.Append("</ul>");

        body.Append("<h2>Code surfaces</h2>");
        if (!entry.IsNavigable || model.GameSymbolCount is null)
        {
            body.Append("<p>Code surfaces are available once this build is indexed and verified.</p>");
        }
        else
        {
            body.Append("<ul>");
            body.Append(
                $"<li>Schedule I (Installed): FACT: {model.GameSymbolCount} symbols in this index. " +
                $"<a href=\"/search?build={Html.UrlSegment(buildId)}\">Search this build</a></li>");
            foreach (var surface in model.ApiSurfaces)
            {
                var label = $"{Html.CodebaseLabel(surface.Codebase)} ({Html.ChannelLabel(surface.Channel)})";
                var codebase = SearchView.CodebaseValue(surface.Codebase);
                body.Append(
                    $"<li>{Html.Escape(label)}: FACT: {surface.SymbolCount} symbols in this index. " +
                    $"<a href=\"/search?{Html.Escape($"codebase={codebase}")}\">Search {Html.Escape(Html.CodebaseLabel(surface.Codebase))}</a></li>");
            }

            body.Append("</ul>");
        }

        body.Append("<h2>Adjacent diffs</h2>");
        if (model.AdjacentDiffs.Count == 0)
        {
            body.Append("<p>No adjacent diffs yet.</p>");
        }
        else
        {
            body.Append("<ul>");
            foreach (var diff in model.AdjacentDiffs)
            {
                var url = $"/diff?from={diff.FromBuildId}&to={diff.ToBuildId}";
                body.Append(
                    $"<li><a href=\"{Html.Escape(url)}\">Diff {Html.Escape(diff.FromBuildId)} → {Html.Escape(diff.ToBuildId)}</a></li>");
            }

            body.Append("</ul>");
        }

        body.Append("<h2>Environment</h2>");
        body.Append(model.IsCurrent
            ? "<p><a href=\"/environment\">Environment for this build</a></p>"
            : "<p>Environment facts are recorded for the current build only.</p>");
        return Html.Layout(buildId, body.ToString());
    }
}
