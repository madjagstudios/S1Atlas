using S1Atlas.Core.Scenes;
using Xunit;

namespace S1Atlas.Core.Tests.Scenes;

public sealed class SceneModelTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Recovery_and_resolution_enums_expose_the_approved_values()
    {
        Assert.Equal(
            ["FullyRecovered", "PartiallyRecovered", "GraphOnly", "StubOrUnavailable", "Unknown"],
            Enum.GetNames<SceneRecoveryStatus>());
        Assert.Equal(["Scene", "Prefab"], Enum.GetNames<SceneDocumentKind>());
        Assert.Equal(["Running", "Completed", "Failed"], Enum.GetNames<SceneSnapshotStatus>());
        Assert.Equal(
            ["Resolved", "UnresolvedText", "Ambiguous", "NotIndexed", "Unavailable"],
            Enum.GetNames<SceneResolutionStatus>());
    }

    [Fact]
    public void Snapshot_preserves_exact_ids_and_allows_absent_terminal_facts_while_running()
    {
        var snapshot = new SceneSnapshotRecord(
            "scene-snapshot-id", "build-id", "extraction-id", "input-snapshot-id", "code-snapshot-id", "code-index-id",
            "parser-id", "parser-version", Hash, SceneSnapshotStatus.Running, SceneRecoveryStatus.GraphOnly,
            "2026-08-14T00:00:00Z");

        Assert.Equal("scene-snapshot-id", snapshot.SceneSnapshotId);
        Assert.Equal("build-id", snapshot.BuildId);
        Assert.Equal("extraction-id", snapshot.ExtractionId);
        Assert.Equal("input-snapshot-id", snapshot.InputSnapshotId);
        Assert.Equal("code-snapshot-id", snapshot.CodeSnapshotId);
        Assert.Equal("code-index-id", snapshot.CodeIndexId);
        Assert.Null(snapshot.CompletedAtUtc);
        Assert.Null(snapshot.FailureCode);
        Assert.Null(snapshot.FailureMessage);
    }

    [Fact]
    public void Models_preserve_nullable_recovery_facts_without_fabricating_values()
    {
        var document = new SceneDocumentRecord("scene-id", "snapshot-id", "container-id", SceneDocumentKind.Scene, "Main", null, 0, 0, SceneRecoveryStatus.Unknown);
        var gameObject = new SceneGameObjectRecord("game-object-id", "scene-id", "container-id", 1, "Player", null, null, null, SceneRecoveryStatus.GraphOnly);
        var transform = new SceneTransformRecord("game-object-id", null, null, null, null, null, null, null, null, null, null, null, null, SceneRecoveryStatus.GraphOnly);
        var component = new SceneComponentRecord("component-id", "game-object-id", "container-id", 2, 114, "MonoBehaviour", null, null, null, null, null, SceneResolutionStatus.NotIndexed, SceneRecoveryStatus.GraphOnly);
        var reference = new SceneReferenceRecord("reference-id", "snapshot-id", null, null, null, "container-id", 3, null, null, null, null, null, null, SceneResolutionStatus.Unavailable, "object table only", SceneRecoveryStatus.StubOrUnavailable);

        Assert.Null(document.SourceLocalFileId);
        Assert.Null(gameObject.Active);
        Assert.Null(gameObject.Layer);
        Assert.Null(gameObject.Tag);
        Assert.Null(transform.ParentGameObjectId);
        Assert.Null(transform.SiblingIndex);
        Assert.Null(transform.PositionX);
        Assert.Null(transform.PositionY);
        Assert.Null(transform.PositionZ);
        Assert.Null(transform.RotationX);
        Assert.Null(transform.RotationY);
        Assert.Null(transform.RotationZ);
        Assert.Null(transform.RotationW);
        Assert.Null(transform.ScaleX);
        Assert.Null(transform.ScaleY);
        Assert.Null(transform.ScaleZ);
        Assert.Null(component.ScriptAssembly);
        Assert.Null(component.ScriptNamespace);
        Assert.Null(component.ScriptClass);
        Assert.Null(component.ResolvedTypeSymbolId);
        Assert.Null(component.ResolvedCodeIndexId);
        Assert.Null(reference.SourceComponentId);
        Assert.Null(reference.FieldPath);
        Assert.Null(reference.DeclaredType);
        Assert.Null(reference.TargetContainerId);
        Assert.Null(reference.TargetLocalFileId);
        Assert.Null(reference.TargetGameObjectId);
        Assert.Null(reference.TargetComponentId);
        Assert.Null(reference.TargetSymbolId);
        Assert.Null(reference.TargetText);
    }

    [Fact]
    public void Models_preserve_exact_identity_values_at_every_graph_level()
    {
        var container = new SceneContainerRecord("container-id", "snapshot-id", "level0", "SerializedFile", "2022.3.62f1", 22, 1, Hash, "[]");
        var document = new SceneDocumentRecord("scene-id", "snapshot-id", "container-id", SceneDocumentKind.Scene, "Main", 1, 1, 1, SceneRecoveryStatus.FullyRecovered);
        var gameObject = new SceneGameObjectRecord("game-object-id", "scene-id", "container-id", 2, "Player", true, 0, "Untagged", SceneRecoveryStatus.FullyRecovered);
        var transform = new SceneTransformRecord("game-object-id", "parent-game-object-id", 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, SceneRecoveryStatus.FullyRecovered);
        var component = new SceneComponentRecord("component-id", "game-object-id", "container-id", 3, 1, "Transform", null, null, null, "symbol-id", "index-id", SceneResolutionStatus.Resolved, SceneRecoveryStatus.FullyRecovered);
        var reference = new SceneReferenceRecord("reference-id", "snapshot-id", "component-id", null, null, "container-id", 4, "target-container-id", 5, "target-game-object-id", "target-component-id", "target-symbol-id", null, SceneResolutionStatus.Resolved, "evidence", SceneRecoveryStatus.FullyRecovered);

        Assert.Equal("container-id", container.ContainerId);
        Assert.Equal("snapshot-id", container.SceneSnapshotId);
        Assert.Equal("scene-id", document.SceneId);
        Assert.Equal("snapshot-id", document.SceneSnapshotId);
        Assert.Equal("container-id", document.ContainerId);
        Assert.Equal("game-object-id", gameObject.GameObjectId);
        Assert.Equal("scene-id", gameObject.SceneId);
        Assert.Equal("container-id", gameObject.ContainerId);
        Assert.Equal("game-object-id", transform.GameObjectId);
        Assert.Equal("parent-game-object-id", transform.ParentGameObjectId);
        Assert.Equal("component-id", component.ComponentId);
        Assert.Equal("game-object-id", component.GameObjectId);
        Assert.Equal("container-id", component.ContainerId);
        Assert.Equal("symbol-id", component.ResolvedTypeSymbolId);
        Assert.Equal("index-id", component.ResolvedCodeIndexId);
        Assert.Equal("reference-id", reference.ReferenceId);
        Assert.Equal("snapshot-id", reference.SceneSnapshotId);
        Assert.Equal("component-id", reference.SourceComponentId);
        Assert.Equal("container-id", reference.SourceContainerId);
        Assert.Equal("target-container-id", reference.TargetContainerId);
        Assert.Equal("target-game-object-id", reference.TargetGameObjectId);
        Assert.Equal("target-component-id", reference.TargetComponentId);
        Assert.Equal("target-symbol-id", reference.TargetSymbolId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Records_reject_blank_required_ids(string invalidId)
    {
        Assert.Throws<ArgumentException>(() => new SceneContainerRecord(invalidId, "snapshot-id", "level0", "SerializedFile", "2022.3.62f1", 22, 1, Hash, "[]"));
        Assert.Throws<ArgumentException>(() => new SceneDocumentRecord("scene-id", invalidId, "container-id", SceneDocumentKind.Scene, "Main", null, 0, 0, SceneRecoveryStatus.FullyRecovered));
        Assert.Throws<ArgumentException>(() => new SceneGameObjectRecord("game-object-id", invalidId, "container-id", 1, "Player", true, 0, "Untagged", SceneRecoveryStatus.FullyRecovered));
        Assert.Throws<ArgumentException>(() => new SceneComponentRecord("component-id", invalidId, "container-id", 1, 1, "Transform", null, null, null, null, null, SceneResolutionStatus.Resolved, SceneRecoveryStatus.FullyRecovered));
        Assert.Throws<ArgumentException>(() => new SceneReferenceRecord("reference-id", invalidId, null, null, null, "container-id", 1, null, null, null, null, null, null, SceneResolutionStatus.Unavailable, "evidence", SceneRecoveryStatus.Unknown));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("abcdef")]
    public void Containers_and_snapshots_reject_invalid_hashes(string invalidHash)
    {
        Assert.Throws<ArgumentException>(() => new SceneContainerRecord("container-id", "snapshot-id", "level0", "SerializedFile", "2022.3.62f1", 22, 1, invalidHash, "[]"));
        Assert.Throws<ArgumentException>(() => new SceneSnapshotRecord("snapshot-id", "build-id", "extraction-id", "input-id", "code-snapshot-id", "code-index-id", "parser-id", "parser-version", invalidHash, SceneSnapshotStatus.Running, SceneRecoveryStatus.Unknown, "2026-08-14T00:00:00Z"));
    }

    [Fact]
    public void Models_reject_nonpositive_required_local_file_ids()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneGameObjectRecord("game-object-id", "scene-id", "container-id", 0, "Player", true, 0, "Untagged", SceneRecoveryStatus.FullyRecovered));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneComponentRecord("component-id", "game-object-id", "container-id", -1, 1, "Transform", null, null, null, null, null, SceneResolutionStatus.Resolved, SceneRecoveryStatus.FullyRecovered));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneReferenceRecord("reference-id", "snapshot-id", null, null, null, "container-id", 0, null, null, null, null, null, null, SceneResolutionStatus.Unavailable, "evidence", SceneRecoveryStatus.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneReferenceRecord("reference-id", "snapshot-id", null, null, null, "container-id", 1, null, 0, null, null, null, null, SceneResolutionStatus.Unavailable, "evidence", SceneRecoveryStatus.Unknown));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Query_options_reject_nonpositive_limits(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneListQueryOptions("snapshot-id", Limit: limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameObjectListQueryOptions("snapshot-id", Limit: limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ComponentListQueryOptions("snapshot-id", Limit: limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReferenceListQueryOptions("snapshot-id", Limit: limit));
    }

    [Fact]
    public void Scene_page_result_rejects_counts_that_do_not_describe_its_rows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScenePageResult<string>(-1, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScenePageResult<string>(0, -1, []));
        Assert.Throws<ArgumentException>(() => new ScenePageResult<string>(3, 1, []));
        Assert.Throws<ArgumentException>(() => new ScenePageResult<string>(1, 2, ["first", "second"]));
    }

    [Fact]
    public void Scene_page_result_rejects_null_rows_and_preserves_exact_page_counts()
    {
        Assert.Throws<ArgumentNullException>(() => new ScenePageResult<string>(0, 0, null!));

        var result = new ScenePageResult<string>(3, 2, ["first", "second"]);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.ReturnedCount);
        Assert.Equal(["first", "second"], result.Rows);
    }
}
