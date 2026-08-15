using S1Atlas.Core.Scenes;

namespace S1Atlas.Core.Storage;

public interface ISceneRepository
{
    Task CreateSceneSnapshotAsync(SceneSnapshotRecord snapshot, CancellationToken cancellationToken);
    Task StartSceneSnapshotAsync(string sceneSnapshotId, string startedAtUtc, CancellationToken cancellationToken);
    Task CompleteSceneSnapshotAsync(string sceneSnapshotId, SceneWriteSet writeSet, string completedAtUtc, CancellationToken cancellationToken);
    Task PublishSceneSnapshotAsync(string sceneSnapshotId, string publishedAtUtc, CancellationToken cancellationToken);
    Task FailSceneSnapshotAsync(string sceneSnapshotId, string failureCode, string failureMessage, string completedAtUtc, CancellationToken cancellationToken);
    Task<SceneSnapshotRecord?> GetCompletedSceneSnapshotAsync(string sceneSnapshotId, CancellationToken cancellationToken);
    Task<SceneSnapshotRecord?> GetLatestCompletedSceneSnapshotAsync(string buildId, CancellationToken cancellationToken);
    Task<SceneIndexStatistics?> GetSceneIndexStatisticsAsync(string sceneSnapshotId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SceneContainerRecord>> GetSceneContainersAsync(string sceneSnapshotId, IReadOnlyList<string> containerIds, CancellationToken cancellationToken);
    Task<ScenePageResult<SceneDocumentRecord>> ListScenesAsync(SceneListQueryOptions options, CancellationToken cancellationToken);
    Task<IReadOnlyList<SceneDocumentRecord>> FindScenesByExactNameAsync(string sceneSnapshotId, string name, SceneDocumentKind? kind, int limit, CancellationToken cancellationToken);
    Task<SceneDocumentRecord?> GetSceneAsync(string sceneSnapshotId, string sceneId, CancellationToken cancellationToken);
    Task<ScenePageResult<SceneGameObjectRecord>> ListGameObjectsAsync(GameObjectListQueryOptions options, CancellationToken cancellationToken);
    Task<IReadOnlyList<SceneGameObjectRecord>> FindGameObjectsByExactNameAsync(string sceneSnapshotId, string sceneId, string name, int limit, CancellationToken cancellationToken);
    Task<SceneGameObjectRecord?> GetGameObjectAsync(string sceneSnapshotId, string gameObjectId, CancellationToken cancellationToken);
    Task<ScenePageResult<SceneComponentRecord>> ListComponentsAsync(ComponentListQueryOptions options, CancellationToken cancellationToken);
    Task<IReadOnlyList<SceneComponentRecord>> FindComponentsByExactTypeAsync(string sceneSnapshotId, string selector, int limit, CancellationToken cancellationToken);
    Task<SceneComponentRecord?> GetComponentAsync(string sceneSnapshotId, string componentId, CancellationToken cancellationToken);
    Task<ScenePageResult<SceneReferenceRecord>> ListReferencesAsync(ReferenceListQueryOptions options, CancellationToken cancellationToken);
}
