using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Scenes;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests.Scene;

public sealed class SqliteSceneRepositoryTests : IAsyncDisposable
{
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-scene-repository-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private readonly SqliteAtlasRepository _repository;

    public SqliteSceneRepositoryTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
        _repository = new SqliteAtlasRepository(_databasePath);
    }

    [Fact]
    public async Task Completion_inserts_parents_before_children_and_returns_sorted_limited_pages_with_exact_counts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a");
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);

        await _repository.CompleteSceneSnapshotAsync(snapshot.SceneSnapshotId, CreateWriteSet(snapshot, includeSecondDocument: true), "2026-08-14T01:01:00Z", cancellationToken);
        await _repository.PublishSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:02:00Z", cancellationToken);

        var completed = await _repository.GetCompletedSceneSnapshotAsync(snapshot.SceneSnapshotId, cancellationToken);
        Assert.Equal(SceneSnapshotStatus.Completed, completed!.Status);
        Assert.Equal(2, (await _repository.ListScenesAsync(new SceneListQueryOptions(snapshot.SceneSnapshotId, Limit: 1), cancellationToken)).TotalCount);
        var scenes = await _repository.ListScenesAsync(new SceneListQueryOptions(snapshot.SceneSnapshotId, Limit: 1), cancellationToken);
        Assert.Equal(1, scenes.ReturnedCount);
        Assert.Equal("Alpha", scenes.Rows[0].Name);
        var gameObjects = await _repository.ListGameObjectsAsync(new GameObjectListQueryOptions(snapshot.SceneSnapshotId, Limit: 1), cancellationToken);
        Assert.Equal(2, gameObjects.TotalCount);
        Assert.Equal("Alpha Root", gameObjects.Rows[0].Name);
        var components = await _repository.ListComponentsAsync(new ComponentListQueryOptions(snapshot.SceneSnapshotId, Limit: 1), cancellationToken);
        Assert.Equal(2, components.TotalCount);
        Assert.Equal("MeshRenderer", components.Rows[0].Kind);
        var references = await _repository.ListReferencesAsync(new ReferenceListQueryOptions(snapshot.SceneSnapshotId, Limit: 1), cancellationToken);
        Assert.Equal(2, references.TotalCount);
        Assert.Equal("target", references.Rows[0].FieldPath);
    }

    [Fact]
    public async Task Completion_rejects_duplicate_local_file_identity_and_rolls_back_every_scene_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a");
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);
        var writeSet = CreateWriteSet(snapshot) with
        {
            GameObjects =
            [
                CreateGameObject("object-a", "scene-a", "container-a", 11, "First"),
                CreateGameObject("object-b", "scene-a", "container-a", 11, "Duplicate")
            ]
        };

        await Assert.ThrowsAsync<SqliteException>(() => _repository.CompleteSceneSnapshotAsync(snapshot.SceneSnapshotId, writeSet, "2026-08-14T01:01:00Z", cancellationToken));

        Assert.Null(await _repository.GetCompletedSceneSnapshotAsync(snapshot.SceneSnapshotId, cancellationToken));
        Assert.Equal(0, (await _repository.ListScenesAsync(new SceneListQueryOptions(snapshot.SceneSnapshotId), cancellationToken)).TotalCount);
        Assert.Equal(0L, await CountAsync("scene_containers", cancellationToken));
    }

    [Fact]
    public async Task Create_snapshot_rejects_missing_foreign_authority()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);

        await Assert.ThrowsAsync<SqliteException>(() => _repository.CreateSceneSnapshotAsync(CreateSnapshot("snapshot-a", "missing-build"), cancellationToken));
    }

    [Fact]
    public async Task Create_snapshot_reconciles_stale_running_and_failed_attempts_with_the_same_identity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a");

        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.CreateSceneSnapshotAsync(snapshot with { StartedAtUtc = "2026-08-14T01:00:00Z" }, cancellationToken);
        await _repository.FailSceneSnapshotAsync(
            snapshot.SceneSnapshotId,
            "parse_failed",
            "synthetic parser failure",
            "2026-08-14T01:01:00Z",
            cancellationToken);

        await _repository.CreateSceneSnapshotAsync(snapshot with { StartedAtUtc = "2026-08-14T01:02:00Z" }, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:02:00Z", cancellationToken);

        Assert.Equal(1L, await CountAsync("scene_snapshots", cancellationToken));
    }

    [Fact]
    public async Task Create_snapshot_reconciles_an_unpublished_completion_and_removes_its_owned_graph_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a");
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);
        await _repository.CompleteSceneSnapshotAsync(
            snapshot.SceneSnapshotId,
            CreateWriteSet(snapshot),
            "2026-08-14T01:01:00Z",
            cancellationToken);

        await _repository.CreateSceneSnapshotAsync(snapshot with { StartedAtUtc = "2026-08-14T01:02:00Z" }, cancellationToken);

        Assert.Equal(1L, await CountAsync("scene_snapshots", cancellationToken));
        Assert.Equal(0L, await CountAsync("scene_containers", cancellationToken));
        Assert.Equal(0L, await CountAsync("serialized_refs", cancellationToken));
    }

    [Fact]
    public async Task Create_snapshot_preserves_an_immutable_published_snapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a");
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);
        await _repository.CompleteSceneSnapshotAsync(
            snapshot.SceneSnapshotId,
            CreateWriteSet(snapshot),
            "2026-08-14T01:01:00Z",
            cancellationToken);
        await _repository.PublishSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:02:00Z", cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.CreateSceneSnapshotAsync(snapshot with { StartedAtUtc = "2026-08-14T01:03:00Z" }, cancellationToken));

        Assert.NotNull(await _repository.GetCompletedSceneSnapshotAsync(snapshot.SceneSnapshotId, cancellationToken));
        Assert.Equal(1L, await CountAsync("scene_containers", cancellationToken));
    }

    [Fact]
    public async Task Completion_atomically_persists_aggregate_and_document_recovery_with_reusable_statistics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a");
        var writeSet = CreateWriteSet(snapshot) with
        {
            Snapshot = snapshot with { RecoveryStatus = SceneRecoveryStatus.GraphOnly },
            Documents =
            [
                new SceneDocumentRecord(
                    "scene-a",
                    snapshot.SceneSnapshotId,
                    "container-a",
                    SceneDocumentKind.Scene,
                    "Alpha",
                    1,
                    2,
                    1,
                    SceneRecoveryStatus.PartiallyRecovered)
            ]
        };
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);

        await _repository.CompleteSceneSnapshotAsync(
            snapshot.SceneSnapshotId,
            writeSet,
            "2026-08-14T01:01:00Z",
            cancellationToken);
        await _repository.PublishSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:02:00Z", cancellationToken);

        var completed = await _repository.GetCompletedSceneSnapshotAsync(snapshot.SceneSnapshotId, cancellationToken);
        var document = Assert.Single((await _repository.ListScenesAsync(
            new SceneListQueryOptions(snapshot.SceneSnapshotId), cancellationToken)).Rows);
        var statistics = await _repository.GetSceneIndexStatisticsAsync(snapshot.SceneSnapshotId, cancellationToken);

        Assert.Equal(SceneRecoveryStatus.GraphOnly, completed!.RecoveryStatus);
        Assert.Equal(SceneRecoveryStatus.PartiallyRecovered, document.RecoveryStatus);
        Assert.NotNull(statistics);
        Assert.NotEmpty(statistics.RecoveryCounts);
        Assert.Equal(1, statistics.RecoveryCounts[nameof(SceneRecoveryStatus.PartiallyRecovered)]);
    }

    [Fact]
    public async Task GameObject_sourced_references_use_container_local_identity_for_filters_and_full_unresolved_counts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a");
        var writeSet = CreateWriteSet(snapshot) with
        {
            References =
            [
                new SceneReferenceRecord("reference-a", snapshot.SceneSnapshotId, null, "a", "GameObject", "container-a", 11, "container-a", 11, "object-a", null, null, null, SceneResolutionStatus.Resolved, "evidence", SceneRecoveryStatus.FullyRecovered),
                new SceneReferenceRecord("reference-b", snapshot.SceneSnapshotId, null, "b", "GameObject", "container-a", 11, null, null, null, null, null, "missing-b", SceneResolutionStatus.UnresolvedText, "evidence", SceneRecoveryStatus.PartiallyRecovered),
                new SceneReferenceRecord("reference-c", snapshot.SceneSnapshotId, null, "c", "GameObject", "container-a", 11, null, null, null, null, null, "missing-c", SceneResolutionStatus.Unavailable, "evidence", SceneRecoveryStatus.GraphOnly)
            ]
        };
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);
        await _repository.CompleteSceneSnapshotAsync(snapshot.SceneSnapshotId, writeSet, "2026-08-14T01:01:00Z", cancellationToken);
        await _repository.PublishSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:02:00Z", cancellationToken);

        var byScene = await _repository.ListReferencesAsync(
            new ReferenceListQueryOptions(snapshot.SceneSnapshotId, SceneId: "scene-a", Limit: 1),
            cancellationToken);
        var byObject = await _repository.ListReferencesAsync(
            new ReferenceListQueryOptions(snapshot.SceneSnapshotId, GameObjectId: "object-a", Limit: 1),
            cancellationToken);

        Assert.Equal(3, byScene.TotalCount);
        Assert.Equal(1, byScene.ReturnedCount);
        Assert.Equal(2, byScene.UnresolvedCount);
        Assert.Equal(3, byObject.TotalCount);
        Assert.Equal(1, byObject.ReturnedCount);
        Assert.Equal(2, byObject.UnresolvedCount);
    }

    [Fact]
    public async Task Completion_rejects_cross_build_authorities_without_committing_scene_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        await SeedBuildAsync("build-b", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-b", "build-b");
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.CompleteSceneSnapshotAsync(snapshot.SceneSnapshotId, CreateWriteSet(snapshot), "2026-08-14T01:01:00Z", cancellationToken));

        Assert.Equal(0L, await CountAsync("scene_containers", cancellationToken));
        Assert.Null(await _repository.GetCompletedSceneSnapshotAsync(snapshot.SceneSnapshotId, cancellationToken));
    }

    [Fact]
    public async Task Completion_rejects_an_unverified_input_snapshot_without_committing_scene_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        await ExecuteAsync("UPDATE input_snapshots SET replay_verified = 0 WHERE input_snapshot_id = 'input-build-a';");
        var snapshot = CreateSnapshot("snapshot-a", "build-a");
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.CompleteSceneSnapshotAsync(snapshot.SceneSnapshotId, CreateWriteSet(snapshot), "2026-08-14T01:01:00Z", cancellationToken));

        Assert.Equal(0L, await CountAsync("scene_containers", cancellationToken));
        Assert.Null(await _repository.GetCompletedSceneSnapshotAsync(snapshot.SceneSnapshotId, cancellationToken));
    }

    [Fact]
    public async Task Completion_rejects_a_replay_verified_input_unrelated_to_the_validated_extraction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        await ExecuteAsync("INSERT INTO input_snapshots(input_snapshot_id, build_id, root_path, manifest_digest, created_at_utc, replay_verified) VALUES ('input-other', 'build-a', 'other-root', 'other-digest', '2026-01-01', 1);");
        var snapshot = CreateSnapshot("snapshot-a", "build-a") with { InputSnapshotId = "input-other" };
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.CompleteSceneSnapshotAsync(snapshot.SceneSnapshotId, CreateWriteSet(snapshot), "2026-08-14T01:01:00Z", cancellationToken));

        Assert.Equal(0L, await CountAsync("scene_containers", cancellationToken));
        Assert.Null(await _repository.GetCompletedSceneSnapshotAsync(snapshot.SceneSnapshotId, cancellationToken));
    }

    [Fact]
    public async Task Completion_rejects_a_resolved_component_without_exact_symbol_and_index_ids()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a");
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);
        var writeSet = CreateWriteSet(snapshot) with
        {
            Components =
            [
                new SceneComponentRecord("component-a", "object-a", "container-a", 12, 4, "Transform", null, null, null, null, null, SceneResolutionStatus.Resolved, SceneRecoveryStatus.FullyRecovered)
            ]
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.CompleteSceneSnapshotAsync(snapshot.SceneSnapshotId, writeSet, "2026-08-14T01:01:00Z", cancellationToken));

        Assert.Equal(0L, await CountAsync("scene_containers", cancellationToken));
        Assert.Null(await _repository.GetCompletedSceneSnapshotAsync(snapshot.SceneSnapshotId, cancellationToken));
    }

    [Fact]
    public async Task Completion_rejects_a_resolved_component_from_a_different_code_snapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        await ExecuteAsync("INSERT INTO code_snapshots(snapshot_id, codebase, channel, environment_snapshot_id, source_identity, created_at_utc) VALUES ('code-api', 'S1Api', 'Installed', 'environment-build-a', 'api-source', '2026-01-01'); INSERT INTO index_runs(index_id, snapshot_id, status, started_at_utc, completed_at_utc) VALUES ('index-api', 'code-api', 'Completed', '2026-01-01', '2026-01-01'); INSERT INTO symbols(symbol_id, snapshot_id, canonical_key, kind, qualified_name, signature, is_best_effort) VALUES ('symbol-api', 'code-api', 'S1Api:Installed:Type:Api.Widget', 'Type', 'Api.Widget', 'Api.Widget', 0);");
        var snapshot = CreateSnapshot("snapshot-a", "build-a");
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);
        var writeSet = CreateWriteSet(snapshot) with
        {
            Components =
            [
                new SceneComponentRecord("component-a", "object-a", "container-a", 12, 4, "Transform", null, null, null, "symbol-api", "index-api", SceneResolutionStatus.Resolved, SceneRecoveryStatus.FullyRecovered)
            ]
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.CompleteSceneSnapshotAsync(snapshot.SceneSnapshotId, writeSet, "2026-08-14T01:01:00Z", cancellationToken));

        Assert.Equal(0L, await CountAsync("scene_containers", cancellationToken));
        Assert.Null(await _repository.GetCompletedSceneSnapshotAsync(snapshot.SceneSnapshotId, cancellationToken));
    }

    [Fact]
    public async Task Exact_component_type_lookup_matches_built_in_kind_and_normalized_mono_behaviour_identity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a");
        var writeSet = CreateWriteSet(snapshot) with
        {
            Components =
            [
                new SceneComponentRecord("component-a", "object-a", "container-a", 12, 4, "Transform", null, null, null, "symbol-build-a", "index-build-a", SceneResolutionStatus.Resolved, SceneRecoveryStatus.FullyRecovered),
                new SceneComponentRecord("component-widget-a", "object-a", "container-a", 13, 114, "MonoBehaviour", "Assembly-CSharp", "Game", "Widget", null, null, SceneResolutionStatus.NotIndexed, SceneRecoveryStatus.GraphOnly),
                new SceneComponentRecord("component-widget-b", "object-a", "container-a", 14, 114, "MonoBehaviour", "Assembly-CSharp", "Game", "Widget", null, null, SceneResolutionStatus.NotIndexed, SceneRecoveryStatus.GraphOnly),
                new SceneComponentRecord("component-unique", "object-a", "container-a", 15, 114, "MonoBehaviour", "Assembly-CSharp", "Game", "Unique", null, null, SceneResolutionStatus.NotIndexed, SceneRecoveryStatus.GraphOnly)
            ]
        };
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);
        await _repository.CompleteSceneSnapshotAsync(snapshot.SceneSnapshotId, writeSet, "2026-08-14T01:01:00Z", cancellationToken);
        await _repository.PublishSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:02:00Z", cancellationToken);

        var builtIn = await _repository.FindComponentsByExactTypeAsync(
            snapshot.SceneSnapshotId, "Transform", 50, cancellationToken);
        var uniqueScript = await _repository.FindComponentsByExactTypeAsync(
            snapshot.SceneSnapshotId, "Game.Unique", 50, cancellationToken);
        var ambiguousScript = await _repository.FindComponentsByExactTypeAsync(
            snapshot.SceneSnapshotId, "Game.Widget", 50, cancellationToken);

        Assert.Equal("component-a", Assert.Single(builtIn).ComponentId);
        Assert.Equal("component-unique", Assert.Single(uniqueScript).ComponentId);
        Assert.Equal(["component-widget-a", "component-widget-b"], ambiguousScript.Select(component => component.ComponentId));
    }

    [Fact]
    public async Task Completion_rejects_reference_target_symbol_from_another_build_code_index()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        await SeedBuildAsync("build-b", cancellationToken);
        await ExecuteAsync("INSERT INTO environment_snapshots(snapshot_id, build_id, atlas_version, captured_at_utc, identity_version) VALUES ('environment-build-b', 'build-b', 'atlas', '2026-01-02', 1); INSERT INTO code_snapshots(snapshot_id, codebase, channel, environment_snapshot_id, source_identity, created_at_utc) VALUES ('code-build-b', 'ScheduleI', 'Installed', 'environment-build-b', 'source-b', '2026-01-02'); INSERT INTO index_runs(index_id, snapshot_id, status, started_at_utc, completed_at_utc) VALUES ('index-build-b', 'code-build-b', 'Completed', '2026-01-02', '2026-01-02'); INSERT INTO symbols(symbol_id, snapshot_id, canonical_key, kind, qualified_name, signature, is_best_effort) VALUES ('symbol-build-b', 'code-build-b', 'ScheduleI:Installed:Type:Game.OtherBuild', 'Type', 'Game.OtherBuild', 'Game.OtherBuild', 0);");
        var snapshot = CreateSnapshot("snapshot-a", "build-a");
        var writeSet = CreateWriteSet(snapshot) with
        {
            References =
            [
                new SceneReferenceRecord("reference-a", snapshot.SceneSnapshotId, "component-a", "target", "MonoScript", "container-a", 12, null, null, null, null, "symbol-build-b", null, SceneResolutionStatus.Resolved, "evidence", SceneRecoveryStatus.FullyRecovered)
            ]
        };
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.CompleteSceneSnapshotAsync(snapshot.SceneSnapshotId, writeSet, "2026-08-14T01:01:00Z", cancellationToken));

        Assert.Equal(0L, await CountAsync("scene_containers", cancellationToken));
    }

    [Fact]
    public async Task Completion_rejects_reference_target_symbol_from_another_same_build_code_index()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        await ExecuteAsync("INSERT INTO code_snapshots(snapshot_id, codebase, channel, environment_snapshot_id, source_identity, created_at_utc) VALUES ('code-other', 'ScheduleI', 'Installed', 'environment-build-a', 'source', '2026-01-02'); INSERT INTO index_runs(index_id, snapshot_id, status, started_at_utc, completed_at_utc) VALUES ('index-other', 'code-other', 'Completed', '2026-01-02', '2026-01-02'); INSERT INTO symbols(symbol_id, snapshot_id, canonical_key, kind, qualified_name, signature, is_best_effort) VALUES ('symbol-other', 'code-other', 'ScheduleI:Installed:Type:Game.Other', 'Type', 'Game.Other', 'Game.Other', 0);");
        var snapshot = CreateSnapshot("snapshot-a", "build-a");
        var writeSet = CreateWriteSet(snapshot) with
        {
            References =
            [
                new SceneReferenceRecord("reference-a", snapshot.SceneSnapshotId, "component-a", "target", "MonoScript", "container-a", 12, null, null, null, null, "symbol-other", null, SceneResolutionStatus.Resolved, "evidence", SceneRecoveryStatus.FullyRecovered)
            ]
        };
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.CompleteSceneSnapshotAsync(snapshot.SceneSnapshotId, writeSet, "2026-08-14T01:01:00Z", cancellationToken));

        Assert.Equal(0L, await CountAsync("scene_containers", cancellationToken));
    }

    [Fact]
    public async Task Completed_snapshot_is_not_queryable_until_publication_succeeds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a");
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);
        await _repository.CompleteSceneSnapshotAsync(snapshot.SceneSnapshotId, CreateWriteSet(snapshot), "2026-08-14T01:01:00Z", cancellationToken);

        Assert.Null(await _repository.GetCompletedSceneSnapshotAsync(snapshot.SceneSnapshotId, cancellationToken));
        Assert.Equal(0, (await _repository.ListScenesAsync(
            new SceneListQueryOptions(snapshot.SceneSnapshotId, null, null, 50), cancellationToken)).TotalCount);

        await _repository.PublishSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:02:00Z", cancellationToken);

        Assert.NotNull(await _repository.GetCompletedSceneSnapshotAsync(snapshot.SceneSnapshotId, cancellationToken));
        Assert.Equal(1, (await _repository.ListScenesAsync(
            new SceneListQueryOptions(snapshot.SceneSnapshotId, null, null, 50), cancellationToken)).TotalCount);
    }

    [Fact]
    public async Task Unpublished_completion_can_be_failed_after_promotion_failure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a");
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);
        await _repository.CompleteSceneSnapshotAsync(snapshot.SceneSnapshotId, CreateWriteSet(snapshot), "2026-08-14T01:01:00Z", cancellationToken);

        await _repository.FailSceneSnapshotAsync(snapshot.SceneSnapshotId, "promotion_failed", "marker write failed", "2026-08-14T01:02:00Z", cancellationToken);

        Assert.Null(await _repository.GetCompletedSceneSnapshotAsync(snapshot.SceneSnapshotId, cancellationToken));
        Assert.Null(await _repository.GetLatestCompletedSceneSnapshotAsync("build-a", cancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.PublishSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:03:00Z", cancellationToken));
    }

    [Fact]
    public async Task Failed_snapshot_is_not_queryable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAuthoritiesAsync("build-a", cancellationToken);
        var snapshot = CreateSnapshot("snapshot-a", "build-a");
        await _repository.CreateSceneSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartSceneSnapshotAsync(snapshot.SceneSnapshotId, "2026-08-14T01:00:00Z", cancellationToken);

        await _repository.FailSceneSnapshotAsync(snapshot.SceneSnapshotId, "parse_failed", "bad serialized file", "2026-08-14T01:01:00Z", cancellationToken);

        Assert.Null(await _repository.GetCompletedSceneSnapshotAsync(snapshot.SceneSnapshotId, cancellationToken));
        Assert.Null(await _repository.GetLatestCompletedSceneSnapshotAsync("build-a", cancellationToken));
        Assert.Equal(0, (await _repository.ListScenesAsync(new SceneListQueryOptions(snapshot.SceneSnapshotId), cancellationToken)).TotalCount);
    }

    private async Task SeedAuthoritiesAsync(string buildId, CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        await SeedBuildAsync(buildId, cancellationToken);
        await ExecuteAsync($"INSERT INTO input_snapshots(input_snapshot_id, build_id, root_path, manifest_digest, created_at_utc, replay_verified) VALUES ('input-{buildId}', '{buildId}', 'root', 'digest', '2026-01-01', 1); INSERT INTO tool_instances(tool_instance_id, tool_name, version_label, platform, trust_level, definition_digest, package_sha256, executable_sha256, observed_path, first_observed_at_utc, last_verified_at_utc, status) VALUES ('tool-{buildId}', 'tool', NULL, 'win', 'test', NULL, NULL, 'hash', 'path', '2026-01-01', '2026-01-01', 'ok'); INSERT INTO extraction_attempts(attempt_id, build_id, profile_id, profile_version, profile_digest, validation_policy_id, validation_policy_version, validation_policy_digest, adapter_version, extraction_schema_version, input_snapshot_id, status, created_at_utc, working_path, stdout_path, stderr_path, stdout_truncated, stderr_truncated, stdout_discarded_bytes, stderr_discarded_bytes, keep_failed_artifacts, discarded_file_count, discarded_byte_count) VALUES ('attempt-{buildId}', '{buildId}', 'profile', 1, 'digest', 'policy', 1, 'digest', 1, 1, 'input-{buildId}', 'Completed', '2026-01-01', 'work', 'stdout', 'stderr', 0, 0, 0, 0, 0, 0, 0); INSERT INTO validated_extractions(extraction_id, recipe_id, build_id, tool_instance_id, source_attempt_id, profile_id, profile_version, profile_digest, adapter_version, extraction_schema_version, artifact_manifest_digest, root_path, created_at_utc, trust_level, validation_outcome, artifact_count, library_count, managed_assembly_count, type_count, method_count, field_count, property_count, event_count, total_output_bytes, total_managed_bytes) VALUES ('extraction-{buildId}', 'recipe', '{buildId}', 'tool-{buildId}', 'attempt-{buildId}', 'profile', 1, 'digest', 1, 1, 'digest', 'root', '2026-01-01', 'trusted', 'Passed', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0); INSERT INTO environment_snapshots(snapshot_id, build_id, atlas_version, captured_at_utc, identity_version) VALUES ('environment-{buildId}', '{buildId}', 'atlas', '2026-01-01', 1); INSERT INTO code_snapshots(snapshot_id, codebase, channel, environment_snapshot_id, source_identity, created_at_utc) VALUES ('code-{buildId}', 'ScheduleI', 'Installed', 'environment-{buildId}', 'source', '2026-01-01'); INSERT INTO index_runs(index_id, snapshot_id, status, started_at_utc, completed_at_utc) VALUES ('index-{buildId}', 'code-{buildId}', 'Completed', '2026-01-01', '2026-01-01'); INSERT INTO symbols(symbol_id, snapshot_id, canonical_key, kind, qualified_name, signature, is_best_effort) VALUES ('symbol-{buildId}', 'code-{buildId}', 'ScheduleI:Installed:Type:Game.Widget', 'Type', 'Game.Widget', 'Game.Widget', 0);");
    }

    private async Task SeedBuildAsync(string buildId, CancellationToken cancellationToken) =>
        await _repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                2,
                new GameBuild(buildId, "assembly-" + buildId, "metadata-" + buildId, DateTimeOffset.Parse("2026-08-14T00:00:00Z"), true),
                new InstallationObservation("2022.3", "app", "steam", "root", "assembly", "metadata"),
                [],
                "test",
                DateTimeOffset.Parse("2026-08-14T00:00:00Z")),
            cancellationToken);

    private static SceneSnapshotRecord CreateSnapshot(string snapshotId, string buildId) =>
        new(snapshotId, buildId, "extraction-build-a", "input-build-a", "code-build-a", "index-build-a", "parser", "1", Digest, SceneSnapshotStatus.Running, SceneRecoveryStatus.FullyRecovered, "2026-08-14T00:00:00Z");

    private static SceneWriteSet CreateWriteSet(SceneSnapshotRecord snapshot, bool includeSecondDocument = false)
    {
        var documents = new List<SceneDocumentRecord> { new("scene-a", snapshot.SceneSnapshotId, "container-a", SceneDocumentKind.Scene, "Alpha", 1, 2, 1, SceneRecoveryStatus.FullyRecovered) };
        var gameObjects = new List<SceneGameObjectRecord> { CreateGameObject("object-a", "scene-a", "container-a", 11, "Alpha Root") };
        var components = new List<SceneComponentRecord> { new("component-a", "object-a", "container-a", 12, 4, "Transform", null, null, null, "symbol-build-a", "index-build-a", SceneResolutionStatus.Resolved, SceneRecoveryStatus.FullyRecovered) };
        var references = new List<SceneReferenceRecord> { new("reference-a", snapshot.SceneSnapshotId, "component-a", "target", "GameObject", "container-a", 12, "container-a", 11, "object-a", null, "symbol-build-a", null, SceneResolutionStatus.Resolved, "evidence", SceneRecoveryStatus.FullyRecovered) };
        if (includeSecondDocument)
        {
            documents.Add(new SceneDocumentRecord("scene-b", snapshot.SceneSnapshotId, "container-a", SceneDocumentKind.Prefab, "Beta", 2, 1, 1, SceneRecoveryStatus.FullyRecovered));
            gameObjects.Add(CreateGameObject("object-b", "scene-b", "container-a", 13, "Beta Root"));
            components.Add(new SceneComponentRecord("component-b", "object-b", "container-a", 14, 4, "MeshRenderer", null, null, null, null, null, SceneResolutionStatus.NotIndexed, SceneRecoveryStatus.FullyRecovered));
            references.Add(new SceneReferenceRecord("reference-b", snapshot.SceneSnapshotId, "component-b", "zzz", "Material", "container-a", 14, null, null, null, null, null, "text", SceneResolutionStatus.UnresolvedText, "evidence", SceneRecoveryStatus.FullyRecovered));
        }

        return new SceneWriteSet(snapshot, [new SceneContainerRecord("container-a", snapshot.SceneSnapshotId, "assets/main.assets", "Assets", "2022.3", 1, 10, Digest, "manifest")], documents, gameObjects, [new SceneTransformRecord("object-a", null, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, SceneRecoveryStatus.FullyRecovered)], components, references);
    }

    private static SceneGameObjectRecord CreateGameObject(string id, string sceneId, string containerId, long localFileId, string name) =>
        new(id, sceneId, containerId, localFileId, name, true, 0, "Untagged", SceneRecoveryStatus.FullyRecovered);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;" + sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string table, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
