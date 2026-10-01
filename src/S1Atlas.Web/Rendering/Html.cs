using System.Net;
using S1Atlas.Application.Authority;
using S1Atlas.Core.Indexing;

namespace S1Atlas.Web.Rendering;

internal static class Html
{
    internal static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    internal static string UrlSegment(string value) => Uri.EscapeDataString(value);

    internal static string BuildStatusLabel(InstalledBuildHistoryStatus status) => status switch
    {
        InstalledBuildHistoryStatus.IndexedVerified => "Indexed and verified",
        InstalledBuildHistoryStatus.NotIndexed => "Not indexed",
        InstalledBuildHistoryStatus.IntegrityFailed => "Integrity check failed",
        _ => status.ToString()
    };

    internal static string CodebaseLabel(CodebaseKind codebase) => codebase switch
    {
        CodebaseKind.ScheduleI => "Schedule I",
        CodebaseKind.S1Api => "S1API",
        CodebaseKind.S1MApi => "S1MAPI",
        CodebaseKind.ReferenceMod => "Reference mod",
        _ => codebase.ToString()
    };

    internal static string ChannelLabel(CodeChannel channel) => channel switch
    {
        CodeChannel.Installed => "Installed",
        CodeChannel.Release => "Release",
        CodeChannel.Preview => "Preview",
        _ => channel.ToString()
    };

    internal static string KindLabel(string kind) => kind switch
    {
        "Type" => "Type",
        "Constructor" => "Constructor",
        "Method" => "Method",
        "Field" => "Field",
        "Property" => "Property",
        "Event" => "Event",
        _ => kind
    };

    internal static string Layout(string title, string body) =>
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
        $"<title>{Escape(title)} - S1Atlas serve</title><style>{Css}</style></head>" +
        "<body><header><nav><a href=\"/\">Home</a> <a href=\"/search\">Search</a></nav></header>" +
        $"<main>{body}</main><footer><p>Local read-only S1Atlas view.</p></footer></body></html>";

    internal static string Badge(string text) =>
        $"<span class=\"badge\">{Escape(text)}</span>";

    internal static string CodebaseBadge(string codebase) => Badge(codebase switch
    {
        "ScheduleI" => "Schedule I",
        "S1Api" => "S1API",
        "S1MApi" => "S1MAPI",
        "ReferenceMod" => "Reference mod",
        _ => codebase
    });

    internal static string PageTitle(string heading) => $"<h1>{Escape(heading)}</h1>";

    internal static string AuthorityMessage(S1Atlas.Application.Authority.InstalledBuildAuthority authority) =>
        authority.Status switch
        {
            S1Atlas.Application.Authority.InstalledBuildAuthorityStatus.NoCurrentBuild =>
                "No atlas has been scanned yet. Scan a game installation, then reload this page.",
            S1Atlas.Application.Authority.InstalledBuildAuthorityStatus.BuildNotFound =>
                "The requested build was not found.",
            S1Atlas.Application.Authority.InstalledBuildAuthorityStatus.NoPreferredVerifiedExtraction =>
                "No verified extraction is available yet.",
            S1Atlas.Application.Authority.InstalledBuildAuthorityStatus.ExtractionIntegrityFailure =>
                "The verified extraction failed integrity checks.",
            S1Atlas.Application.Authority.InstalledBuildAuthorityStatus.NoCompletedIndex =>
                "No completed index exists yet.",
            S1Atlas.Application.Authority.InstalledBuildAuthorityStatus.IndexBuildMismatch =>
                authority.Message ?? "The completed index does not match the build.",
            _ => authority.Message ?? authority.Status.ToString()
        };

    private const string Css =
        "body{font-family:system-ui,sans-serif;margin:0 auto;max-width:60rem;padding:0 1rem;line-height:1.45}" +
        "header nav{padding:1rem 0;border-bottom:1px solid #ccc}header nav a{margin-right:1rem}" +
        "footer{margin-top:2rem;border-top:1px solid #ccc;color:#666;font-size:.85rem}" +
        ".badge{display:inline-block;background:#eee;border-radius:.25rem;padding:0 .4rem;margin-right:.4rem;font-size:.85rem}" +
        "table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:.2rem .5rem;text-align:left}" +
        "pre.source{background:#f6f6f6;padding:.5rem;overflow-x:auto}pre.source .ln{color:#999;user-select:none}" +
        ".fact{color:#111}.derived{color:#444}.pager{margin:1rem 0}.pager a,.pager span{margin-right:.8rem}";
}
