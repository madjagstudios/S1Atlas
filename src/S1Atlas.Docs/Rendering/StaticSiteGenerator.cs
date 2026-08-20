using S1Atlas.Core.Indexing;
using S1Atlas.Docs.Generation;
using S1Atlas.Docs.Identity;

namespace S1Atlas.Docs.Rendering;

public sealed class StaticSiteGenerator
{
    private readonly HtmlPageRenderer _pages = new();
    private readonly PortalSectionRenderers _sections = new();
    private readonly PortalSlugService _slugs = new();
    private readonly PortalLinkResolver _links = new();

    public async Task GenerateAsync(PortalSiteModel site, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var allSymbols = site.Indexes.SelectMany(index => index.Namespaces.SelectMany(ns => ns.Symbols)).ToArray();
        var effective = EffectiveSymbols(site.Indexes, allSymbols).ToArray();
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["index.html"] = _pages.Render("index.html", "S1Atlas.Docs", Landing(site, effective, "index.html")),
            ["search.html"] = _pages.Render("search.html", "Search", "<input id=\"search-query\" type=\"search\" placeholder=\"Search symbols\"><ul id=\"search-results\"></ul>", true),
            ["builds/index.html"] = _pages.Render("builds/index.html", "Build history", BuildIndex(site, "builds/index.html"))
        };
        foreach (var entry in site.BuildHistory.Entries)
        {
            var path = $"builds/{entry.Build.BuildId}.html";
            files[path] = _pages.Render(path, entry.Build.BuildId, _sections.Build(site, entry, path));
        }
        if (site.CurrentEnvironment is not null)
        {
            var path = site.CurrentEnvironment.PagePath;
            files[path] = _pages.Render(path, "Current environment", Environment(site.CurrentEnvironment));
        }
        foreach (var index in site.Indexes)
        {
            var codePath = CodePath(index);
            files[codePath] = _pages.Render(codePath, $"{index.Codebase} {index.Channel}", CodeIndex(site, index, codePath));
            foreach (var ns in index.Namespaces)
            {
                var nsPath = NamespacePath(index, ns.Name);
                files[nsPath] = _pages.Render(nsPath, ns.Name, Namespace(index, ns, nsPath));
            }
            foreach (var symbol in effective.Where(symbol => symbol.IndexId == index.IndexId && IsStandalone(symbol.Kind)))
            {
                var body = _sections.Provenance(index) + _sections.Symbol(index, symbol, symbol.PagePath);
                if (symbol.Kind == SymbolKind.Type)
                    body += InlineMembers(effective, symbol);
                files[symbol.PagePath] = _pages.Render(symbol.PagePath, symbol.QualifiedName, body);
            }
        }
        foreach (var diff in site.Diffs)
            files[diff.PagePath] = _pages.Render(diff.PagePath, $"Diff {diff.BeforeBuildId} → {diff.AfterBuildId}", $"<section><h2>FACT evidence</h2><p>FACT: adjacent verified Schedule I diff from {diff.BeforeBuildId} to {diff.AfterBuildId}.</p><p>DERIVED: {diff.Result.Changes.Count} changed symbols measured.</p></section>");
        var historySymbols = effective.Where(symbol => symbol.Codebase == CodebaseKind.ScheduleI && IsStandalone(symbol.Kind)).ToArray();
        foreach (var symbol in historySymbols)
        {
            var slug = _slugs.Create(symbol.CanonicalKey);
            var path = $"history/schedule-i/symbols/{slug.HashPrefix}/{slug.FileStem}.html";
            files[path] = _pages.Render(path, $"History: {symbol.QualifiedName}", $"<section><h2>FACT evidence</h2><p>FACT: Schedule I cross-build history for <code>{HtmlPageRenderer.Escape(symbol.CanonicalKey)}</code>.</p></section>");
        }
        files["history/schedule-i/index.html"] = _pages.Render("history/schedule-i/index.html", "Schedule I history", "<p>FACT: Schedule I symbol history is cross-build and authority-scoped.</p>");
        foreach (var asset in new StaticAssets().Render(effective)) files[asset.Key] = asset.Value;
        foreach (var file in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteAsync(output, file.Key, file.Value, cancellationToken);
        }
    }

    private string Landing(PortalSiteModel site, IReadOnlyList<PortalSymbolModel> symbols, string path)
    {
        var surfaces = new List<string>();
        foreach (var index in site.Indexes)
            surfaces.Add($"<p><a href=\"{_links.RelativeHref(path, CodePath(index))}\">{index.Codebase}/{index.Channel}</a> — FACT: {HtmlPageRenderer.Escape(index.IndexId)}</p>");
        foreach (var codebase in new[] { CodebaseKind.S1Api, CodebaseKind.S1MApi })
            foreach (var channel in Enum.GetValues<CodeChannel>())
                if (!site.Indexes.Any(index => index.Codebase == codebase && index.Channel == channel))
                    surfaces.Add($"<p>{codebase}/{channel} — FACT: not indexed</p>");
        return $"<section><h2>Resolved game surface</h2><p>FACT: Schedule I Installed build {HtmlPageRenderer.Escape(site.ResolvedBuildId)} is the preferred, integrity-verified authority.</p></section><section><h2>Code surfaces</h2>{string.Join(string.Empty, surfaces)}</section><section><h2>Coverage</h2><p>DERIVED: {symbols.Count} indexed symbols are available in this generated site.</p></section>";
    }

    private string BuildIndex(PortalSiteModel site, string path) => "<section><h2>All known builds</h2>" + string.Join(string.Empty, site.BuildHistory.Entries.Select(entry => entry.IsNavigable ? $"<p><a href=\"{_links.RelativeHref(path, $"builds/{entry.Build.BuildId}.html")}\">{entry.Build.BuildId}</a> — {entry.Status}</p>" : $"<p>{entry.Build.BuildId} — {entry.Status} (not navigable)</p>")) + "</section>";

    private string CodeIndex(PortalSiteModel site, PortalIndexModel index, string path) => _sections.Provenance(index) + "<section><h2>Namespaces</h2>" + string.Join(string.Empty, index.Namespaces.Select(ns => $"<p><a href=\"{_links.RelativeHref(path, NamespacePath(index, ns.Name))}\">{HtmlPageRenderer.Escape(ns.Name)}</a> ({ns.TotalCount})</p>")) + "</section>";

    private string Namespace(PortalIndexModel index, PortalNamespaceModel ns, string path) => "<section><h2>Symbols</h2>" + string.Join(string.Empty, ns.Symbols.Select(symbol => $"<p><a href=\"{_links.RelativeHref(path, symbol.PagePath, symbol.Anchor)}\">{HtmlPageRenderer.Escape(symbol.QualifiedName)}</a> — {symbol.Kind}</p>")) + "</section>";

    private static string Environment(PortalEnvironmentModel environment) => "<section><h2>FACT evidence</h2><p>FACT: environment facts are recorded for the current resolved Schedule I build only.</p><p>FACT: build " + HtmlPageRenderer.Escape(environment.Snapshot.Build.BuildId) + ".</p></section>";

    private static string CodePath(PortalIndexModel index) => $"code/{index.Codebase.ToString().ToLowerInvariant()}/{index.Channel.ToString().ToLowerInvariant()}/index.html";
    private PortalSlugService SlugService => _slugs;
    private string NamespacePath(PortalIndexModel index, string name)
    {
        var slug = _slugs.Create(string.IsNullOrEmpty(name) ? "global" : name);
        return $"code/{index.Codebase.ToString().ToLowerInvariant()}/{index.Channel.ToString().ToLowerInvariant()}/namespaces/{slug.FileStem}.html";
    }

    private static string InlineMembers(IReadOnlyList<PortalSymbolModel> symbols, PortalSymbolModel type)
    {
        var members = symbols
            .Where(symbol => symbol.PagePath == type.PagePath && !IsStandalone(symbol.Kind))
            .OrderBy(symbol => symbol.Kind)
            .ThenBy(symbol => symbol.QualifiedName, StringComparer.Ordinal)
            .ToArray();
        if (members.Length == 0) return string.Empty;
        return "<section><h2>Inline members</h2>" + string.Join(string.Empty, members.Select(member => $"<h3 id=\"{HtmlPageRenderer.Escape(member.Anchor)}\">{HtmlPageRenderer.Escape(member.QualifiedName)}</h3><p>FACT: {HtmlPageRenderer.Escape(member.Kind.ToString())} <code>{HtmlPageRenderer.Escape(member.CanonicalKey)}</code>.</p>")) + "</section>";
    }

    private IEnumerable<PortalSymbolModel> EffectiveSymbols(IReadOnlyList<PortalIndexModel> indexes, IReadOnlyList<PortalSymbolModel> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (IsStandalone(symbol.Kind)) { yield return symbol; continue; }
            var owner = symbol.CanonicalKey.Split(':', 4).LastOrDefault()?.Split("::", StringSplitOptions.None)[0];
            var containing = indexes.SelectMany(index => index.Namespaces).SelectMany(ns => ns.Symbols).FirstOrDefault(candidate => candidate.Kind == SymbolKind.Type && candidate.CanonicalKey.EndsWith(":Type:" + owner, StringComparison.Ordinal));
            yield return containing is null ? symbol : symbol with { PagePath = containing.PagePath, Anchor = _slugs.MemberAnchor(symbol.CanonicalKey) };
        }
    }

    private static bool IsStandalone(SymbolKind kind) => kind is SymbolKind.Type or SymbolKind.Method or SymbolKind.Constructor;

    private static async Task WriteAsync(string output, string relativePath, string contents, CancellationToken cancellationToken)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Split(['/', '\\']).Any(segment => segment is "" or "." or "..")) throw new InvalidDataException("Unsafe generated path.");
        var full = Path.Combine(output, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var normalized = contents.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        await File.WriteAllTextAsync(full, normalized, new System.Text.UTF8Encoding(false), cancellationToken);
    }
}
