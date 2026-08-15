using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Scenes;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Scene;
using S1Atlas.Indexing.Scene;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests.Scene;

public sealed class SceneNormalizerTests : IAsyncDisposable
{
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-scene-normalizer-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public SceneNormalizerTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task Build_settings_scene_paths_name_level_documents()
    {
        var containers = new[]
        {
            Container("Schedule I_Data/level0"),
            Container("Schedule I_Data/level1"),
            Container("Schedule I_Data/level2"),
            Container(
                "Schedule I_Data/globalgamemanagers",
                objects:
                [Object(100, 141, ParsedSceneObjectKind.BuildSettings, buildSettings: new ParsedBuildSettingsData([
                    "Assets/Scenes/Tutorial.unity",
                    "Assets/Scenes/Main.unity",
                    "Assets/Scenes/Interior.unity"
                ]))])
        };

        var result = await NormalizeAsync(containers);

        Assert.Equal("Tutorial", DocumentFor(result, "level0").Name);
        Assert.Equal("Main", DocumentFor(result, "level1").Name);
        Assert.Equal("Interior", DocumentFor(result, "level2").Name);
    }

    [Fact]
    public async Task Missing_build_settings_uses_raw_level_name_fallback()
    {
        var result = await NormalizeAsync([Container("Schedule I_Data/level0")]);

        var document = Assert.Single(result.Documents);
        Assert.Equal("level0", document.Name);
        Assert.Equal(SceneRecoveryStatus.PartiallyRecovered, document.RecoveryStatus);
    }

    [Fact]
    public async Task Marker_text_without_prefab_class_relationship_does_not_create_prefab()
    {
        var asset = Container(
            "Schedule I_Data/sharedassets0.assets",
            objects: [GameObject(1, "Prefab PrefabInstance marker only")]);

        var result = await NormalizeAsync([asset]);

        Assert.DoesNotContain(result.Documents, document => document.Kind == SceneDocumentKind.Prefab);
        Assert.Single(result.GameObjects);
    }

    [Fact]
    public async Task Prefab_class_id_with_root_relationship_creates_proven_prefab_document()
    {
        var evidenceReference = new ParsedSceneReference("m_RootGameObject", "PPtr<GameObject>", new ParsedScenePPtr(0, 1));
        var asset = Container(
            "Schedule I_Data/sharedassets0.assets",
            objects:
            [
                GameObject(1, "Dealer Prefab"),
                Object(9, 1001, ParsedSceneObjectKind.PrefabEvidence, references: [evidenceReference])
            ],
            hasPrefabEvidence: true);

        var result = await NormalizeAsync([asset]);

        var prefab = Assert.Single(result.Documents, document => document.Kind == SceneDocumentKind.Prefab);
        Assert.Equal("Dealer Prefab", prefab.Name);
        Assert.Equal(9, prefab.SourceLocalFileId);
        Assert.Equal(prefab.SceneId, Assert.Single(result.GameObjects).SceneId);
    }

    [Fact]
    public async Task Prefab_class_id_alone_creates_evidence_row_without_claiming_asset_roots()
    {
        var asset = Container(
            "Schedule I_Data/sharedassets0.assets",
            objects:
            [
                GameObject(1, "Ordinary Asset Root"),
                Object(9, 1001480554, ParsedSceneObjectKind.PrefabEvidence)
            ],
            hasPrefabEvidence: true);

        var result = await NormalizeAsync([asset]);

        var prefab = Assert.Single(result.Documents, document => document.Kind == SceneDocumentKind.Prefab);
        Assert.Equal(9, prefab.SourceLocalFileId);
        Assert.Equal(0, prefab.ObjectCount);
        var ordinary = Assert.Single(result.Documents, document => document.Kind == SceneDocumentKind.Scene);
        Assert.Equal(ordinary.SceneId, Assert.Single(result.GameObjects).SceneId);
    }

    [Fact]
    public async Task Parent_cycles_are_rejected_as_invalid_facts()
    {
        var firstTransform = Transform(11, 1, parentTransformId: 12);
        var secondTransform = Transform(12, 2, parentTransformId: 11);
        var level = Container(
            "Schedule I_Data/level0",
            objects:
            [
                GameObject(1, "First", [new ParsedScenePPtr(0, 11)]),
                GameObject(2, "Second", [new ParsedScenePPtr(0, 12)]),
                firstTransform,
                secondTransform
            ]);

        await Assert.ThrowsAsync<InvalidDataException>(() => NormalizeAsync([level]));
    }

    [Fact]
    public async Task External_pptrs_resolve_by_external_path_not_container_ordinal()
    {
        var componentReference = new ParsedSceneReference("m_Component.Array[0].component", "PPtr<Component>", new ParsedScenePPtr(7, 50));
        var level = Container(
            "Schedule I_Data/level0",
            objects: [GameObject(1, "Root", [new ParsedScenePPtr(7, 50)], [componentReference])],
            externals: [new ParsedSceneExternalReference(7, "sharedassets2.assets", "archive:/CAB/sharedassets2.assets")]);
        var unrelatedOrdinalContainer = Container("Schedule I_Data/sharedassets0.assets");
        var actualTargetContainer = Container(
            "Schedule I_Data/sharedassets2.assets",
            objects: [Object(50, 23, ParsedSceneObjectKind.Other)]);

        var result = await NormalizeAsync([level, unrelatedOrdinalContainer, actualTargetContainer]);

        var component = Assert.Single(result.Components);
        Assert.Equal(50, component.LocalFileId);
        var targetContainer = Assert.Single(result.Containers, container => container.ContainerId == component.ContainerId);
        Assert.EndsWith("sharedassets2.assets", targetContainer.RelativePath, StringComparison.Ordinal);
        var reference = Assert.Single(result.References, item => item.FieldPath == componentReference.FieldPath);
        Assert.Equal(SceneResolutionStatus.Resolved, reference.ResolutionStatus);
        Assert.Equal(component.ComponentId, reference.TargetComponentId);
    }

    [Fact]
    public async Task Missing_external_targets_are_preserved_as_unresolved_text()
    {
        var externalReference = new ParsedSceneReference("m_Target", "PPtr<GameObject>", new ParsedScenePPtr(4, 99));
        var level = Container(
            "Schedule I_Data/level0",
            objects: [Object(40, 23, ParsedSceneObjectKind.Other, references: [externalReference])],
            externals: [new ParsedSceneExternalReference(4, "missing.assets", "archive:/CAB/missing.assets")]);

        var result = await NormalizeAsync([level]);

        var reference = Assert.Single(result.References);
        Assert.Equal(SceneResolutionStatus.UnresolvedText, reference.ResolutionStatus);
        Assert.Null(reference.TargetContainerId);
        Assert.Null(reference.TargetLocalFileId);
        Assert.Contains("fileId=4", reference.TargetText, StringComparison.Ordinal);
        Assert.Contains("localFileId=99", reference.TargetText, StringComparison.Ordinal);
        Assert.Contains("missing.assets", reference.TargetText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nonexistent_target_in_a_parsed_container_is_rejected()
    {
        var missingLocal = new ParsedSceneReference("m_Target", "PPtr<GameObject>", new ParsedScenePPtr(0, 99));
        var level = Container("Schedule I_Data/level0", objects: [Object(40, 23, ParsedSceneObjectKind.Other, references: [missingLocal])]);

        await Assert.ThrowsAsync<InvalidDataException>(() => NormalizeAsync([level]));
    }

    [Fact]
    public async Task Transform_values_are_only_captured_for_the_known_schema_and_valid_bounds()
    {
        var valid = Container(
            "Schedule I_Data/level0",
            objects: [GameObject(1, "Valid", [new ParsedScenePPtr(0, 11)]), Transform(11, 1)]);
        var invalid = Container(
            "Schedule I_Data/level1",
            serializedFileVersion: 21,
            objects: [GameObject(2, "Unknown schema", [new ParsedScenePPtr(0, 12)]), Transform(12, 2)]);

        var result = await NormalizeAsync([valid, invalid]);

        var validTransform = result.Transforms.Single(item => result.GameObjects.Single(gameObject => gameObject.GameObjectId == item.GameObjectId).Name == "Valid");
        var invalidTransform = result.Transforms.Single(item => result.GameObjects.Single(gameObject => gameObject.GameObjectId == item.GameObjectId).Name == "Unknown schema");
        Assert.Equal(1, validTransform.PositionX);
        Assert.Null(invalidTransform.PositionX);
        Assert.Equal(SceneRecoveryStatus.GraphOnly, invalidTransform.RecoveryStatus);
    }

    [Fact]
    public async Task Invalid_transform_schema_cannot_establish_component_ownership()
    {
        var invalid = Container(
            "Schedule I_Data/level0",
            serializedFileVersion: 21,
            objects: [GameObject(1, "Root"), Transform(11, 1)]);

        var result = await NormalizeAsync([invalid]);

        Assert.Empty(result.Transforms);
        Assert.Empty(result.Components);
    }

    [Fact]
    public async Task Transform_values_outside_verified_container_bounds_are_not_captured()
    {
        var outOfBoundsTransform = Transform(11, 1, byteOffset: 1000, byteCount: 64);
        var level = Container(
            "Schedule I_Data/level0",
            objects: [GameObject(1, "Root", [new ParsedScenePPtr(0, 11)]), outOfBoundsTransform]);

        var result = await NormalizeAsync([level]);

        var transform = Assert.Single(result.Transforms);
        Assert.Null(transform.PositionX);
        Assert.Equal(SceneRecoveryStatus.GraphOnly, transform.RecoveryStatus);
    }

    [Fact]
    public async Task Missing_external_transform_parent_is_partial_and_preserved_as_unresolved()
    {
        var parentReference = new ParsedSceneReference("m_Father", "PPtr<Transform>", new ParsedScenePPtr(4, 99));
        var transform = Transform(11, 1, parentTransformId: 99, parentFileId: 4, references: [parentReference]);
        var level = Container(
            "Schedule I_Data/level0",
            objects: [GameObject(1, "Root", [new ParsedScenePPtr(0, 11)]), transform],
            externals: [new ParsedSceneExternalReference(4, "missing.assets", "archive:/CAB/missing.assets")]);

        var result = await NormalizeAsync([level]);

        var normalized = Assert.Single(result.Transforms);
        Assert.Null(normalized.ParentGameObjectId);
        Assert.Equal(SceneRecoveryStatus.PartiallyRecovered, normalized.RecoveryStatus);
        var reference = Assert.Single(result.References, item => item.FieldPath == "m_Father");
        Assert.Equal(SceneResolutionStatus.UnresolvedText, reference.ResolutionStatus);
        Assert.Contains("missing.assets", reference.TargetText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_pptr_is_a_fully_recovered_explicit_absence()
    {
        var nullReference = new ParsedSceneReference("m_Optional", "PPtr<GameObject>", new ParsedScenePPtr(0, 0));
        var level = Container("Schedule I_Data/level0", objects: [Object(40, 23, ParsedSceneObjectKind.Other, references: [nullReference])]);

        var result = await NormalizeAsync([level]);

        var reference = Assert.Single(result.References);
        Assert.Equal(SceneResolutionStatus.Unavailable, reference.ResolutionStatus);
        Assert.Equal(SceneRecoveryStatus.FullyRecovered, reference.RecoveryStatus);
        Assert.Equal("fileId=0;localFileId=0", reference.TargetText);
    }

    [Fact]
    public async Task Exact_monoscript_resolution_propagates_only_the_exact_symbol_to_component_and_reference()
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        const string codeSnapshotId = "code-a";
        const string canonicalName = "ScheduleOne.PlayerController";
        var canonicalKey = SymbolIdentity.Create(CodebaseKind.ScheduleI, CodeChannel.Installed, SymbolKind.Type, canonicalName).CanonicalKey;
        var symbol = new IndexSymbolRecord(Hash(canonicalKey), codeSnapshotId, canonicalKey, "Type", canonicalName, canonicalName, false);
        await _repository.CreateCodeSnapshotAsync(new CodeSnapshotRecord(codeSnapshotId, CodebaseKind.ScheduleI, CodeChannel.Installed, "extraction-a", "2026-08-15T00:00:00Z"), TestContext.Current.CancellationToken);
        await _repository.StartIndexRunAsync(new IndexRunRecord("index-a", codeSnapshotId, IndexRunStatus.Running, "2026-08-15T00:00:00Z"), TestContext.Current.CancellationToken);
        await _repository.CompleteIndexRunAsync("index-a", new IndexWriteSet([symbol], [], [], [], []), "2026-08-15T00:01:00Z", TestContext.Current.CancellationToken);

        var scriptReference = new ParsedSceneReference("m_Script", "PPtr<MonoScript>", new ParsedScenePPtr(0, 20));
        var level = Container(
            "Schedule I_Data/level0",
            objects:
            [
                GameObject(1, "Player", [new ParsedScenePPtr(0, 10)]),
                Object(10, 114, ParsedSceneObjectKind.MonoBehaviour, references: [scriptReference], monoBehaviour: new ParsedMonoBehaviourData(new ParsedScenePPtr(0, 1), new ParsedScenePPtr(0, 20), true)),
                Object(20, 115, ParsedSceneObjectKind.MonoScript, monoScript: new ParsedMonoScriptData("Assembly-CSharp.dll", "ScheduleOne", "PlayerController"))
            ]);
        var resolver = new SceneCodeSymbolResolver(
            _repository,
            (extractionId, _) => Task.FromResult<SceneCodeBuildAuthority?>(new(extractionId, "build-a")));

        var result = await NormalizeAsync([level], resolver);

        var component = Assert.Single(result.Components);
        Assert.Equal(SceneResolutionStatus.Resolved, component.TypeResolutionStatus);
        Assert.Equal(symbol.SymbolId, component.ResolvedTypeSymbolId);
        Assert.Equal("index-a", component.ResolvedCodeIndexId);
        var reference = Assert.Single(result.References, item => item.FieldPath == "m_Script");
        Assert.Equal(symbol.SymbolId, reference.TargetSymbolId);
        Assert.Equal(SceneResolutionStatus.Resolved, reference.ResolutionStatus);
    }

    private async Task<SceneWriteSet> NormalizeAsync(
        IReadOnlyList<ParsedSceneContainer> parsed,
        SceneCodeSymbolResolver? resolver = null)
    {
        await _repository.InitializeAsync(TestContext.Current.CancellationToken);
        var normalizer = new SceneNormalizer(resolver ?? new SceneCodeSymbolResolver(_repository), new SceneRecoveryClassifier());
        var verified = parsed.Select(container => new VerifiedSceneContainer(
            container.RelativePath,
            container.PrimaryPath,
            container.SidecarPaths,
            container.Sha256,
            1024,
            container.UnityVersion,
            container.SerializedFileVersion,
            "[]")).ToArray();
        return await normalizer.NormalizeAsync(Snapshot(), verified, parsed, TestContext.Current.CancellationToken);
    }

    private static SceneSnapshotRecord Snapshot() =>
        new(
            "snapshot-a",
            "build-a",
            "extraction-a",
            "input-a",
            "code-a",
            "index-a",
            "assetstools-net",
            "3.0.5",
            Digest,
            SceneSnapshotStatus.Running,
            SceneRecoveryStatus.Unknown,
            "2026-08-15T00:00:00Z");

    private static ParsedSceneContainer Container(
        string relativePath,
        IReadOnlyList<ParsedSceneObject>? objects = null,
        IReadOnlyList<ParsedSceneExternalReference>? externals = null,
        bool hasPrefabEvidence = false,
        int serializedFileVersion = 22,
        string unityVersion = "2022.3.62f1") =>
        new(
            relativePath,
            "C:/game/" + relativePath.Replace('/', '\\'),
            [],
            Digest,
            unityVersion,
            serializedFileVersion,
            objects ?? [],
            externals ?? [],
            hasPrefabEvidence);

    private static ParsedSceneObject GameObject(
        long localFileId,
        string name,
        IReadOnlyList<ParsedScenePPtr>? components = null,
        IReadOnlyList<ParsedSceneReference>? references = null) =>
        Object(
            localFileId,
            1,
            ParsedSceneObjectKind.GameObject,
            references,
            gameObject: new ParsedGameObjectData(name, 0, 0, true, components ?? []));

    private static ParsedSceneObject Transform(
        long localFileId,
        long gameObjectId,
        long parentTransformId = 0,
        int parentFileId = 0,
        IReadOnlyList<ParsedSceneReference>? references = null,
        long byteOffset = 128,
        long byteCount = 64) =>
        Object(
            localFileId,
            4,
            ParsedSceneObjectKind.Transform,
            references,
            transform: new ParsedTransformData(
                new ParsedScenePPtr(0, gameObjectId),
                new ParsedScenePPtr(parentFileId, parentTransformId),
                [],
                new ParsedSceneVector3(1, 2, 3),
                new ParsedSceneQuaternion(0, 0, 0, 1),
                new ParsedSceneVector3(1, 1, 1),
                0),
            byteOffset: byteOffset,
            byteCount: byteCount);

    private static ParsedSceneObject Object(
        long localFileId,
        int classId,
        ParsedSceneObjectKind kind,
        IReadOnlyList<ParsedSceneReference>? references = null,
        ParsedGameObjectData? gameObject = null,
        ParsedTransformData? transform = null,
        ParsedMonoBehaviourData? monoBehaviour = null,
        ParsedMonoScriptData? monoScript = null,
        ParsedBuildSettingsData? buildSettings = null,
        long byteOffset = 128,
        long byteCount = 64) =>
        new(
            localFileId,
            classId,
            byteOffset,
            byteCount,
            kind,
            references ?? [],
            gameObject,
            transform,
            monoBehaviour,
            monoScript,
            buildSettings);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static SceneDocumentRecord DocumentFor(SceneWriteSet writeSet, string fileName)
    {
        var container = writeSet.Containers.Single(item => Path.GetFileName(item.RelativePath) == fileName);
        return writeSet.Documents.Single(item => item.ContainerId == container.ContainerId && item.Kind == SceneDocumentKind.Scene);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
