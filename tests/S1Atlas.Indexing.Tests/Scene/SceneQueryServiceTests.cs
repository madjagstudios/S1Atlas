using S1Atlas.Core.Scenes;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Scene;
using Xunit;

namespace S1Atlas.Indexing.Tests.Scene;

public sealed class SceneQueryServiceTests
{
    [Fact]
    public async Task Scenes_uses_the_default_bounded_page_and_preserves_the_repository_total()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot();
        repository.Documents = [Document("scene-a", "Arena"), Document("scene-b", "Prefab", SceneDocumentKind.Prefab)];
        var service = new SceneQueryService(repository);

        var result = await service.ScenesAsync(
            new SceneListRequest(SceneSnapshotId: "snapshot-a", Limit: 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(SceneQueryStatus.Resolved, result.Status);
        Assert.Equal(2, result.Page.TotalCount);
        Assert.Equal(1, result.Page.ReturnedCount);
        Assert.Equal(1, repository.SceneLimits.Single());

        await service.ScenesAsync(new SceneListRequest(SceneSnapshotId: "snapshot-a"), TestContext.Current.CancellationToken);
        Assert.Equal(50, repository.SceneLimits.Last());
    }

    [Fact]
    public async Task Selector_resolves_exact_ids_before_unique_exact_names_and_never_selects_a_tie()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot();
        repository.Documents = [Document("scene-a", "Arena"), Document("scene-b", "Arena"), Document("scene-c", "Different")];
        var selector = new SceneSelector(repository);

        var byId = await selector.ResolveSceneAsync("snapshot-a", "scene-c", null, TestContext.Current.CancellationToken);
        var byName = await selector.ResolveSceneAsync("snapshot-a", "Different", null, TestContext.Current.CancellationToken);
        var ambiguous = await selector.ResolveSceneAsync("snapshot-a", "Arena", null, TestContext.Current.CancellationToken);

        Assert.Equal(SceneQueryStatus.Resolved, byId.Status);
        Assert.Equal("scene-c", byId.Selected!.SceneId);
        Assert.Equal(SceneQueryStatus.Resolved, byName.Status);
        Assert.Equal("scene-c", byName.Selected!.SceneId);
        Assert.Equal(SceneQueryStatus.AmbiguousScene, ambiguous.Status);
        Assert.Null(ambiguous.Selected);
        Assert.Equal(["scene-a", "scene-b"], ambiguous.Candidates.Select(candidate => candidate.SceneId));
    }

    [Fact]
    public async Task Scenes_reports_no_completed_scene_index_without_a_snapshot()
    {
        var result = await new SceneQueryService(new QueryRepository()).ScenesAsync(
            new SceneListRequest(BuildId: "build-a"),
            TestContext.Current.CancellationToken);

        Assert.Equal(SceneQueryStatus.NoCompletedSceneIndex, result.Status);
        Assert.Equal(0, result.Page.TotalCount);
    }

    [Fact]
    public async Task Component_code_marks_an_unresolved_exact_link_without_a_fallback_lookup()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot();
        repository.Components = [Component("component-a", SceneResolutionStatus.NotIndexed)];
        var service = new SceneQueryService(repository);

        var result = await service.ComponentAsync(
            new ComponentQueryRequest("snapshot-a", "component-a", IncludeCode: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(SceneQueryStatus.UnresolvedCodeSymbol, result.Status);
        Assert.NotNull(result.Component);
        Assert.Null(result.Component!.ResolvedTypeSymbolId);
    }

    [Fact]
    public async Task Prefabs_returns_a_valid_empty_proven_prefab_page()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot();
        repository.Documents = [Document("scene-a", "Arena")];
        var service = new SceneQueryService(repository);

        var result = await service.ScenesAsync(
            new SceneListRequest(SceneSnapshotId: "snapshot-a", Kind: SceneDocumentKind.Prefab),
            TestContext.Current.CancellationToken);

        Assert.Equal(SceneQueryStatus.Resolved, result.Status);
        Assert.Equal(0, result.Page.TotalCount);
        Assert.Empty(result.Page.Rows);
    }

    [Fact]
    public async Task Scene_query_preserves_partial_recovery_in_its_outcome()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot();
        repository.Documents = [Document("scene-a", "Arena", recovery: SceneRecoveryStatus.PartiallyRecovered)];
        var service = new SceneQueryService(repository);

        var result = await service.SceneAsync(
            new SceneQueryRequest("snapshot-a", "scene-a"),
            TestContext.Current.CancellationToken);

        Assert.Equal(SceneQueryStatus.PartialRecovery, result.Status);
        Assert.Equal(SceneRecoveryStatus.PartiallyRecovered, result.Scene!.RecoveryStatus);
    }

    [Fact]
    public async Task Scene_query_resolves_verified_container_facts_without_fabrication()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot();
        repository.Documents = [Document("scene-a", "Arena")];
        repository.Containers = [new SceneContainerRecord("container-a", "snapshot-a", "Schedule I_Data/level0", "Assets", "2022.3.62", 22, 10, new string('b', 64), "sidecar.json")];

        var result = await new SceneQueryService(repository).SceneAsync(new SceneQueryRequest("snapshot-a", "scene-a"), TestContext.Current.CancellationToken);

        Assert.NotNull(result.Containers);
        var container = Assert.Single(result.Containers!);
        Assert.Equal("Schedule I_Data/level0", container.RelativePath);
        Assert.Equal(new string('b', 64), container.Sha256);
        Assert.Equal("sidecar.json", container.SidecarManifest);
    }

    [Fact]
    public async Task Selector_uses_unbounded_exact_name_lookup_instead_of_a_bounded_contains_page()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot();
        repository.Documents = Enumerable.Range(0, 51).Select(index => Document("scene-" + index, index == 50 ? "Arena" : "Arena filler " + index)).ToArray();

        var result = await new SceneSelector(repository).ResolveSceneAsync("snapshot-a", "Arena", null, TestContext.Current.CancellationToken);

        Assert.Equal(SceneQueryStatus.Resolved, result.Status);
        Assert.Equal("scene-50", result.Selected!.SceneId);
        Assert.Equal(1, repository.ExactSceneNameLookups);
        Assert.Empty(repository.SceneLimits);
    }

    private static SceneSnapshotRecord Snapshot() => new(
        "snapshot-a", "build-a", "extraction-a", "input-a", "code-a", "index-a", "parser", "1",
        new string('a', 64), SceneSnapshotStatus.Completed, SceneRecoveryStatus.FullyRecovered, "2026-08-15T00:00:00Z");

    private static SceneDocumentRecord Document(string id, string name, SceneDocumentKind kind = SceneDocumentKind.Scene, SceneRecoveryStatus recovery = SceneRecoveryStatus.FullyRecovered) =>
        new(id, "snapshot-a", "container-a", kind, name, 1, 1, 1, recovery);

    private static SceneComponentRecord Component(string id, SceneResolutionStatus status) =>
        new(id, "object-a", "container-a", 2, 114, "MonoBehaviour", "Assembly-CSharp", "Game", "Widget", null, null, status, SceneRecoveryStatus.FullyRecovered);

    private sealed class QueryRepository : ISceneRepository
    {
        public Dictionary<string, SceneSnapshotRecord> Snapshots { get; } = [];
        public IReadOnlyList<SceneDocumentRecord> Documents { get; set; } = [];
        public IReadOnlyList<SceneComponentRecord> Components { get; set; } = [];
        public IReadOnlyList<SceneContainerRecord> Containers { get; set; } = [];
        public List<int> SceneLimits { get; } = [];
        public int ExactSceneNameLookups { get; private set; }

        public Task<SceneSnapshotRecord?> GetCompletedSceneSnapshotAsync(string sceneSnapshotId, CancellationToken cancellationToken) => Task.FromResult(Snapshots.GetValueOrDefault(sceneSnapshotId));
        public Task<SceneSnapshotRecord?> GetLatestCompletedSceneSnapshotAsync(string buildId, CancellationToken cancellationToken) => Task.FromResult(Snapshots.Values.SingleOrDefault(snapshot => snapshot.BuildId == buildId));
        public Task<ScenePageResult<SceneDocumentRecord>> ListScenesAsync(SceneListQueryOptions options, CancellationToken cancellationToken)
        {
            SceneLimits.Add(options.Limit);
            var rows = Documents.Where(document => document.SceneSnapshotId == options.SceneSnapshotId)
                .Where(document => options.Kind is null || document.Kind == options.Kind)
                .Where(document => options.Query is null || document.Name.Contains(options.Query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(document => document.Name, StringComparer.Ordinal).ThenBy(document => document.SceneId, StringComparer.Ordinal).ToArray();
            return Task.FromResult(new ScenePageResult<SceneDocumentRecord>(rows.Length, Math.Min(rows.Length, options.Limit), rows.Take(options.Limit).ToArray()));
        }
        public Task<SceneDocumentRecord?> GetSceneAsync(string sceneSnapshotId, string sceneId, CancellationToken cancellationToken) => Task.FromResult(Documents.SingleOrDefault(document => document.SceneSnapshotId == sceneSnapshotId && document.SceneId == sceneId));
        public Task<IReadOnlyList<SceneContainerRecord>> GetSceneContainersAsync(string sceneSnapshotId, IReadOnlyList<string> containerIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SceneContainerRecord>>(Containers.Where(container => container.SceneSnapshotId == sceneSnapshotId && containerIds.Contains(container.ContainerId, StringComparer.Ordinal)).ToArray());
        public Task<IReadOnlyList<SceneDocumentRecord>> FindScenesByExactNameAsync(string sceneSnapshotId, string name, SceneDocumentKind? kind, int limit, CancellationToken cancellationToken) { ExactSceneNameLookups++; return Task.FromResult<IReadOnlyList<SceneDocumentRecord>>(Documents.Where(document => document.SceneSnapshotId == sceneSnapshotId && document.Kind == (kind ?? document.Kind) && document.Name == name).Take(limit).ToArray()); }
        public Task<ScenePageResult<SceneGameObjectRecord>> ListGameObjectsAsync(GameObjectListQueryOptions options, CancellationToken cancellationToken) => Task.FromResult(new ScenePageResult<SceneGameObjectRecord>(0, 0, []));
        public Task<SceneGameObjectRecord?> GetGameObjectAsync(string sceneSnapshotId, string gameObjectId, CancellationToken cancellationToken) => Task.FromResult<SceneGameObjectRecord?>(null);
        public Task<IReadOnlyList<SceneGameObjectRecord>> FindGameObjectsByExactNameAsync(string sceneSnapshotId, string sceneId, string name, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SceneGameObjectRecord>>([]);
        public Task<ScenePageResult<SceneComponentRecord>> ListComponentsAsync(ComponentListQueryOptions options, CancellationToken cancellationToken)
        {
            var rows = Components.Where(component => options.Query is null || component.Kind.Contains(options.Query, StringComparison.OrdinalIgnoreCase)).Take(options.Limit).ToArray();
            return Task.FromResult(new ScenePageResult<SceneComponentRecord>(rows.Length, rows.Length, rows));
        }
        public Task<SceneComponentRecord?> GetComponentAsync(string sceneSnapshotId, string componentId, CancellationToken cancellationToken) => Task.FromResult(Components.SingleOrDefault(component => component.ComponentId == componentId));
        public Task<IReadOnlyList<SceneComponentRecord>> FindComponentsByExactKindAsync(string sceneSnapshotId, string kind, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SceneComponentRecord>>(Components.Where(component => component.Kind == kind).Take(limit).ToArray());
        public Task<ScenePageResult<SceneReferenceRecord>> ListReferencesAsync(ReferenceListQueryOptions options, CancellationToken cancellationToken) => Task.FromResult(new ScenePageResult<SceneReferenceRecord>(0, 0, []));
        public Task CreateSceneSnapshotAsync(SceneSnapshotRecord snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StartSceneSnapshotAsync(string sceneSnapshotId, string startedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteSceneSnapshotAsync(string sceneSnapshotId, SceneWriteSet writeSet, string completedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PublishSceneSnapshotAsync(string sceneSnapshotId, string publishedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailSceneSnapshotAsync(string sceneSnapshotId, string failureCode, string failureMessage, string completedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
