using S1Atlas.Core.Scenes;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Scene;

public sealed class SceneSelector
{
    private const int CandidateLimit = 50;
    private readonly ISceneRepository _repository;

    public SceneSelector(ISceneRepository repository) =>
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<SceneSelectionResult<SceneDocumentRecord>> ResolveSceneAsync(
        string sceneSnapshotId,
        string selector,
        SceneDocumentKind? kind,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var byId = await _repository.GetSceneAsync(sceneSnapshotId, selector, cancellationToken);
        if (byId is not null && (kind is null || byId.Kind == kind))
            return SceneSelectionResult<SceneDocumentRecord>.Resolved(byId);

        var matches = (await _repository.FindScenesByExactNameAsync(sceneSnapshotId, selector, kind, CandidateLimit, cancellationToken))
            .OrderBy(row => row.Name, StringComparer.Ordinal)
            .ThenBy(row => row.SceneId, StringComparer.Ordinal)
            .ToArray();
        return matches.Length switch
        {
            0 => SceneSelectionResult<SceneDocumentRecord>.NotFound(SceneQueryStatus.SceneNotFound),
            1 => SceneSelectionResult<SceneDocumentRecord>.Resolved(matches[0]),
            _ => SceneSelectionResult<SceneDocumentRecord>.Ambiguous(SceneQueryStatus.AmbiguousScene, matches)
        };
    }

    public async Task<SceneSelectionResult<SceneGameObjectRecord>> ResolveGameObjectAsync(
        string sceneSnapshotId,
        string selector,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var byId = await _repository.GetGameObjectAsync(sceneSnapshotId, selector, cancellationToken);
        if (byId is not null) return SceneSelectionResult<SceneGameObjectRecord>.Resolved(byId);

        var slash = selector.IndexOf('/');
        if (slash <= 0 || slash == selector.Length - 1)
            return SceneSelectionResult<SceneGameObjectRecord>.NotFound(SceneQueryStatus.GameObjectNotFound);

        var sceneId = selector[..slash];
        var name = selector[(slash + 1)..];
        var scene = await _repository.GetSceneAsync(sceneSnapshotId, sceneId, cancellationToken);
        if (scene is null) return SceneSelectionResult<SceneGameObjectRecord>.NotFound(SceneQueryStatus.GameObjectNotFound);

        var matches = (await _repository.FindGameObjectsByExactNameAsync(sceneSnapshotId, scene.SceneId, name, CandidateLimit, cancellationToken))
            .OrderBy(row => row.Name, StringComparer.Ordinal)
            .ThenBy(row => row.GameObjectId, StringComparer.Ordinal)
            .ToArray();
        return matches.Length switch
        {
            0 => SceneSelectionResult<SceneGameObjectRecord>.NotFound(SceneQueryStatus.GameObjectNotFound),
            1 => SceneSelectionResult<SceneGameObjectRecord>.Resolved(matches[0]),
            _ => SceneSelectionResult<SceneGameObjectRecord>.Ambiguous(SceneQueryStatus.AmbiguousGameObject, matches)
        };
    }

    public async Task<SceneSelectionResult<SceneComponentRecord>> ResolveComponentAsync(
        string sceneSnapshotId,
        string selector,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var byId = await _repository.GetComponentAsync(sceneSnapshotId, selector, cancellationToken);
        if (byId is not null) return SceneSelectionResult<SceneComponentRecord>.Resolved(byId);

        var matches = (await _repository.FindComponentsByExactKindAsync(sceneSnapshotId, selector, CandidateLimit, cancellationToken))
            .OrderBy(row => row.Kind, StringComparer.Ordinal)
            .ThenBy(row => row.ComponentId, StringComparer.Ordinal)
            .ToArray();
        return matches.Length switch
        {
            0 => SceneSelectionResult<SceneComponentRecord>.NotFound(SceneQueryStatus.ComponentNotFound),
            1 => SceneSelectionResult<SceneComponentRecord>.Resolved(matches[0]),
            _ => SceneSelectionResult<SceneComponentRecord>.Ambiguous(SceneQueryStatus.AmbiguousComponent, matches)
        };
    }
}

public sealed record SceneSelectionResult<T>(SceneQueryStatus Status, T? Selected, IReadOnlyList<T> Candidates)
{
    public static SceneSelectionResult<T> Resolved(T selected) => new(SceneQueryStatus.Resolved, selected, []);
    public static SceneSelectionResult<T> NotFound(SceneQueryStatus status) => new(status, default, []);
    public static SceneSelectionResult<T> Ambiguous(SceneQueryStatus status, IReadOnlyList<T> candidates) => new(status, default, candidates);
}
