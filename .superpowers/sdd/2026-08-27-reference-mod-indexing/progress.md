# SDD ledger — plan: .superpowers/plans/2026-08-27-reference-mod-indexing.md

## Setup

- Worktree: `codex/at-26-reference-mod-manifest`
- Base commit: `f087f04` (merged Task 1 and post-merge documentation)
- Spec: `docs/design/2026-08-27-reference-mod-indexing-design.md`
- Baseline: fresh worktree required restore; after `dotnet restore S1Atlas.sln`, baseline suite passed with 1,249 tests.

## Preflight scan

| Tasks | Shared file/interface | Check | Ruling or result |
|---|---|---|---|
| 2 ↔ 3 | `ReferenceCollectionDefinition`, `ReferenceModDefinition`, selected input files | Task 2 normalizes the manifest and selector inputs; Task 3 consumes them for selected-only indexing and post-read drift checks. | Consistent. Task 2 does not implement workflow or decompilation. |
| 2 ↔ 4 | collection identity and normalized file metadata | Task 2 excludes absolute paths from hashes; Task 4 consumes collection identity and provenance. | Consistent. |
| 2 ↔ 5 | collection definition and selectors | Task 5 owns CLI validation/index commands; Task 2 owns loader/selector behavior. | Consistent. |
| 2 ↔ 6 | manifest shape and local-only trust | Task 6 documents the manifest and consumes collection metadata; it does not add downloading. | Consistent. |
| Task 2 self-check | loader, selector, hasher | Tests cover invalid manifests, path safety, selected extensions, exclusions, deterministic ordering, hash-only identity, drift, and cancellation. | Consistent. |

## Rulings

- Ruling: keep the Task 2 implementation limited to manifest normalization, local file selection, and hashing — workflow orchestration and decompilation belong to Task 3 — because the later tasks consume these seams independently; cost if wrong is a follow-up refactor, not a schema change.

## Task status

- Task 2: completed — implementation `0709483`, review fix `7e025fe`; initial review found three Important findings and fix round 1 addressed all three. Scoped re-review found no new Critical/Important breakage. Focused ReferenceMod suite passed 20/20.
- Task 3: completed — implementation `2e8af6a`, review fix `17686f0`; initial review found one Critical and two Important findings, and fix round 1 addressed all three. Scoped re-review approved with no new Critical/Important breakage. Focused suite passed 6/6; Indexing 216/216; Storage 142/142. One parallel integration fixture failure passed in isolation and was not changed.
- Task 4: completed — implementation `59c03a7` plus review fix rounds 1–2; current focused query/provenance tests passed 32/32, indexing 235/235, storage 142/142, core 127/127, full solution 1,294/1,294, and isolated IntegrationTests 161/161. Fix rounds cover game-scope federation isolation, location-backed multi-assembly source provenance, bounded document reads, codebase-derived origins, cross-origin/dedup/collection coverage, and reparse-point safety. The known intermittent ManagedToolCli parallel-suite flake is documented in `task-4-report.md`. No CLI, MCP, or docs-surface code was added.
- Task 5: completed — implementation `e7fb606`, review fix `55efc6e`; initial review found three Important findings and fix round 1 addressed all three. Scoped re-review approved with no new Critical/Important breakage. Focused CLI passed 19/19; Integration 180/180; serial full solution 1,313/1,313; build clean. Four formatter diagnostics remain pre-existing in `ReferenceModFileSelector.cs`, outside the Task 5 diff.
- Task 6: completed — read-only MCP reference collection listing and scoped
  symbol/source/relationship queries are implemented; public usage docs and
  the S1Atlas skill document the local-only boundary and AT-24/AT-25/AT-26
  orthogonality. Focused MCP contract tests passed 7/7 and the full solution
  passed 1,318/1,318. Detailed report:
  `.superpowers/sdd/2026-08-27-reference-mod-indexing/task-6-report.md`.

## Task 3 review checkpoint

- Critical finding addressed: normal completed Schedule I indexes without an environment-snapshot link are now loadable; the caller-validated build ID remains the provenance input for unlinked snapshots, while linked snapshots retain repository build validation.
- Important finding addressed: declared manifest `ContentSha256` participates in collection identity.
- Important finding addressed: drift coverage computes the production identity and asserts staging cleanup plus no completed result for that actual run ID.
- Review verdict: approved by Socrates after comparing `2e8af6a88729948440dd68df830d169b79d44f7..17686f0` in the Task 3 worktree.

## Task 4 review checkpoint

- Scope isolation: Game rejects `ReferenceCollection`; reference relationships are federated only for All.
- Provenance: multi-assembly symbols/relationships use only exact persisted locations; missing locations remain unavailable rather than guessing.
- Resource safety: completed-reference document queries apply SQL limits before content materialization; reference roots use ancestor reparse-point validation.
- Origin correctness: Schedule I is `game`; S1API/S1MAPI results retain nullable non-game origin.
- Review verdict: approved by Zeno after review fix rounds `a27046d` and `ac69e3c`, with only Minor source-location preload and test-strength observations.

## Task 5 review checkpoint

- CLI collection commands, scope/collection routing, offline indexing, and JSON path hygiene are covered by real integration tests.
- Hash timing now reports the measured combined pre/post passes; collection provenance is canonical across name and index-ID selectors.
- Review verdict: approved by Peirce after fix round `55efc6e`; no Critical/Important issues remain. The four whole-solution format diagnostics are pre-existing and outside this diff.

## Task 5 status

- Task 5: implemented in the current worktree; CLI integration GREEN is 16/16. A fresh full solution run passed 1,310 tests: Core 127, Docs 8, Indexing 235, Storage 142, Extraction 551, Integration 177, and MCP 70.
- RED evidence: the initial `ReferenceModCliTests` run before CLI implementation failed 9/15 tests for the missing reference command, scope/collection options, and output behavior; six pre-existing contract checks passed.
- GREEN evidence: the focused reference CLI suite passed 16/16; affected Indexing 235/235, Storage 142/142, and Core 127/127 also passed. Scoped format verification and `git diff --check` passed.
- Contract ruling: `type` and `method` remain game/API-only convenience commands per the design/current contract; `callable` remains Schedule-I-only. Scope/collection is exposed only on search, source, callers, callees, and refs.
- Whole-solution `dotnet format --verify-no-changes` reports four pre-existing whitespace diagnostics in Task 2's `ReferenceModFileSelector.cs` lines 44–47; no unrelated format changes were made.
- Existing code-map check reports nine generator-version mismatches; no unrelated code-map regeneration was included.
- Detailed report: `.superpowers/sdd/2026-08-27-reference-mod-indexing/task-5-report.md`.

### Task 5 review fix round 1

- Peirce Important 1 fixed: reference workflow returns measured combined initial/post-read hash duration; CLI JSON and human output include `InputHashMilliseconds`/`hash ...ms` without affecting other workflow callers.
- Peirce Important 2 fixed: reference query provenance now uses the completed snapshot's persisted source identity for both collection-name and index-ID selectors; regression coverage asserts identical canonical provenance.
- Peirce Important 3 fixed: real CLI integration coverage now exercises successful `reference` and `all` source/callers/callees/refs routes with source and endpoint provenance assertions.
- RED: new review regression set failed 4/19 before fixes; GREEN focused suite passed 19/19, Integration 180/180, Indexing 235/235, and serial full solution 1,313/1,313. Build passed 0/0 warnings/errors; changed-file format and diff checks passed.
- Whole-solution format retains four pre-existing whitespace diagnostics; parallel-only extraction flakes passed isolated serial reruns. No push.
