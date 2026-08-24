# S1Atlas V1 Milestone 2 — Scene Intelligence Design

**Status:** Proposed design for review
**Date:** 2026-08-14
**Milestone:** V1 Completion — 2 of 6 (roadmap milestone 2)
**Scope:** Offline static indexing of Schedule I scene/object graphs and their links to the existing code index
**Target platform:** Windows
**Primary language:** C# / .NET 8

## 1. Purpose

This milestone adds the smallest useful static model of Schedule I's Unity object graph: scenes, GameObjects, parenting, components, proven prefab roots, and serialized object/script references. It makes the graph searchable beside the existing IL2CPP-derived code index so a modder can answer questions such as:

- Which scene contains a named GameObject?
- Which components are attached to it, and which indexed `ScheduleOne.*` types do they represent?
- What is the parent/child path and which serialized references leave the object?
- Which prefabs are present in the build's serialized asset files?

The source is an IL2CPP player build, not a Unity editor project. The milestone therefore reads Unity's binary SerializedFile formats and records exactly where those formats and available type metadata stop being trustworthy. It does not attempt to recreate an editor project or infer missing serialized data.

The governing principle carries forward from the prior milestones:

> **Progressive readability, not progressive disclosure of truth.** A scene query may present a readable path or type name, but it must keep exact container paths, local file IDs, hashes, symbol identities, evidence, and recovery status visible.

## 2. Honest Fidelity Boundary (read this first)

### 2.1 What the real Schedule I install establishes

The operator's installed copy was inspected read-only on 2026-08-14. Under `Schedule I_Data` it contains:

```text
level0
level1                         + level1.resS
level2                         + level2.resS
sharedassets0.assets           + .resS + .resource
sharedassets1.assets           + .resS + .resource
sharedassets2.assets           + .resS + .resource
resources.assets               + .resS
globalgamemanagers             + globalgamemanagers.assets + .resS
```

The binary files identify Unity `2022.3.62`. Binary strings include markers such as `GameObject` and `Prefab`, but a string marker is not evidence of a serialized class ID or prefab asset. The inspected install has no human-readable `.unity` or `.prefab` files and no `.bundle` or `.unity3d` files under `Schedule I_Data`. The repository's extraction profile names `Schedule I_Data/globalgamemanagers` as the first Unity-version source, and the Windows metadata reader records the resulting installation observation without executing the game.

This commits v1 to the following parse target:

```text
Scene roots:       level0, level1, level2
Asset dependencies: sharedassets0.assets, sharedassets1.assets, sharedassets2.assets,
                    resources.assets
Metadata/support:  globalgamemanagers and globalgamemanagers.assets
Sidecars:          matching .resS and .resource files, read only as payload providers
Deferred:          AssetBundle/UnityFS containers and any future containers not present
                   in the verified input manifest
```

The parser must discover external-file references rather than assuming that `levelN` and `sharedassetsN` have matching ordinals. `globalgamemanagers*` participates in metadata/type-tree support and provenance; it is not presented as a gameplay scene unless the object metadata proves that it is one.

### 2.2 What can be recovered

When the SerializedFile header, object table, external references, and required type information are valid, v1 can reliably recover:

```text
FACT                         Recovery basis
container path/hash/size     verified input manifest and SerializedFile header
scene/asset document         level file or a selected asset container
GameObject identity/name     GameObject object record and local file ID
component attachment         GameObject component PPtrs and local file IDs
parenting/hierarchy          Transform parent PPtrs and sibling order
native component identity    Unity class ID and known built-in schema
MonoBehaviour script identity MonoScript PPtr plus assembly/namespace/class text,
                              when those fields are present
object references            PPtr file/local IDs, resolved only when target is parsed
code link                    exact qualified-name match in the build-matched code index
```

The code index can confirm that a type exists and provide its canonical identity. It cannot, by itself, reconstruct the byte layout or serialized defaults of a MonoBehaviour.

### 2.3 What is not reliably recoverable

An IL2CPP binary does not retain the editor YAML source. Custom MonoBehaviour fields are interpretable only when the SerializedFile supplies an embedded or otherwise reviewed TypeTree-equivalent schema. IL2CPP metadata and the reconstructed `ScheduleOne.*` symbols are not a license to guess that schema.

**V1 decision:** the required value proposition is the GameObject/Transform/component/reference graph plus exact code links. General custom MonoBehaviour field-value recovery is a best-effort extra, not a v1 success criterion. Scene Intelligence will not generate a new IL2CPP type database from `global-metadata.dat` and reconstructed assemblies in this milestone. That can be a later, separately justified milestone if real mod-development questions show that field values are worth the added parser/type-database and storage complexity.

Therefore v1 does not claim to recover arbitrary serialized values when:

- the container is unreadable or truncated;
- the object has no usable TypeTree-equivalent schema;
- a custom field layout is absent, stripped, or ambiguous;
- a PPtr target is in an unparsed or missing external container; or
- the script identity cannot be mapped exactly to the selected code index.

V1 stores field paths and declared types only for serialized references when supplied by the parser's type information. It stores built-in Transform values only when the known Unity schema and byte boundaries are unambiguous. It stores PPtrs as references, not as guessed names. It does not persist a general custom-field value table. Unsupported nested data, custom MonoBehaviour values without a reviewed type schema, large opaque blobs, textures, meshes, audio, shaders, and arbitrary asset payloads remain unavailable.

Every scene, component, serialized field, and serialized reference has a `SceneRecovery` availability classification:

```text
FullyRecovered       all data required by this record's declared v1 scope is available
PartiallyRecovered   graph/type information exists, but one or more requested fields are unsupported
GraphOnly            identity, attachment, and hierarchy are available; serialized field values are not
StubOrUnavailable    the container/object data could not be read or was not supported
Unknown               recovery was not evaluated
```

These are categorical availability states, not numerical confidence scores. `GraphOnly` is not the same as an empty component; it is an explicit statement that the graph exists while field interpretation does not.

## 3. Scope

### 3.1 Included

```text
binary Unity SerializedFile parsing for the committed Schedule I container set
scene documents from level0, level1, and level2
GameObjects, names, active state when available, local file IDs, and scene membership
Transform parenting, root order, sibling order, and local transform values when the
  built-in Transform schema is available; no derived world transforms
components attached to GameObjects, including built-in components and MonoBehaviours
MonoScript identity extraction and exact linking to the selected Schedule I code index
asset-file GameObject roots and components available for graph/reference resolution
serialized reference field paths/types when supplied by type information; no general
  custom MonoBehaviour value capture
serialized PPtr references within a parsed scene/asset graph and to indexed code symbols
immutable, build-scoped SQLite scene snapshots
bounded/countable human and JSON queries
```

**Prefab decision:** bundle-only prefabs are deferred. In the selected `.assets` files, a `scenes.kind = Prefab` row is emitted only when the parser verifies a supported prefab/PrefabInstance class ID or another parser-certified prefab-asset relationship. A string such as `Prefab` never classifies an object. If the real-player files contain no such records, v1 still indexes asset-file GameObject roots as ordinary graph objects for reference resolution, but emits no prefab entity and does not claim prefab coverage. The `prefab` query is therefore a bounded view over proven prefab rows, not a promise that every GameObject root is a prefab.

### 3.2 Deferred

```text
AssetBundle/UnityFS parsing and prefabs found only inside bundles
generic discovery of arbitrary future containers beyond the verified input manifest
editor YAML import/export or reconstruction of a Unity project
full serialized value capture for every custom MonoBehaviour field
managed-reference graphs, animation curves, controller graphs, and editor-only metadata
texture, mesh, material, shader, audio, lightmap, or other payload extraction
visual scene reconstruction, rendering, spatial indexing, or world-coordinate inference
runtime probing, game execution, Unity execution, or scene loading
automatic prefab dependency expansion outside parsed SerializedFiles
multi-game, multi-engine, or generalized Unity framework support
portal, MCP, agent-skill, or other later-interface work
```

The scope boundary is intentionally narrow: answer object/component graph questions without becoming an asset-ripping project.

## 4. Inputs, Authority, and Privacy

Scene indexing consumes only the same build authority used by the existing extraction and code index:

- The Schedule I installation is read-only input. S1Atlas never edits it.
- The selected `build_id` must be valid and must identify the same installed game build as the verified scene containers.
- Containers and sidecars are accepted only through an integrity-verified input snapshot. The scene workflow records each relative path, size, SHA-256, role, and parser-relevant header fact before parsing and verifies the inputs again before promotion.
- The scene snapshot records the validated extraction ID and the completed code `index_id`/`snapshot_id` used for type resolution. A scene snapshot cannot be completed against an unrelated build or a Release/Preview API snapshot.
- A scene index is immutable after promotion. Re-parsing with a different parser or policy produces a new scene snapshot; it does not mutate an old one.
- Scene facts are `FACT` when read from the SerializedFile or verified manifest. Symbol links, ancestor paths, counts, and coverage summaries are `DERIVED`. Unavailable script names, field paths, and target IDs remain unresolved textual/raw facts rather than invented values.

Parsed scene data is proprietary-derived data. The database, scene rows, container manifests, names, local IDs, and generated scene-index files remain under the local Atlas data root and must never enter Git, a PR artifact, or CI artifacts. Real-install smoke evidence is a local, untracked aggregate of counts/statuses only; it must not include the database or copied asset bytes.

The repository-hygiene gate must be extended with the future output vocabulary before implementation is merged. At minimum it must reject tracked `scene-manifest.json`, `scene-validation.json`, `scene-indexes`/`scene-staging` path segments, and any scene snapshot database or generated scene output under `data/`, `installed/`, or `upstream/`. Its tests must cover both the new basenames and path segments. The existing `*.db`, `data/`, and generated-output rules remain in force.

## 5. Architecture and Tooling Isolation

The static pipeline is:

```text
verified game input snapshot
        |
        v
container/header/external-reference validation
        |
        v
S1Atlas-owned Unity SerializedFile adapter
        |
        +--> object identities, GameObjects, Transforms, components
        +--> TypeTree-backed built-in data and reference PPtrs
        |
        v
exact MonoScript -> code-index symbol resolution
        |
        v
normalized scene snapshot in SQLite
        |
        v
bounded shared query layer -> CLI human/JSON output
```

The parser boundary is S1Atlas-owned. The Core/Storage/CLI layers consume S1Atlas-owned records and enums, never third-party Unity parser types. The adapter must expose only operations equivalent to:

```text
ReadVerifiedSerializedFiles(inputManifest) -> SerializedFileDocument records
ReadObjectTable(document) -> ObjectRecord records
DecodeWithTypeTree(object, typeTree) -> supported built-in/reference data or explicit unavailable status
ResolvePPtr(reference, parsedDocuments) -> exact target identity or unresolved reference
```

If a Unity-asset parser dependency is used, it must be selected and reviewed before implementation against Unity `2022.3.62`, pinned to an exact version and package/binary SHA-256, and recorded through the existing managed-tool supply-chain metadata. License compatibility is a release gate, not merely documentation: a copyleft parser cannot be bundled or redistributed unless the planned S1Atlas distribution model explicitly approves that consequence. The selected parser's license, transitive licenses, provenance, update path, and redistribution terms must be recorded. Network access is allowed only for an explicit tool acquisition/update operation; normal scene indexing is offline. A parser process may run as a static parser, but it must never load or execute the game, Unity, a mod, a managed game assembly, or serialized asset code.

The exact parser package/version is an implementation gate, not an excuse to weaken the boundary: no candidate is authoritative until it passes the fixture and real-install smoke against the committed container set. If no candidate can legally and reliably parse the required files, the milestone reports `UnsupportedContainer` and does not fabricate a partial success.

## 6. Scene-to-Code Linking

The link is exact and build-scoped.

1. For a `MonoBehaviour` component, read its `m_Script` PPtr and the target `MonoScript` object.
2. Recover the script's assembly, namespace, and class text when present. Normalize the qualified type name with the existing `CanonicalSignatureRenderer` rules.
3. Select the completed Schedule I `Installed` code index for the same `build_id`. Construct the existing identity with the equivalent of `SymbolIdentity.Create(CodebaseKind.ScheduleI, CodeChannel.Installed, SymbolKind.Type, qualifiedName)`.
4. Resolve only an exact `symbols.canonical_key` match in that selected index. The persisted component keeps the resolved `symbol_id`, canonical key, index ID, and readable qualified name together.
5. If the script has no usable identity, the exact type is absent, the selected code index is missing, or more than one candidate remains, preserve the raw assembly/namespace/class text and classify the link as unresolved. No fuzzy name match or first-candidate choice is permitted.

The type link is a `DERIVED` join over two immutable authorities: the serialized `MonoScript` fact and the code-index symbol fact. It must not silently cross a build, channel, or codebase boundary. A resolved type link lets `component` output hand the exact symbol ID to existing `type`, `source`, and code relationship queries. An unresolved textual link remains useful for diagnosis but cannot be presented as an indexed type.

Object PPtrs follow the same honesty rule. A local file ID is resolved to a GameObject/component only when its container is in the parsed snapshot and the object exists. External or missing targets retain file ID, file index/path text, and raw field path with an unresolved status. A target `MonoScript` may additionally carry a `target_symbol_id`; ordinary asset objects never become code symbols.

## 7. Persistence and Migration 8

Scene data is stored in the existing SQLite database through one additive migration, `8-scene-intelligence-v8`. Migration 8 creates new tables and indexes only; it does not rewrite or reinterpret existing symbol, relationship, extraction, or snapshot rows. Existing migration checksums, ordering, and idempotence rules remain unchanged.

The normalized shape is:

```text
scene_snapshots
  scene_snapshot_id PK, build_id FK, extraction_id FK, input_snapshot_id FK,
  code_snapshot_id, code_index_id, parser_id/version, container_manifest_digest,
  status, recovery_status, started/completed timestamps, failure code/message

scene_containers
  container_id PK, scene_snapshot_id FK, relative_path, container_kind,
  unity_version, serialized_file_version, byte_count, sha256, sidecar manifest

scenes
  scene_id PK, scene_snapshot_id FK, container_id FK, kind (Scene|Prefab),
  name, source_local_file_id, object_count, root_count, recovery_status

game_objects
  game_object_id PK, scene_id FK, container_id FK, local_file_id,
  name, active, tag/layer when available, recovery_status

transforms
  game_object_id PK/FK, parent_game_object_id FK nullable, sibling_index,
  local position/rotation/scale nullable, recovery_status

components
  component_id PK, game_object_id FK, container_id FK, local_file_id,
  unity_class_id, kind, script assembly/namespace/class text nullable,
  resolved_type_symbol_id nullable, resolved_code_index_id nullable,
  type_resolution_status, recovery_status

serialized_refs
  reference_id PK, scene_snapshot_id FK, source_component_id FK nullable,
  field_path/declared_type nullable, source_container/local_file ID,
  target_container/local_file ID nullable, target_game_object_id nullable,
  target_component_id nullable, target_symbol_id nullable, target_text nullable,
  resolution_status, evidence, recovery_status
```

The table names and keys are deliberate. Local file IDs are not globally unique, so every persisted object identity is scoped by the scene snapshot and container. `scenes.kind = Prefab` avoids a second graph model for proven prefab roots. `serialized_refs` is the normalized cross-object/code edge table; unresolved target columns remain null while raw target identity/text remains present. There is deliberately no general `serialized_fields` table in v1: custom field values are not the milestone's authority or success criterion.

Required indexes include:

```text
scene_snapshots(build_id, status, completed_at_utc)
scene_containers(scene_snapshot_id, relative_path)
scenes(scene_snapshot_id, kind, name)
game_objects(scene_id, name), game_objects(scene_snapshot_id, name)
game_objects(scene_snapshot_id, container_id, local_file_id) UNIQUE
transforms(parent_game_object_id)
components(game_object_id, kind), components(resolved_type_symbol_id)
serialized_refs(source_component_id, field_path)
serialized_refs(target_game_object_id), serialized_refs(target_symbol_id)
```

The parser stages any disposable manifests under the Atlas data root, validates counts, foreign-key targets, input hashes, and recovery classifications, then commits the normalized rows and `Completed` status in one database transaction. A failed or cancelled import has no queryable completed snapshot. The prior completed scene snapshot and the prior current Atlas state remain authoritative. Filesystem promotion and database promotion follow the existing staged/atomic snapshot pattern; no in-place database or generated-artifact mutation is allowed.

Expected volume is many thousands of GameObjects plus multiple components and references per object. The importer processes one verified container at a time, uses prepared batched inserts inside the promotion transaction, and does not retain full object payloads in memory. Queries must filter and count in SQLite, use indexed predicates, and return only bounded pages. The design does not permit loading the complete graph merely to satisfy a default list query.

## 8. Query and CLI Surface

The scene commands use the shared query layer and Milestone 1 conventions: human and JSON output, exact identities beside readable labels, deterministic resolution, `--limit`, accurate `totalCount`/`returnedCount`, and no raw stack traces.

```text
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
```

List and child/reference queries default to a documented limit of 50. Human output says, for example, `Found 18,421 GameObjects. Showing 50.` Detail output includes the scene snapshot ID, build ID, container relative path and SHA-256, local file ID, readable name/path, `SceneRecovery`, raw/derived evidence, and exact code symbol ID/canonical signature when resolved. `--code` prints a handoff to the existing symbol/source query rather than duplicating source retrieval.

Single-object selection accepts exact IDs first, then unique exact names within the selected snapshot/document, and otherwise fails as ambiguous. Broad `--query` remains broad. Proposed machine-stable outcome codes include:

```text
NoCompletedSceneIndex
SceneSnapshotNotFound
SceneNotFound
AmbiguousScene
GameObjectNotFound
AmbiguousGameObject
ComponentNotFound
AmbiguousComponent
SceneInputIntegrityFailure
UnsupportedContainer
PartialRecovery
UnresolvedSceneReference
UnresolvedCodeSymbol
```

`PartialRecovery` and unresolved-reference statuses are not permission to suppress results; they explain why some fields or links are absent. Missing authority, integrity failure, and ambiguity are distinct from a valid empty result. JSON exposes status codes and structured candidates; human output remains readable without hiding the exact IDs needed for automation.

## 9. Testing Strategy

### 9.1 Unit and fixture tests

All fixture tests are offline and use synthetic or sanitized SerializedFile fixtures; they do not require Unity, the game, or a proprietary asset copy. They cover:

```text
Unity 2022.3 SerializedFile header/version and sidecar discovery
validated input manifest creation, re-read hash verification, and path containment
external-file table and local/external PPtr resolution
GameObject/component/Transform extraction and parent-cycle rejection
class-ID-based prefab classification without a second graph model; marker strings are not evidence
TypeTree decoding for supported built-in/reference cases only
missing/stripped TypeTree -> GraphOnly, never guessed field values
custom MonoBehaviour fields without a reviewed type database -> GraphOnly, never guessed values
exact MonoScript -> SymbolIdentity resolution, missing, and ambiguous cases
same-build enforcement for scene snapshot and code index
migration 8 creation, checksum/idempotence, foreign keys, and indexes
atomic rollback after parse, validation, or database failure
bounded/countable queries and all machine-stable outcome codes
human/JSON output retaining readable names plus exact identities/evidence
```

### 9.2 Real-install smoke

After implementation, the operator must run the smoke against the actual installed Schedule I build without launching the game or Unity. The smoke must first record a local verified input manifest for the committed target files and then report counts, never copy asset bytes into the repository.

The smoke report must quantify at least:

```text
containers discovered / accepted / rejected, with rejection reasons
Scene documents, proven Prefab documents, asset-file GameObject roots, Transforms, and components
serialized class IDs and prefab/PrefabInstance evidence, including the explicit no-prefab case
components with usable MonoScript identity
components resolved to an exact ScheduleI Installed Type symbol
components unresolved by reason: missing identity, not indexed, ambiguous, unavailable
custom MonoBehaviour components classified GraphOnly versus any fields decoded by a reviewed
  TypeTree-equivalent schema; custom-field decoding is reported, not required for success
serialized references total, resolved to GameObject/component, resolved to code symbol,
  and unresolved textual/external targets
counts by every SceneRecovery state
```

The report must also prove that the selected files' hashes are unchanged before/after, the game and Unity were not run, no runtime probing occurred, and normal parsing made no network request. It must record whether embedded TypeTree-equivalent data was present per container and whether `resources.assets` or any external reference contributed graph objects. It must not claim complete scene coverage from a successful parse; coverage is the recorded denominator/numerator counts and availability categories.

The current install inspection resolved the container names but did not establish parser-level TypeTree availability, serialized prefab class IDs, final scene names, or the complete reference population. Those are deliberate real-smoke checks, not guessed design facts. A smoke that cannot parse a selected container records `UnsupportedContainer` and its impact on coverage instead of silently dropping it. Scene names must come from the build-settings scene-path list in `globalgamemanagers`; if that list cannot be recovered, the container basename remains a raw fallback such as `level1`, not a fabricated human scene name.

## 10. Anti-Overengineering Guardrails

```text
no Unity runtime, Unity editor, game execution, or runtime probing
no YAML assumption and no editor-project reconstruction
no full asset-ripping: no textures, meshes, audio, shaders, or payload export
no visual scene reconstruction or spatial/world simulation
no numerical confidence scoring
no generalized multi-game/multi-engine abstraction
no AssetBundle/UnityFS support in v1
no generated IL2CPP type database or general custom-field value model in v1
no graph database, blob store, or second authority
no fuzzy scene-to-code linking or fabricated field values/references
no portal, MCP, agent-skill, or other later-milestone work
prefer a small normalized graph and bounded queries over a universal asset model
```

## 11. Definition of Done

```text
[ ] v1 parses the verified Schedule I level0/1/2 and sharedassets0/1/2/resources
    SerializedFiles, with globalgamemanagers* used for support/provenance and sidecars
    treated as byte providers
[ ] no YAML, Unity runtime, game execution, or runtime probing is required
[ ] scenes, proven prefab documents, GameObjects, parenting, components, selected Transform
    data, and serialized references have normalized SQLite storage in migration 8
[ ] every scene/component/reference exposes SceneRecovery availability status; custom
    MonoBehaviour field recovery is explicitly best-effort and not a completion gate
[ ] no field value or target is fabricated when TypeTree/type/reference information is absent
[ ] prefab rows are emitted only from verified class-ID/object-relationship evidence; a
    marker string or ordinary asset-file GameObject root is never mislabeled as a prefab
[ ] MonoBehaviour script identity resolves only by exact existing SymbolIdentity in the
    same build's completed ScheduleI Installed code index
[ ] unresolved textual and ambiguous links remain explicit and queryable
[ ] scene snapshots consume integrity-verified inputs, are immutable, build-scoped, and
    promote atomically without damaging the last completed snapshot
[ ] parser dependencies are behind an S1Atlas adapter, pinned/reviewed, and license/
    supply-chain facts are recorded without third-party types leaking into Core/Storage/CLI
[ ] scene queries are bounded, counted, human/JSON, and expose stable outcome codes
[ ] repository hygiene rejects all defined scene-generated output paths and artifacts
[ ] fixture tests cover missing TypeTrees, unresolved PPtrs, exact code linking, rollback,
    migration behavior, and bounded queries
[ ] the real-install smoke records actual container, recovery, symbol-link, and reference
    coverage without placing proprietary-derived data in Git or CI artifacts
[ ] build, tests, format, and hygiene gates pass after implementation
```

## 12. Hard Invariants

```text
The game installation is read-only input; S1Atlas never edits it.
Scene parsing is static and offline during normal operation.
The game, Unity, managed game assemblies, mods, and serialized asset code are never loaded
  or executed for scene indexing.
Only integrity-verified containers and sidecars may feed a completed scene snapshot.
A scene snapshot is immutable, scoped to one build_id, and linked to one completed same-build
  ScheduleI Installed code index.
Every resolved scene-to-code link is an exact SymbolIdentity/canonical-key match.
Unresolved names, field values, and PPtrs remain explicitly unresolved; Atlas never fabricates them.
SceneRecovery is categorical availability, not numerical confidence or completeness.
Failed, cancelled, or integrity-invalid parsing cannot replace the last completed snapshot.
Parsed scene data and generated manifests never enter Git or CI artifacts.
Third-party parser types never cross the S1Atlas-owned adapter boundary.
All large scene/object/reference lists are bounded and counted at the persistence/query layer.
```

## 13. Roadmap Context

Scene Intelligence is roadmap milestone 2 of the six V1 completion milestones. It is sequenced after Polish & Usability because it reuses exact selection, bounded queries, provenance, recovery-status conventions, and the existing build/index authority. It is independent of Build & Symbol Diffing and feeds later portal and agent-facing work through the same query layer.

```text
1. Polish & Usability            complete
2. Scene Intelligence            this document
3. Build & Symbol Diffing        in review / separate milestone
4. Human Portal                  consumes scene and code queries
5. MCP + Agent Skill             consumes the same evidence and links
6. V1 Hardening & Release        real-install validation and operational hardening
```

This document is design-only. It authorizes review of the scene model and fidelity boundary; it does not authorize implementation, schema edits, parser acquisition, or smoke execution in this change.
