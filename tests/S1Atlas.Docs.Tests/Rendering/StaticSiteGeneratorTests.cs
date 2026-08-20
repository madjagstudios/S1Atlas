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
        var relationship = new RelationshipQueryResult(
            "relationship-1",
            "ReadsField",
            "RecoveredIL",
            "Outgoing",
            new RelationshipEndpointQueryResult(type.SymbolId, type.QualifiedName, type.Signature, null, true),
            new RelationshipEndpointQueryResult(field.SymbolId, field.QualifiedName, field.Signature, null, true));
        type = type with
        {
            Evidence = new PortalSymbolEvidenceModel(
                new PortalRelationshipEvidenceModel([relationship], 1, [], 0, [relationship], 1, "callers complete", "callees complete"),
                new PortalSourceResult(PortalSourceState.Unavailable, null, "source unavailable"),
                new DerivedContext(
                    [new DerivedStatement("DERIVED: one caller.", "evidence")],
                    [new DerivedStatement("DERIVED: modder relevance signal.", "evidence")],
                    []))
        };
        var schedule = new PortalIndexModel(
            new IndexRunRecord("index-game", "snapshot-game", IndexRunStatus.Completed, "2026-08-20T00:00:00Z", "2026-08-20T00:01:00Z"),
            CodebaseKind.ScheduleI, CodeChannel.Installed, "index-game", "extraction-1", "build-1", "extraction-1", true,
            [new PortalNamespaceModel("Demo", [type, field], 2)], 2);
        var api = new PortalIndexModel(
            new IndexRunRecord("index-api", "snapshot-api", IndexRunStatus.Completed, "2026-08-20T00:00:00Z", "2026-08-20T00:01:00Z"),
            CodebaseKind.S1Api, CodeChannel.Release, "index-api", "s1api:release:commit", null, null, false,
            [new PortalNamespaceModel("Api", [Symbol("index-api", "api", SymbolKind.Type, "Api.Widget", "S1Api:Release:Type:Api.Widget", "code/s1api/release/symbols/bb/api.html", CodebaseKind.S1Api, CodeChannel.Release)], 1)], 1);
        var build = new GameBuild("build-1", "assembly", "metadata", DateTimeOffset.Parse("2026-08-20T00:00:00Z"), true);
        var buildDiff = new PortalDiffModel(
            "build-0",
            "build-1",
            new BuildDiffResult("index-old", "index-game", "ScheduleI", "Installed", 1, 2,
                new Dictionary<DiffClassification, int> { [DiffClassification.Added] = 1, [DiffClassification.Removed] = 0, [DiffClassification.MethodBodyChanged] = 0, [DiffClassification.RelationshipsChanged] = 0, [DiffClassification.Unchanged] = 1 },
                [new SymbolDiff(type.CanonicalKey, type.QualifiedName, "Type", DiffClassification.Added, null, type.Signature)]),
            "diffs/build-0--build-1.html");
        var model = new PortalSiteModel(
            "build-1",
            [schedule, api],
            new PortalBuildHistoryModel([new PortalBuildEntry(build, S1Atlas.Application.Authority.InstalledBuildHistoryStatus.IndexedVerified, true, "builds/build-1.html")], []),
            new PortalEnvironmentModel(new EnvironmentSnapshot(2, build, new InstallationObservation("1.0", "123", "456", "C:/game", "C:/game/GameAssembly.dll", "C:/game/global-metadata.dat"), [new DependencyVersion(DependencyKind.S1Api, "0.1", "tools/s1api", true, "dep-hash")], "test", build.FirstSeenAtUtc), "environment/build-1.html"),
            [buildDiff], [], [new PortalSymbolHistoryModel(type.CanonicalKey, type.QualifiedName, "history/schedule-i/symbols/aa/widget-history.html", [new S1Atlas.Application.Authority.SymbolHistoryOccurrence("build-1", "index-game", true, type.SymbolId, type.QualifiedName, type.Signature)])]);

        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, ".s1atlas-generated-files"), "stale.html\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(output, "stale.html"), "stale", TestContext.Current.CancellationToken);
        await new StaticSiteGenerator().GenerateAsync(model, output, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(output, "index.html")));
        Assert.True(File.Exists(Path.Combine(output, "search.html")));
        Assert.True(File.Exists(Path.Combine(output, "builds", "build-1.html")));
        Assert.True(File.Exists(Path.Combine(output, "environment", "build-1.html")));
        Assert.True(File.Exists(Path.Combine(output, "diffs", "build-0--build-1.html")));
        Assert.True(File.Exists(Path.Combine(output, "history", "schedule-i", "symbols", "aa", "widget-history.html")));
        Assert.False(File.Exists(Path.Combine(output, "stale.html")));
        Assert.True(File.Exists(Path.Combine(output, "code", "s1api", "release", "index.html")));
        Assert.False(File.Exists(Path.Combine(output, "code", "schedule-i", "installed", "symbols", "aa", "value.html")));
        Assert.False(Directory.Exists(Path.Combine(output, "code", "schedule-i", "installed", "scenes")));
        var typeHtml = await File.ReadAllTextAsync(Path.Combine(output, type.PagePath.Replace('/', Path.DirectorySeparatorChar)), TestContext.Current.CancellationToken);
        Assert.Contains($"id=\"{new PortalSlugService().MemberAnchor(field.CanonicalKey)}\"", typeHtml, StringComparison.Ordinal);

        var buildHtml = await File.ReadAllTextAsync(Path.Combine(output, "builds", "build-1.html"), TestContext.Current.CancellationToken);
        Assert.Contains("Scene intelligence (scenes, prefabs, GameObjects, components) is available via the CLI and MCP; static scene pages are a post-V1 portal addition.", buildHtml, StringComparison.Ordinal);
        Assert.Contains("FACT", buildHtml, StringComparison.Ordinal);
        Assert.Contains("preferred verified extraction", buildHtml, StringComparison.Ordinal);
        Assert.Contains("installation executable version", await File.ReadAllTextAsync(Path.Combine(output, "environment", "build-1.html"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("DERIVED: one caller.", typeHtml, StringComparison.Ordinal);
        Assert.Contains("ReadsField", typeHtml, StringComparison.Ordinal);
        Assert.Contains("#evidence", typeHtml, StringComparison.Ordinal);
        var diffHtml = await File.ReadAllTextAsync(Path.Combine(output, "diffs", "build-0--build-1.html"), TestContext.Current.CancellationToken);
        Assert.Contains("Added", diffHtml, StringComparison.Ordinal);
        Assert.Contains(type.CanonicalKey, diffHtml, StringComparison.Ordinal);
        var historyHtml = await File.ReadAllTextAsync(Path.Combine(output, "history", "schedule-i", "symbols", "aa", "widget-history.html"), TestContext.Current.CancellationToken);
        Assert.Contains("present in build build-1", historyHtml, StringComparison.Ordinal);
        var apiHtml = await File.ReadAllTextAsync(Path.Combine(output, "code", "s1api", "release", "index.html"), TestContext.Current.CancellationToken);
        Assert.Contains("latest completed index", apiHtml, StringComparison.Ordinal);
        Assert.Contains("api-authority", apiHtml, StringComparison.Ordinal);
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
