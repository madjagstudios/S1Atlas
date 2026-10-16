using System.Text.Json.Serialization;
using S1Atlas.Core.Indexing;
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
            return new SceneListResult(snapshot.Status, snapshot.Snapshot, Empty<SceneDocumentRecord>());

        var window = IndexPaging.PageWindow(request.Offset, request.Limit);
        var fetched = await _repository.ListScenesAsync(
            new SceneListQueryOptions(snapshot.Snapshot.SceneSnapshotId, request.Kind, request.Query, window, request.Offset), cancellationToken);
        var (rows, hasMore) = IndexPaging.TakePage(fetched.Rows, 0, request.Limit);
        var page = fetched with { ReturnedCount = rows.Count, Rows = rows };
        return new SceneListResult(
            SceneQueryStatus.Resolved,
            snapshot.Snapshot,
            page,
            await ContainersAsync(snapshot.Snapshot.SceneSnapshotId, page.Rows.Select(row => row.ContainerId), cancellationToken),
            HasMore: hasMore);
    }

    public async Task<SceneDocumentQueryResult> SceneAsync(SceneQueryRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await ResolveSnapshotAsync(null, request.SceneSnapshotId, cancellationToken);
        if (snapshot.Status != SceneQueryStatus.Resolved || snapshot.Snapshot is null)
            return new SceneDocumentQueryResult(snapshot.Status, snapshot.Snapshot, null, [], Empty<SceneGameObjectRecord>(), Empty<SceneComponentRecord>(), Empty<SceneReferenceRecord>());

        var selection = await _selector.ResolveSceneAsync(snapshot.Snapshot.SceneSnapshotId, request.Selector, request.Kind, cancellationToken);
        if (selection.Selected is null)
            return new SceneDocumentQueryResult(selection.Status, snapshot.Snapshot, null, selection.Candidates, Empty<SceneGameObjectRecord>(), Empty<SceneComponentRecord>(), Empty<SceneReferenceRecord>(), await ContainersAsync(snapshot.Snapshot.SceneSnapshotId, selection.Candidates.Select(row => row.ContainerId), cancellationToken));

        var scene = selection.Selected;
        var children = request.IncludeChildren
            ? await _repository.ListGameObjectsAsync(new GameObjectListQueryOptions(snapshot.Snapshot.SceneSnapshotId, scene.SceneId, Limit: request.Limit, Offset: request.Offset), cancellationToken)
            : Empty<SceneGameObjectRecord>();
        var components = request.IncludeComponents
            ? await _repository.ListComponentsAsync(new ComponentListQueryOptions(snapshot.Snapshot.SceneSnapshotId, scene.SceneId, Limit: request.Limit, Offset: request.Offset), cancellationToken)
            : Empty<SceneComponentRecord>();
        var references = request.IncludeReferences
            ? await _repository.ListReferencesAsync(new ReferenceListQueryOptions(snapshot.Snapshot.SceneSnapshotId, scene.SceneId, Limit: request.Limit, Offset: request.Offset), cancellationToken)
            : Empty<SceneReferenceRecord>();
        return new SceneDocumentQueryResult(Outcome(scene.RecoveryStatus, references), snapshot.Snapshot, scene, [], children, components, references, await ContainersAsync(snapshot.Snapshot.SceneSnapshotId, new[] { scene.ContainerId }.Concat(children.Rows.Select(row => row.ContainerId)).Concat(components.Rows.Select(row => row.ContainerId)).Concat(references.Rows.SelectMany(row => new[] { row.SourceContainerId, row.TargetContainerId }.OfType<string>())), cancellationToken), HasMore: HasMore(request.Offset, children.TotalCount, children.ReturnedCount) || HasMore(request.Offset, components.TotalCount, components.ReturnedCount) || HasMore(request.Offset, references.TotalCount, references.ReturnedCount));
    }

    public async Task<GameObjectQueryResult> GameObjectAsync(GameObjectQueryRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await ResolveSnapshotAsync(null, request.SceneSnapshotId, cancellationToken);
        if (snapshot.Status != SceneQueryStatus.Resolved || snapshot.Snapshot is null)
            return new GameObjectQueryResult(snapshot.Status, snapshot.Snapshot, null, [], Empty<SceneGameObjectRecord>(), Empty<SceneComponentRecord>(), Empty<SceneReferenceRecord>());
        var selection = await _selector.ResolveGameObjectAsync(snapshot.Snapshot.SceneSnapshotId, request.Selector, cancellationToken);
        if (selection.Selected is null)
            return new GameObjectQueryResult(selection.Status, snapshot.Snapshot, null, selection.Candidates, Empty<SceneGameObjectRecord>(), Empty<SceneComponentRecord>(), Empty<SceneReferenceRecord>(), await ContainersAsync(snapshot.Snapshot.SceneSnapshotId, selection.Candidates.Select(row => row.ContainerId), cancellationToken));

        var gameObject = selection.Selected;
        var children = request.IncludeChildren
            ? await _repository.ListGameObjectsAsync(new GameObjectListQueryOptions(snapshot.Snapshot.SceneSnapshotId, ParentGameObjectId: gameObject.GameObjectId, Limit: request.Limit, Offset: request.Offset), cancellationToken)
            : Empty<SceneGameObjectRecord>();
        var components = request.IncludeComponents
            ? await _repository.ListComponentsAsync(new ComponentListQueryOptions(snapshot.Snapshot.SceneSnapshotId, GameObjectId: gameObject.GameObjectId, Limit: request.Limit, Offset: request.Offset), cancellationToken)
            : Empty<SceneComponentRecord>();
        var references = request.IncludeReferences
            ? await _repository.ListReferencesAsync(new ReferenceListQueryOptions(snapshot.Snapshot.SceneSnapshotId, GameObjectId: gameObject.GameObjectId, Limit: request.Limit, Offset: request.Offset), cancellationToken)
            : Empty<SceneReferenceRecord>();
        // The selected object's own transform (local position, rotation, scale, parent, sibling
        // index) is always returned: it is one row and the primary spatial fact a reader wants.
        var transform = await _repository.GetTransformAsync(snapshot.Snapshot.SceneSnapshotId, gameObject.GameObjectId, cancellationToken);
        var incoming = request.IncludeIncoming
            ? await IncomingToGameObjectAsync(snapshot.Snapshot.SceneSnapshotId, gameObject, request.ExternalOnly, request.Limit, request.Offset, cancellationToken)
            : null;
        var containerIds = new[] { gameObject.ContainerId }
            .Concat(children.Rows.Select(row => row.ContainerId))
            .Concat(components.Rows.Select(row => row.ContainerId))
            .Concat(references.Rows.SelectMany(row => new[] { row.SourceContainerId, row.TargetContainerId }.OfType<string>()))
            .Concat(incoming?.Rows.Select(row => row.SourceContainerId) ?? []);
        var hasMore = HasMore(request.Offset, children.TotalCount, children.ReturnedCount) || HasMore(request.Offset, components.TotalCount, components.ReturnedCount) ||
            HasMore(request.Offset, references.TotalCount, references.ReturnedCount) || (incoming is not null && HasMore(request.Offset, incoming.TotalCount, incoming.ReturnedCount));
        return new GameObjectQueryResult(Outcome(gameObject.RecoveryStatus, references), snapshot.Snapshot, gameObject, [], children, components, references, await ContainersAsync(snapshot.Snapshot.SceneSnapshotId, containerIds, cancellationToken), transform, incoming, HasMore: hasMore);
    }

    public Task<SceneDocumentQueryResult> PrefabAsync(PrefabQueryRequest request, CancellationToken cancellationToken) =>
        SceneAsync(new SceneQueryRequest(request.SceneSnapshotId, request.Selector, SceneDocumentKind.Prefab, request.IncludeObjects, request.IncludeComponents, request.IncludeReferences, request.Limit, request.Offset), cancellationToken);

    /// <summary>
    /// Game objects in the snapshot whose name contains a case-insensitive fragment, optionally
    /// within one scene document, paged by offset.
    /// </summary>
    public async Task<GameObjectListResult> GameObjectsAsync(GameObjectListRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await ResolveSnapshotAsync(request.BuildId, request.SceneSnapshotId, cancellationToken);
        if (snapshot.Status != SceneQueryStatus.Resolved || snapshot.Snapshot is null)
            return new GameObjectListResult(snapshot.Status, snapshot.Snapshot, null, [], Empty<SceneGameObjectRecord>(), []);
        var snapshotId = snapshot.Snapshot.SceneSnapshotId;
        var scope = await ResolveSceneScopeAsync(snapshotId, request.Scene, cancellationToken);
        if (scope.Status != SceneQueryStatus.Resolved)
            return new GameObjectListResult(scope.Status, snapshot.Snapshot, null, scope.Candidates, Empty<SceneGameObjectRecord>(), [], await ContainersAsync(snapshotId, scope.Candidates.Select(row => row.ContainerId), cancellationToken));

        var page = await _repository.ListGameObjectsAsync(
            new GameObjectListQueryOptions(snapshotId, scope.Selected?.SceneId, Query: request.Query, Limit: request.Limit, Offset: request.Offset), cancellationToken);
        var scenes = new List<SceneDocumentRecord>();
        foreach (var sceneId in page.Rows.Select(row => row.SceneId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            if (await _repository.GetSceneAsync(snapshotId, sceneId, cancellationToken) is { } document)
                scenes.Add(document);
        return new GameObjectListResult(
            SceneQueryStatus.Resolved, snapshot.Snapshot, scope.Selected, [], page, scenes,
            await ContainersAsync(snapshotId, page.Rows.Select(row => row.ContainerId), cancellationToken),
            HasMore: HasMore(request.Offset, page.TotalCount, page.ReturnedCount));
    }

    /// <summary>
    /// Components whose type is an exact Unity kind or Namespace.Class, optionally with every
    /// type the code index records as deriving from it, each with only the requested decoded
    /// script-field paths (a path also selects the leaves nested under it).
    /// </summary>
    public async Task<ComponentListResult> ComponentsAsync(ComponentListRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Type);
        var snapshot = await ResolveSnapshotAsync(request.BuildId, request.SceneSnapshotId, cancellationToken);
        if (snapshot.Status != SceneQueryStatus.Resolved || snapshot.Snapshot is null)
            return new ComponentListResult(snapshot.Status, snapshot.Snapshot, null, [], request.Type, null, [], null, Empty<SceneComponentListRow>());
        var snapshotId = snapshot.Snapshot.SceneSnapshotId;
        var scope = await ResolveSceneScopeAsync(snapshotId, request.Scene, cancellationToken);
        if (scope.Status != SceneQueryStatus.Resolved)
            return new ComponentListResult(scope.Status, snapshot.Snapshot, null, scope.Candidates, request.Type, null, [], null, Empty<SceneComponentListRow>(), await ContainersAsync(snapshotId, scope.Candidates.Select(row => row.ContainerId), cancellationToken));

        // A type in the same-build code index is matched by the components' resolved type symbol
        // (indexed, and the only way to add derived types); anything else, such as a Unity kind
        // or an unindexed script, by its exact script text.
        var (types, derivedUnavailable) = await TypeSymbolsAsync(snapshot.Snapshot.CodeIndexId, request.Type, request.IncludeDerived, cancellationToken);
        var matchedBy = types.Count > 0 ? ComponentTypeMatch.ResolvedCodeSymbol : ComponentTypeMatch.ScriptText;
        var page = await _repository.ListComponentsAsync(
            new ComponentListQueryOptions(snapshotId, scope.Selected?.SceneId, Limit: request.Limit, Offset: request.Offset,
                ExactType: matchedBy == ComponentTypeMatch.ScriptText ? request.Type : null,
                TypeSymbolIds: matchedBy == ComponentTypeMatch.ResolvedCodeSymbol ? types.Select(type => type.SymbolId).ToArray() : null), cancellationToken);
        var componentIds = page.Rows.Select(row => row.ComponentId).ToArray();
        var gameObjects = (await _repository.GetGameObjectsAsync(snapshotId, page.Rows.Select(row => row.GameObjectId).Distinct(StringComparer.Ordinal).ToArray(), cancellationToken))
            .ToDictionary(row => row.GameObjectId, StringComparer.Ordinal);
        var fieldSets = request.Fields is { Count: > 0 }
            ? (await _repository.GetScriptFieldSetsAsync(snapshotId, componentIds, cancellationToken)).ToDictionary(row => row.OwnerId, StringComparer.Ordinal)
            : [];
        var rows = page.Rows.Select(component => new SceneComponentListRow(
            component,
            gameObjects.GetValueOrDefault(component.GameObjectId),
            fieldSets.TryGetValue(component.ComponentId, out var fields) ? Project(fields, request.Fields!) : null)).ToArray();
        return new ComponentListResult(
            SceneQueryStatus.Resolved, snapshot.Snapshot, scope.Selected, [], request.Type, matchedBy,
            types.Select(type => type.QualifiedName).ToArray(), derivedUnavailable,
            new ScenePageResult<SceneComponentListRow>(page.TotalCount, rows.Length, rows),
            await ContainersAsync(snapshotId, page.Rows.Select(row => row.ContainerId), cancellationToken),
            HasMore: HasMore(request.Offset, page.TotalCount, page.ReturnedCount));
    }

    public async Task<ComponentQueryResult> ComponentAsync(ComponentQueryRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await ResolveSnapshotAsync(null, request.SceneSnapshotId, cancellationToken);
        if (snapshot.Status != SceneQueryStatus.Resolved || snapshot.Snapshot is null)
            return new ComponentQueryResult(snapshot.Status, snapshot.Snapshot, null, [], Empty<SceneReferenceRecord>());
        var selection = await _selector.ResolveComponentAsync(snapshot.Snapshot.SceneSnapshotId, request.Selector, cancellationToken);
        if (selection.Selected is null)
            return new ComponentQueryResult(selection.Status, snapshot.Snapshot, null, selection.Candidates, Empty<SceneReferenceRecord>(), await ContainersAsync(snapshot.Snapshot.SceneSnapshotId, selection.Candidates.Select(row => row.ContainerId), cancellationToken));
        var component = selection.Selected;
        var references = request.IncludeReferences
            ? await _repository.ListReferencesAsync(new ReferenceListQueryOptions(snapshot.Snapshot.SceneSnapshotId, SourceComponentId: component.ComponentId, Limit: request.Limit, Offset: request.Offset), cancellationToken)
            : Empty<SceneReferenceRecord>();
        var status = request.IncludeCode && component.ResolvedTypeSymbolId is null
            ? SceneQueryStatus.UnresolvedCodeSymbol
            : Outcome(component.RecoveryStatus, references);
        var scriptFields = await _repository.GetScriptFieldSetAsync(snapshot.Snapshot.SceneSnapshotId, component.ComponentId, cancellationToken);
        var incoming = request.IncludeIncoming
            ? await _repository.ListIncomingReferencesAsync(new IncomingReferenceQueryOptions(snapshot.Snapshot.SceneSnapshotId, [component.ComponentId], component.GameObjectId, request.ExternalOnly, request.Limit, request.Offset), cancellationToken)
            : null;
        var containerIds = new[] { component.ContainerId }
            .Concat(references.Rows.SelectMany(row => new[] { row.SourceContainerId, row.TargetContainerId }.OfType<string>()))
            .Concat(incoming?.Rows.Select(row => row.SourceContainerId) ?? []);
        var hasMore = HasMore(request.Offset, references.TotalCount, references.ReturnedCount) || (incoming is not null && HasMore(request.Offset, incoming.TotalCount, incoming.ReturnedCount));
        return new ComponentQueryResult(status, snapshot.Snapshot, component, [], references, await ContainersAsync(snapshot.Snapshot.SceneSnapshotId, containerIds, cancellationToken), scriptFields, incoming, HasMore: hasMore);
    }

    public async Task<ScriptableAssetQueryResult> ScriptableAssetAsync(ScriptableAssetQueryRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await ResolveSnapshotAsync(null, request.SceneSnapshotId, cancellationToken);
        if (snapshot.Status != SceneQueryStatus.Resolved || snapshot.Snapshot is null)
            return new ScriptableAssetQueryResult(snapshot.Status, snapshot.Snapshot, null, [], null);
        var snapshotId = snapshot.Snapshot.SceneSnapshotId;
        var selection = await _selector.ResolveScriptableAssetAsync(snapshotId, request.Selector, cancellationToken);
        if (selection.Selected is null)
            return new ScriptableAssetQueryResult(selection.Status, snapshot.Snapshot, null, selection.Candidates, null, await ContainersAsync(snapshotId, selection.Candidates.Select(row => row.ContainerId), cancellationToken));
        var asset = selection.Selected;
        var fields = await _repository.GetScriptFieldSetAsync(snapshotId, asset.AssetId, cancellationToken);
        return new ScriptableAssetQueryResult(SceneQueryStatus.Resolved, snapshot.Snapshot, asset, [], fields, await ContainersAsync(snapshotId, [asset.ContainerId], cancellationToken));
    }

    private async Task<SceneSnapshotQueryResult> ResolveSnapshotAsync(string? buildId, string? sceneSnapshotId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(sceneSnapshotId))
        {
            var specified = await _repository.GetCompletedSceneSnapshotAsync(sceneSnapshotId, cancellationToken);
            return specified is null
                ? new SceneSnapshotQueryResult(SceneQueryStatus.SceneSnapshotNotFound, null)
                : await GuardRecoveredAsync(specified, cancellationToken);
        }
        if (!string.IsNullOrWhiteSpace(buildId))
        {
            var latest = await _repository.GetLatestCompletedSceneSnapshotAsync(buildId, cancellationToken);
            return latest is null
                ? new SceneSnapshotQueryResult(SceneQueryStatus.NoCompletedSceneIndex, null)
                : await GuardRecoveredAsync(latest, cancellationToken);
        }
        if (_atlasRepository is null)
            return new SceneSnapshotQueryResult(SceneQueryStatus.NoCompletedSceneIndex, null);

        var current = await _atlasRepository.GetCurrentSnapshotAsync(cancellationToken);
        if (current is null)
            return new SceneSnapshotQueryResult(SceneQueryStatus.NoCompletedSceneIndex, null);
        var completed = await _repository.GetLatestCompletedSceneSnapshotAsync(current.Build.BuildId, cancellationToken);
        return completed is null
            ? new SceneSnapshotQueryResult(SceneQueryStatus.NoCompletedSceneIndex, null)
            : await GuardRecoveredAsync(completed, cancellationToken);
    }

    // A completed snapshot whose parser decoded no GameObject at all (snapshot recovery is
    // StubOrUnavailable and game_objects is empty) is not usable scene intelligence. Report it
    // as NoRecoverableSceneObjects instead of serving an empty index as Resolved.
    private async Task<SceneSnapshotQueryResult> GuardRecoveredAsync(SceneSnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.RecoveryStatus != SceneRecoveryStatus.StubOrUnavailable)
            return new SceneSnapshotQueryResult(SceneQueryStatus.Resolved, snapshot);
        var statistics = await _repository.GetSceneIndexStatisticsAsync(snapshot.SceneSnapshotId, cancellationToken);
        return statistics is { GameObjectCount: 0 }
            ? new SceneSnapshotQueryResult(SceneQueryStatus.NoRecoverableSceneObjects, snapshot)
            : new SceneSnapshotQueryResult(SceneQueryStatus.Resolved, snapshot);
    }

    // A GameObject is pointed at directly or through one of its components, so both are targets.
    private async Task<SceneIncomingReferencePage> IncomingToGameObjectAsync(string snapshotId, SceneGameObjectRecord gameObject, bool externalOnly, int limit, int offset, CancellationToken cancellationToken)
    {
        var attached = await _repository.ListComponentsAsync(new ComponentListQueryOptions(snapshotId, GameObjectId: gameObject.GameObjectId, Limit: MaxAttachedComponents), cancellationToken);
        if (attached.TotalCount > attached.ReturnedCount)
            throw new InvalidOperationException($"Game object {gameObject.GameObjectId} has more than {MaxAttachedComponents} components.");
        var targets = new[] { gameObject.GameObjectId }.Concat(attached.Rows.Select(row => row.ComponentId)).ToArray();
        return await _repository.ListIncomingReferencesAsync(new IncomingReferenceQueryOptions(snapshotId, targets, gameObject.GameObjectId, externalOnly, limit, offset), cancellationToken);
    }

    // No scene filter is a resolved, unscoped selection.
    private async Task<SceneSelectionResult<SceneDocumentRecord>> ResolveSceneScopeAsync(string snapshotId, string? scene, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(scene)
            ? new SceneSelectionResult<SceneDocumentRecord>(SceneQueryStatus.Resolved, null, [])
            : await _selector.ResolveSceneAsync(snapshotId, scene, null, cancellationToken);

    // The type (by exact Namespace.Class canonical key) and, when asked, every type the same-build
    // code index records as inheriting from or implementing it, transitively.
    private async Task<(IReadOnlyList<IndexSymbolRecord> Types, string? DerivedUnavailable)> TypeSymbolsAsync(string codeIndexId, string type, bool includeDerived, CancellationToken cancellationToken)
    {
        if (_repository is not IIndexRepository index)
            return ([], includeDerived ? "The repository has no code index, so derived types were not added." : null);
        var roots = await index.GetCompletedSymbolByCanonicalKeyAsync(codeIndexId, "ScheduleI:Installed:Type:" + type, cancellationToken);
        if (roots.Count == 0)
            return ([], includeDerived ? $"{type} is not a type in code index {codeIndexId}, so derived types were not added." : null);
        if (!includeDerived)
            return (roots, null);

        var found = roots.ToDictionary(row => row.SymbolId, StringComparer.Ordinal);
        var pending = new Queue<string>(found.Keys);
        while (pending.Count > 0 && found.Count < MaxDerivedTypes)
        {
            var baseId = pending.Dequeue();
            var edges = new List<IndexRelationshipRecord>();
            foreach (var kind in DerivedKinds)
                edges.AddRange(await index.GetCompletedRelationshipsByTargetSymbolIdAsync(codeIndexId, baseId, kind, MaxDerivedTypes, cancellationToken));
            var fresh = edges.Select(edge => edge.SourceSymbolId).Where(id => !found.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToArray();
            if (fresh.Length == 0) continue;
            foreach (var symbol in await index.GetCompletedSymbolsByIdsAsync(codeIndexId, fresh, cancellationToken))
            {
                if (found.TryAdd(symbol.SymbolId, symbol))
                    pending.Enqueue(symbol.SymbolId);
            }
        }

        return (found.Values.OrderBy(row => row.QualifiedName, StringComparer.Ordinal).ToArray(),
            found.Count >= MaxDerivedTypes ? $"Derived types were capped at {MaxDerivedTypes}." : null);
    }

    // Keeps the leaves at a requested path or nested under it: "a" selects "a", "a.b" and
    // "a.Array[0]", and "a.Array" selects "a.Array.size" and each "a.Array[n]".
    private static SceneScriptFieldSetRecord Project(SceneScriptFieldSetRecord fields, IReadOnlyList<string> paths) =>
        fields.Status != SceneScriptFieldSetStatus.Decoded
            ? fields
            : fields with
            {
                Fields = fields.Fields.Where(field => paths.Any(path =>
                    field.Path == path ||
                    (field.Path.Length > path.Length && field.Path.StartsWith(path, StringComparison.Ordinal) && field.Path[path.Length] is '.' or '['))).ToArray()
            };

    private static bool HasMore(int offset, int totalCount, int returnedCount) => (long)offset + returnedCount < totalCount;

    private const int MaxAttachedComponents = 1000;
    private const int MaxDerivedTypes = 1000;
    private static readonly string[] DerivedKinds = [nameof(RelationshipKind.Inherits), nameof(RelationshipKind.ImplementsInterface)];

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
    SceneIndexInProgress,
    SceneTypeTreeUnavailable,
    NoRecoverableSceneObjects,
    ScriptableAssetNotFound,
    AmbiguousScriptableAsset
}

public sealed record SceneListRequest(string? BuildId = null, string? SceneSnapshotId = null, SceneDocumentKind? Kind = null, string? Query = null, int Limit = SceneQueryService.DefaultLimit, int Offset = 0);
// Offset skips that many rows in every included nested list (children, components, references, incoming).
public sealed record SceneQueryRequest(string? SceneSnapshotId, string Selector, SceneDocumentKind? Kind = null, bool IncludeChildren = false, bool IncludeComponents = false, bool IncludeReferences = false, int Limit = SceneQueryService.DefaultLimit, int Offset = 0);
public sealed record GameObjectQueryRequest(string? SceneSnapshotId, string Selector, bool IncludeChildren = false, bool IncludeComponents = false, bool IncludeReferences = false, int Limit = SceneQueryService.DefaultLimit, bool IncludeIncoming = false, bool ExternalOnly = false, int Offset = 0);
public sealed record PrefabQueryRequest(string? SceneSnapshotId, string Selector, bool IncludeObjects = false, bool IncludeComponents = false, bool IncludeReferences = false, int Limit = SceneQueryService.DefaultLimit, int Offset = 0);
public sealed record ComponentQueryRequest(string? SceneSnapshotId, string Selector, bool IncludeReferences = false, bool IncludeCode = false, int Limit = SceneQueryService.DefaultLimit, bool IncludeIncoming = false, bool ExternalOnly = false, int Offset = 0);
public sealed record GameObjectListRequest(string? BuildId = null, string? SceneSnapshotId = null, string? Query = null, string? Scene = null, int Limit = SceneQueryService.DefaultLimit, int Offset = 0);
public sealed record ComponentListRequest(string Type, string? BuildId = null, string? SceneSnapshotId = null, bool IncludeDerived = false, IReadOnlyList<string>? Fields = null, string? Scene = null, int Limit = SceneQueryService.DefaultLimit, int Offset = 0);
public sealed record SceneSnapshotQueryResult(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot);
public sealed record SceneListResult(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot, ScenePageResult<SceneDocumentRecord> Page, IReadOnlyList<SceneContainerRecord>? Containers = null, string? NextCursor = null, [property: JsonIgnore] bool HasMore = false);
public sealed record SceneDocumentQueryResult(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot, SceneDocumentRecord? Scene, IReadOnlyList<SceneDocumentRecord> Candidates, ScenePageResult<SceneGameObjectRecord> Children, ScenePageResult<SceneComponentRecord> Components, ScenePageResult<SceneReferenceRecord> References, IReadOnlyList<SceneContainerRecord>? Containers = null, string? NextCursor = null, [property: JsonIgnore] bool HasMore = false);
public sealed record GameObjectQueryResult(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot, SceneGameObjectRecord? GameObject, IReadOnlyList<SceneGameObjectRecord> Candidates, ScenePageResult<SceneGameObjectRecord> Children, ScenePageResult<SceneComponentRecord> Components, ScenePageResult<SceneReferenceRecord> References, IReadOnlyList<SceneContainerRecord>? Containers = null, SceneTransformRecord? Transform = null, SceneIncomingReferencePage? Incoming = null, string? NextCursor = null, [property: JsonIgnore] bool HasMore = false);
public sealed record ComponentQueryResult(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot, SceneComponentRecord? Component, IReadOnlyList<SceneComponentRecord> Candidates, ScenePageResult<SceneReferenceRecord> References, IReadOnlyList<SceneContainerRecord>? Containers = null, SceneScriptFieldSetRecord? ScriptFields = null, SceneIncomingReferencePage? Incoming = null, string? NextCursor = null, [property: JsonIgnore] bool HasMore = false);
// Scene is the resolved --scene filter; SceneCandidates is set when that filter is ambiguous.
public sealed record GameObjectListResult(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot, SceneDocumentRecord? Scene, IReadOnlyList<SceneDocumentRecord> SceneCandidates, ScenePageResult<SceneGameObjectRecord> Page, IReadOnlyList<SceneDocumentRecord> Scenes, IReadOnlyList<SceneContainerRecord>? Containers = null, string? NextCursor = null, [property: JsonIgnore] bool HasMore = false);
public enum ComponentTypeMatch { ResolvedCodeSymbol, ScriptText }
// IncludedTypes lists the code types matched by symbol (with --derived, the derived ones too);
// DerivedTypesUnavailable says why --derived added none.
public sealed record ComponentListResult(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot, SceneDocumentRecord? Scene, IReadOnlyList<SceneDocumentRecord> SceneCandidates, string Type, ComponentTypeMatch? MatchedBy, IReadOnlyList<string> IncludedTypes, string? DerivedTypesUnavailable, ScenePageResult<SceneComponentListRow> Page, IReadOnlyList<SceneContainerRecord>? Containers = null, string? NextCursor = null, [property: JsonIgnore] bool HasMore = false);
public sealed record SceneComponentListRow(SceneComponentRecord Component, SceneGameObjectRecord? GameObject, SceneScriptFieldSetRecord? ScriptFields);
public sealed record ScriptableAssetQueryRequest(string? SceneSnapshotId, string Selector);
public sealed record ScriptableAssetQueryResult(SceneQueryStatus Status, SceneSnapshotRecord? Snapshot, SceneScriptableAssetRecord? Asset, IReadOnlyList<SceneScriptableAssetRecord> Candidates, SceneScriptFieldSetRecord? ScriptFields, IReadOnlyList<SceneContainerRecord>? Containers = null);
