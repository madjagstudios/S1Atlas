using S1Atlas.Application.Authority;
using S1Atlas.Docs.Generation;
using S1Atlas.Docs.Identity;

namespace S1Atlas.Docs.Rendering;

public sealed class PortalSectionRenderers
{
    private readonly PortalLinkResolver _links = new();

    public string Provenance(PortalIndexModel index)
    {
        var commit = index.SourceIdentity.Split(':', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? index.SourceIdentity;
        var authority = index.IsVerifiedAuthority
            ? $"FACT: preferred verified extraction authority; build {index.BuildId}; extraction {index.ExtractionId}; index {index.IndexId}."
            : $"FACT: latest completed index — {index.Codebase}/{index.Channel} @ commit {commit}; source identity {index.SourceIdentity}; index {index.IndexId}.";
        return $"<section class=\"provenance\"><h2>Provenance</h2><p>{HtmlPageRenderer.Escape(authority)}</p></section>";
    }

    public string Symbol(PortalIndexModel index, PortalSymbolModel symbol, string pagePath)
    {
        var body = $"<section class=\"fact\"><h2>FACT evidence</h2><p>FACT: indexed {HtmlPageRenderer.Escape(symbol.Kind.ToString())} <code>{HtmlPageRenderer.Escape(symbol.CanonicalKey)}</code>.</p><p>FACT: index {HtmlPageRenderer.Escape(index.IndexId)}.</p></section>";
        if (symbol.Evidence is not null)
        {
            body += Section("Plain-English overview", symbol.Evidence.Context.Overview, pagePath);
            body += Section("Why a modder might care", symbol.Evidence.Context.ModderRelevance, pagePath);
            body += Section("C# learning context", symbol.Evidence.Context.Learning, pagePath);
            body += Source(symbol.Evidence.Source);
            body += Relationships(symbol.Evidence.Relationships);
        }
        else
        {
            body += "<section><h2>Plain-English overview</h2><p>DERIVED: context is not available for this symbol.</p></section>";
            body += "<section><h2>Why a modder might care</h2><p>DERIVED: no relationship evidence was materialized.</p></section>";
            body += "<section><h2>C# learning context</h2><p>DERIVED: no displayed source span is available.</p></section>";
        }
        return body;
    }

    public string Source(PortalSourceResult source) =>
        $"<section><h2>Decompiled source</h2><p>FACT: {HtmlPageRenderer.Escape(source.Label)}.</p>" +
        (source.Snippet is null ? string.Empty : $"<pre id=\"source-span\"><code>{HtmlPageRenderer.Escape(source.Snippet.Text)}</code></pre>") + "</section>";

    public string Relationships(PortalRelationshipEvidenceModel relationships) =>
        $"<section><h2>Relationships</h2><p>FACT: {relationships.ReferenceTotal} references measured in this index.</p><p>FACT: {relationships.CallerTotal} callers measured in this index.</p><p>FACT: {relationships.CalleeTotal} callees measured in this index.</p></section>";

    public string Build(PortalSiteModel site, PortalBuildEntry entry, string pagePath)
    {
        var schedule = site.Indexes.FirstOrDefault(index => index.IsVerifiedAuthority);
        var body = $"<section><h2>Build status</h2><p>FACT: {HtmlPageRenderer.Escape(entry.Status.ToString())}.</p></section>";
        if (schedule is not null && entry.IsNavigable)
        {
            body += Provenance(schedule);
            body += $"<p><a href=\"{_links.RelativeHref(pagePath, $"code/{schedule.Codebase.ToString().ToLowerInvariant()}/{schedule.Channel.ToString().ToLowerInvariant()}/index.html")}\">Schedule I code surface</a></p>";
        }
        body += "<section><h2>Scene intelligence</h2><p>Scene intelligence (scenes, prefabs, GameObjects, components) is available via the CLI and MCP; static scene pages are a post-V1 portal addition.</p></section>";
        var diffs = site.Diffs.Where(diff => diff.BeforeBuildId == entry.Build.BuildId || diff.AfterBuildId == entry.Build.BuildId).ToArray();
        body += "<section><h2>Adjacent diffs</h2>" + (diffs.Length == 0 ? "<p>no diffs available yet</p>" : string.Join(string.Empty, diffs.Select(diff => $"<p><a href=\"{_links.RelativeHref(pagePath, diff.PagePath)}\">{HtmlPageRenderer.Escape(diff.BeforeBuildId)} → {HtmlPageRenderer.Escape(diff.AfterBuildId)}</a></p>"))) + "</section>";
        body += entry.Build.BuildId == site.ResolvedBuildId && site.CurrentEnvironment is not null
            ? $"<p><a href=\"{_links.RelativeHref(pagePath, site.CurrentEnvironment.PagePath)}\">Current environment</a></p>"
            : "<p>Environment facts are recorded for the current build only.</p>";
        return body;
    }

    private string Section(string title, IReadOnlyList<DerivedStatement> statements, string pagePath)
    {
        var items = string.Join(string.Empty, statements.Select(statement =>
            $"<p><a href=\"{_links.RelativeHref(pagePath, pagePath, statement.EvidenceHref.TrimStart('#'))}\">{HtmlPageRenderer.Escape(statement.Text)}</a></p>"));
        return $"<section><h2>{HtmlPageRenderer.Escape(title)}</h2>{items}</section>";
    }
}
