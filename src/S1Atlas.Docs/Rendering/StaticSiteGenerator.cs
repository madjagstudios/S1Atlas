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
        await RemovePreviousGeneratedFilesAsync(output, cancellationToken);
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
            files[diff.PagePath] = _pages.Render(diff.PagePath, $"Diff {diff.BeforeBuildId} → {diff.AfterBuildId}", Diff(site, diff));
        var historySymbols = site.SymbolHistories ?? [];
        foreach (var history in historySymbols)
        {
            files[history.PagePath] = _pages.Render(history.PagePath, $"History: {history.QualifiedName}", SymbolHistory(history));
        }
        files["history/schedule-i/index.html"] = _pages.Render("history/schedule-i/index.html", "Schedule I history", "<p>FACT: Schedule I symbol history is cross-build and authority-scoped.</p>");
        foreach (var asset in new StaticAssets().Render(effective)) files[asset.Key] = asset.Value;
        files[".s1atlas-generated-files"] = string.Join("\n", files.Keys.OrderBy(path => path, StringComparer.Ordinal).Append(".s1atlas-generated-files")) + "\n";
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

    private static string Environment(PortalEnvironmentModel environment)
    {
        var snapshot = environment.Snapshot;
        var installation = snapshot.Installation;
        var facts = new List<string>
        {
            Fact("build ID", snapshot.Build.BuildId),
            Fact("GameAssembly SHA-256", snapshot.Build.GameAssemblySha256),
            Fact("global-metadata SHA-256", snapshot.Build.MetadataSha256),
            Fact("build first seen", snapshot.Build.FirstSeenAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            Fact("installation executable version", installation.ExecutableVersion),
            Fact("Steam app ID", installation.SteamAppId),
            Fact("Steam build ID", installation.SteamBuildId),
            Fact("installation root", installation.InstallationRoot),
            Fact("GameAssembly path", installation.GameAssemblyPath),
            Fact("global-metadata path", installation.GlobalMetadataPath),
            Fact("Atlas version", snapshot.AtlasVersion),
            Fact("environment identity version", snapshot.IdentityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        facts.AddRange(snapshot.Dependencies
            .OrderBy(dependency => dependency.Kind)
            .ThenBy(dependency => dependency.Version, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.Path, StringComparer.Ordinal)
            .Select(dependency => $"FACT: dependency {HtmlPageRenderer.Escape(dependency.Kind.ToString())}: {HtmlPageRenderer.Escape(dependency.Version ?? "not recorded")} ({HtmlPageRenderer.Escape(dependency.Path ?? "path not recorded")}); installed={dependency.IsInstalled.ToString().ToLowerInvariant()}; SHA-256={HtmlPageRenderer.Escape(dependency.BinarySha256 ?? "not recorded")}."));
        return "<section><h2>FACT evidence</h2><p>FACT: this is the current resolved Schedule I environment snapshot only.</p><ul>" + string.Join(string.Empty, facts.Select(fact => "<li>" + fact + "</li>")) + "</ul></section>";
    }

    private string Diff(PortalSiteModel site, PortalDiffModel diff)
    {
        var counts = string.Join(string.Empty, Enum.GetValues<DiffClassification>().Select(classification =>
            $"<li>FACT: {HtmlPageRenderer.Escape(classification.ToString())}: {diff.Result.CountsByClassification.GetValueOrDefault(classification).ToString(System.Globalization.CultureInfo.InvariantCulture)}.</li>"));
        var scheduleSymbols = site.Indexes
            .Where(index => index.Codebase == CodebaseKind.ScheduleI && index.Channel == CodeChannel.Installed)
            .SelectMany(index => index.Namespaces)
            .SelectMany(namespaceModel => namespaceModel.Symbols)
            .ToDictionary(symbol => symbol.CanonicalKey, StringComparer.Ordinal);
        var changes = diff.Result.Changes.Count == 0
            ? "<p>FACT: no changed symbols in this adjacent pair.</p>"
            : "<ul>" + string.Join(string.Empty, diff.Result.Changes.Select(change =>
            {
                var label = $"{change.QualifiedName} — {change.Classification}";
                var rendered = scheduleSymbols.TryGetValue(change.CanonicalKey, out var symbol)
                    ? $"<a href=\"{_links.RelativeHref(diff.PagePath, symbol.PagePath, symbol.Anchor)}\">{HtmlPageRenderer.Escape(label)}</a>"
                    : HtmlPageRenderer.Escape(label);
                return $"<li>{rendered} — FACT: canonical key <code>{HtmlPageRenderer.Escape(change.CanonicalKey)}</code>.</li>";
            })) + "</ul>";
        return $"<section><h2>FACT evidence</h2><p>FACT: adjacent verified Schedule I diff from {HtmlPageRenderer.Escape(diff.BeforeBuildId)} to {HtmlPageRenderer.Escape(diff.AfterBuildId)}.</p><p>FACT: before index {HtmlPageRenderer.Escape(diff.Result.IndexIdA)}; after index {HtmlPageRenderer.Escape(diff.Result.IndexIdB)}.</p></section><section><h2>Classifications</h2><ul>{counts}</ul></section><section><h2>Changed symbols</h2>{changes}</section>";
    }

    private static string SymbolHistory(PortalSymbolHistoryModel history)
    {
        var occurrences = history.Occurrences.Count == 0
            ? "<p>FACT: no verified indexed occurrences are available.</p>"
            : "<ul>" + string.Join(string.Empty, history.Occurrences.Select(occurrence => occurrence.Present
                ? $"<li>FACT: present in build {HtmlPageRenderer.Escape(occurrence.BuildId)}; index {HtmlPageRenderer.Escape(occurrence.IndexId)}; signature {HtmlPageRenderer.Escape(occurrence.Signature ?? "not recorded")}.</li>"
                : $"<li>FACT: not present in build {HtmlPageRenderer.Escape(occurrence.BuildId)}.</li>")) + "</ul>";
        return $"<section id=\"evidence\"><h2>FACT evidence</h2><p>FACT: Schedule I cross-build history for <code>{HtmlPageRenderer.Escape(history.CanonicalKey)}</code>.</p><p>DERIVED: showing {history.Occurrences.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} verified indexed occurrences.</p>{occurrences}</section>";
    }

    private static string Fact(string label, string? value) => $"FACT: {HtmlPageRenderer.Escape(label)}: {HtmlPageRenderer.Escape(value ?? "not recorded")}.";

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

    private static async Task RemovePreviousGeneratedFilesAsync(string output, CancellationToken cancellationToken)
    {
        var manifest = Path.Combine(output, ".s1atlas-generated-files");
        if (!File.Exists(manifest)) return;
        var paths = await File.ReadAllLinesAsync(manifest, cancellationToken);
        foreach (var relativePath in paths)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) continue;
            var segments = relativePath.Split(['/', '\\']);
            if (segments.Any(segment => segment is "" or "." or "..")) continue;
            var full = Path.Combine(output, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) File.Delete(full);
        }
    }

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
