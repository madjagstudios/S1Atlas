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
    public async Task Ambiguous_scene_candidates_include_verified_container_facts()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot();
        repository.Documents = [Document("scene-a", "Arena"), Document("scene-b", "Arena")];
        repository.Containers = [new SceneContainerRecord("container-a", "snapshot-a", "Schedule I_Data/level0", "Assets", "2022.3.62", 22, 10, new string('b', 64), "sidecar.json")];

        var result = await new SceneQueryService(repository).SceneAsync(new SceneQueryRequest("snapshot-a", "Arena"), TestContext.Current.CancellationToken);

        Assert.Equal(SceneQueryStatus.AmbiguousScene, result.Status);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("Schedule I_Data/level0", Assert.Single(result.Containers!).RelativePath);
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
    public async Task Component_query_returns_the_components_field_set()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot();
        repository.Components = [Component("component-a", SceneResolutionStatus.NotIndexed)];
        repository.FieldSets.Add(new SceneScriptFieldSetRecord("component-a", "snapshot-a", SceneScriptFieldOwnerKind.Component,
            SceneScriptFieldSetStatus.Decoded, null, false, [new SceneScriptField("Price", "float", SceneScriptFieldValueKind.Float, "50000")]));

        var result = await new SceneQueryService(repository).ComponentAsync(
            new ComponentQueryRequest("snapshot-a", "component-a"),
            TestContext.Current.CancellationToken);

        Assert.Equal(SceneQueryStatus.Resolved, result.Status);
        Assert.Equal("50000", Assert.Single(result.ScriptFields!.Fields).Value);
    }

    [Fact]
    public async Task Component_query_without_a_field_set_returns_no_fields()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot();
        repository.Components = [Component("component-a", SceneResolutionStatus.NotIndexed)];

        var result = await new SceneQueryService(repository).ComponentAsync(
            new ComponentQueryRequest("snapshot-a", "component-a"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.Component);
        Assert.Null(result.ScriptFields);
    }

    [Theory]
    [InlineData("asset-a")]
    [InlineData("SCD_Bikers")]
    [InlineData("ScheduleOne.SpecialCustomers.SpecialCustomerData")]
    public async Task Scriptable_asset_resolves_by_id_name_or_type_with_fields(string selector)
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot();
        repository.Assets.Add(Asset("asset-a", 40, "SCD_Bikers"));
        repository.FieldSets.Add(new SceneScriptFieldSetRecord("asset-a", "snapshot-a", SceneScriptFieldOwnerKind.ScriptableAsset,
            SceneScriptFieldSetStatus.Decoded, null, false, [new SceneScriptField("MaxBuyQuantity", "int", SceneScriptFieldValueKind.Integer, "100")]));

        var result = await new SceneQueryService(repository).ScriptableAssetAsync(
            new ScriptableAssetQueryRequest("snapshot-a", selector),
            TestContext.Current.CancellationToken);

        Assert.Equal(SceneQueryStatus.Resolved, result.Status);
        Assert.Equal("asset-a", result.Asset!.AssetId);
        Assert.Equal("100", Assert.Single(result.ScriptFields!.Fields).Value);
    }

    [Fact]
    public async Task Scriptable_asset_selector_matching_two_assets_is_ambiguous()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot();
        repository.Assets.Add(Asset("asset-a", 40, "SCD_Bikers"));
        repository.Assets.Add(Asset("asset-b", 41, "SCD_Hippies"));

        var result = await new SceneQueryService(repository).ScriptableAssetAsync(
            new ScriptableAssetQueryRequest("snapshot-a", "ScheduleOne.SpecialCustomers.SpecialCustomerData"),
            TestContext.Current.CancellationToken);

        Assert.Equal(SceneQueryStatus.AmbiguousScriptableAsset, result.Status);
        Assert.Null(result.Asset);
        Assert.Equal(["asset-a", "asset-b"], result.Candidates.Select(candidate => candidate.AssetId));
    }

    [Fact]
    public async Task Unknown_scriptable_asset_is_not_found()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot();

        var result = await new SceneQueryService(repository).ScriptableAssetAsync(
            new ScriptableAssetQueryRequest("snapshot-a", "NoSuchAsset"),
            TestContext.Current.CancellationToken);

        Assert.Equal(SceneQueryStatus.ScriptableAssetNotFound, result.Status);
    }

    private static SceneScriptableAssetRecord Asset(string id, long localFileId, string name) =>
        new(id, "snapshot-a", "container-a", localFileId, name, "Assembly-CSharp", "ScheduleOne.SpecialCustomers", "SpecialCustomerData",
            null, null, SceneResolutionStatus.NotIndexed, SceneRecoveryStatus.FullyRecovered);

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

    [Fact]
    public async Task Scene_reference_outcome_uses_the_full_filtered_set_beyond_the_returned_page()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot();
        repository.Documents = [Document("scene-a", "Arena")];
        repository.References = Enumerable.Range(0, 51)
            .Select(index => new SceneReferenceRecord(
                "reference-" + index,
                "snapshot-a",
                null,
                "field-" + index,
                "GameObject",
                "container-a",
                1,
                null,
                null,
                null,
                null,
                null,
                index == 50 ? "missing" : null,
                index == 50 ? SceneResolutionStatus.UnresolvedText : SceneResolutionStatus.Resolved,
                "evidence",
                SceneRecoveryStatus.FullyRecovered))
            .ToArray();

        var result = await new SceneQueryService(repository).SceneAsync(
            new SceneQueryRequest("snapshot-a", "scene-a", IncludeReferences: true, Limit: 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(51, result.References.TotalCount);
        Assert.Equal(1, result.References.ReturnedCount);
        Assert.Equal(1, result.References.UnresolvedCount);
        Assert.All(result.References.Rows, reference => Assert.Equal(SceneResolutionStatus.Resolved, reference.ResolutionStatus));
        Assert.Equal(SceneQueryStatus.UnresolvedSceneReference, result.Status);
    }

    [Fact]
    public async Task Component_selector_preserves_exact_and_ambiguous_semantics_for_normalized_script_types()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot();
        repository.Components =
        [
            new SceneComponentRecord("component-transform", "object-a", "container-a", 1, 4, "Transform", null, null, null, null, null, SceneResolutionStatus.NotIndexed, SceneRecoveryStatus.FullyRecovered),
            new SceneComponentRecord("component-unique", "object-a", "container-a", 2, 114, "MonoBehaviour", "Assembly-CSharp", "Game", "Unique", null, null, SceneResolutionStatus.NotIndexed, SceneRecoveryStatus.GraphOnly),
            new SceneComponentRecord("component-widget-a", "object-a", "container-a", 3, 114, "MonoBehaviour", "Assembly-CSharp", "Game", "Widget", null, null, SceneResolutionStatus.NotIndexed, SceneRecoveryStatus.GraphOnly),
            new SceneComponentRecord("component-widget-b", "object-a", "container-a", 4, 114, "MonoBehaviour", "Assembly-CSharp", "Game", "Widget", null, null, SceneResolutionStatus.NotIndexed, SceneRecoveryStatus.GraphOnly)
        ];
        var selector = new SceneSelector(repository);

        var builtIn = await selector.ResolveComponentAsync("snapshot-a", "Transform", TestContext.Current.CancellationToken);
        var unique = await selector.ResolveComponentAsync("snapshot-a", "Game.Unique", TestContext.Current.CancellationToken);
        var ambiguous = await selector.ResolveComponentAsync("snapshot-a", "Game.Widget", TestContext.Current.CancellationToken);

        Assert.Equal("component-transform", builtIn.Selected!.ComponentId);
        Assert.Equal("component-unique", unique.Selected!.ComponentId);
        Assert.Equal(SceneQueryStatus.AmbiguousComponent, ambiguous.Status);
        Assert.Equal(["component-widget-a", "component-widget-b"], ambiguous.Candidates.Select(component => component.ComponentId));
    }

    [Fact]
    public async Task Completed_snapshot_that_recovered_no_game_object_is_reported_not_served_as_resolved()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot() with { RecoveryStatus = SceneRecoveryStatus.StubOrUnavailable };
        repository.Statistics = new SceneIndexStatistics(9, 7, 0, 0, 0, 0, new Dictionary<string, int> { ["StubOrUnavailable"] = 7 });
        repository.Documents = [Document("scene-a", "level1", recovery: SceneRecoveryStatus.StubOrUnavailable)];
        var service = new SceneQueryService(repository);

        var scenes = await service.ScenesAsync(new SceneListRequest(BuildId: "build-a"), TestContext.Current.CancellationToken);
        var scene = await service.SceneAsync(new SceneQueryRequest("snapshot-a", "level1"), TestContext.Current.CancellationToken);
        var gameObject = await service.GameObjectAsync(new GameObjectQueryRequest("snapshot-a", "scene-a/Nightclub"), TestContext.Current.CancellationToken);

        Assert.Equal(SceneQueryStatus.NoRecoverableSceneObjects, scenes.Status);
        Assert.Equal("snapshot-a", scenes.Snapshot!.SceneSnapshotId);
        Assert.Equal(0, scenes.Page.TotalCount);
        Assert.Equal(SceneQueryStatus.NoRecoverableSceneObjects, scene.Status);
        Assert.Null(scene.Scene);
        Assert.Equal(SceneQueryStatus.NoRecoverableSceneObjects, gameObject.Status);
        Assert.Equal(0, repository.ExactSceneNameLookups);
    }

    [Fact]
    public async Task Stub_snapshot_with_recovered_game_objects_still_resolves()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot() with { RecoveryStatus = SceneRecoveryStatus.StubOrUnavailable };
        repository.Statistics = new SceneIndexStatistics(9, 7, 3, 3, 0, 0, new Dictionary<string, int>());
        repository.Documents = [Document("scene-a", "level1")];

        var scenes = await new SceneQueryService(repository).ScenesAsync(new SceneListRequest(SceneSnapshotId: "snapshot-a"), TestContext.Current.CancellationToken);

        Assert.Equal(SceneQueryStatus.Resolved, scenes.Status);
        Assert.Equal(1, scenes.Page.TotalCount);
    }

    // the selected game object's own transform rides along with the query result so a
    // reader gets its local position without a second lookup.
    [Fact]
    public async Task Game_object_query_returns_the_selected_objects_transform()
    {
        var repository = new QueryRepository();
        repository.Snapshots["snapshot-a"] = Snapshot();
        repository.Documents = [Document("scene-a", "level1")];
        repository.GameObjects = [new SceneGameObjectRecord("object-a", "scene-a", "container-a", 2616, "desert town hall", true, 0, "0", SceneRecoveryStatus.FullyRecovered)];
        repository.Transforms = [new SceneTransformRecord("object-a", "object-parent", 3, 1.5f, -2f, 3.25f, 0, 0, 0, 1, 1, 1, 1, SceneRecoveryStatus.FullyRecovered)];
        var service = new SceneQueryService(repository);

        var result = await service.GameObjectAsync(new GameObjectQueryRequest("snapshot-a", "scene-a/desert town hall"), TestContext.Current.CancellationToken);

        Assert.Equal(SceneQueryStatus.Resolved, result.Status);
        Assert.Equal("object-a", result.GameObject!.GameObjectId);
        Assert.NotNull(result.Transform);
        Assert.Equal((1.5f, -2f, 3.25f), (result.Transform.PositionX, result.Transform.PositionY, result.Transform.PositionZ));
        Assert.Equal("object-parent", result.Transform.ParentGameObjectId);
        Assert.Equal(3, result.Transform.SiblingIndex);
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
        public List<SceneScriptFieldSetRecord> FieldSets { get; } = [];
        public List<SceneScriptableAssetRecord> Assets { get; } = [];
        public Task<SceneScriptFieldSetRecord?> GetScriptFieldSetAsync(string sceneSnapshotId, string ownerId, CancellationToken cancellationToken) => Task.FromResult(FieldSets.SingleOrDefault(row => row.SceneSnapshotId == sceneSnapshotId && row.OwnerId == ownerId));
        public Task<SceneScriptableAssetRecord?> GetScriptableAssetAsync(string sceneSnapshotId, string assetId, CancellationToken cancellationToken) => Task.FromResult(Assets.SingleOrDefault(row => row.SceneSnapshotId == sceneSnapshotId && row.AssetId == assetId));
        public Task<IReadOnlyList<SceneScriptableAssetRecord>> FindScriptableAssetsAsync(string sceneSnapshotId, string selector, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SceneScriptableAssetRecord>>(Assets.Where(row => row.SceneSnapshotId == sceneSnapshotId && (row.Name == selector || $"{row.ScriptNamespace}.{row.ScriptClass}" == selector)).Take(limit).ToArray());
        public Dictionary<string, SceneSnapshotRecord> Snapshots { get; } = [];
        public IReadOnlyList<SceneDocumentRecord> Documents { get; set; } = [];
        public IReadOnlyList<SceneComponentRecord> Components { get; set; } = [];
        public IReadOnlyList<SceneReferenceRecord> References { get; set; } = [];
        public IReadOnlyList<SceneContainerRecord> Containers { get; set; } = [];
        public IReadOnlyList<SceneGameObjectRecord> GameObjects { get; set; } = [];
        public IReadOnlyList<SceneTransformRecord> Transforms { get; set; } = [];
        public List<int> SceneLimits { get; } = [];
        public int ExactSceneNameLookups { get; private set; }
        public SceneIndexStatistics? Statistics { get; set; }

        public Task<SceneSnapshotRecord?> GetCompletedSceneSnapshotAsync(string sceneSnapshotId, CancellationToken cancellationToken) => Task.FromResult(Snapshots.GetValueOrDefault(sceneSnapshotId));
        public Task<SceneSnapshotRecord?> GetLatestCompletedSceneSnapshotAsync(string buildId, CancellationToken cancellationToken) => Task.FromResult(Snapshots.Values.SingleOrDefault(snapshot => snapshot.BuildId == buildId));
        public Task<SceneIndexStatistics?> GetSceneIndexStatisticsAsync(string sceneSnapshotId, CancellationToken cancellationToken) => Statistics is null ? throw new NotSupportedException() : Task.FromResult<SceneIndexStatistics?>(Statistics);
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
        public Task<SceneGameObjectRecord?> GetGameObjectAsync(string sceneSnapshotId, string gameObjectId, CancellationToken cancellationToken) => Task.FromResult(GameObjects.SingleOrDefault(row => row.GameObjectId == gameObjectId));
        public Task<SceneTransformRecord?> GetTransformAsync(string sceneSnapshotId, string gameObjectId, CancellationToken cancellationToken) => Task.FromResult(Transforms.SingleOrDefault(row => row.GameObjectId == gameObjectId));
        public Task<IReadOnlyList<SceneGameObjectRecord>> FindGameObjectsByExactNameAsync(string sceneSnapshotId, string sceneId, string name, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SceneGameObjectRecord>>(GameObjects.Where(row => row.SceneId == sceneId && row.Name == name).Take(limit).ToArray());
        public Task<ScenePageResult<SceneComponentRecord>> ListComponentsAsync(ComponentListQueryOptions options, CancellationToken cancellationToken)
        {
            var rows = Components.Where(component => options.Query is null || component.Kind.Contains(options.Query, StringComparison.OrdinalIgnoreCase)).Take(options.Limit).ToArray();
            return Task.FromResult(new ScenePageResult<SceneComponentRecord>(rows.Length, rows.Length, rows));
        }
        public Task<SceneComponentRecord?> GetComponentAsync(string sceneSnapshotId, string componentId, CancellationToken cancellationToken) => Task.FromResult(Components.SingleOrDefault(component => component.ComponentId == componentId));
        public Task<IReadOnlyList<SceneComponentRecord>> FindComponentsByExactTypeAsync(string sceneSnapshotId, string selector, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SceneComponentRecord>>(Components.Where(component => component.Kind == selector || string.Equals(string.IsNullOrEmpty(component.ScriptNamespace) ? component.ScriptClass : component.ScriptNamespace + "." + component.ScriptClass, selector, StringComparison.Ordinal)).Take(limit).ToArray());
        public Task<ScenePageResult<SceneReferenceRecord>> ListReferencesAsync(ReferenceListQueryOptions options, CancellationToken cancellationToken)
        {
            var rows = References
                .Where(reference => reference.SceneSnapshotId == options.SceneSnapshotId)
                .Where(reference => options.SourceComponentId is null || reference.SourceComponentId == options.SourceComponentId)
                .ToArray();
            var page = rows.Take(options.Limit).ToArray();
            return Task.FromResult(new ScenePageResult<SceneReferenceRecord>(
                rows.Length,
                page.Length,
                page,
                rows.Count(reference => reference.ResolutionStatus != SceneResolutionStatus.Resolved)));
        }
        public Task CreateSceneSnapshotAsync(SceneSnapshotRecord snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StartSceneSnapshotAsync(string sceneSnapshotId, string startedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteSceneSnapshotAsync(string sceneSnapshotId, SceneWriteSet writeSet, string completedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PublishSceneSnapshotAsync(string sceneSnapshotId, string publishedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailSceneSnapshotAsync(string sceneSnapshotId, string failureCode, string failureMessage, string completedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
