using System.Globalization;
using System.Text;
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
        body.Append("<ul>");
        foreach (var change in result.Changes)
        {
            var name = change.SymbolId is null
                ? Html.Escape(change.QualifiedName)
                : $"<a href=\"/symbol/{Uri.EscapeDataString(change.SymbolId)}\">{Html.Escape(change.QualifiedName)}</a>";
            body.Append(CultureInfo.InvariantCulture,
                $"<li>[{ClassificationLabel(change.Classification)}] {Html.Escape(change.Kind)} {name}</li>");
        }

        body.Append("</ul>");
        body.Append(RenderPager(model));
        return Html.Layout("Diff", body.ToString());
    }

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
