using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Docs.Generation;
using S1Atlas.Docs.Identity;
using S1Atlas.Docs.Rendering;
using Xunit;

namespace S1Atlas.Docs.Tests.Rendering;

public sealed class StaticSiteGeneratorTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-docs-render-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Generate_writes_static_pages_assets_provenance_and_reserved_scene_seam()
    {
        var output = Path.Combine(_root, "site");
        var type = Symbol("index-game", "type", SymbolKind.Type, "Demo.Widget", "ScheduleI:Installed:Type:Demo.Widget", "code/schedule-i/installed/symbols/aa/widget.html");
        var field = Symbol("index-game", "field", SymbolKind.Field, "Demo.Widget.Value", "ScheduleI:Installed:Field:Demo.Widget::Value", type.PagePath);
        var schedule = new PortalIndexModel(
            new IndexRunRecord("index-game", "snapshot-game", IndexRunStatus.Completed, "2026-08-20T00:00:00Z", "2026-08-20T00:01:00Z"),
            CodebaseKind.ScheduleI, CodeChannel.Installed, "index-game", "extraction-1", "build-1", "extraction-1", true,
            [new PortalNamespaceModel("Demo", [type, field], 2)], 2);
        var api = new PortalIndexModel(
            new IndexRunRecord("index-api", "snapshot-api", IndexRunStatus.Completed, "2026-08-20T00:00:00Z", "2026-08-20T00:01:00Z"),
            CodebaseKind.S1Api, CodeChannel.Release, "index-api", "s1api:release:commit", null, null, false,
            [new PortalNamespaceModel("Api", [Symbol("index-api", "api", SymbolKind.Type, "Api.Widget", "S1Api:Release:Type:Api.Widget", "code/s1api/release/symbols/bb/api.html", CodebaseKind.S1Api, CodeChannel.Release)], 1)], 1);
        var build = new GameBuild("build-1", "assembly", "metadata", DateTimeOffset.Parse("2026-08-20T00:00:00Z"), true);
        var model = new PortalSiteModel(
            "build-1",
            [schedule, api],
            new PortalBuildHistoryModel([new PortalBuildEntry(build, S1Atlas.Application.Authority.InstalledBuildHistoryStatus.IndexedVerified, true, "builds/build-1.html")], []),
            new PortalEnvironmentModel(new EnvironmentSnapshot(2, build, InstallationObservation.Unknown, [], "test", build.FirstSeenAtUtc), "environment/build-1.html"),
            [], []);

        await new StaticSiteGenerator().GenerateAsync(model, output, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(output, "index.html")));
        Assert.True(File.Exists(Path.Combine(output, "search.html")));
        Assert.True(File.Exists(Path.Combine(output, "builds", "build-1.html")));
        Assert.True(File.Exists(Path.Combine(output, "environment", "build-1.html")));
        Assert.True(File.Exists(Path.Combine(output, "code", "s1api", "release", "index.html")));
        Assert.False(File.Exists(Path.Combine(output, "code", "schedule-i", "installed", "symbols", "aa", "value.html")));
        Assert.False(Directory.Exists(Path.Combine(output, "code", "schedule-i", "installed", "scenes")));
        var typeHtml = await File.ReadAllTextAsync(Path.Combine(output, type.PagePath.Replace('/', Path.DirectorySeparatorChar)), TestContext.Current.CancellationToken);
        Assert.Contains($"id=\"{new PortalSlugService().MemberAnchor(field.CanonicalKey)}\"", typeHtml, StringComparison.Ordinal);

        var buildHtml = await File.ReadAllTextAsync(Path.Combine(output, "builds", "build-1.html"), TestContext.Current.CancellationToken);
        Assert.Contains("Scene intelligence (scenes, prefabs, GameObjects, components) is available via the CLI and MCP; static scene pages are a post-V1 portal addition.", buildHtml, StringComparison.Ordinal);
        Assert.Contains("FACT", buildHtml, StringComparison.Ordinal);
        Assert.Contains("preferred verified extraction", buildHtml, StringComparison.Ordinal);
        var apiHtml = await File.ReadAllTextAsync(Path.Combine(output, "code", "s1api", "release", "index.html"), TestContext.Current.CancellationToken);
        Assert.Contains("latest completed index", apiHtml, StringComparison.Ordinal);
        Assert.Contains("s1api:release:commit", apiHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("preferred verified extraction", apiHtml, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(output, "assets", "search-index.js")));
        Assert.Contains("Object.freeze", await File.ReadAllTextAsync(Path.Combine(output, "assets", "search-index.js"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    private static PortalSymbolModel Symbol(string indexId, string id, SymbolKind kind, string qualifiedName, string canonicalKey, string pagePath, CodebaseKind codebase = CodebaseKind.ScheduleI, CodeChannel channel = CodeChannel.Installed) =>
        new(indexId, codebase, channel, id, canonicalKey, kind, qualifiedName, qualifiedName, false, null, pagePath, "member-" + id);

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
