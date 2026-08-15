# Scene Intelligence Final-Review Fix Wave

Date: 2026-08-15
Implementation branch: `codex/scene-intelligence-implementation`
Starting head: `dfa638ecf14561f40d7bd7deacdb449408e7b7a5`
Scope: the seven Important whole-branch review findings plus the two adjacent minors explicitly allowed in the fix brief.

## Outcome

All seven Important findings were reproduced with focused regression coverage and fixed in one coherent wave. Published scene snapshots remain immutable and queryable only after filesystem publication. Retryable rows and paths are reconciled without weakening build, extraction, input, parser, or code-index authority. Recovery and query results remain categorical and evidence-backed; no values, identities, fields, scene facts, or proprietary data were fabricated.

No game, Unity process, runtime probe, managed game/asset code load, network request, or real-install mutation was used in this wave. Tests use synthetic/sanitized fixtures and temporary Atlas roots only.

## Finding-by-finding changes

### 1. Retry and reconciliation

Root cause:

- Deterministic IDs looked up only published snapshots for reuse.
- A failed, stale `Running`, or completed-but-unpublished row retained the same primary key, so `CreateSceneSnapshotAsync` could fail on retry.
- A crash-created final directory was rejected before the workflow could reconcile it.

Fix:

- `CreateSceneSnapshotAsync` now performs reconciliation and replacement in one SQLite transaction.
- It first rejects any row with `published_at_utc`, preserving published snapshot immutability.
- For an unpublished row it deletes owned graph rows in foreign-key-safe child-to-parent order, deletes the unpublished snapshot row, and inserts the new `Running` attempt atomically.
- The workflow removes only the exact owned stale final/staging directories after confirming that no published snapshot is reusable.
- Crash windows are recoverable: pre-completion staging, database-completed/pre-promotion staging, and promoted/pre-publication final directories no longer permanently block the deterministic rerun.

Coverage:

- stale `Running` replacement;
- failed-attempt replacement;
- completed-but-unpublished graph replacement;
- published snapshot mutation rejection;
- parser failure, stale staging/final remnants, deterministic successful retry;
- existing cancellation, rollback, promotion-failure, and reuse cases remain covered.

### 2. Recovery persistence and reuse

Root cause:

- Normalization computed `SceneWriteSet.Snapshot.RecoveryStatus`, but completion did not copy that aggregate into `scene_snapshots`.
- Reuse rebuilt only some totals through bounded list calls, returned zero container/transform counts, and returned an empty recovery-count map.

Fix:

- Completion writes the normalized aggregate recovery status in the same transaction as graph rows and status transition.
- Per-document recovery remains inserted in that transaction and is asserted after publication.
- Added persisted `SceneIndexStatistics` retrieval for container/document/GameObject/Transform/component/reference totals plus recovery counts across documents, GameObjects, transforms, components, and references.
- Reused workflow results now come from those persisted statistics and preserve nonempty recovery counts.

Coverage:

- aggregate `GraphOnly` persistence;
- independent per-document `PartiallyRecovered` persistence;
- nonempty persisted recovery counts;
- fresh/reused parity for all counts and recovery counts.

### 3. Stripped MonoBehaviour graph preservation

Root cause:

- GameObject component PPtrs correctly retained a stripped MonoBehaviour attachment, but script resolution unconditionally dereferenced the null decoded MonoBehaviour payload.

Fix:

- A retained MonoBehaviour class/object record with null decoded payload is normalized as the attached `MonoBehaviour` component.
- Script assembly/namespace/class, resolved symbol, and resolved code-index identity remain null.
- Type resolution is `Unavailable`; component recovery remains the honest graph-only state.

Coverage:

- focused normalizer regression;
- end-to-end workflow, SQLite completion/publication, and query regression using a synthetic parser record with retained GameObject/component identity and null MonoBehaviour payload.

### 4. Reference scoping and full-set status

Root cause:

- Scene/object filters joined references only through `source_component_id`; GameObject-origin references have a null source component and disappeared from filtered results.
- unresolved outcome logic inspected only returned rows, so an unresolved reference beyond `LIMIT` was ignored.

Fix:

- Reference SQL now resolves source ownership through either the source component's GameObject or, when the source component is null, the source snapshot/container/local-file identity of the source GameObject.
- Scene and object filters use that resolved source identity.
- Reference pages include `UnresolvedCount`, computed by SQLite over the complete filtered set before paging.
- query outcome logic uses the full unresolved count; human and JSON output expose the count.

Coverage:

- GameObject-origin references filtered by scene and object;
- exact total/returned/unresolved counts with `limit = 1`;
- 51-reference regression where the returned row is resolved but the unresolved row lies beyond the page.

### 5. Component exact-type selection

Root cause:

- exact component lookup compared only `components.kind`, so every custom script component appeared only as `MonoBehaviour`.

Fix:

- The repository contract now names the operation `FindComponentsByExactTypeAsync`.
- Exact binary matching accepts either the built-in component kind or the normalized MonoBehaviour script type (`ScriptNamespace + '.' + ScriptClass`, with class-only matching for an empty namespace).
- ID-first, unique, not-found, and ambiguous selector behavior remains in `SceneSelector`.

Coverage:

- built-in `Transform` selection;
- unique normalized script selection;
- two exact script matches returning `AmbiguousComponent` with deterministic candidates.

### 6. Serialized-reference symbol authority

Root cause:

- resolved component type symbols were validated at completion, but non-null `serialized_refs.target_symbol_id` values relied only on the broad symbol foreign key.

Fix:

- Completion now validates every distinct non-null target symbol in bounded batches before inserts.
- Each symbol must be a type in the scene snapshot's exact code snapshot and exact completed code index, with `ScheduleI`, `Installed`, the Schedule I canonical-key prefix, and the same environment build.
- Any mismatch rolls back the entire scene transaction.

Coverage:

- target symbol from another build's completed Schedule I Installed index;
- target symbol from another completed Schedule I Installed index for the same build;
- existing exact target symbols continue to complete successfully.

### 7. `index --scene` provenance and outcomes

Root cause:

- CLI output used parser constants instead of the persisted workflow result, hiding the forced parser-version nonce.
- build/code-index/reuse/container/transform facts were absent from the output model.
- reuse dropped persisted statistics.
- several authority failures were plain `InvalidOperationException` values and became `OperationalFailure` at the command boundary.

Fix:

- `SceneIndexWorkflowResult` now carries build ID, code index ID, persisted parser ID/version, reuse, all six graph/reference counts, recovery counts, and warnings.
- Fresh and reused results use the same shape; a forced run returns its actual persisted `:forced:<nonce>` parser version.
- human and JSON command output use one shared mapping and expose identical provenance/count fields.
- Authority/integrity state changes now use distinct stable `SceneIndexFailureException` statuses: `NoPreferredVerifiedExtraction`, `NoReplayVerifiedExtractionInput`, `NoMatchingEnvironmentSnapshot`, `NoCompletedScheduleOneCodeIndex`, `CrossBuildCodeIndex`, `PreferredExtractionChanged`, `ReplayVerifiedInputChanged`, `CodeIndexChanged`, and `NoVerifiedSceneContainers`.

Coverage:

- workflow fresh/reuse/force provenance and count parity;
- forced parser nonce preservation;
- human/JSON success mapping parity for every field and warnings;
- human/JSON `NoPreferredVerifiedExtraction` parity without `OperationalFailure`;
- existing integrity and unsupported-container stable-code coverage remains green.

## Adjacent review minors

- Added the actual generated basename `scene-index.manifest.json` to the tracked-path hygiene ban and its synthetic violation theory.
- Pinned migration 8's version, exact name, and committed checksum in tests.
- Added a migration-7-to-8 test that runs the production migrator twice, asserts one migration-8 row/checksum, all seven scene tables, one schema-8 backup, and no duplicate application.
- Migration 8 SQL itself was not changed, preserving the committed checksum and additive migration history.

## Verification evidence

Baseline before fixes:

- `dotnet test S1Atlas.sln --configuration Release --no-restore`
- PASS: 1,068 total tests (Core 125, Storage 116, Extraction 549, Indexing 143, Integration 135).

Focused red/green evidence included:

- storage scene lifecycle/recovery/reference/type/symbol tests;
- indexing workflow, normalizer, selector, and query-service tests;
- integration stripped-MonoBehaviour workflow/SQLite regression;
- integration CLI provenance and stable-authority-code parity;
- hygiene basename theory;
- migration name/checksum/v7-to-v8 idempotence tests.

Consolidated pre-commit gates:

- `dotnet format S1Atlas.sln --verify-no-changes --no-restore` — PASS.
- `dotnet test S1Atlas.sln --configuration Release --no-restore` — PASS: 1,085 total tests, 0 failed, 0 skipped (Core 125, Storage 125, Extraction 549, Indexing 147, Integration 139).
- `& .\scripts\verify-repository-hygiene.ps1` — PASS.
- `git diff --check` — PASS.

Final committed-range gates:

- `git diff --check origin/main...HEAD` — PASS.
- `git diff --name-status origin/main...HEAD` — PASS after inspection; the range contains only the approved Scene Intelligence milestone, this fix wave, and its documentation.
- `& .\scripts\verify-repository-hygiene.ps1` — PASS against the committed file set.
- `git status --short --branch` — PASS; the implementation branch is clean.
- The fix-wave commit's parent is exactly the supplied starting head, `dfa638ecf14561f40d7bd7deacdb449408e7b7a5`.

## Privacy, runtime, and smoke status

- No game, Unity, runtime probing, network, or real-install write was performed.
- No Schedule I file, database, generated manifest, container/scene name, local ID, path, hash, or proprietary byte was added to Git/CI.
- The real-install production smoke remains honestly blocked at `NoReplayVerifiedExtractionInput`, as already recorded in the redacted smoke document. This fix wave does not fabricate replay authority, alter preferred extraction authority, launch the extraction tool, or substitute the read-only supplementary harness for production replay verification.

## Remaining concerns

- No code-level or architectural blocker remains for the seven review findings.
- The only open operational concern is the pre-existing real-install replay-verification prerequisite. Production smoke completion requires a separately authorized extraction-maintenance session; until then the honest result remains blocked.
