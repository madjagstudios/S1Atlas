using System.Net;
using S1Atlas.Docs.Identity;

namespace S1Atlas.Docs.Rendering;

public sealed class HtmlPageRenderer
{
    private readonly PortalLinkResolver _links = new();

    public string Render(string pagePath, string title, string body, bool searchScripts = false)
    {
        var css = _links.RelativeHref(pagePath, "assets/site.css");
        var scripts = searchScripts
            ? $"<script src=\"{_links.RelativeHref(pagePath, "assets/search-index.js")}\"></script><script src=\"{_links.RelativeHref(pagePath, "assets/search.js")}\"></script>"
            : string.Empty;
        return $"<!doctype html>\n<html lang=\"en\">\n<head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>{Escape(title)}</title><link rel=\"stylesheet\" href=\"{css}\"></head>\n<body><header><a href=\"{_links.RelativeHref(pagePath, "index.html")}\">S1Atlas.Docs</a> · <a href=\"{_links.RelativeHref(pagePath, "search.html")}\">Search</a></header><main><h1>{Escape(title)}</h1>{body}</main>{scripts}</body>\n</html>\n";
    }

    public static string Escape(string value) => WebUtility.HtmlEncode(value);
}
