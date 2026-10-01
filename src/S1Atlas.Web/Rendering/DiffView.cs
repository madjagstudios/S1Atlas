using System.Globalization;
using System.Text;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Indexing;
using S1Atlas.Web.Api;

namespace S1Atlas.Web.Rendering;

internal sealed record DiffModel(
    ServeDiffResult Result,
    SymbolKind? Kind);

internal static class DiffView
{
    internal static string Render(DiffModel model)
    {
        var result = model.Result;
        var body = new StringBuilder();
        body.Append(Html.PageTitle($"Diff {result.FromBuildId} → {result.ToBuildId}"));
        body.Append("<ul>");
        Fact(body, "from build", result.FromBuildId);
        Fact(body, "to build", result.ToBuildId);
        Fact(body, "codebase", result.Codebase);
        Fact(body, "channel", result.Channel);
        Fact(body, "symbols before", result.TotalSymbolsA);
        Fact(body, "symbols after", result.TotalSymbolsB);
        Fact(body, "added", result.Counts.Added);
        Fact(body, "removed", result.Counts.Removed);
        Fact(body, "method body changed", result.Counts.MethodBodyChanged);
        Fact(body, "relationships changed", result.Counts.RelationshipsChanged);
        Fact(body, "unchanged", result.Counts.Unchanged);
        body.Append("</ul>");
        body.Append(CultureInfo.InvariantCulture,
            $"<p>Changed symbols (showing {result.Changes.Count} of {result.TotalChanged}).</p>");
        if (result.Changes.Count > 0)
        {
            body.Append("<ul>");
            foreach (var change in result.Changes)
            {
                var name = change.SymbolId is null
                    ? Html.Escape(change.QualifiedName)
                    : $"<a href=\"/symbol/{Uri.EscapeDataString(change.SymbolId)}\">{Html.Escape(change.QualifiedName)}</a>";
                body.Append(CultureInfo.InvariantCulture,
                    $"<li>[{ClassificationLabel(change.Classification)}] {Html.Escape(change.Kind)} {name}" +
                    $"<br><code>{Html.Escape(change.SignatureBefore ?? "—")} → {Html.Escape(change.SignatureAfter ?? "—")}</code></li>");
            }

            body.Append("</ul>");
        }

        body.Append(RenderPager(model));
        return Html.Layout("Diff", body.ToString());
    }

    internal static string RenderPicker(
        IReadOnlyList<GameBuild> builds,
        string? from,
        string? to,
        SymbolKind? kind)
    {
        var body = new StringBuilder();
        body.Append(Html.PageTitle("Diff"));
        if (builds.Count < 2)
        {
            body.Append("<p>Diffing needs two indexed builds.</p>");
            return Html.Layout("Diff", body.ToString());
        }

        body.Append("<form method=\"get\" action=\"/diff\">");
        body.Append($"<label>From <select name=\"from\">{BuildOptions(builds, from)}</select></label> ");
        body.Append($"<label>To <select name=\"to\">{BuildOptions(builds, to)}</select></label> ");
        body.Append("<label>Kind <select name=\"kind\">");
        body.Append(KindOption(null, "Any kind", kind));
        foreach (var value in Enum.GetValues<SymbolKind>())
        {
            body.Append(KindOption(value, value.ToString(), kind));
        }

        body.Append("</select></label> ");
        body.Append("<button type=\"submit\">Compare</button></form>");
        return Html.Layout("Diff", body.ToString());
    }

    private static string BuildOptions(IReadOnlyList<GameBuild> builds, string? selected)
    {
        var options = new StringBuilder();
        foreach (var build in builds)
        {
            options.Append(CultureInfo.InvariantCulture,
                $"<option value=\"{Html.Escape(build.BuildId)}\"" +
                $"{(build.BuildId.Equals(selected, StringComparison.Ordinal) ? " selected" : string.Empty)}>" +
                $"{Html.Escape(build.BuildId)}</option>");
        }

        return options.ToString();
    }

    private static string KindOption(SymbolKind? value, string label, SymbolKind? selected) =>
        $"<option value=\"{(value?.ToString().ToLowerInvariant() ?? string.Empty)}\"" +
        $"{(value == selected ? " selected" : string.Empty)}>{Html.Escape(label)}</option>";

    private static void Fact(StringBuilder body, string name, string value) =>
        body.Append($"<li>FACT: {Html.Escape(name)} {Html.Escape(value)}.</li>");

    private static void Fact(StringBuilder body, string name, int value) =>
        Fact(body, name, value.ToString(CultureInfo.InvariantCulture));

    internal static string ClassificationLabel(DiffClassification classification) => classification switch
    {
        DiffClassification.Added => "Added",
        DiffClassification.Removed => "Removed",
        DiffClassification.MethodBodyChanged => "BodyChange",
        DiffClassification.RelationshipsChanged => "RelChange",
        DiffClassification.Unchanged => "Unchanged",
        _ => classification.ToString()
    };

    private static string RenderPager(DiffModel model)
    {
        var result = model.Result;
        var shown = result.Page * result.PageSize + result.Changes.Count;
        var body = new StringBuilder();
        body.Append("<p class=\"pager\">");
        body.Append(result.Page > 0
            ? $"<a href=\"{PageUrl(model, result.Page - 1)}\">Previous</a>"
            : "<span>Previous</span>");
        body.Append(shown < result.TotalChanged
            ? $"<a href=\"{PageUrl(model, result.Page + 1)}\">Next</a>"
            : "<span>Next</span>");
        body.Append("</p>");
        return body.ToString();
    }

    private static string PageUrl(DiffModel model, int page)
    {
        var result = model.Result;
        var query = $"/diff?from={Uri.EscapeDataString(result.FromBuildId)}" +
            $"&to={Uri.EscapeDataString(result.ToBuildId)}" +
            $"&codebase={Uri.EscapeDataString(result.Codebase.ToLowerInvariant())}" +
            $"&page={page}";
        if (model.Kind is { } kind)
        {
            query += $"&kind={Uri.EscapeDataString(kind.ToString().ToLowerInvariant())}";
        }

        return query;
    }
}
