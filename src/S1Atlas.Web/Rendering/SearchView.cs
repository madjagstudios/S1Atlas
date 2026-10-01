using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Web.Queries;

namespace S1Atlas.Web.Rendering;

internal sealed record SearchModel(
    string Query,
    string? Kind,
    CodebaseKind Codebase,
    string Channel,
    int Page,
    int PageSize,
    SymbolSearchResult Result,
    string? Build,
    bool ResultsLinkable);

internal static class SearchView
{
    internal static string Render(SearchModel model)
    {
        var body = new StringBuilder();
        body.Append(Html.PageTitle("Search"));
        body.Append(RenderForm(model));
        var total = model.Result.TotalCount;
        var scope = model.Build is null ? string.Empty : $" for build {model.Build}";
        body.Append($"<p class=\"fact\">FACT: {total} matches in {Html.Escape(Html.CodebaseLabel(model.Codebase))} ({Html.Escape(model.Channel)}){Html.Escape(scope)}.</p>");
        if (total == 0)
        {
            body.Append("<p class=\"derived\">DERIVED: showing 0 of 0 matches.</p>");
            return Html.Layout("Search", body.ToString());
        }

        if (model.Result.Results.Count == 0)
        {
            body.Append($"<p class=\"derived\">DERIVED: showing 0 of {total} matches.</p>");
            return Html.Layout("Search", body.ToString());
        }

        var from = model.Page * model.PageSize + 1;
        var to = Math.Min(from + model.Result.Results.Count - 1, total);
        body.Append($"<p class=\"derived\">DERIVED: showing {from}&ndash;{to} of {total} matches.</p>");
        if (!model.ResultsLinkable)
        {
            body.Append("<p>Symbol pages cover the current build only.</p>");
        }

        body.Append("<ul>");
        foreach (var result in model.Result.Results)
        {
            body.Append("<li>");
            body.Append(Html.CodebaseBadge(result.Codebase));
            body.Append($"<span class=\"badge\">{Html.Escape(Html.KindLabel(result.Kind))}</span> ");
            body.Append(model.ResultsLinkable
                ? $"<a href=\"/symbol/{Html.UrlSegment(result.SymbolId)}\">{Html.Escape(result.QualifiedName)}</a>"
                : Html.Escape(result.QualifiedName));
            if (!string.IsNullOrWhiteSpace(result.Signature))
            {
                body.Append($"<br><code>{Html.Escape(result.Signature)}</code>");
            }

            body.Append("</li>");
        }

        body.Append("</ul>");
        body.Append(RenderPager(model, total, to));
        return Html.Layout("Search", body.ToString());
    }

    internal static string RenderForm(SearchModel model)
    {
        var body = new StringBuilder();
        body.Append("<form method=\"get\" action=\"/search\">");
        body.Append($"<input type=\"search\" name=\"q\" value=\"{Html.Escape(model.Query)}\" aria-label=\"Query\"> ");
        body.Append("<select name=\"kind\" aria-label=\"Kind\">");
        body.Append(Option("", "Any kind", model.Kind));
        foreach (var kind in new[] { "Type", "Constructor", "Method", "Field", "Property", "Event" })
        {
            body.Append(Option(kind, kind, model.Kind));
        }

        body.Append("</select> ");
        body.Append("<select name=\"codebase\" aria-label=\"Codebase\">");
        body.Append(Option("schedule-i", "Schedule I", CodebaseValue(model.Codebase)));
        body.Append(Option("s1api", "S1API", CodebaseValue(model.Codebase)));
        body.Append(Option("s1mapi", "S1MAPI", CodebaseValue(model.Codebase)));
        body.Append("</select> ");
        if (model.Build is not null)
        {
            body.Append($"<input type=\"hidden\" name=\"build\" value=\"{Html.Escape(model.Build)}\"> ");
        }

        body.Append("<button type=\"submit\">Search</button></form>");
        return body.ToString();
    }

    private static string Option(string value, string label, string? selected) =>
        $"<option value=\"{value}\"{(value.Equals(selected, StringComparison.OrdinalIgnoreCase) ? " selected" : string.Empty)}>{Html.Escape(label)}</option>";

    internal static string CodebaseValue(CodebaseKind codebase) => codebase switch
    {
        CodebaseKind.ScheduleI => "schedule-i",
        CodebaseKind.S1Api => "s1api",
        CodebaseKind.S1MApi => "s1mapi",
        _ => codebase.ToString().ToLowerInvariant()
    };

    private static string RenderPager(SearchModel model, int total, int to)
    {
        var body = new StringBuilder();
        body.Append("<p class=\"pager\">");
        if (model.Page > 0)
        {
            body.Append($"<a href=\"{PageUrl(model, model.Page - 1)}\">Previous</a>");
        }
        else
        {
            body.Append("<span>Previous</span>");
        }

        if (to < total)
        {
            body.Append($"<a href=\"{PageUrl(model, model.Page + 1)}\">Next</a>");
        }
        else
        {
            body.Append("<span>Next</span>");
        }

        body.Append("</p>");
        return body.ToString();
    }

    private static string PageUrl(SearchModel model, int page)
    {
        var query = $"/search?q={Uri.EscapeDataString(model.Query)}&codebase={CodebaseValue(model.Codebase)}&page={page}";
        if (!string.IsNullOrEmpty(model.Kind))
        {
            query += $"&kind={Uri.EscapeDataString(model.Kind)}";
        }

        if (model.Build is not null)
        {
            query += $"&build={Uri.EscapeDataString(model.Build)}";
        }

        return query;
    }
}
