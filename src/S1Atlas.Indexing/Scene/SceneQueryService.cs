using S1Atlas.Core.Scenes;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Scene;

public sealed class SceneQueryService
{
    public const int DefaultLimit = 50;

    private readonly ISceneRepository _repository;
    private readonly IAtlasRepository? _atlasRepository;
    private readonly SceneSelector _selector;

    public SceneQueryService(ISceneRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _selector = new SceneSelector(repository);
    }

    public SceneQueryService(ISceneRepository repository, IAtlasRepository atlasRepository)
        : this(repository)
    {
        _atlasRepository = atlasRepository ?? throw new ArgumentNullException(nameof(atlasRepository));
    }

    public async Task<SceneListResult> ScenesAsync(SceneListRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await ResolveSnapshotAsync(request.BuildId, request.SceneSnapshotId, cancellationToken);
        if (snapshot.Status != SceneQueryStatus.Resolved || snapshot.Snapshot is null)
            return new SceneListResult(snapshot.Status, null, Empty<SceneDocumentRecord>());

        var page = await _repository.ListScenesAsync(
            new SceneListQueryOptions(snapshot.Snapshot.SceneSnapshotId, request.Kind, request.Query, request.Limit), cancellationToken);
        return new SceneListResult(SceneQueryStatus.Resolved, snapshot.Snapshot, page, await ContainersAsync(snapshot.Snapshot.SceneSnapshotId, page.Rows.Select(row => row.ContainerId), cancellationToken));
    }

    public async Task<SceneDocumentQueryResult> SceneAsync(SceneQueryRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await ResolveSnapshotAsync(null, request.SceneSnapshotId, cancellationToken);
        if (snapshot.Snapshot is null)
            return new SceneDocumentQueryResult(snapshot.Status, null, null, [], Empty<SceneGameObjectRecord>(), Empty<SceneComponentRecord>(), Empty<SceneReferenceRecord>());

        var selection = await _selector.ResolveSceneAsync(snapshot.Snapshot.SceneSnapshotId, request.Selector, request.Kind, cancellationToken);
        if (selection.Selected is null)
            return new SceneDocumentQueryResult(selection.Status, snapshot.Snapshot, null, selection.Candidates, Empty<SceneGameObjectRecord>(), Empty<SceneComponentRecord>(), Empty<SceneReferenceRecord>(), await ContainersAsync(snapshot.Snapshot.SceneSnapshotId, selection.Candidates.Select(row => row.ContainerId), cancellationToken));

        var scene = selection.Selected;
        var children = request.IncludeChildren
            ? await _repository.ListGameObjectsAsync(new GameObjectListQueryOptions(snapshot.Snapshot.SceneSnapshotId, scene.SceneId, Limit: request.Limit), cancellationToken)
            : Empty<SceneGameObjectRecord>();
        var components = request.IncludeComponents
            ? await _repository.ListComponentsAsync(new ComponentListQueryOptions(snapshot.Snapshot.SceneSnapshotId, scene.SceneId, Limit: request.Limit), cancellationToken)
            : Empty<SceneComponentRecord>();
        var references = request.IncludeReferences
            ? await _repository.ListReferencesAsync(new ReferenceListQueryOptions(snapshot.Snapshot.SceneSnapshotId, scene.SceneId, Limit: request.Limit), cancellationToken)
            : Empty<SceneReferenceRecord>();
        return new SceneDocumentQueryResult(Outcome(scene.RecoveryStatus, references), snapshot.Snapshot, scene, [], children, components, references, await ContainersAsync(snapshot.Snapshot.SceneSnapshotId, new[] { scene.ContainerId }.Concat(children.Rows.Select(row => row.ContainerId)).Concat(components.Rows.Select(row => row.ContainerId)).Concat(references.Rows.SelectMany(row => new[] { row.SourceContainerId, row.TargetContainerId }.OfType<string>())), cancellationToken));
    }

    public async Task<GameObjectQueryResult> GameObjectAsync(GameObjectQueryRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await ResolveSnapshotAsync(null, request.SceneSnapshotId, cancellationToken);
        if (snapshot.Snapshot is null)
            return new GameObjectQueryResult(snapshot.Status, null, null, [], Empty<SceneGameObjectRecord>(), Empty<SceneComponentRecord>(), Empty<SceneReferenceRecord>());
        var selection = await _selector.ResolveGameObjectAsync(snapshot.Snapshot.SceneSnapshotId, request.Selector, cancellationToken);
        if (selection.Selected is null)
            return new GameObjectQueryResult(selection.Status, snapshot.Snapshot, null, selection.Candidates, Empty<SceneGameObjectRecord>(), Empty<SceneComponentRecord>(), Empty<SceneReferenceRecord>(), await ContainersAsync(snapshot.Snapshot.SceneSnapshotId, selection.Candidates.Select(row => row.ContainerId), cancellationToken));

        var gameObject = selection.Selected;
        var children = request.IncludeChildren
            ? await _repository.ListGameObjectsAsync(new GameObjectListQueryOptions(snapshot.Snapshot.SceneSnapshotId, ParentGameObjectId: gameObject.GameObjectId, Limit: request.Limit), cancellationToken)
            : Empty<SceneGameObjectRecord>();
        var components = request.IncludeComponents
            ? await _repository.ListComponentsAsync(new ComponentListQueryOptions(snapshot.Snapshot.SceneSnapshotId, GameObjectId: gameObject.GameObjectId, Limit: request.Limit), cancellationToken)
            : Empty<SceneComponentRecord>();
        var references = request.IncludeReferences
            ? await _repository.ListReferencesAsync(new ReferenceListQueryOptions(snapshot.Snapshot.SceneSnapshotId, GameObjectId: gameObject.GameObjectId, Limit: request.Limit), cancellationToken)
            : Empty<SceneReferenceRecord>();
        return new GameObjectQueryResult(Outcome(gameObject.RecoveryStatus, references), snapshot.Snapshot, gameObject, [], children, components, references, await ContainersAsync(snapshot.Snapshot.SceneSnapshotId, new[] { gameObject.ContainerId }.Concat(children.Rows.Select(row => row.ContainerId)).Concat(components.Rows.Select(row => row.ContainerId)).Concat(references.Rows.SelectMany(row => new[] { row.SourceContainerId, row.TargetContainerId }.OfType<string>())), cancellationToken));
    }

    public Task<SceneDocumentQueryResult> PrefabAsync(PrefabQueryRequest request, CancellationToken cancellationToken) =>
        SceneAsync(new SceneQueryRequest(request.SceneSnapshotId, request.Selector, SceneDocumentKind.Prefab, request.IncludeObjects, request.IncludeComponents, request.IncludeReferences, request.Limit), cancellationToken);

    public async Task<ComponentQueryResult> ComponentAsync(ComponentQueryRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await ResolveSnapshotAsync(null, request.SceneSnapshotId, cancellationToken);
        if (snapshot.Snapshot is null)
            return new ComponentQueryResult(snapshot.Status, null, null, [], Empty<SceneReferenceRecord>());
        var selection = await _selector.ResolveComponentAsync(snapshot.Snapshot.SceneSnapshotId, request.Selector, cancellationToken);
        if (selection.Selected is null)
            return new ComponentQueryResult(selection.Status, snapshot.Snapshot, null, selection.Candidates, Empty<SceneReferenceRecord>(), await ContainersAsync(snapshot.Snapshot.SceneSnapshotId, selection.Candidates.Select(row => row.ContainerId), cancellationToken));
        var component = selection.Selected;
        var references = request.IncludeReferences
            ? await _repository.ListReferencesAsync(new ReferenceListQueryOptions(snapshot.Snapshot.SceneSnapshotId, SourceComponentId: component.ComponentId, Limit: request.Limit), cancellationToken)
            : Empty<SceneReferenceRecord>();
        var status = request.IncludeCode && component.ResolvedTypeSymbolId is null
            ? SceneQueryStatus.UnresolvedCodeSymbol
            : Outcome(component.RecoveryStatus, references);
        return new ComponentQueryResult(status, snapshot.Snapshot, component, [], references, await ContainersAsync(snapshot.Snapshot.SceneSnapshotId, new[] { component.ContainerId }.Concat(references.Rows.SelectMany(row => new[] { row.SourceContainerId, row.TargetContainerId }.OfType<string>())), cancellationToken));
    }

    private async Task<SceneSnapshotQueryResult> ResolveSnapshotAsync(string? buildId, string? sceneSnapshotId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(sceneSnapshotId))
        {
            var specified = await _repository.GetCompletedSceneSnapshotAsync(sceneSnapshotId, cancellationToken);
            return specified is null
                ? new SceneSnapshotQueryResult(SceneQueryStatus.SceneSnapshotNotFound, null)
                : new SceneSnapshotQueryResult(SceneQueryStatus.Resolved, specified);
        }
        if (!string.IsNullOrWhiteSpace(buildId))
        {
            var latest = await _repository.GetLatestCompletedSceneSnapshotAsync(buildId, cancellationToken);
            return latest is null
                ? new SceneSnapshotQueryResult(SceneQueryStatus.NoCompletedSceneIndex, null)
                : new SceneSnapshotQueryResult(SceneQueryStatus.Resolved, latest);
        }
        if (_atlasRepository is null)
            return new SceneSnapshotQueryResult(SceneQueryStatus.NoCompletedSceneIndex, null);

        var current = await _atlasRepository.GetCurrentSnapshotAsync(cancellationToken);
        if (current is null)
            return new SceneSnapshotQueryResult(SceneQueryStatus.NoCompletedSceneIndex, null);
        var completed = await _repository.GetLatestCompletedSceneSnapshotAsync(current.Build.BuildId, cancellationToken);
        return completed is null
            ? new SceneSnapshotQueryResult(SceneQueryStatus.NoCompletedSceneIndex, null)
            : new SceneSnapshotQueryResult(SceneQueryStatus.Resolved, completed);
    }

    private static SceneQueryStatus Outcome(SceneRecoveryStatus recovery, ScenePageResult<SceneReferenceRecord> references) =>
        recovery == SceneRecoveryStatus.PartiallyRecovered ? SceneQueryStatus.PartialRecovery :
        references.UnresolvedCount > 0 ? SceneQueryStatus.UnresolvedSceneReference :
        SceneQueryStatus.Resolved;

    private static ScenePageResult<T> Empty<T>() => new(0, 0, []);
    private Task<IReadOnlyList<SceneContainerRecord>> ContainersAsync(string snapshotId, IEnumerable<string> ids, CancellationToken cancellationToken) => _repository.GetSceneContainersAsync(snapshotId, ids.Distinct(StringComparer.Ordinal).ToArray(), cancellationToken);
}

public enum SceneQueryStatus
{
    Resolved,
    NoCompletedSceneIndex,
    SceneSnapshotNotFound,
    SceneNotFound,
    AmbiguousScene,
    GameObjectNotFound,
    AmbiguousGameObject,
    ComponentNotFound,
    AmbiguousComponent,
    SceneInputIntegrityFailure,
    UnsupportedContainer,
    PartialRecovery,
    UnresolvedSceneReference,
    UnresolvedCodeSymbol,
    NoPreferredVerifiedExtraction,
    NoReplayVerifiedExtractionInput,
    NoMatchingEnvironmentSnapshot,
    NoCompletedScheduleOneCodeIndex,
    CrossBuildCodeIndex,
    PreferredExtractionChanged,
    ReplayVerifiedInputChanged,
    CodeIndexChanged,
    NoVerifiedSceneContainers,
    SceneIndexInProgress
}

public sealed record SceneListRequest(string? BuildId = null, string? SceneSnapshotId = null, SceneDocumentKind? Kind = null, string? Query = null, int Limit = SceneQueryService.DefaultLimit);
public sealed record SceneQueryRequest(string? SceneSnapshotId, string Selector, SceneDocumentKind? Kind = null, bool IncludeChildren = false, bool IncludeComponents = false, bool IncludeReferences = false, int Limit = SceneQueryService.DefaultLimit);
public sealed record GameObjectQueryRequest(string? SceneSnapshotId, string Selector, bool IncludeChildren = false, bool IncludeComponents = false, bool IncludeReferences = false, int Limit = SceneQueryService.DefaultLimit);
public sealed record PrefabQueryRequest(string? SceneSnapshotId, string Selector, bool IncludeObjects = false, bool IncludeComponents = false, bool IncludeReferences = false, int Limit = SceneQueryService.DefaultLimit);
public sealed record ComponentQueryRequest(string? SceneSnapshotId, string Selector, bool IncludeReferences = false, bool IncludeCode = false, int Limit = SceneQueryService.DefaultLimit);
public sealed record SceneSnapshotQueryResult(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot);
public sealed record SceneListResult(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot, ScenePageResult<SceneDocumentRecord> Page, IReadOnlyList<SceneContainerRecord>? Containers = null);
public sealed record SceneDocumentQueryResult(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot, SceneDocumentRecord? Scene, IReadOnlyList<SceneDocumentRecord> Candidates, ScenePageResult<SceneGameObjectRecord> Children, ScenePageResult<SceneComponentRecord> Components, ScenePageResult<SceneReferenceRecord> References, IReadOnlyList<SceneContainerRecord>? Containers = null);
public sealed record GameObjectQueryResult(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot, SceneGameObjectRecord? GameObject, IReadOnlyList<SceneGameObjectRecord> Candidates, ScenePageResult<SceneGameObjectRecord> Children, ScenePageResult<SceneComponentRecord> Components, ScenePageResult<SceneReferenceRecord> References, IReadOnlyList<SceneContainerRecord>? Containers = null);
public sealed record ComponentQueryResult(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot, SceneComponentRecord? Component, IReadOnlyList<SceneComponentRecord> Candidates, ScenePageResult<SceneReferenceRecord> References, IReadOnlyList<SceneContainerRecord>? Containers = null);
