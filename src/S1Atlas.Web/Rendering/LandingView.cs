using System.Text;
using S1Atlas.Application.Authority;
using S1Atlas.Web.Api;

namespace S1Atlas.Web.Rendering;

internal sealed record LandingModel(
    InstalledBuildAuthority? Authority,
    ServeIndexStatus? Game,
    IReadOnlyList<ServeIndexStatus> Apis);

internal static class LandingView
{
    internal static string Render(LandingModel model)
    {
        var body = new StringBuilder();
        body.Append(Html.PageTitle("S1Atlas"));
        if (model.Authority is null)
        {
            body.Append("<p>No Atlas data store was found. Scan a game installation, then reload this page.</p>");
            return Html.Layout("Home", body.ToString());
        }

        var authority = model.Authority;
        if (authority.Status != InstalledBuildAuthorityStatus.Resolved)
        {
            body.Append($"<p>{Html.Escape(Html.AuthorityMessage(authority))}</p>");
            return Html.Layout("Home", body.ToString());
        }

        body.Append($"<p class=\"fact\">FACT: resolved build {Html.Escape(authority.ResolvedBuildId ?? "unknown")}.</p>");
        if (model.Game is { } game)
        {
            body.Append("<h2>Indexes</h2><ul>");
            body.Append($"<li>{Html.Badge(Html.CodebaseLabel(game.Codebase))} {Html.Escape(Html.ChannelLabel(game.Channel))}: FACT: {game.SymbolCount} symbols in this index.</li>");
            foreach (var api in model.Apis)
            {
                body.Append($"<li>{Html.Badge(Html.CodebaseLabel(api.Codebase))} {Html.Escape(Html.ChannelLabel(api.Channel))}: FACT: {api.SymbolCount} symbols in this index.</li>");
            }

            body.Append("</ul>");
        }
        else
        {
            body.Append("<p>No completed index exists yet.</p>");
        }

        body.Append("<h2>Search</h2>");
        body.Append("<form method=\"get\" action=\"/search\"><input type=\"search\" name=\"q\" aria-label=\"Query\"> ");
        body.Append("<button type=\"submit\">Search</button></form>");
        return Html.Layout("Home", body.ToString());
    }

}
