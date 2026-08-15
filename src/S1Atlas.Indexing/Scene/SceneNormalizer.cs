using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Scenes;
using S1Atlas.Extraction.Scene;

namespace S1Atlas.Indexing.Scene;

public sealed class SceneNormalizer
{
    private const int SupportedSerializedFileVersion = 22;
    private const int PrefabInstanceClassId = 1001;
    private const int PrefabClassId = 1001480554;
    private readonly SceneCodeSymbolResolver _symbolResolver;
    private readonly SceneRecoveryClassifier _recoveryClassifier;

    public SceneNormalizer(
        SceneCodeSymbolResolver symbolResolver,
        SceneRecoveryClassifier recoveryClassifier)
    {
        _symbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
        _recoveryClassifier = recoveryClassifier ?? throw new ArgumentNullException(nameof(recoveryClassifier));
    }

    public async Task<SceneWriteSet> NormalizeAsync(
        SceneSnapshotRecord snapshot,
        IReadOnlyList<VerifiedSceneContainer> verifiedContainers,
        IReadOnlyList<ParsedSceneContainer> parsedContainers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(verifiedContainers);
        ArgumentNullException.ThrowIfNull(parsedContainers);
        cancellationToken.ThrowIfCancellationRequested();

        var verifiedByPath = UniqueByPath(verifiedContainers, container => container.RelativePath, nameof(verifiedContainers));
        var parsedByPath = UniqueByPath(parsedContainers, container => container.RelativePath, nameof(parsedContainers));
        if (!verifiedByPath.Keys.Order(StringComparer.Ordinal).SequenceEqual(parsedByPath.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Verified and parsed scene container sets do not match.");

        foreach (var path in verifiedByPath.Keys)
        {
            var verified = verifiedByPath[path];
            var parsed = parsedByPath[path];
            if (!string.Equals(verified.Sha256, parsed.Sha256, StringComparison.Ordinal) ||
                !string.Equals(verified.UnityVersion, parsed.UnityVersion, StringComparison.Ordinal) ||
                verified.SerializedFileVersion != parsed.SerializedFileVersion)
            {
                throw new InvalidDataException($"Parsed scene container '{path}' does not match its verified facts.");
            }
        }

        var orderedPaths = parsedByPath.Keys.Order(StringComparer.Ordinal).ToArray();
        var containerIds = orderedPaths.ToDictionary(
            path => path,
            path => HashId(snapshot.SceneSnapshotId, "container", path),
            StringComparer.Ordinal);
        var containerRecords = orderedPaths.Select(path =>
        {
            var verified = verifiedByPath[path];
            return new SceneContainerRecord(
                containerIds[path],
                snapshot.SceneSnapshotId,
                path,
                ContainerKind(path),
                verified.UnityVersion,
                verified.SerializedFileVersion,
                verified.ByteCount,
                verified.Sha256,
                verified.SidecarManifest);
        }).ToArray();

        var objects = new Dictionary<ObjectKey, ParsedSceneObject>();
        foreach (var path in orderedPaths)
        {
            foreach (var item in parsedByPath[path].Objects.OrderBy(item => item.LocalFileId))
            {
                if (item.LocalFileId <= 0 || !objects.TryAdd(new ObjectKey(path, item.LocalFileId), item))
                    throw new InvalidDataException($"Container '{path}' has an invalid or duplicate local file ID '{item.LocalFileId}'.");
            }
        }

        var pointers = new PointerResolver(parsedByPath, objects);
        var gameObjectKeys = objects
            .Where(pair => pair.Value.Kind == ParsedSceneObjectKind.GameObject && pair.Value.GameObject is not null)
            .Select(pair => pair.Key)
            .ToHashSet();
        var componentOwners = BuildComponentOwners(parsedByPath, verifiedByPath, objects, gameObjectKeys, pointers);
        var transformFacts = BuildTransformFacts(parsedByPath, verifiedByPath, objects, componentOwners, gameObjectKeys, pointers);
        RejectParentCycles(transformFacts.Values);

        var documentAssignments = BuildDocumentAssignments(
            snapshot,
            parsedByPath,
            objects,
            gameObjectKeys,
            transformFacts,
            pointers,
            containerIds);
        var gameObjectIds = documentAssignments.GameObjectSceneIds.Keys.ToDictionary(
            key => key,
            key => HashId(snapshot.SceneSnapshotId, "game-object", key.ContainerPath, key.LocalFileId.ToString(CultureInfo.InvariantCulture)));

        var gameObjects = documentAssignments.GameObjectSceneIds
            .OrderBy(pair => pair.Key.ContainerPath, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.LocalFileId)
            .Select(pair =>
            {
                var parsed = objects[pair.Key];
                var data = parsed.GameObject!;
                return new SceneGameObjectRecord(
                    gameObjectIds[pair.Key],
                    pair.Value,
                    containerIds[pair.Key.ContainerPath],
                    pair.Key.LocalFileId,
                    data.Name,
                    data.IsActive,
                    checked((int)data.Layer),
                    data.Tag.ToString(CultureInfo.InvariantCulture),
                    _recoveryClassifier.Classify(new SceneRecoveryFacts(true, true, true, true, true)));
            })
            .ToArray();

        var transforms = transformFacts
            .Where(pair => gameObjectIds.ContainsKey(pair.Value.GameObject))
            .OrderBy(pair => pair.Value.GameObject.ContainerPath, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value.GameObject.LocalFileId)
            .Select(pair => ToTransformRecord(pair.Value, gameObjectIds))
            .ToArray();

        var componentIds = componentOwners
            .Where(pair => gameObjectIds.ContainsKey(pair.Value))
            .ToDictionary(
                pair => pair.Key,
                pair => HashId(snapshot.SceneSnapshotId, "component", pair.Key.ContainerPath, pair.Key.LocalFileId.ToString(CultureInfo.InvariantCulture)));
        var scriptResolutions = new Dictionary<ObjectKey, SceneCodeSymbolResolution>();
        var components = new List<SceneComponentRecord>(componentIds.Count);
        foreach (var componentKey in componentIds.Keys.OrderBy(key => key.ContainerPath, StringComparer.Ordinal).ThenBy(key => key.LocalFileId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = objects[componentKey];
            var owner = componentOwners[componentKey];
            SceneCodeSymbolResolution? scriptResolution = null;
            if (item.Kind == ParsedSceneObjectKind.MonoBehaviour)
            {
                scriptResolution = item.MonoBehaviour is null
                    ? UnavailableScript()
                    : await ResolveMonoBehaviourScriptAsync(
                        snapshot,
                        componentKey,
                        item,
                        objects,
                        pointers,
                        scriptResolutions,
                        cancellationToken);
            }

            components.Add(new SceneComponentRecord(
                componentIds[componentKey],
                gameObjectIds[owner],
                containerIds[componentKey.ContainerPath],
                componentKey.LocalFileId,
                item.UnityClassId,
                ComponentKind(item),
                scriptResolution?.RawAssemblyName,
                scriptResolution?.RawNamespace,
                scriptResolution?.RawClassName,
                scriptResolution?.SymbolId,
                scriptResolution?.CodeIndexId,
                scriptResolution?.Status ?? SceneResolutionStatus.NotIndexed,
                ComponentRecovery(item, transformFacts.TryGetValue(componentKey, out var transform) && transform.SchemaValid)));
        }

        var references = await BuildReferencesAsync(
            snapshot,
            objects,
            pointers,
            containerIds,
            gameObjectIds,
            componentIds,
            scriptResolutions,
            cancellationToken);
        var documents = FinalizeDocuments(documentAssignments.Documents, gameObjects, transforms);
        var recovery = AggregateRecovery(
            documents.Select(document => document.RecoveryStatus)
                .Concat(gameObjects.Select(item => item.RecoveryStatus))
                .Concat(transforms.Select(item => item.RecoveryStatus))
                .Concat(components.Select(item => item.RecoveryStatus))
                .Concat(references.Select(item => item.RecoveryStatus)));

        return new SceneWriteSet(
            snapshot with { RecoveryStatus = recovery },
            containerRecords,
            documents,
            gameObjects,
            transforms,
            components,
            references);
    }

    private static Dictionary<ObjectKey, ObjectKey> BuildComponentOwners(
        IReadOnlyDictionary<string, ParsedSceneContainer> containers,
        IReadOnlyDictionary<string, VerifiedSceneContainer> verifiedContainers,
        IReadOnlyDictionary<ObjectKey, ParsedSceneObject> objects,
        IReadOnlySet<ObjectKey> gameObjectKeys,
        PointerResolver pointers)
    {
        var owners = new Dictionary<ObjectKey, ObjectKey>();
        foreach (var gameObjectKey in gameObjectKeys.OrderBy(key => key.ContainerPath, StringComparer.Ordinal).ThenBy(key => key.LocalFileId))
        {
            foreach (var pointer in objects[gameObjectKey].GameObject!.Components)
            {
                var target = pointers.Resolve(gameObjectKey.ContainerPath, pointer);
                if (target.Target is null)
                    continue;
                var targetObject = objects[target.Target.Value];
                if (targetObject.Kind is ParsedSceneObjectKind.GameObject or ParsedSceneObjectKind.MonoScript or ParsedSceneObjectKind.BuildSettings or ParsedSceneObjectKind.PrefabEvidence)
                    throw new InvalidDataException("A GameObject component PPtr targets a non-component object.");
                AddOwner(owners, target.Target.Value, gameObjectKey);
            }
        }

        foreach (var pair in objects)
        {
            ParsedScenePPtr? ownerPointer = pair.Value.Kind switch
            {
                ParsedSceneObjectKind.Transform when pair.Value.Transform is not null &&
                    HasKnownSchemaAndBounds(
                        containers[pair.Key.ContainerPath],
                        pair.Value,
                        verifiedContainers[pair.Key.ContainerPath].ByteCount) => pair.Value.Transform.GameObject,
                ParsedSceneObjectKind.MonoBehaviour when pair.Value.MonoBehaviour is not null => pair.Value.MonoBehaviour.GameObject,
                _ => null
            };
            if (ownerPointer is null)
                continue;
            var owner = pointers.Resolve(pair.Key.ContainerPath, ownerPointer.Value);
            if (owner.Target is null || !gameObjectKeys.Contains(owner.Target.Value))
                throw new InvalidDataException("A component's GameObject PPtr does not resolve to a parsed GameObject.");
            AddOwner(owners, pair.Key, owner.Target.Value);
        }
        return owners;
    }

    private static void AddOwner(Dictionary<ObjectKey, ObjectKey> owners, ObjectKey component, ObjectKey owner)
    {
        if (owners.TryGetValue(component, out var existing) && existing != owner)
            throw new InvalidDataException("A component is attached to more than one GameObject.");
        owners[component] = owner;
    }

    private static Dictionary<ObjectKey, TransformFact> BuildTransformFacts(
        IReadOnlyDictionary<string, ParsedSceneContainer> containers,
        IReadOnlyDictionary<string, VerifiedSceneContainer> verifiedContainers,
        IReadOnlyDictionary<ObjectKey, ParsedSceneObject> objects,
        IReadOnlyDictionary<ObjectKey, ObjectKey> componentOwners,
        IReadOnlySet<ObjectKey> gameObjectKeys,
        PointerResolver pointers)
    {
        var result = new Dictionary<ObjectKey, TransformFact>();
        foreach (var pair in objects.Where(pair => pair.Value.Kind == ParsedSceneObjectKind.Transform && pair.Value.Transform is not null))
        {
            if (!componentOwners.TryGetValue(pair.Key, out var gameObject))
                continue;
            var data = pair.Value.Transform!;
            var container = containers[pair.Key.ContainerPath];
            var schemaValid = HasKnownSchemaAndBounds(
                container,
                pair.Value,
                verifiedContainers[pair.Key.ContainerPath].ByteCount);
            ObjectKey? parentGameObject = null;
            int? siblingIndex = null;
            var hierarchyComplete = schemaValid;
            if (schemaValid && data.ParentTransform.LocalFileId != 0)
            {
                var parent = pointers.Resolve(pair.Key.ContainerPath, data.ParentTransform);
                if (parent.Target is not null)
                {
                    if (!objects.TryGetValue(parent.Target.Value, out var parentObject) ||
                        parentObject.Kind != ParsedSceneObjectKind.Transform ||
                        !componentOwners.TryGetValue(parent.Target.Value, out var resolvedParentGameObject) ||
                        !gameObjectKeys.Contains(resolvedParentGameObject))
                    {
                        throw new InvalidDataException("A Transform parent PPtr does not resolve to a parsed Transform with a GameObject.");
                    }
                    parentGameObject = resolvedParentGameObject;
                }
                else
                {
                    hierarchyComplete = false;
                }
            }
            else if (schemaValid)
            {
                siblingIndex = data.RootOrder;
            }

            result[pair.Key] = new TransformFact(pair.Key, gameObject, parentGameObject, siblingIndex, data, schemaValid, hierarchyComplete);
        }

        var listedChildren = result.Values
            .Where(fact => fact.SchemaValid)
            .ToDictionary(fact => fact.Transform, _ => new HashSet<ObjectKey>());
        foreach (var fact in result.Values.Where(fact => fact.SchemaValid).ToArray())
        {
            for (var index = 0; index < fact.Data.Children.Count; index++)
            {
                var child = pointers.Resolve(fact.Transform.ContainerPath, fact.Data.Children[index]);
                if (child.Target is null)
                {
                    result[fact.Transform] = result[fact.Transform] with { HierarchyComplete = false };
                    continue;
                }
                if (!result.TryGetValue(child.Target.Value, out var childFact) || childFact.ParentGameObject != fact.GameObject)
                    throw new InvalidDataException("Transform child and parent relationships disagree.");
                listedChildren[fact.Transform].Add(child.Target.Value);
                result[child.Target.Value] = childFact with { SiblingIndex = index };
            }
        }

        var transformByGameObject = result.Values.ToDictionary(fact => fact.GameObject, fact => fact.Transform);
        foreach (var childFact in result.Values.Where(fact => fact.SchemaValid && fact.ParentGameObject is not null).ToArray())
        {
            if (!transformByGameObject.TryGetValue(childFact.ParentGameObject!.Value, out var parentTransform) ||
                !listedChildren.TryGetValue(parentTransform, out var children) ||
                children.Contains(childFact.Transform))
            {
                continue;
            }

            result[parentTransform] = result[parentTransform] with { HierarchyComplete = false };
            result[childFact.Transform] = result[childFact.Transform] with { HierarchyComplete = false };
        }
        return result;
    }

    private static void RejectParentCycles(IEnumerable<TransformFact> transforms)
    {
        var parents = transforms.ToDictionary(fact => fact.GameObject, fact => fact.ParentGameObject);
        foreach (var start in parents.Keys)
        {
            var seen = new HashSet<ObjectKey>();
            var current = start;
            while (parents.TryGetValue(current, out var parent) && parent is not null)
            {
                if (!seen.Add(current))
                    throw new InvalidDataException("Transform parent cycle detected.");
                current = parent.Value;
            }
        }
    }

    private DocumentAssignments BuildDocumentAssignments(
        SceneSnapshotRecord snapshot,
        IReadOnlyDictionary<string, ParsedSceneContainer> containers,
        IReadOnlyDictionary<ObjectKey, ParsedSceneObject> objects,
        IReadOnlySet<ObjectKey> gameObjectKeys,
        IReadOnlyDictionary<ObjectKey, TransformFact> transforms,
        PointerResolver pointers,
        IReadOnlyDictionary<string, string> containerIds)
    {
        var documents = new List<SceneDocumentRecord>();
        var assignments = new Dictionary<ObjectKey, string>();
        var sceneNames = ReadBuildSettingsSceneNames(containers.Values);
        foreach (var path in containers.Keys.Where(IsLevelPath).OrderBy(LevelNumber))
        {
            var level = LevelNumber(path);
            var hasBuildSettingsName = level < sceneNames.Count && !string.IsNullOrWhiteSpace(sceneNames[level]);
            var objectTableGameObjects = objects
                .Where(pair => string.Equals(pair.Key.ContainerPath, path, StringComparison.Ordinal) &&
                               pair.Value.Kind == ParsedSceneObjectKind.GameObject)
                .Select(pair => pair.Value)
                .ToArray();
            var hasUndecodedGameObjects = objectTableGameObjects.Any(item => item.GameObject is null);
            var name = hasBuildSettingsName
                ? SceneNameFromPath(sceneNames[level])
                : Path.GetFileName(path);
            var sceneId = HashId(snapshot.SceneSnapshotId, "scene", path);
            documents.Add(new SceneDocumentRecord(
                sceneId,
                snapshot.SceneSnapshotId,
                containerIds[path],
                SceneDocumentKind.Scene,
                name,
                null,
                objectTableGameObjects.Length,
                0,
                hasUndecodedGameObjects
                    ? SceneRecoveryStatus.StubOrUnavailable
                    : hasBuildSettingsName
                    ? SceneRecoveryStatus.FullyRecovered
                    : SceneRecoveryStatus.PartiallyRecovered));
            foreach (var gameObject in gameObjectKeys.Where(key => string.Equals(key.ContainerPath, path, StringComparison.Ordinal)))
                assignments[gameObject] = sceneId;
        }

        var prefabRoots = FindProvenPrefabRoots(containers, objects, gameObjectKeys, pointers);
        var children = transforms.Values
            .Where(fact => fact.ParentGameObject is not null)
            .GroupBy(fact => fact.ParentGameObject!.Value)
            .ToDictionary(group => group.Key, group => group.Select(fact => fact.GameObject).ToArray());
        foreach (var prefab in prefabRoots.OrderBy(item => item.Evidence.ContainerPath, StringComparer.Ordinal).ThenBy(item => item.Evidence.LocalFileId))
        {
            var sceneId = HashId(snapshot.SceneSnapshotId, "prefab", prefab.Evidence.ContainerPath, prefab.Evidence.LocalFileId.ToString(CultureInfo.InvariantCulture));
            documents.Add(new SceneDocumentRecord(
                sceneId,
                snapshot.SceneSnapshotId,
                containerIds[prefab.Evidence.ContainerPath],
                SceneDocumentKind.Prefab,
                prefab.Root is not null
                    ? objects[prefab.Root.Value].GameObject!.Name
                    : Path.GetFileName(prefab.Evidence.ContainerPath),
                prefab.Evidence.LocalFileId,
                0,
                0,
                prefab.Root is null
                    ? SceneRecoveryStatus.StubOrUnavailable
                    : SceneRecoveryStatus.FullyRecovered));
            if (prefab.Root is null || assignments.ContainsKey(prefab.Root.Value))
                continue;
            foreach (var member in Descendants(prefab.Root.Value, children))
            {
                if (!assignments.TryAdd(member, sceneId))
                    throw new InvalidDataException("A GameObject belongs to more than one proven prefab graph.");
            }
        }

        foreach (var path in containers.Keys.Where(IsOrdinaryAssetPath).Order(StringComparer.Ordinal))
        {
            var remaining = gameObjectKeys
                .Where(key => string.Equals(key.ContainerPath, path, StringComparison.Ordinal) && !assignments.ContainsKey(key))
                .ToArray();
            var undecodedCount = objects.Count(pair =>
                string.Equals(pair.Key.ContainerPath, path, StringComparison.Ordinal) &&
                pair.Value.Kind == ParsedSceneObjectKind.GameObject &&
                pair.Value.GameObject is null);
            if (remaining.Length == 0 && undecodedCount == 0)
                continue;
            var sceneId = HashId(snapshot.SceneSnapshotId, "asset-graph", path);
            documents.Add(new SceneDocumentRecord(
                sceneId,
                snapshot.SceneSnapshotId,
                containerIds[path],
                SceneDocumentKind.Scene,
                Path.GetFileName(path),
                null,
                remaining.Length + undecodedCount,
                0,
                undecodedCount > 0
                    ? SceneRecoveryStatus.StubOrUnavailable
                    : SceneRecoveryStatus.GraphOnly));
            foreach (var gameObject in remaining)
                assignments[gameObject] = sceneId;
        }

        return new DocumentAssignments(documents, assignments);
    }

    private static IReadOnlyList<PrefabRoot> FindProvenPrefabRoots(
        IReadOnlyDictionary<string, ParsedSceneContainer> containers,
        IReadOnlyDictionary<ObjectKey, ParsedSceneObject> objects,
        IReadOnlySet<ObjectKey> gameObjectKeys,
        PointerResolver pointers)
    {
        var roots = new List<PrefabRoot>();
        foreach (var container in containers.Values.Where(container =>
                     container.HasPrefabEvidence && IsOrdinaryAssetPath(container.RelativePath)))
        {
            foreach (var evidence in container.Objects.Where(item =>
                         item.Kind == ParsedSceneObjectKind.PrefabEvidence &&
                         item.UnityClassId is PrefabInstanceClassId or PrefabClassId))
            {
                var candidates = new HashSet<ObjectKey>();
                foreach (var reference in evidence.References.Where(reference =>
                             reference.DeclaredType.Contains("GameObject", StringComparison.Ordinal) &&
                             reference.FieldPath.Contains("Root", StringComparison.OrdinalIgnoreCase)))
                {
                    var target = pointers.Resolve(container.RelativePath, reference.Target);
                    if (target.Target is not null && gameObjectKeys.Contains(target.Target.Value))
                        candidates.Add(target.Target.Value);
                }
                if (candidates.Count > 1)
                    throw new InvalidDataException("A prefab evidence object identifies more than one root GameObject.");
                roots.Add(new PrefabRoot(
                    new ObjectKey(container.RelativePath, evidence.LocalFileId),
                    candidates.Count == 1 ? candidates.Single() : null));
            }
        }
        return roots.Distinct().ToArray();
    }

    private async Task<SceneCodeSymbolResolution> ResolveMonoBehaviourScriptAsync(
        SceneSnapshotRecord snapshot,
        ObjectKey componentKey,
        ParsedSceneObject component,
        IReadOnlyDictionary<ObjectKey, ParsedSceneObject> objects,
        PointerResolver pointers,
        IDictionary<ObjectKey, SceneCodeSymbolResolution> cache,
        CancellationToken cancellationToken)
    {
        var pointer = component.MonoBehaviour!.Script;
        var target = pointers.Resolve(componentKey.ContainerPath, pointer);
        if (target.Target is null)
            return UnavailableScript();
        if (!objects.TryGetValue(target.Target.Value, out var scriptObject) || scriptObject.Kind != ParsedSceneObjectKind.MonoScript)
            throw new InvalidDataException("A MonoBehaviour script PPtr does not resolve to a parsed MonoScript.");
        if (scriptObject.MonoScript is null)
            return UnavailableScript();
        if (cache.TryGetValue(target.Target.Value, out var cached))
            return cached;
        var resolution = await _symbolResolver.ResolveAsync(
            snapshot.BuildId,
            snapshot.ExtractionId,
            snapshot.CodeIndexId,
            scriptObject.MonoScript,
            cancellationToken);
        cache[target.Target.Value] = resolution;
        return resolution;
    }

    private async Task<IReadOnlyList<SceneReferenceRecord>> BuildReferencesAsync(
        SceneSnapshotRecord snapshot,
        IReadOnlyDictionary<ObjectKey, ParsedSceneObject> objects,
        PointerResolver pointers,
        IReadOnlyDictionary<string, string> containerIds,
        IReadOnlyDictionary<ObjectKey, string> gameObjectIds,
        IReadOnlyDictionary<ObjectKey, string> componentIds,
        IDictionary<ObjectKey, SceneCodeSymbolResolution> scriptResolutions,
        CancellationToken cancellationToken)
    {
        var records = new List<SceneReferenceRecord>();
        foreach (var source in objects.OrderBy(pair => pair.Key.ContainerPath, StringComparer.Ordinal).ThenBy(pair => pair.Key.LocalFileId))
        {
            for (var index = 0; index < source.Value.References.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reference = source.Value.References[index];
                var target = pointers.Resolve(source.Key.ContainerPath, reference.Target);
                string? targetGameObjectId = null;
                string? targetComponentId = null;
                string? targetSymbolId = null;
                var status = target.Target is null ? target.Status : SceneResolutionStatus.Resolved;
                if (target.Target is not null)
                {
                    gameObjectIds.TryGetValue(target.Target.Value, out targetGameObjectId);
                    componentIds.TryGetValue(target.Target.Value, out targetComponentId);
                    var targetObject = objects[target.Target.Value];
                    if (targetObject.Kind == ParsedSceneObjectKind.MonoScript)
                    {
                        if (targetObject.MonoScript is null)
                        {
                            status = SceneResolutionStatus.Unavailable;
                        }
                        else
                        {
                            if (!scriptResolutions.TryGetValue(target.Target.Value, out var scriptResolution))
                            {
                                scriptResolution = await _symbolResolver.ResolveAsync(
                                    snapshot.BuildId,
                                    snapshot.ExtractionId,
                                    snapshot.CodeIndexId,
                                    targetObject.MonoScript,
                                    cancellationToken);
                                scriptResolutions[target.Target.Value] = scriptResolution;
                            }
                            targetSymbolId = scriptResolution.SymbolId;
                            status = scriptResolution.Status;
                        }
                    }
                }

                records.Add(new SceneReferenceRecord(
                    HashId(snapshot.SceneSnapshotId, "reference", source.Key.ContainerPath, source.Key.LocalFileId.ToString(CultureInfo.InvariantCulture), index.ToString(CultureInfo.InvariantCulture), reference.FieldPath),
                    snapshot.SceneSnapshotId,
                    componentIds.GetValueOrDefault(source.Key),
                    EmptyToNull(reference.FieldPath),
                    EmptyToNull(reference.DeclaredType),
                    containerIds[source.Key.ContainerPath],
                    source.Key.LocalFileId,
                    target.Target is null ? null : containerIds[target.Target.Value.ContainerPath],
                    target.Target?.LocalFileId,
                    targetGameObjectId,
                    targetComponentId,
                    targetSymbolId,
                    target.Target is null ? target.TargetText : null,
                    status,
                    "SerializedFileTypeTreePPtr",
                    _recoveryClassifier.Classify(new SceneRecoveryFacts(
                        true,
                        true,
                        true,
                        !string.IsNullOrWhiteSpace(reference.FieldPath) && !string.IsNullOrWhiteSpace(reference.DeclaredType),
                        target.IsExplicitNull || status == SceneResolutionStatus.Resolved))));
            }
        }
        return records.OrderBy(record => record.ReferenceId, StringComparer.Ordinal).ToArray();
    }

    private SceneTransformRecord ToTransformRecord(
        TransformFact fact,
        IReadOnlyDictionary<ObjectKey, string> gameObjectIds)
    {
        var recovery = _recoveryClassifier.Classify(new SceneRecoveryFacts(
            true,
            true,
            true,
            fact.SchemaValid,
            fact.SchemaValid && fact.HierarchyComplete));
        if (!fact.SchemaValid)
        {
            return new SceneTransformRecord(
                gameObjectIds[fact.GameObject],
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                recovery);
        }
        return new SceneTransformRecord(
            gameObjectIds[fact.GameObject],
            fact.ParentGameObject is not null && gameObjectIds.TryGetValue(fact.ParentGameObject.Value, out var parentId) ? parentId : null,
            fact.SiblingIndex,
            fact.Data.LocalPosition.X,
            fact.Data.LocalPosition.Y,
            fact.Data.LocalPosition.Z,
            fact.Data.LocalRotation.X,
            fact.Data.LocalRotation.Y,
            fact.Data.LocalRotation.Z,
            fact.Data.LocalRotation.W,
            fact.Data.LocalScale.X,
            fact.Data.LocalScale.Y,
            fact.Data.LocalScale.Z,
            recovery);
    }

    private static IReadOnlyList<SceneDocumentRecord> FinalizeDocuments(
        IReadOnlyList<SceneDocumentRecord> documents,
        IReadOnlyList<SceneGameObjectRecord> gameObjects,
        IReadOnlyList<SceneTransformRecord> transforms)
    {
        var transformed = transforms.ToDictionary(transform => transform.GameObjectId, StringComparer.Ordinal);
        return documents
            .Select(document =>
            {
                var members = gameObjects.Where(item => string.Equals(item.SceneId, document.SceneId, StringComparison.Ordinal)).ToArray();
                var roots = members.Count(item => !transformed.TryGetValue(item.GameObjectId, out var transform) || transform.ParentGameObjectId is null);
                var recovery = AggregateRecovery(
                    new[] { document.RecoveryStatus }
                        .Concat(members
                            .Where(item => transformed.ContainsKey(item.GameObjectId))
                            .Select(item => transformed[item.GameObjectId].RecoveryStatus)));
                return document with
                {
                    ObjectCount = Math.Max(document.ObjectCount, members.Length),
                    RootCount = roots,
                    RecoveryStatus = recovery
                };
            })
            .OrderBy(document => document.SceneId, StringComparer.Ordinal)
            .ToArray();
    }

    private SceneRecoveryStatus ComponentRecovery(ParsedSceneObject item, bool transformSchemaValid) =>
        item.Kind switch
        {
            ParsedSceneObjectKind.Transform => _recoveryClassifier.Classify(new SceneRecoveryFacts(true, true, true, transformSchemaValid, transformSchemaValid)),
            ParsedSceneObjectKind.MonoBehaviour => _recoveryClassifier.Classify(new SceneRecoveryFacts(true, true, true, false, false)),
            _ => _recoveryClassifier.Classify(new SceneRecoveryFacts(true, true, true, false, false))
        };

    private static bool HasKnownSchemaAndBounds(
        ParsedSceneContainer container,
        ParsedSceneObject item,
        long verifiedContainerSize)
    {
        if (container.SerializedFileVersion != SupportedSerializedFileVersion ||
            !container.UnityVersion.StartsWith("2022.3.62", StringComparison.Ordinal) ||
            item.ByteOffset < 0 || item.ByteCount <= 0)
        {
            return false;
        }
        try
        {
            var end = checked(item.ByteOffset + item.ByteCount);
            return end <= verifiedContainerSize;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ReadBuildSettingsSceneNames(IEnumerable<ParsedSceneContainer> containers) =>
        containers
            .Where(container => string.Equals(Path.GetFileName(container.RelativePath), "globalgamemanagers", StringComparison.Ordinal))
            .SelectMany(container => container.Objects)
            .Where(item => item.Kind == ParsedSceneObjectKind.BuildSettings && item.BuildSettings is not null)
            .OrderBy(item => item.LocalFileId)
            .Select(item => item.BuildSettings!.ScenePaths)
            .FirstOrDefault() ?? [];

    private static string SceneNameFromPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        return name.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;
    }

    private static IEnumerable<ObjectKey> Descendants(
        ObjectKey root,
        IReadOnlyDictionary<ObjectKey, ObjectKey[]> children)
    {
        var queue = new Queue<ObjectKey>();
        var seen = new HashSet<ObjectKey>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
                continue;
            yield return current;
            if (!children.TryGetValue(current, out var descendants))
                continue;
            foreach (var child in descendants.OrderBy(item => item.ContainerPath, StringComparer.Ordinal).ThenBy(item => item.LocalFileId))
                queue.Enqueue(child);
        }
    }

    private static string ComponentKind(ParsedSceneObject item) => item.Kind switch
    {
        ParsedSceneObjectKind.Transform => "Transform",
        ParsedSceneObjectKind.MonoBehaviour => "MonoBehaviour",
        _ => "UnityClass:" + item.UnityClassId.ToString(CultureInfo.InvariantCulture)
    };

    private static string ContainerKind(string path) => IsLevelPath(path)
        ? "SerializedFileScene"
        : IsMetadataPath(path)
            ? "SerializedFileMetadata"
            : "SerializedFileAsset";

    private static bool IsLevelPath(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length == 6 && name.StartsWith("level", StringComparison.Ordinal) && name[5] is >= '0' and <= '2';
    }

    private static int LevelNumber(string path) => Path.GetFileName(path)[5] - '0';

    private static bool IsMetadataPath(string path) =>
        Path.GetFileName(path).StartsWith("globalgamemanagers", StringComparison.Ordinal);

    private static bool IsOrdinaryAssetPath(string path) => !IsLevelPath(path) && !IsMetadataPath(path);

    private static SceneCodeSymbolResolution UnavailableScript() =>
        new(null, null, null, null, null, null, SceneResolutionStatus.Unavailable);

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string HashId(params string[] parts)
    {
        var input = string.Join("\n", parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static Dictionary<string, T> UniqueByPath<T>(
        IReadOnlyList<T> values,
        Func<T, string> pathSelector,
        string parameterName)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var path = pathSelector(value);
            ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
            path = path.Replace('\\', '/');
            if (!result.TryAdd(path, value))
                throw new ArgumentException($"Duplicate scene container path '{path}'.", parameterName);
        }
        return result;
    }

    private static SceneRecoveryStatus AggregateRecovery(IEnumerable<SceneRecoveryStatus> statuses)
    {
        var materialized = statuses.ToArray();
        if (materialized.Length == 0)
            return SceneRecoveryStatus.Unknown;
        if (materialized.Contains(SceneRecoveryStatus.StubOrUnavailable))
            return SceneRecoveryStatus.StubOrUnavailable;
        if (materialized.Contains(SceneRecoveryStatus.PartiallyRecovered))
            return SceneRecoveryStatus.PartiallyRecovered;
        if (materialized.Contains(SceneRecoveryStatus.GraphOnly))
            return SceneRecoveryStatus.GraphOnly;
        if (materialized.Contains(SceneRecoveryStatus.Unknown))
            return SceneRecoveryStatus.Unknown;
        return SceneRecoveryStatus.FullyRecovered;
    }

    private readonly record struct ObjectKey(string ContainerPath, long LocalFileId);
    private sealed record PointerTarget(ObjectKey? Target, SceneResolutionStatus Status, string TargetText, bool IsExplicitNull = false);
    private sealed record TransformFact(
        ObjectKey Transform,
        ObjectKey GameObject,
        ObjectKey? ParentGameObject,
        int? SiblingIndex,
        ParsedTransformData Data,
        bool SchemaValid,
        bool HierarchyComplete);
    private sealed record PrefabRoot(ObjectKey Evidence, ObjectKey? Root);
    private sealed record DocumentAssignments(
        IReadOnlyList<SceneDocumentRecord> Documents,
        IReadOnlyDictionary<ObjectKey, string> GameObjectSceneIds);

    private sealed class PointerResolver
    {
        private readonly IReadOnlyDictionary<string, ParsedSceneContainer> _containers;
        private readonly IReadOnlyDictionary<ObjectKey, ParsedSceneObject> _objects;

        public PointerResolver(
            IReadOnlyDictionary<string, ParsedSceneContainer> containers,
            IReadOnlyDictionary<ObjectKey, ParsedSceneObject> objects)
        {
            _containers = containers;
            _objects = objects;
        }

        public PointerTarget Resolve(string sourceContainerPath, ParsedScenePPtr pointer)
        {
            if (pointer.LocalFileId == 0)
                return new PointerTarget(null, SceneResolutionStatus.Unavailable, $"fileId={pointer.FileId};localFileId=0", IsExplicitNull: true);
            if (pointer.LocalFileId < 0)
                throw new InvalidDataException("Negative PPtr local file IDs are invalid.");

            var targetPath = sourceContainerPath;
            string? externalText = null;
            if (pointer.FileId != 0)
            {
                var source = _containers[sourceContainerPath];
                var external = source.ExternalReferences.SingleOrDefault(item => item.FileId == pointer.FileId);
                if (external is null)
                    return new PointerTarget(null, SceneResolutionStatus.UnresolvedText, $"fileId={pointer.FileId};localFileId={pointer.LocalFileId};external=<missing-table-entry>");
                externalText = string.IsNullOrWhiteSpace(external.PathName) ? external.OriginalPathName : external.PathName;
                var matches = ResolveExternalPaths(external).ToArray();
                if (matches.Length != 1)
                    return new PointerTarget(null, SceneResolutionStatus.UnresolvedText, $"fileId={pointer.FileId};localFileId={pointer.LocalFileId};external={externalText}");
                targetPath = matches[0];
            }

            var key = new ObjectKey(targetPath, pointer.LocalFileId);
            if (!_objects.ContainsKey(key))
                throw new InvalidDataException($"PPtr target '{targetPath}' local file ID '{pointer.LocalFileId}' does not exist in the parsed container.");
            return new PointerTarget(key, SceneResolutionStatus.Resolved, externalText ?? string.Empty);
        }

        private IEnumerable<string> ResolveExternalPaths(ParsedSceneExternalReference external)
        {
            var candidates = new[] { external.PathName, external.OriginalPathName }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizeExternalPath)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var exact = _containers.Keys.Where(path => candidates.Contains(path, StringComparer.Ordinal)).ToArray();
            if (exact.Length > 0)
                return exact;
            var names = candidates.Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.Ordinal);
            return _containers.Keys.Where(path => names.Contains(Path.GetFileName(path)));
        }

        private static string NormalizeExternalPath(string path)
        {
            var normalized = path.Replace('\\', '/');
            var archiveSeparator = normalized.LastIndexOf("archive:/", StringComparison.OrdinalIgnoreCase);
            if (archiveSeparator >= 0)
                normalized = normalized[(archiveSeparator + "archive:/".Length)..];
            var cabSeparator = normalized.LastIndexOf("/CAB/", StringComparison.OrdinalIgnoreCase);
            if (cabSeparator >= 0)
                normalized = normalized[(cabSeparator + 5)..];
            return normalized.TrimStart('/');
        }
    }
}
