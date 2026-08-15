# Scene Intelligence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the approved S1Atlas Scene Intelligence milestone as an offline, build-scoped index of Schedule I SerializedFile scene/object graphs linked exactly to the existing Schedule I code index.

**Architecture:** S1Atlas.Core owns scene facts, recovery/status enums, query contracts, and repository interfaces. S1Atlas.Extraction owns the verified-input reader and an adapter around the pinned AssetsTools.NET parser; third-party types stop at that adapter. S1Atlas.Indexing owns scene normalization, exact symbol linking, atomic indexing orchestration, and shared queries; S1Atlas.Storage owns migration 8 and SQLite persistence; S1Atlas.Cli owns command registration and formatting.

**Tech Stack:** C# / .NET 8, Microsoft.Data.Sqlite 8.0.29, System.CommandLine 2.0.10, xUnit v3, AssetsTools.NET 3.0.5 (MIT), existing Cpp2IL/ILSpy authority and input-verification services.

## Global Constraints

- Parse only the verified Schedule I `level0`, `level1`, `level2`, `sharedassets0.assets`, `sharedassets1.assets`, `sharedassets2.assets`, `resources.assets`, `globalgamemanagers`, and `globalgamemanagers.assets` containers plus matching `.resS`/`.resource` sidecars.
- The parser targets Unity `2022.3.62`; it must discover external-file references and must not assume `levelN` and `sharedassetsN` ordinals correspond.
- The game installation is read-only input; never run the game or Unity, never load/execute game or serialized asset code, and never use runtime probing.
- `SceneRecovery` is categorical availability (`FullyRecovered`, `PartiallyRecovered`, `GraphOnly`, `StubOrUnavailable`, `Unknown`), never a numerical confidence score.
- General custom MonoBehaviour field values and a generated IL2CPP type database are not v1 goals; no general `serialized_fields` table is added.
- Prefab rows are emitted only from verified serialized class-ID/object-relationship evidence; marker strings and ordinary asset-file GameObject roots are not prefab evidence.
- A resolved component type must be an exact `SymbolIdentity`/`symbols.canonical_key` match in the same build's completed Schedule I `Installed` code index.
- Scene snapshots consume a validated extraction, a replay-verified extraction input snapshot ID, and a scene-specific before/after hash manifest; a failed or cancelled run cannot become queryable.
- Third-party parser types may exist only inside the S1Atlas.Extraction adapter; Core, Storage, Indexing, and CLI use S1Atlas-owned records.
- Normal scene indexing is offline; package acquisition is an explicit setup action and the pinned parser/license facts are recorded before use.
- Scene-generated files remain under the local Atlas data root and never enter Git or CI artifacts; hygiene tests reject their basenames and path segments.
- Large lists are counted and bounded in SQLite-backed queries with a default limit of 50; human and JSON output expose exact IDs, hashes, evidence, and recovery state beside readable names.

---

## File Map

| Area | Files | Responsibility |
|---|---|---|
| Core scene domain | `src/S1Atlas.Core/Scenes/*.cs` | Recovery, document, object, component, transform, reference, snapshot, and query records |
| Core persistence contract | `src/S1Atlas.Core/Storage/ISceneRepository.cs` | Scene snapshot lifecycle, write set, and bounded query interfaces |
| Extraction adapter | `src/S1Atlas.Extraction/Scene/*.cs` | Verified container inputs and AssetsTools.NET isolation |
| Scene workflow/query | `src/S1Atlas.Indexing/Scene/*.cs` | Normalization, exact code linking, atomic workflow, query resolution |
| Paths | `src/S1Atlas.Indexing/Paths/OwnedScenePaths.cs` | Contained staging/final scene-index paths |
| SQLite | `src/S1Atlas.Storage/Migrations/SqliteMigrations.cs`, `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Scene.cs` | Migration 8 and scene persistence/query SQL |
| CLI | `src/S1Atlas.Cli/Commands/Scene*.cs`, `src/S1Atlas.Cli/Output/SceneOutputModels.cs`, `src/S1Atlas.Cli/CliApplication.cs` | Indexing entry point, list/detail commands, human/JSON rendering |
| Hygiene | `scripts/verify-repository-hygiene.ps1`, `tests/S1Atlas.IntegrationTests/Repository/RepositoryHygieneScriptTests.cs` | Reject scene-generated/proprietary paths |
| Tests | `tests/S1Atlas.Core.Tests/Scenes`, `tests/S1Atlas.Extraction.Tests/Scene`, `tests/S1Atlas.Indexing.Tests/Scene`, `tests/S1Atlas.Storage.Tests/Scene`, `tests/S1Atlas.IntegrationTests/Scene` | Offline fixtures, persistence, workflow, CLI, and smoke coverage |
| Dependency record | `docs/dependencies/assetstools-net-3.0.5.md` | Pinned package, source, license, package hash, and transitive-license audit |

## Task 1: Add S1Atlas-Owned Scene Contracts

**Files:**

- Create: `src/S1Atlas.Core/Scenes/SceneRecoveryStatus.cs`
- Create: `src/S1Atlas.Core/Scenes/SceneModels.cs`
- Create: `src/S1Atlas.Core/Scenes/SceneQueryModels.cs`
- Create: `src/S1Atlas.Core/Storage/ISceneRepository.cs`
- Create: `tests/S1Atlas.Core.Tests/Scenes/SceneModelTests.cs`

**Interfaces:**

~~~csharp
public enum SceneRecoveryStatus
{
    FullyRecovered,
    PartiallyRecovered,
    GraphOnly,
    StubOrUnavailable,
    Unknown
}

public enum SceneDocumentKind { Scene, Prefab }
public enum SceneSnapshotStatus { Running, Completed, Failed }
public enum SceneResolutionStatus { Resolved, UnresolvedText, Ambiguous, NotIndexed, Unavailable }

public sealed record SceneSnapshotRecord(
    string SceneSnapshotId,
    string BuildId,
    string ExtractionId,
    string InputSnapshotId,
    string CodeSnapshotId,
    string CodeIndexId,
    string ParserId,
    string ParserVersion,
    string ContainerManifestDigest,
    SceneSnapshotStatus Status,
    SceneRecoveryStatus RecoveryStatus,
    string StartedAtUtc,
    string? CompletedAtUtc = null,
    string? FailureCode = null,
    string? FailureMessage = null);

public sealed record SceneContainerRecord(
    string ContainerId,
    string SceneSnapshotId,
    string RelativePath,
    string ContainerKind,
    string UnityVersion,
    int SerializedFileVersion,
    long ByteCount,
    string Sha256,
    string SidecarManifest);

public sealed record SceneDocumentRecord(
    string SceneId,
    string SceneSnapshotId,
    string ContainerId,
    SceneDocumentKind Kind,
    string Name,
    long? SourceLocalFileId,
    int ObjectCount,
    int RootCount,
    SceneRecoveryStatus RecoveryStatus);

public sealed record SceneGameObjectRecord(
    string GameObjectId,
    string SceneId,
    string ContainerId,
    long LocalFileId,
    string Name,
    bool? Active,
    int? Layer,
    string? Tag,
    SceneRecoveryStatus RecoveryStatus);

public sealed record SceneTransformRecord(
    string GameObjectId,
    string? ParentGameObjectId,
    int? SiblingIndex,
    float? PositionX,
    float? PositionY,
    float? PositionZ,
    float? RotationX,
    float? RotationY,
    float? RotationZ,
    float? RotationW,
    float? ScaleX,
    float? ScaleY,
    float? ScaleZ,
    SceneRecoveryStatus RecoveryStatus);

public sealed record SceneComponentRecord(
    string ComponentId,
    string GameObjectId,
    string ContainerId,
    long LocalFileId,
    int UnityClassId,
    string Kind,
    string? ScriptAssembly,
    string? ScriptNamespace,
    string? ScriptClass,
    string? ResolvedTypeSymbolId,
    string? ResolvedCodeIndexId,
    SceneResolutionStatus TypeResolutionStatus,
    SceneRecoveryStatus RecoveryStatus);

public sealed record SceneReferenceRecord(
    string ReferenceId,
    string SceneSnapshotId,
    string? SourceComponentId,
    string? FieldPath,
    string? DeclaredType,
    string SourceContainerId,
    long SourceLocalFileId,
    string? TargetContainerId,
    long? TargetLocalFileId,
    string? TargetGameObjectId,
    string? TargetComponentId,
    string? TargetSymbolId,
    string? TargetText,
    SceneResolutionStatus ResolutionStatus,
    string Evidence,
    SceneRecoveryStatus RecoveryStatus);

public sealed record SceneWriteSet(
    SceneSnapshotRecord Snapshot,
    IReadOnlyList<SceneContainerRecord> Containers,
    IReadOnlyList<SceneDocumentRecord> Documents,
    IReadOnlyList<SceneGameObjectRecord> GameObjects,
    IReadOnlyList<SceneTransformRecord> Transforms,
    IReadOnlyList<SceneComponentRecord> Components,
    IReadOnlyList<SceneReferenceRecord> References);
~~~

The repository contract must expose lifecycle methods `CreateSceneSnapshotAsync`, `StartSceneSnapshotAsync`, `CompleteSceneSnapshotAsync`, `FailSceneSnapshotAsync`, `GetCompletedSceneSnapshotAsync`, and `GetLatestCompletedSceneSnapshotAsync`, plus bounded methods `ListScenesAsync`, `GetSceneAsync`, `ListGameObjectsAsync`, `GetGameObjectAsync`, `ListComponentsAsync`, `GetComponentAsync`, and `ListReferencesAsync`. Each list method accepts a snapshot/document/parent filter, a query or exact selector where applicable, `limit`, and cancellation; each returns `TotalCount`, `ReturnedCount`, and rows.

- [ ] Write failing model tests for every enum value, nullability rule, and exact-ID preservation.
- [ ] Run `dotnet test tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~SceneModelTests`; verify the new tests fail because the contracts do not exist.
- [ ] Add the records/enums with constructor validation for nonblank IDs, lower-case 64-character hashes, positive local-file IDs where required, and positive limits in query options.
- [ ] Run the focused Core tests and the full `dotnet test S1Atlas.sln --configuration Release --no-restore`; verify they pass.
- [ ] Commit with `git add src/S1Atlas.Core/Scenes src/S1Atlas.Core/Storage/ISceneRepository.cs tests/S1Atlas.Core.Tests/Scenes && git commit -m "feat: add scene intelligence domain contracts"`.

## Task 2: Add Migration 8 and Transactional SQLite Persistence

**Files:**

- Modify: `src/S1Atlas.Storage/Migrations/SqliteMigrations.cs`
- Create: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Scene.cs`
- Modify: `tests/S1Atlas.Storage.Tests/Migrations/IndexingMigrationTests.cs`
- Create: `tests/S1Atlas.Storage.Tests/Scene/SqliteSceneRepositoryTests.cs`
- Create: `tests/S1Atlas.Storage.Tests/Scene/SceneSchemaTests.cs`

Migration 8 must be named `scene-intelligence-v8` and create only these tables: `scene_snapshots`, `scene_containers`, `scenes`, `game_objects`, `transforms`, `components`, and `serialized_refs`. Use foreign keys to `builds`, `validated_extractions`, `input_snapshots`, `code_snapshots`, `index_runs`, and the scene tables; use `CHECK` constraints for snapshot status, recovery status, document kind, resolution status, boolean fields, and nonnegative counts. Keep `local_file_id` as SQLite `INTEGER`/signed 64-bit and make `(scene_snapshot_id, container_id, local_file_id)` unique for parsed object identities. Do not add a blob column or a `serialized_fields` table.

Required indexes are:

~~~sql
scene_snapshots(build_id, status, completed_at_utc)
scene_containers(scene_snapshot_id, relative_path)
scenes(scene_snapshot_id, kind, name)
game_objects(scene_id, name)
game_objects(scene_snapshot_id, name)
game_objects(scene_snapshot_id, container_id, local_file_id) UNIQUE
transforms(parent_game_object_id)
components(game_object_id, kind)
components(resolved_type_symbol_id)
serialized_refs(source_component_id, field_path)
serialized_refs(target_game_object_id)
serialized_refs(target_symbol_id)
~~~

`CompleteSceneSnapshotAsync` must open one SQLite transaction, verify that every write-set row belongs to the running snapshot, insert parents before children, update status to `Completed`, and commit. Any validation or insert error rolls back every scene row. `FailSceneSnapshotAsync` records the failure without leaving partial rows queryable. Cross-table same-build checks that SQLite cannot express must compare the snapshot's build ID, validated extraction build ID, input snapshot build ID, environment/code snapshot build ID, and code index snapshot before the transaction commits.

- [ ] Extend `IndexingMigrationTests` to expect migration version 8, all seven tables, all required indexes, and the status/type constraints; run it and verify failure against current migration 7.
- [ ] Add migration 8 SQL and register `new(8, "scene-intelligence-v8", SceneIntelligenceV8Sql)` after migration 7.
- [ ] Add repository tests for parent/child insertion, duplicate local-file identity rejection, foreign-key rejection, same-build rejection, failed transaction rollback, failed snapshot invisibility, and successful completion counts.
- [ ] Implement the partial repository with prepared commands, parameterized SQL, sorted deterministic reads, and `LIMIT` applied in SQLite after an exact `COUNT(*)` query.
- [ ] Run the focused Storage tests and the full solution suite; verify migration checksum/idempotence and all repository tests pass.
- [ ] Commit with `git add src/S1Atlas.Storage/Migrations/SqliteMigrations.cs src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Scene.cs tests/S1Atlas.Storage.Tests && git commit -m "feat: persist scene intelligence in migration 8"`.

## Task 3: Pin AssetsTools.NET Behind the Extraction Adapter

**Files:**

- Modify: `src/S1Atlas.Extraction/S1Atlas.Extraction.csproj`
- Create: `src/S1Atlas.Extraction/Scene/IUnitySerializedFileParser.cs`
- Create: `src/S1Atlas.Extraction/Scene/AssetsToolsUnitySerializedFileParser.cs`
- Create: `src/S1Atlas.Extraction/Scene/SceneInputVerifier.cs`
- Create: `src/S1Atlas.Extraction/Scene/SceneParserModels.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Scene/SceneInputVerifierTests.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Scene/AssetsToolsUnitySerializedFileParserTests.cs`
- Create: `docs/dependencies/assetstools-net-3.0.5.md`

Pin `<PackageReference Include="AssetsTools.NET" Version="3.0.5" />` in Extraction only. Record the official NuGet page, source repository, MIT license, exact restored `.nupkg` SHA-256, transitive package/license inventory, and the reason the package is acceptable for local and planned V1 distribution in `docs/dependencies/assetstools-net-3.0.5.md`. Do not add AssetsTools.NET to Core, Storage, Indexing, or CLI.

The S1Atlas-owned adapter must expose only S1Atlas models:

~~~csharp
public sealed record VerifiedSceneContainer(
    string RelativePath,
    string PrimaryPath,
    IReadOnlyList<string> SidecarPaths,
    string Sha256,
    long ByteCount,
    string UnityVersion,
    int SerializedFileVersion,
    string SidecarManifest);

public interface IUnitySerializedFileParser
{
    Task<IReadOnlyList<ParsedSceneContainer>> ParseAsync(
        IReadOnlyList<VerifiedSceneContainer> containers,
        CancellationToken cancellationToken);
}
~~~

`AssetsToolsUnitySerializedFileParser` may use AssetsTools.NET types internally, but it returns no third-party objects. It reads only serialized-file object tables and supported built-in/reference data; it does not call bundle extraction, texture/mesh/audio/shader APIs, Unity APIs, or managed assembly loading. It must inspect class IDs for `GameObject`, `Transform`, `MonoBehaviour`, `MonoScript`, and prefab evidence; a UTF-8/ASCII string marker is never used as classification evidence.

`SceneInputVerifier` must reuse the existing `PathSafety` and `IFileHasher` semantics: resolve relative paths under the discovered install root, reject reparse points and traversal, hash before parsing, verify size/timestamp stability, and hash again after parsing. It returns the exact verified paths and manifest digest to the workflow; it never writes the game install. The scene snapshot's `input_snapshot_id` comes from the replay-verified extraction attempt, while `scene_containers` stores the scene-specific verified container manifest.

- [ ] Add the package reference and restore; compute the package hash with `Get-FileHash` against the restored `AssetsTools.NET.3.0.5.nupkg` and record it with license/provenance facts.
- [ ] Write failing verifier tests for missing files, traversal, reparse points, changed bytes, sidecar mismatch, and stable hashes.
- [ ] Implement `SceneInputVerifier` using the existing path/hash policy and add tests until the verifier suite passes.
- [ ] Write failing parser adapter tests using a sanitized Unity 2022.3 SerializedFile fixture for header/version/object-table reads, external references, and no-prefab-class-ID behavior.
- [ ] Implement the adapter mapping into S1Atlas-owned `ParsedSceneContainer`/object records; assert that AssetsTools.NET namespaces do not appear outside this file and its test seam.
- [ ] Run focused Extraction tests and the full solution suite; verify no game/Unity process or network access is involved.
- [ ] Commit with `git add src/S1Atlas.Extraction/S1Atlas.Extraction.csproj src/S1Atlas.Extraction/Scene tests/S1Atlas.Extraction.Tests/Scene docs/dependencies/assetstools-net-3.0.5.md && git commit -m "feat: isolate the Unity serialized-file parser"`.

## Task 4: Normalize the Object Graph and Resolve Code Symbols Exactly

**Files:**

- Create: `src/S1Atlas.Indexing/Scene/SceneSnapshotIdentity.cs`
- Create: `src/S1Atlas.Indexing/Scene/SceneRecoveryClassifier.cs`
- Create: `src/S1Atlas.Indexing/Scene/SceneCodeSymbolResolver.cs`
- Create: `src/S1Atlas.Indexing/Scene/SceneNormalizer.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Scene/SceneSnapshotIdentityTests.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Scene/SceneRecoveryClassifierTests.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Scene/SceneCodeSymbolResolverTests.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Scene/SceneNormalizerTests.cs`

`SceneSnapshotIdentity.Create` must hash, in canonical order, the build ID, validated extraction ID, input manifest digest, code index ID, parser ID/version, serialized-file schema version, and sorted container `(relativePath, size, sha256, sidecarManifest)` facts into a lower-case 64-character scene snapshot ID. A non-forced rerun with the same facts reuses the completed snapshot; `--force` changes the parser settings/version input and produces a new candidate.

`SceneCodeSymbolResolver` must implement this exact flow:

~~~csharp
var typeIdentity = SymbolIdentity.Create(
    CodebaseKind.ScheduleI,
    CodeChannel.Installed,
    SymbolKind.Type,
    normalizedQualifiedName);
var symbol = await indexRepository.GetCompletedSymbolByCanonicalKeyAsync(
    codeIndexId,
    typeIdentity.CanonicalKey,
    cancellationToken);
~~~

If assembly identity conflicts, namespace/class text is missing, the code index is not completed, the exact key is absent, or more than one exact candidate exists, persist raw script text and `SceneResolutionStatus` rather than fuzzy matching. The resolver must verify that the selected code index resolves to the same `build_id`; it must not resolve against S1API, S1MAPI, Release, Preview, or another Schedule I build.

`SceneNormalizer` must:

1. Read scene names from the build-settings scene-path list in `globalgamemanagers`, using `levelN` only as a raw fallback when the list is unavailable.
2. Create scene documents for `level0/1/2`.
3. Create prefab documents only when parser-certified class-ID/object-relationship evidence proves a prefab asset; otherwise retain asset-file GameObject roots as ordinary graph objects.
4. Resolve GameObject/component/Transform local and external PPtrs without assuming container ordinal alignment.
5. Capture built-in Transform hierarchy and values only when the known schema and byte boundaries are valid.
6. Capture `serialized_refs` with field path/declared type when known, target local/container IDs when parsed, target symbol ID only for an exact MonoScript/code-index resolution, and explicit recovery/resolution status everywhere else.
7. Reject parent cycles and references to non-existent parsed targets as invalid facts; preserve external/missing target text as unresolved rather than dropping it.

- [ ] Write failing tests for deterministic snapshot IDs, build-settings scene-name mapping, level fallback, no-marker-string prefab classification, proven prefab classification, parent cycles, external PPtrs, exact symbol matches, absent symbols, and code-index mismatch.
- [ ] Implement the identity, classifier, resolver, and normalizer with deterministic ordering and no raw third-party types.
- [ ] Run focused Indexing Scene tests and verify failures become passes.
- [ ] Run the full solution suite and commit with `git add src/S1Atlas.Indexing/Scene tests/S1Atlas.Indexing.Tests/Scene src/S1Atlas.Core/Storage/IIndexRepository.cs && git commit -m "feat: normalize scene graphs and link indexed types"`.

## Task 5: Add Atomic Scene Indexing Workflow and Owned Paths

**Files:**

- Create: `src/S1Atlas.Indexing/Paths/OwnedScenePaths.cs`
- Create: `src/S1Atlas.Indexing/Scene/SceneIndexWorkflow.cs`
- Create: `src/S1Atlas.Indexing/Scene/SceneIndexWorkflowResult.cs`
- Modify: `src/S1Atlas.Core/Storage/IIndexRepository.cs`
- Modify: `src/S1Atlas.Cli/Configuration/AtlasPaths.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Paths/OwnedScenePathsTests.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Scene/SceneIndexWorkflowTests.cs`

`OwnedScenePaths.ForScheduleOne(dataRoot, buildId, sceneSnapshotId)` must enforce the same lower-case-hex and reparse-point protections as `OwnedIndexPaths`, with final output under `builds/<build-id>/scene-indexes/<scene-snapshot-id>` and staging under a direct `.staging` sibling. Add `AtlasPaths.GetBuildSceneIndexesDirectory`, `GetBuildSceneIndexDirectory`, and `GetBuildSceneIndexStagingDirectory` only if the existing path object is the selected shared authority; do not duplicate root/ID validation in CLI.

`SceneIndexWorkflow.RunScheduleOneAsync(buildId, force, cancellationToken)` must:

1. Resolve the preferred validated extraction with `PreferredVerifiedExtractionResolver`; fail with `NoPreferredVerifiedExtraction` if missing or invalid.
2. Load the source extraction attempt and require its `InputSnapshotId` to resolve to a replay-verified input snapshot for the same build.
3. Load the current environment snapshot and require its installation root/build ID to match the selected build.
4. Verify the selected scene container set before parsing using `SceneInputVerifier` and calculate the scene snapshot ID.
5. Reuse a completed matching scene snapshot unless `force` is supplied.
6. Create the `.staging` directory, start a `Running` scene snapshot, parse/normalize into a bounded `SceneWriteSet`, and re-verify all source hashes.
7. Call `CompleteSceneSnapshotAsync` once; only after the database commit, atomically move staging to final and write `complete.marker`.
8. On any exception, mark the run failed if it started, delete only the owned staging directory, and leave the previous completed scene snapshot untouched.

The workflow must reject a preferred-extraction change, code-index change, input hash change, parser version mismatch, failed class-ID probe, or cross-build link before completion. It must never execute a game, Unity, Assembly.Load, or network request.

- [ ] Write failing path tests for invalid IDs, path traversal, existing reparse points, final/staging containment, and complete-marker paths.
- [ ] Implement `OwnedScenePaths` and add the path methods required by `AtlasPaths`; run focused path tests.
- [ ] Write failing workflow tests for no authority, replay-unverified input, cross-build code index, parser failure, canceled parse, staging cleanup, DB rollback, rerun reuse, and forced new snapshot.
- [ ] Implement the workflow with the parser/verifier/normalizer/repository seams from Tasks 2–4 and add deterministic result counts.
- [ ] Run focused workflow tests and the full solution suite; commit with `git add src/S1Atlas.Indexing/Paths src/S1Atlas.Indexing/Scene src/S1Atlas.Core/Storage/IIndexRepository.cs src/S1Atlas.Cli/Configuration/AtlasPaths.cs tests/S1Atlas.Indexing.Tests/Paths tests/S1Atlas.Indexing.Tests/Scene && git commit -m "feat: add atomic scene indexing workflow"`.

## Task 6: Add Shared Scene Queries and CLI Commands

**Files:**

- Create: `src/S1Atlas.Indexing/Scene/SceneQueryService.cs`
- Create: `src/S1Atlas.Indexing/Scene/SceneSelector.cs`
- Create: `src/S1Atlas.Cli/Commands/ScenesCommand.cs`
- Create: `src/S1Atlas.Cli/Commands/SceneCommand.cs`
- Create: `src/S1Atlas.Cli/Commands/GameObjectCommand.cs`
- Create: `src/S1Atlas.Cli/Commands/PrefabCommand.cs`
- Create: `src/S1Atlas.Cli/Commands/ComponentCommand.cs`
- Create: `src/S1Atlas.Cli/Output/SceneOutputModels.cs`
- Modify: `src/S1Atlas.Cli/Commands/IndexCommand.cs`
- Modify: `src/S1Atlas.Cli/CliApplication.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Scene/SceneQueryServiceTests.cs`
- Create: `tests/S1Atlas.IntegrationTests/Scene/SceneCliTests.cs`

`SceneQueryService` must be the only layer that interprets scene selection/status semantics. It must use SQLite count-plus-page methods and default `limit = 50`. `SceneSelector` resolves exact scene/object/component IDs first, then unique exact names within the selected snapshot/document, and returns candidates plus `Ambiguous*` status on ties. It never picks the first textual match.

Register these commands and options:

~~~text
s1atlas index --scene [--build <build-id>] [--force] [--json]
s1atlas scenes [--build <build-id>] [--snapshot <scene-snapshot-id>]
               [--kind scene|prefab] [--query <text>] [--limit <n>] [--json]
s1atlas scene <scene-id|exact-name> [--children] [--components]
               [--refs] [--limit <n>] [--json]
s1atlas gameobject <game-object-id|scene-id/name>
                    [--children] [--components] [--refs]
                    [--limit <n>] [--json]
s1atlas prefab <prefab-id|exact-name> [--objects] [--components]
               [--refs] [--limit <n>] [--json]
s1atlas component <component-id|exact-type-selector>
                  [--refs] [--code] [--limit <n>] [--json]
~~~

`index --scene` returns the scene snapshot ID, build ID, parser identity, document/object/component/reference counts, recovery counts, and warnings. Query output reuses `CliEnvelope<T>` and adds `totalCount`, `returnedCount`, exact IDs, relative container path/hash, local file IDs, evidence, readable names, `SceneRecovery`, and resolved code symbol ID/canonical signature. Use distinct error codes `NoCompletedSceneIndex`, `SceneSnapshotNotFound`, `SceneNotFound`, `AmbiguousScene`, `GameObjectNotFound`, `AmbiguousGameObject`, `ComponentNotFound`, `AmbiguousComponent`, `SceneInputIntegrityFailure`, `UnsupportedContainer`, `PartialRecovery`, `UnresolvedSceneReference`, and `UnresolvedCodeSymbol`.

The `component --code` path hands the exact resolved symbol ID to existing code queries; it does not duplicate source retrieval. A prefab query over zero proven prefab rows returns a valid empty result with counts, not a fabricated prefab list.

- [ ] Write failing service tests for bounded counts/pages, exact ID/name selection, ambiguity, no completed snapshot, unresolved code links, empty proven-prefab results, and partial recovery.
- [ ] Implement `SceneQueryService` and `SceneSelector` over `ISceneRepository`; keep all status/code semantics out of command renderers.
- [ ] Write failing CLI integration tests for every command in human and JSON modes, default/explicit limits, exact IDs, counts, ambiguous selectors, and machine-stable failures.
- [ ] Add the `--scene` indexing path to `IndexCommand`, register all scene query commands in `CliApplication`, and implement output models/renderers using the existing envelope.
- [ ] Run focused query/CLI tests and the full solution suite; commit with `git add src/S1Atlas.Indexing/Scene src/S1Atlas.Cli/Commands src/S1Atlas.Cli/Output src/S1Atlas.Cli/CliApplication.cs tests/S1Atlas.Indexing.Tests/Scene tests/S1Atlas.IntegrationTests/Scene && git commit -m "feat: expose bounded scene intelligence queries"`.

## Task 7: Extend Repository Hygiene and Generated-Output Isolation

**Files:**

- Modify: `scripts/verify-repository-hygiene.ps1`
- Modify: `tests/S1Atlas.IntegrationTests/Repository/RepositoryHygieneScriptTests.cs`
- Modify: `.gitignore`
- Create: `tests/S1Atlas.IntegrationTests/Scene/SceneOutputIsolationTests.cs`

Add `scene-manifest.json` and `scene-validation.json` to prohibited basenames and `scene-indexes`, `scene-staging`, and `scene-recovery` to prohibited path segments. Add explicit `.gitignore` entries for the local scene-index roots even though `data/` and `*.db` already cover the common cases. Test both synthetic tracked-path violations and the real repository. The scene workflow tests must prove it writes only below an Atlas data root, never below the repository, and never copies the game install into Git-visible paths.

- [ ] Add failing hygiene tests for each new basename and segment.
- [ ] Update the script, `.gitignore`, and isolation test; run the focused hygiene/isolation tests.
- [ ] Run `& .\scripts\verify-repository-hygiene.ps1` and the full solution suite; commit with `git add scripts/verify-repository-hygiene.ps1 tests/S1Atlas.IntegrationTests/Repository/RepositoryHygieneScriptTests.cs tests/S1Atlas.IntegrationTests/Scene .gitignore && git commit -m "chore: isolate scene-generated data"`.

## Task 8: Fixture, Real-Install Smoke, and Release Evidence

**Files:**

- Create: `tests/S1Atlas.Extraction.Tests/Scene/SerializedFileFixtureBuilder.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Scene/SerializedFileFixtureTests.cs`
- Create: `tests/S1Atlas.IntegrationTests/Scene/SceneWorkflowIntegrationTests.cs`
- Create: `docs/smoke-tests/2026-08-14-scene-intelligence.md`
- Modify: `README.md`

The fixture builder must produce sanitized Unity 2022.3 SerializedFile inputs containing at least one scene GameObject, Transform, built-in component, MonoBehaviour/MonoScript pair, local PPtr, external PPtr, missing PPtr, and no prefab class-ID record. A second fixture must contain parser-certified prefab evidence. Fixtures must contain no Schedule I names, game bytes, or proprietary asset payloads.

Integration coverage must exercise the full offline path: verified input manifest, parser adapter, normalization, exact code-index link, migration 8, transaction rollback, final-marker promotion, SQLite count/page queries, and CLI JSON. It must assert that the absence of a custom TypeTree-equivalent schema yields `GraphOnly` rather than invented fields or values.

The smoke procedure must run against the actual install at `Schedule I_Data` without launching the game or Unity and record only aggregate/local evidence:

~~~text
container discovery/acceptance/rejection and parser version
scene documents, proven prefab documents, asset-file roots, GameObjects, roots, transforms, components
MonoScript identity and exact ScheduleI Installed symbol-link counts/reasons
custom MonoBehaviour GraphOnly counts versus any reviewed-schema field decodes
serialized reference totals and resolution counts by target kind/status
SceneRecovery counts, hash-before/hash-after result, runtime/process/network invariants
scene-name source result and prefab class-ID/no-prefab result
~~~

Do not commit the database, manifests, container names/local IDs, scene names, or copied asset bytes. The smoke document may contain only redacted procedure and aggregate results approved for repository publication.

- [ ] Write failing fixture/integration tests for all listed graph/reference/recovery cases.
- [ ] Implement the fixture builder and end-to-end integration harness without game/Unity execution or network calls.
- [ ] Run the full solution suite and the hygiene gate; record exact pass/fail output.
- [ ] Run the real-install smoke manually, retain raw evidence outside the repository, and write only a redacted aggregate smoke document.
- [ ] Update README usage with the scene index/query commands and the explicit fidelity boundary.
- [ ] Commit with `git add tests/S1Atlas.Extraction.Tests/Scene tests/S1Atlas.IntegrationTests/Scene docs/smoke-tests/2026-08-14-scene-intelligence.md README.md && git commit -m "docs: record scene intelligence smoke and usage"`.

## Verification Gate Before Opening the Implementation PR

Run all of the following from the implementation branch:

~~~powershell
dotnet format S1Atlas.sln --verify-no-changes
dotnet test S1Atlas.sln --configuration Release --no-restore
& .\scripts\verify-repository-hygiene.ps1
git diff --check origin/main...HEAD
git diff --name-status origin/main...HEAD
~~~

The final diff must contain no game-install files, database files, serialized asset payloads, generated scene manifests, or third-party parser types outside the Extraction adapter. The real smoke is evidence of coverage, not evidence of completeness.

## Spec Coverage Self-Review

~~~text
real binary-container target and no-YAML boundary       Tasks 3 and 8
offline/read-only/no-runtime invariants                 Tasks 3, 5, and 8
SceneRecovery honesty and no fabricated values          Tasks 1, 4, and 8
exact scene-to-code SymbolIdentity linking              Task 4
authority/build/input/hash/atomic promotion             Tasks 2, 3, and 5
additive migration 8 and normalized tables              Task 2
parser isolation/license/supply chain                   Task 3
bounded human/JSON CLI and stable codes                  Task 6
prefab class-ID evidence boundary                       Tasks 3, 4, and 8
scene-name build-settings source                        Tasks 4 and 8
volume/batched persistence/query bounds                 Tasks 2, 5, and 6
repository hygiene/privacy                               Task 7
fixture and honest real-install smoke                   Task 8
anti-overengineering guardrails                         Global Constraints and Tasks 3/4/8
~~~

No task adds Unity runtime execution, game execution, visual reconstruction, asset payload extraction, a generalized multi-game framework, numerical confidence, portal/MCP/agent-skill work, or a second data authority.
