using S1Atlas.Application.Authority;
using S1Atlas.Core.Indexing;
using S1Atlas.Docs.Determinism;
using S1Atlas.Docs.Generation;
using S1Atlas.Docs.Identity;

namespace S1Atlas.Docs.Rendering;

public sealed class PortalSectionRenderers
{
    private readonly PortalLinkResolver _links = new();
    private readonly DeterministicText _text = new();

    public string Provenance(PortalIndexModel index)
    {
        var commit = index.SourceIdentity.Split(':', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? index.SourceIdentity;
        var authority = index.IsVerifiedAuthority
            ? $"FACT: preferred verified extraction authority; build {index.BuildId}; extraction {index.ExtractionId}; index {index.IndexId}."
            : $"FACT: latest completed index — {index.Codebase}/{index.Channel} @ commit {commit}; source identity {index.SourceIdentity}; index {index.IndexId}.";
        var cssClass = index.IsVerifiedAuthority ? "provenance schedule-authority" : "provenance api-authority";
        return $"<section class=\"{cssClass}\"><h2>Provenance</h2><p>{HtmlPageRenderer.Escape(authority)}</p></section>";
    }

    public string Symbol(PortalIndexModel index, PortalSymbolModel symbol, string pagePath)
    {
        var body = $"<section class=\"fact\" id=\"evidence\"><h2>FACT evidence</h2><p>FACT: indexed {HtmlPageRenderer.Escape(symbol.Kind.ToString())} <code>{HtmlPageRenderer.Escape(symbol.CanonicalKey)}</code>.</p><p>FACT: index {HtmlPageRenderer.Escape(index.IndexId)}.</p></section>";
        if (symbol.Evidence is not null)
        {
            body += Section("Plain-English overview", symbol.Evidence.Context.Overview, pagePath);
            body += Section("Why a modder might care", symbol.Evidence.Context.ModderRelevance, pagePath);
            body += Section("C# learning context", symbol.Evidence.Context.Learning, pagePath);
            body += Source(symbol.Evidence.Source);
            body += Relationships(index, symbol, symbol.Evidence.Relationships, pagePath);
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

    public string Relationships(PortalIndexModel index, PortalSymbolModel symbol, PortalRelationshipEvidenceModel relationships, string pagePath)
    {
        return "<section><h2>Relationships</h2>" +
            RelationshipList("References", "references", relationships.References, relationships.ReferenceTotal, string.Empty, index, pagePath) +
            RelationshipList("Callers", "callers", relationships.Callers, relationships.CallerTotal, relationships.CallerCompletenessNotice, index, pagePath) +
            RelationshipList("Callees", "callees", relationships.Callees, relationships.CalleeTotal, relationships.CalleeCompletenessNotice, index, pagePath) +
            "</section>";
    }

    public string Build(PortalSiteModel site, PortalBuildEntry entry, string pagePath)
    {
        var schedule = site.Indexes.FirstOrDefault(index => index.IsVerifiedAuthority);
        var body = $"<section><h2>Build status</h2><p>FACT: {HtmlPageRenderer.Escape(entry.Status.ToString())}.</p></section>";
        if (schedule is not null && entry.IsNavigable && entry.Build.BuildId == site.ResolvedBuildId)
        {
            body += Provenance(schedule);
            body += $"<p><a href=\"{_links.RelativeHref(pagePath, $"code/{schedule.Codebase.ToString().ToLowerInvariant()}/{schedule.Channel.ToString().ToLowerInvariant()}/index.html")}\">Schedule I code surface</a></p>";
        }
        else if (entry.IsNavigable)
        {
            body += "<p>FACT: this historical build has a verified authority; historical code browsing is not rendered in this one-build site.</p>";
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

    private string RelationshipList(
        string title,
        string plural,
        IReadOnlyList<RelationshipQueryResult> results,
        int total,
        string completenessNotice,
        PortalIndexModel index,
        string pagePath)
    {
        var body = $"<h3>{title}</h3><p>FACT: {_text.FormatPlural(total, plural.TrimEnd('s'), plural)} in this index.</p>";
        if (total > 0)
        {
            body += $"<p>DERIVED: {_text.FormatCoverage(results.Count, total)} the true total {plural}.</p>";
            body += "<ul>" + string.Join(string.Empty, results.Select(result => RelationshipItem(result, index, pagePath))) + "</ul>";
        }
        if (!string.IsNullOrEmpty(completenessNotice))
            body += $"<p>FACT: {HtmlPageRenderer.Escape(completenessNotice)}</p>";
        return body;
    }

    private string RelationshipItem(RelationshipQueryResult result, PortalIndexModel index, string pagePath)
    {
        var endpoint = result.Direction == "Incoming" ? result.Source : result.Target;
        var endpointSymbol = endpoint.SymbolId is null
            ? null
            : index.Namespaces.SelectMany(namespaceModel => namespaceModel.Symbols).FirstOrDefault(candidate => candidate.SymbolId == endpoint.SymbolId);
        var label = endpoint.QualifiedName ?? endpoint.RawText ?? "unresolved endpoint";
        var renderedLabel = endpointSymbol is null
            ? HtmlPageRenderer.Escape(label)
            : $"<a href=\"{_links.RelativeHref(pagePath, endpointSymbol.PagePath, endpointSymbol.Anchor)}\">{HtmlPageRenderer.Escape(label)}</a>";
        return $"<li>{renderedLabel} — FACT: {HtmlPageRenderer.Escape(result.Kind)} ({HtmlPageRenderer.Escape(result.Evidence)}).</li>";
    }
}
