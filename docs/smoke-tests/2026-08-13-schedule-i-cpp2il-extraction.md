# Schedule I Cpp2IL Extraction Smoke — 2026-08-13

A real Windows smoke of the Phase 5 retry/replay gate against the operator's live
Schedule I installation and local Atlas data. It records only content-address hashes,
identifiers, and aggregate outcomes — no proprietary bytes, decompiled source,
absolute local paths, full file inventories, or symbol listings.

## Environment

| Fact | Value |
|---|---|
| Branch / tested commit | `feature/cpp2il-phase5-hardening-replay-finalization` @ `a94afed` |
| OS | Windows 11 (10.0.26200) |
| Toolchain | .NET 8 SDK, Release configuration |
| Content-derived build ID | `6fbd38f8401afa2241a1322afd4b8a8eadc99aa1f1c660ece253da7859d54bdc` |
| Executable version | `2022.3.62.7762112` |
| Steam app / build ID | `3164500` / `24587055` |
| Managed Cpp2IL pin | `2022.1.0-pre-release.21` (win-x64), status `Verified`, trust `ManagedPinned` |
| Pin definition digest | `d7e355850f8f435c9d75bb7edcc127b62b4a134de893db185a129f1166d066d6` |
| Pin package / executable SHA-256 | `663fb432433b4371fd1ee0ebc321a8fff2a9aac5ac4230c843f9e03ddee4e04c` |
| Extraction recipe ID | `d3f4e552785dce9ed3f81ac49d2475882ac0ab0781945ddd86cc7c2c535f6d2a` |

The managed pin's executable SHA-256 equals the committed pin, and both capability
probes (`help`, `output-formats`) succeeded, so the run used the exact reviewed pin.

## Automated gate (pre-smoke)

- `dotnet build S1Atlas.sln --configuration Release`: 0 warnings, 0 errors.
- `dotnet test S1Atlas.sln --configuration Release`: **805 passed, 0 failed, 0 skipped**
  (Core 107, Storage 91, Extraction 513, Integration 94).
- `dotnet format S1Atlas.sln --verify-no-changes`: clean.
- `scripts/verify-repository-hygiene.ps1`: passed — no proprietary or generated path is
  tracked.
- `git diff --check`: clean.

## Real extraction runs

All `extract`/`extractions` commands ran fully offline. Every process-backed run
re-verified the authoritative extraction inputs before and after execution; none
reported `InputChangedDuringExtraction`, so `GameAssembly.dll` and
`global-metadata.dat` remained stable across the smoke.

### Baseline

`extractions list --json` reported one validated extraction, preferred:

| Field | Value |
|---|---|
| Extraction ID | `1950abaf5cac2ab4e54436193efa4030c1cd887e431ff18893056c54e3850fbc` |
| Validation outcome | `Valid` |
| Preferred | yes |

`extractions cleanup --json` (preview): 0 eligible, 0 blocked, cutoff
`2026-07-15T00:29:39Z`. No files were deleted.

### Live retry — `extract --snapshot-inputs --retry --json`

| Field | Value |
|---|---|
| Attempt ID | `f77c55ea8cd2461a9636ed17cb81947e` |
| Extraction ID | `41027a0230a63f1dc4d277f1bd26a0a3c5b10b2586fc10d89b21f8f894aa6e7c` |
| Validation outcome | `ValidWithWarnings` |
| Process ran / validation ran / authoritative | yes / yes / yes |
| Input source | `Live` |
| Created input snapshot | `ced0d4f5f15a6c6aa14a223191c6dd77891f7ba5a03e3c3e7e1817fda59b57e1` |
| Snapshot replay-verified | **no** |
| Auto-preferred | no |

The live retry ran a real Cpp2IL process, archived the verified inputs into a new
snapshot, and correctly left that snapshot `replay_verified = false` (a live-input run
never certifies a snapshot).

### Archived-only certified replay — `extract --build <id> --input-snapshot ced0d4f5… --retry --json`

An earlier invocation with a placeholder value returned exit `1` /
`InvalidInputSnapshot`, confirming the 64-hex grammar guard. The real run:

| Field | Value |
|---|---|
| Attempt ID | `fe67125d09684c87bc807baa74504245` |
| Extraction ID | `1df0ee4edeebdbeefbf037aa729121ae8b425b41d89693edb2b31c2959602576` |
| Validation outcome | `ValidWithWarnings` |
| Process ran / validation ran / authoritative | yes / yes / yes |
| Input source | `ArchivedSnapshot` |
| Input snapshot | `ced0d4f5f15a6c6aa14a223191c6dd77891f7ba5a03e3c3e7e1817fda59b57e1` |
| Snapshot replay-verified | **yes** |
| Auto-preferred | no |

Cpp2IL ran from the snapshot's contained `game-root`, produced an authoritative
validated extraction, and the snapshot was certified `replay_verified = 1`. This is
the primary Phase 5 replay-certification path, proven on real data.

### Normal no-op — `extract --json`

| Field | Value |
|---|---|
| Extraction ID | `1950abaf5cac2ab4e54436193efa4030c1cd887e431ff18893056c54e3850fbc` |
| Process ran / validation ran | no / no |
| Reused existing extraction | yes |
| Authoritative / preferred | yes / yes |

The normal run reused the original preferred extraction with no process and no
validation, confirming that neither retry disturbed the preferred output.

## Key finding: equivalent runs produced byte-divergent output

The baseline, live retry, and archived replay share one recipe
(`d3f4e552…`) yet produced three different extraction IDs
(`1950abaf…`, `41027a02…`, `1df0ee4e…`). Because an extraction ID is
`recipe ID + artifact-manifest digest` over `(normalized path, byte size, SHA-256)`,
the different IDs prove that reconstructed assembly bytes differed between runs. The
live retry and archived replay used byte-identical authoritative inputs, so stock
Cpp2IL was not byte-reproducible under the conditions observed in this smoke.

Fresh module version IDs (MVIDs) are a plausible and likely contributor, but this
smoke did not normalize the assemblies or compare every metadata record and method
body. It therefore does not establish that MVIDs were the only difference, and it
does not establish byte-level or behavioral equivalence among the outputs.

Exact identical-output deduplication could not be demonstrated in the observed real
runs. This is accepted as a review-approved Phase 5 limitation rather than hidden by
rewriting or normalizing the reconstructed artifacts. Raw artifact hashes remain the
authoritative provenance and integrity identity.

What the smoke did prove is the designed safety behavior: each divergent same-recipe
run was preserved as a distinct `ValidWithWarnings` extraction, was not automatically
preferred, and left the current preferred extraction untouched and reusable. Each
output passed structural validation, but that result is not proof that method bodies
or eventual decompiled source are identical.

The accurate Phase 5 guarantee is:

> A same-recipe re-run that produces different bytes is preserved as a distinct,
> warning-bearing extraction, is never automatically preferred, and never silently
> replaces the trusted preferred output.

The next ILSpy/source-index milestone should retain raw SHA-256 identity for exact
provenance while adding separate comparison signals, potentially including normalized
metadata fingerprints that exclude reviewed volatile fields, per-assembly structural
fingerprints, and per-symbol or method-body fingerprints.

## Cleanup on the real root

Only `extractions cleanup` preview was run against the real Atlas data (no `--apply`
against real history, by design). It reported nothing eligible and nothing blocked.
Apply behavior on disposable synthetic data is covered by the automated
`Phase5CleanupCliTests` integration tests.

## Definition-of-Done status

| Item | Status |
|---|---|
| Release build 0 warnings / 0 errors | met |
| All tests 0 failures / 0 skips | met (805) |
| Real live `--retry` validated | met |
| Real archived-only `--retry` certified snapshot | met (`replay_verified = 1`) |
| Identical output deduplicated | not demonstrated — review-approved limitation; divergent-output preservation proved instead |
| Normal extract process-free no-op | met |
| Preferred extraction remains integrity verified | met (reused authoritatively) |
| Authoritative extraction inputs unchanged | met (pre/post hashes passed) |
| Full installation inventory comparison | not captured — review-approved limitation |
| Cleanup preview safe; apply proven on disposable data | met |
| Repository hygiene / privacy gates pass | met |
| No proprietary / generated files tracked | met |

## Review-approved limitations

- Per-run wall-clock durations were not separately recorded; the JSON envelope carries
  no timing field.
- A standalone before/after full game-file inventory digest was not captured. The
  pipeline proved that the authoritative extraction inputs remained unchanged, but
  this does not establish that every other file in the installation was unchanged.
  Another Cpp2IL run is not required solely to recreate this missing observation.
- Exact same-recipe byte deduplication was not demonstrated because the observed runs
  produced different artifact bytes. The system's warning, non-preference, and
  preservation behavior was demonstrated instead.
- The smoke created two non-preferred `ValidWithWarnings` extractions
  (`41027a02…`, `1df0ee4e…`); cleanup deliberately never removes validated
  extractions, so they remain as historical evidence until a separately designed
  validated-extraction deletion feature exists.

## Next milestone

Phase 5 closes the validated Cpp2IL extraction milestone with the review-approved
limitations above recorded explicitly. The next design cycle adds ILSpy decompilation,
normalized source/symbol metadata, and initial search/type/method/source commands over
the preferred, integrity-verified extraction — always through the full
integrity-verifying API.

Raw artifact SHA-256 identity remains the source of truth for provenance and exact
integrity. Any future normalized metadata or source fingerprints will be separate
comparison layers designed and reviewed in that next milestone.
