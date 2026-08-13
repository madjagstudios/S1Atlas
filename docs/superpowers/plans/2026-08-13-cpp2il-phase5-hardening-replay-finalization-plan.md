# Cpp2IL Phase 5 Hardening, Replay, and Milestone Finalization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish the validated Cpp2IL extraction milestone with conservative cleanup and retention, explicit archived-input replay certification, final recovery/privacy hardening, real retry/deduplication proof, and a committed non-proprietary smoke report.

**Architecture:** Phase 5 does not alter production extraction identity, validation policy, or the authoritative-output boundary completed in Phase 4. It adds a read-first cleanup planner and a separately invoked apply service that can delete only proven Atlas-owned, age-eligible failure/staging/quarantine data. It also adds an explicit snapshot-only retry path that runs Cpp2IL from an immutable archived `game-root`, marks that exact snapshot replay-verified only after an authoritative process-backed result, and keeps normal extraction reuse unchanged. Repository hygiene becomes an executable CI gate, and the milestone closes with real Windows retry/replay evidence and a source-safe report.

**Tech Stack:** C# / .NET 8, Microsoft.Data.Sqlite, System.CommandLine, `System.Text.Json`, SHA-256 canonical fingerprints, PowerShell 7, xUnit v3, Windows GitHub Actions, the existing pinned Cpp2IL executable and validated-extraction pipeline.

## Global Constraints

- Start from merged Phase 4 `main` commit `d9e889c628ecfa912a778d2afe84502f40f2ea6b`.
- Preserve migrations 1–5 byte-for-byte. Phase 5 adds no schema migration.
- Preserve the production Cpp2IL pin, extraction profile, and `managed-assemblies-v1` validation policy exactly.
- If the real archived-only smoke proves the committed snapshot input set is insufficient for Cpp2IL, stop and open a separate reviewed design/profile change; Phase 5 must not silently broaden archived inputs.
- Preserve production identity rules:
  - `recipe_id` excludes validation policy and input source;
  - artifact identity is normalized relative path + byte size + SHA-256;
  - `extraction_id` is recipe ID + artifact-manifest digest;
  - archived replay of byte-identical output links to the existing extraction rather than copying reconstructed assemblies.
- Preserve the authoritative-output boundary: only a preferred or explicitly selected validated extraction whose SQLite row, artifact rows, strict manifests, `complete.marker`, and current hashes agree may feed future ILSpy work.
- Cleanup is preview-only unless `--apply` is present.
- Cleanup defaults to `30d` and interprets “older than” strictly: an item is eligible only when its controlling timestamp is earlier than the calculated cutoff.
- Cleanup may remove only:
  - `Failed`, `Canceled`, or `Abandoned` attempts older than the cutoff;
  - those attempts’ bounded logs, attempt/validation documents, and `retained-output`;
  - recoverably stale Atlas-owned extraction/input/tool staging paths older than the cutoff;
  - quarantined replaced managed-tool installations older than the cutoff.
- Cleanup must never remove:
  - `ProcessCompleted` candidates;
  - `Succeeded` attempts or any attempt referenced by a validated extraction;
  - validated extraction directories, manifests, markers, artifact rows, preference state, or preference audit history;
  - input snapshots, whether replay-verified or not;
  - the current verified managed-tool installation;
  - active or ambiguous evidence;
  - Phase 4 validated-extraction quarantine.
- Never follow symbolic links, junctions, mount points, or Windows reparse points during cleanup, replay verification, recovery, inventory, or deletion.
- Cleanup apply re-observes every eligible tree immediately before deletion. A changed candidate is not deleted.
- Cleanup filesystem deletion precedes the matching terminal-attempt database deletion. If database deletion fails after files are removed, the remaining row is retained for an idempotent retry; database-first deletion is forbidden.
- Blocked or ambiguous cleanup items remain untouched. Safe eligible items may still be applied, but `--apply` exits `1` when any blocked item or deletion failure remains.
- `--input-snapshot <id>` is the explicit archived-only execution selector. It:
  - requires `--retry`;
  - is mutually exclusive with `--game-path` and `--snapshot-inputs`;
  - resolves exactly one stored snapshot and never falls back to live input;
  - may select an unverified snapshot solely for certification;
  - marks the snapshot replay-verified only after Cpp2IL ran from that snapshot and Phase 4 returned an authoritative validated extraction.
- Implicit historical input resolution continues to use only `replay_verified = 1` snapshots.
- An archived snapshot’s Cpp2IL root is `<snapshot-root>\game-root`, not the snapshot document root.
- A failed process, failed input check, invalid validation outcome, canceled run, or database certification failure cannot mark a snapshot replay-verified.
- Replay certification is idempotent and preserves the timestamp of the first successful certification.
- Snapshot certification is monotonic mutable database state. Recreating identical snapshot bytes may never conflict with or downgrade an already certified snapshot.
- `extract`, `extractions ...`, and cleanup remain offline. Only `tools install cpp2il` may access the network.
- Automated tests use temporary roots, generated bytes, the existing source-built fake Cpp2IL, and injected seams. CI never uses Schedule I files and never downloads Cpp2IL.
- No executable, game binary, reconstructed DLL, SQLite file, backup, input snapshot, attempt document, manifest, marker, log, candidate, retained output, or decompiled source may enter Git or a CI artifact.
- Preserve human/JSON envelope schema version 1 and exit codes `0` success, `1` operational/validation/cleanup failure, `2` cancellation.
- Keep Phase 5 implementation commits green. Do not push intentionally failing or non-compiling TDD checkpoints.

## Cross-Task Invariants

```text
Cleanup planning is read-only.
Cleanup apply always creates a fresh plan after repository initialization and recovery.
The preview output and apply output use the same classification rules.
A cleanup path is eligible only when both database facts and filesystem ownership agree.
Unknown names, changed observations, reparse points, live locks, promotion journals,
candidate-output on a terminal failure, and any complete.marker in staging block deletion.
Extraction staging without a matching database attempt is ambiguous and is never deleted.
No cleanup operation enumerates inside a validated extraction or input snapshot root.
ProcessCompleted candidates remain resumable and are never cleanup candidates.
Succeeded attempts remain immutable provenance and are never cleanup candidates.
InputSnapshot replay_verified is database certification state; snapshot files remain immutable.
Explicit snapshot certification always runs Cpp2IL and never silently reuses output.
Certification may link to an existing extraction, but it must still prove process + validation.
The normal no-op path remains process-free and validation-free after certification.
Repository-hygiene checks inspect tracked paths, not documentation text.
The final smoke report contains complete hashes and aggregate counts, but no proprietary bytes,
decompiled source, or long symbol listings.
```

---

## Phase 5 Scope Boundary

Phase 5 delivers:

```text
extractions cleanup [--older-than <duration>] [--apply] [--json]
30d default retention threshold
strict d/h/m duration parsing
read-only cleanup preview
safe terminal-attempt deletion
stale extraction/input/tool staging cleanup
old managed-tool quarantine cleanup
cleanup/recovery convergence and active-lock refusal
explicit --input-snapshot <snapshot-id> --retry
archived snapshot byte/manifest verification
archived game-root path correction
per-snapshot replay certification
implicit use of replay-verified snapshots
real live retry and identical-output deduplication proof
real archived-only replay proof
repository hygiene script and CI gate
CI format verification
single PR-triggered feature-branch CI run
non-proprietary committed smoke report
final extraction-milestone documentation
```

Phase 5 explicitly does not deliver:

```text
validated extraction deletion
input snapshot deletion
preferred extraction deletion
automatic age deletion without --apply
background cleanup service
scheduled tasks
database vacuum/compaction
new Cpp2IL pin or extraction profile
validation-policy changes
ILSpy decompilation
C# source generation
symbol/call indexing
source/build diffing
HTML portal
MCP
agent skill
```

---

## File Structure

### `S1Atlas.Core`

```text
src/S1Atlas.Core/Extraction/ExtractionCleanupModels.cs
src/S1Atlas.Core/Extraction/ExtractionWorkflowResult.cs
src/S1Atlas.Core/Storage/IExtractionRepository.cs
src/S1Atlas.Core/Storage/IValidatedExtractionRepository.cs
```

`ExtractionCleanupModels.cs` owns stable cleanup projections only. Filesystem-only observation details remain internal to `S1Atlas.Extraction`.

### `S1Atlas.Extraction`

```text
src/S1Atlas.Extraction/Cleanup/CleanupTreeInspector.cs
src/S1Atlas.Extraction/Cleanup/CleanupCandidate.cs
src/S1Atlas.Extraction/Cleanup/ExtractionCleanupPlanner.cs
src/S1Atlas.Extraction/Cleanup/ExtractionCleanupService.cs
src/S1Atlas.Extraction/Inputs/InputSnapshotService.cs
src/S1Atlas.Extraction/Inputs/InputSnapshotDocumentStore.cs
src/S1Atlas.Extraction/Inputs/ExtractionInputResolver.cs
src/S1Atlas.Extraction/ExtractionOrchestrator.cs
src/S1Atlas.Extraction/ValidatedExtractionWorkflow.cs
src/S1Atlas.Extraction/Attempts/ExtractionRecoveryService.cs
src/S1Atlas.Extraction/Tools/ToolPathPolicy.cs
```

### `S1Atlas.Storage`

```text
src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Extractions.cs
src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.ValidatedExtractions.cs
```

No migration file is modified.

### `S1Atlas.Cli`

```text
src/S1Atlas.Cli/Commands/CleanupDurationParser.cs
src/S1Atlas.Cli/Commands/ExtractionsCommand.cs
src/S1Atlas.Cli/Commands/ExtractionsCleanupCommand.cs
src/S1Atlas.Cli/Commands/ExtractCommand.cs
src/S1Atlas.Cli/Configuration/AtlasPaths.cs
src/S1Atlas.Cli/Output/ExtractionCleanupOutputModels.cs
src/S1Atlas.Cli/Output/ExtractionOutputModels.cs
src/S1Atlas.Cli/CliApplication.cs
```

### Repository/CI documentation

```text
.gitignore
.github/workflows/ci.yml
scripts/verify-repository-hygiene.ps1
README.md
docs/smoke-tests/2026-08-13-schedule-i-cpp2il-extraction.md
```

### Tests

```text
tests/S1Atlas.Core.Tests/Extraction/ExtractionCleanupModelsTests.cs

tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryCleanupTests.cs
tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryInputSnapshotReplayTests.cs

tests/S1Atlas.Extraction.Tests/Cleanup/CleanupTreeInspectorTests.cs
tests/S1Atlas.Extraction.Tests/Cleanup/ExtractionCleanupPlannerTests.cs
tests/S1Atlas.Extraction.Tests/Cleanup/ExtractionCleanupServiceTests.cs
tests/S1Atlas.Extraction.Tests/Inputs/ExtractionInputResolverTests.cs
tests/S1Atlas.Extraction.Tests/Inputs/InputSnapshotServiceTests.cs
tests/S1Atlas.Extraction.Tests/ValidatedExtractionWorkflowTests.cs
tests/S1Atlas.Extraction.Tests/ExtractionOrchestratorTests.cs
tests/S1Atlas.Extraction.Tests/Attempts/ExtractionRecoveryServiceTests.cs

tests/S1Atlas.IntegrationTests/Extraction/Phase5CleanupCliTests.cs
tests/S1Atlas.IntegrationTests/Extraction/Phase5ArchivedReplayCliTests.cs
tests/S1Atlas.IntegrationTests/Repository/RepositoryHygieneScriptTests.cs
```

---

### Task 1: Add Cleanup and Replay Output Contracts

**Files:**
- Create: `src/S1Atlas.Core/Extraction/ExtractionCleanupModels.cs`
- Modify: `src/S1Atlas.Core/Extraction/ExtractionWorkflowResult.cs`
- Test: `tests/S1Atlas.Core.Tests/Extraction/ExtractionCleanupModelsTests.cs`

**Interfaces:**

```csharp
public enum ExtractionCleanupItemKind
{
    TerminalAttempt,
    ExtractionStaging,
    InputStaging,
    ToolStaging,
    ToolQuarantine
}

public sealed record ExtractionCleanupItem(
    ExtractionCleanupItemKind Kind,
    string Id,
    string? BuildId,
    string? AttemptId,
    string DisplayPath,
    DateTimeOffset ControllingTimestampUtc,
    int FileCount,
    long ByteCount);

public sealed record ExtractionCleanupBlockedItem(
    ExtractionCleanupItemKind Kind,
    string Id,
    string DisplayPath,
    string Code,
    string Message);

public sealed record ExtractionCleanupPlan(
    TimeSpan OlderThan,
    DateTimeOffset CutoffUtc,
    IReadOnlyList<ExtractionCleanupItem> EligibleItems,
    IReadOnlyList<ExtractionCleanupBlockedItem> BlockedItems)
{
    public int EligibleFileCount =>
        EligibleItems.Sum(item => item.FileCount);

    public long EligibleByteCount =>
        EligibleItems.Sum(item => item.ByteCount);
}

public sealed record ExtractionCleanupFailure(
    ExtractionCleanupItemKind Kind,
    string Id,
    string Code,
    string Message);

public sealed record ExtractionCleanupResult(
    ExtractionCleanupPlan Plan,
    bool Applied,
    IReadOnlyList<ExtractionCleanupItem> DeletedItems,
    IReadOnlyList<ExtractionCleanupFailure> Failures)
{
    public bool HasOperationalProblems =>
        Plan.BlockedItems.Count > 0 || Failures.Count > 0;
}
```

Extend `ExtractionWorkflowResult` with trailing optional facts:

```csharp
ExtractionInputSource? InputSource = null,
string? InputSnapshotId = null,
bool InputSnapshotReplayVerified = false
```

Repository capabilities are added with their concrete SQLite implementations in Task 2 so every committed task remains buildable and green.

- [ ] **Step 1: Write failing cleanup-model invariant tests**

Prove aggregate counts include only eligible items and that `HasOperationalProblems` is false only when both blocked and failure collections are empty. Also prove all public collections are non-null and preserve deterministic caller order.

- [ ] **Step 2: Run the focused Core tests and confirm RED**

```powershell
dotnet test tests\S1Atlas.Core.Tests\S1Atlas.Core.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~ExtractionCleanupModelsTests"
```

Expected: compile failure because the cleanup records do not yet exist.

- [ ] **Step 3: Implement the exact cleanup records**

Create `ExtractionCleanupModels.cs` with the signatures above. Keep path ownership/fingerprint fields out of Core; those are internal implementation details.

Append `InputSource`, `InputSnapshotId`, and `InputSnapshotReplayVerified` to `ExtractionWorkflowResult` as trailing optional parameters so existing positional construction remains source-compatible. Process-backed paths populate them in Task 5; no-op/revalidation paths may leave them null/false.

- [ ] **Step 4: Run Core and solution builds**

```powershell
dotnet test tests\S1Atlas.Core.Tests\S1Atlas.Core.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~ExtractionCleanupModelsTests"

dotnet build S1Atlas.sln --configuration Release
```

Expected: focused tests and the solution build pass with zero warnings.

- [ ] **Step 5: Commit Task 1**

```powershell
git add `
  src/S1Atlas.Core/Extraction/ExtractionCleanupModels.cs `
  src/S1Atlas.Core/Extraction/ExtractionWorkflowResult.cs `
  tests/S1Atlas.Core.Tests/Extraction/ExtractionCleanupModelsTests.cs

git commit -m "feat: define cleanup and replay contracts"
```

---

### Task 2: Add Transactional Attempt Deletion and Snapshot Certification Storage

**Files:**
- Modify: `src/S1Atlas.Core/Storage/IExtractionRepository.cs`
- Modify: `src/S1Atlas.Core/Storage/IValidatedExtractionRepository.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Extractions.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.ValidatedExtractions.cs`
- Modify: `src/S1Atlas.Extraction/Inputs/InputSnapshotService.cs`
- Test: `tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryCleanupTests.cs`
- Test: `tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryInputSnapshotReplayTests.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Inputs/InputSnapshotServiceTests.cs`
- Verify unchanged: `src/S1Atlas.Storage/Migrations/SqliteMigrations.cs`

**Interfaces:**

```csharp
Task<InputSnapshot?> GetInputSnapshotAsync(
    string inputSnapshotId,
    CancellationToken cancellationToken);

Task MarkInputSnapshotReplayVerifiedAsync(
    string inputSnapshotId,
    string expectedBuildId,
    string expectedManifestDigest,
    DateTimeOffset verifiedAtUtc,
    CancellationToken cancellationToken);

Task DeleteCleanupEligibleAttemptAsync(
    string attemptId,
    ExtractionAttemptStatus expectedStatus,
    DateTimeOffset expectedCompletedAtUtc,
    CancellationToken cancellationToken);
```

The SQLite repository and all test fakes are updated in this same task so the commit remains green.

- [ ] **Step 1: Add signatures and failing snapshot lookup/certification tests**

Cover:

```text
GetInputSnapshotAsync returns verified and unverified rows with complete manifest files.
Unknown snapshot ID returns null.
MarkInputSnapshotReplayVerifiedAsync changes only replay_verified/replay_verified_at_utc.
The first successful certification timestamp is preserved on repeated calls.
Build-ID mismatch rejects without mutation.
Manifest-digest mismatch rejects without mutation.
Unknown snapshot rejects without mutation.
Cancellation rolls back.
Saving the same immutable snapshot after certification is a no-op, not a conflict.
Saving the same immutable snapshot can never downgrade replay_verified or its timestamp.
A genuine immutable-fact mismatch still rejects without mutation.
```

Use a fixed snapshot with four profile files and assert the full manifest round-trips. Replay certification is deliberately excluded from the immutable duplicate-snapshot comparison.

- [ ] **Step 2: Run snapshot storage tests and confirm RED**

```powershell
dotnet test tests\S1Atlas.Storage.Tests\S1Atlas.Storage.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~SqliteAtlasRepositoryInputSnapshotReplayTests"
```

- [ ] **Step 3: Implement `GetInputSnapshotAsync`**

Reuse the existing private materialization logic. The public method must require a nonempty ID, return `null` when absent, order manifest files deterministically, and preserve persisted replay fields.

- [ ] **Step 4: Implement idempotent replay certification**

Use one SQLite transaction:

```text
SELECT snapshot header by ID
if absent -> InvalidOperationException
if build or manifest differs -> InvalidOperationException
if replay_verified = 1 -> commit no-op and preserve original timestamp
otherwise:
  UPDATE input_snapshots
  SET replay_verified = 1,
      replay_verified_at_utc = requested UTC timestamp
  WHERE input_snapshot_id, build_id, manifest_digest and replay_verified = 0
require exactly one row
commit
```

Never modify `input_snapshot_files`, `created_at_utc`, `root_path`, or `manifest_digest`.

- [ ] **Step 5: Preserve replay certification during repeated snapshot creation**

Adjust `SaveInputSnapshotAsync` and `InputSnapshotService.CreateAsync`:

```text
when the snapshot ID already exists:
  compare only immutable snapshot facts:
    input_snapshot_id
    build_id
    root_path
    manifest_digest
    created_at_utc
    normalized manifest entries
  ignore incoming replay flags during the immutable comparison
  never write replay_verified = 0 over replay_verified = 1
  never replace replay_verified_at_utc
after SaveInputSnapshotAsync:
  reload the canonical persisted snapshot
  return that persisted record from InputSnapshotService
```

Add `CreateAsync_IdenticalCertifiedSnapshot_PreservesCertification` proving the first certification timestamp remains unchanged.

- [ ] **Step 6: Write failing cleanup-deletion tests**

Create old `Failed`, `Canceled`, and `Abandoned` attempts, plus `ProcessCompleted`, `Succeeded`, source attempts referenced by validated extractions, attempts with validation rows/issues, and expected-timestamp mismatches.

Prove allowed terminal attempts delete validation rows, issues, and the attempt atomically; protected states/references reject; and a forced failure after validation-row deletion rolls back every row.

- [ ] **Step 7: Implement `DeleteCleanupEligibleAttemptAsync`**

Within one transaction:

```text
load current attempt
require status in Failed/Canceled/Abandoned
require current status == expected status
require completed_at_utc == expected completed timestamp
require result_extraction_id IS NULL
require no validated_extractions.source_attempt_id reference
DELETE extraction_validation_results WHERE attempt_id = ...
DELETE extraction_validation_issues WHERE attempt_id = ...
DELETE extraction_attempts WHERE attempt_id = ... AND status = ... AND completed_at_utc = ...
require one deleted attempt row
commit
```

- [ ] **Step 8: Verify migrations are untouched**

```powershell
git diff -- src/S1Atlas.Storage/Migrations/SqliteMigrations.cs
dotnet test tests\S1Atlas.Storage.Tests\S1Atlas.Storage.Tests.csproj --configuration Release
```

Expected: no migration diff; all Storage tests pass.

- [ ] **Step 9: Commit Task 2**

```powershell
git add `
  src/S1Atlas.Core/Storage/IExtractionRepository.cs `
  src/S1Atlas.Core/Storage/IValidatedExtractionRepository.cs `
  src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Extractions.cs `
  src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.ValidatedExtractions.cs `
  src/S1Atlas.Extraction/Inputs/InputSnapshotService.cs `
  tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryCleanupTests.cs `
  tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryInputSnapshotReplayTests.cs `
  tests/S1Atlas.Extraction.Tests/Inputs/InputSnapshotServiceTests.cs

git commit -m "feat: persist cleanup and replay certification"
```

---

### Task 3: Build a Read-Only, Fail-Closed Cleanup Planner

**Files:**
- Create: `src/S1Atlas.Extraction/Cleanup/CleanupTreeInspector.cs`
- Create: `src/S1Atlas.Extraction/Cleanup/CleanupCandidate.cs`
- Create: `src/S1Atlas.Extraction/Cleanup/ExtractionCleanupPlanner.cs`
- Modify: `src/S1Atlas.Extraction/Tools/ToolPathPolicy.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Cleanup/CleanupTreeInspectorTests.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Cleanup/ExtractionCleanupPlannerTests.cs`

**Interfaces:**

```csharp
internal sealed record CleanupTreeObservation(
    int FileCount,
    long ByteCount,
    DateTimeOffset NewestWriteUtc,
    string ObservationDigest);

internal sealed record CleanupCandidate(
    ExtractionCleanupItem PublicItem,
    IReadOnlyList<string> OwnedPaths,
    string ObservationDigest,
    ExtractionAttemptStatus? ExpectedAttemptStatus,
    DateTimeOffset? ExpectedCompletedAtUtc);

internal sealed record CleanupPlanningResult(
    ExtractionCleanupPlan PublicPlan,
    IReadOnlyList<CleanupCandidate> Candidates);

internal sealed class ExtractionCleanupPlanner
{
    public Task<CleanupPlanningResult> PlanAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write failing tree-inspector tests**

Cover:

```text
normal nested tree returns deterministic file count/bytes/newest-write/digest
empty directory uses root last-write time and a stable digest
owned regular-file root (possible tool quarantine) is inspected without traversal
entry order does not change digest
file size, last-write time, or relative path changes digest
case-insensitive duplicate paths block inspection
file or directory reparse point blocks inspection without following it
unreadable entry blocks inspection
overflow returns a structured blocked result
missing root returns an empty observation only when caller explicitly allows missing
```

The digest is SHA-256 over sorted entries containing entry kind (`root-file`, `root-directory`, `file`, or `directory`), normalized relative path (`.` for root), size (`0` for directories), and UTC last-write ticks. It is an observation fingerprint, not content identity, and never reads file bytes.

- [ ] **Step 2: Implement `CleanupTreeInspector`**

Use iterative enumeration and `File.GetAttributes`. Never use `SearchOption.AllDirectories`. Normalize paths to `/`, sort ordinally, reject Windows-case collisions, and include the root observation.

- [ ] **Step 3: Add exact staging/quarantine ownership recognizers**

Reuse `OwnedAttemptPaths.IsLowerGuidN` for extraction and input staging children, whose complete name is exactly a 32-character lower-hex GUID N value.

Add tool-specific helpers to `ToolPathPolicy`:

```csharp
internal static bool IsOwnedToolStagingEntryName(string name);
internal static bool TryGetQuarantineTimestampUtc(
    string name,
    out DateTimeOffset timestampUtc);
```

Tool staging requires a safe nonempty prefix followed by `-<32 lower-hex GUID N>`. Quarantine requires the installer’s exact suffix:

```text
-YYYYMMDDTHHMMSSfffZ-<32 lower-hex GUID N>
```

Parse from the fixed suffix because tool IDs/versions may contain hyphens.

- [ ] **Step 4: Write failing planner tests for terminal attempts**

Prove:

```text
Failed/Canceled/Abandoned completed before cutoff -> eligible
completed exactly at cutoff -> not eligible
completed after cutoff -> not eligible
ProcessCompleted -> never eligible
Succeeded -> never eligible
result_extraction_id present -> blocked
candidate-output present on a failure -> blocked
complete.marker or immutable extraction manifest under attempt evidence -> blocked
attempt path with reparse point -> blocked
missing attempt root -> eligible with zero filesystem bytes so DB cleanup can converge
```

A terminal-attempt candidate owns the exact attempt root and exact extraction staging root for that build/attempt ID. Calculate one canonical aggregate observation digest over sorted `(owned-path role, individual observation digest)` tuples. Aggregate file/byte counts and the newest controlling timestamp across both paths; apply must reproduce the same aggregate digest.

- [ ] **Step 5: Write failing planner tests for stale paths**

Scan only:

```text
builds/<64-lower-hex>/extractions/.staging
builds/<64-lower-hex>/inputs/.staging
tools/.staging
tools/quarantine
```

Prove:

```text
recognized direct child older than cutoff is eligible
newest write anywhere in the tree controls age
unknown child name is blocked
extraction staging with sibling .promotion.json is blocked
extraction staging without a matching database attempt is blocked
extraction staging associated with live/nonterminal/ProcessCompleted attempt is blocked
extraction or input staging containing complete.marker is blocked
input snapshot final directories are never enumerated
validated extraction final directories and Phase 4 quarantine are never enumerated
current managed-tool roots are never enumerated
tool quarantine age uses later of parsed timestamp and tree newest-write time
all reparse points are blocked
```

- [ ] **Step 6: Implement `ExtractionCleanupPlanner`**

Dependencies:

```csharp
string dataRoot
IValidatedExtractionRepository validatedRepository
TimeProvider timeProvider
CleanupTreeInspector treeInspector
```

Algorithm:

```text
validate positive olderThan
cutoff = current UTC - olderThan
list all attempts once and index by ID
classify terminal attempts
scan only the four approved staging/quarantine root shapes
require extraction-staging GUIDs to resolve to a matching database attempt
preserve staging containing complete markers or promotion journals
sort eligible/blocked output by kind then ID/path
return public plan + internal candidates
```

The planner performs no deletion and no database mutation.

- [ ] **Step 7: Verify planner tests repeatedly**

```powershell
1..10 | ForEach-Object {
    dotnet test tests\S1Atlas.Extraction.Tests\S1Atlas.Extraction.Tests.csproj `
      --configuration Release `
      --filter "FullyQualifiedName~CleanupTreeInspectorTests|FullyQualifiedName~ExtractionCleanupPlannerTests" `
      --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
```

- [ ] **Step 8: Commit Task 3**

```powershell
git add `
  src/S1Atlas.Extraction/Cleanup `
  src/S1Atlas.Extraction/Tools/ToolPathPolicy.cs `
  tests/S1Atlas.Extraction.Tests/Cleanup

git commit -m "feat: plan conservative extraction cleanup"
```

---

### Task 4: Apply Cleanup Safely and Expose the CLI

**Files:**
- Create: `src/S1Atlas.Extraction/Cleanup/ExtractionCleanupService.cs`
- Create: `src/S1Atlas.Cli/Commands/CleanupDurationParser.cs`
- Create: `src/S1Atlas.Cli/Commands/ExtractionsCleanupCommand.cs`
- Create: `src/S1Atlas.Cli/Output/ExtractionCleanupOutputModels.cs`
- Modify: `src/S1Atlas.Cli/Commands/ExtractionsCommand.cs`
- Modify: `src/S1Atlas.Cli/Configuration/AtlasPaths.cs`
- Modify: `src/S1Atlas.Cli/CliApplication.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Cleanup/ExtractionCleanupServiceTests.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Attempts/ExtractionRecoveryServiceTests.cs`
- Test: `tests/S1Atlas.IntegrationTests/Extraction/Phase5CleanupCliTests.cs`

**Interfaces:**

```csharp
internal sealed class ExtractionCleanupService
{
    public Task<ExtractionCleanupPlan> PreviewAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken);

    public Task<ExtractionCleanupResult> ApplyAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write failing duration parser tests**

Accept exactly positive lower-case integer values ending in `m`, `h`, or `d`; maximum `36500d`. Reject empty, zero, negative, decimal, `w`, missing unit, upper-case unit, whitespace, overflow, and values above the maximum. Omitted option means exactly `30d`.

- [ ] **Step 2: Implement `CleanupDurationParser`**

Use invariant integer parsing and checked `TimeSpan` construction. Error:

```text
The cleanup duration must be a positive lower-case integer followed by m, h, or d; maximum 36500d.
```

- [ ] **Step 3: Write failing cleanup-service tests**

Required behaviors:

```text
Preview initializes repository, runs Phase 4/generic recovery, and never deletes.
Apply creates a fresh plan after recovery.
Apply re-observes every candidate and compares the aggregate observation digest.
Changed candidate -> CleanupEvidenceChanged and preserved.
Live extraction lock blocks preview/apply before mutation.
Safe eligible items delete even when a separate blocked item remains.
Blocked items remain and make HasOperationalProblems true.
Terminal-attempt filesystem roots delete before repository row.
DB deletion failure leaves truthful failure and is idempotently retryable.
Cancellation stops future items and preserves ambiguous evidence.
Validated extraction, input snapshot, ProcessCompleted candidate, current tool remain unchanged.
Second apply after success is zero-item no-op.
```

Use injected delete delegates to prove ordering and partial-failure behavior.

- [ ] **Step 4: Implement cleanup apply**

Dependencies:

```csharp
Func<CancellationToken, Task> initializeRepositoryAsync
Func<CancellationToken, Task> recoverAsync
ExtractionCleanupPlanner planner
IValidatedExtractionRepository validatedRepository
CleanupTreeInspector treeInspector
```

Algorithm:

```text
initialize
recover
create fresh plan
preflight every eligible candidate by re-observing all owned paths
record changed/unsafe candidates as failures; do not delete them
for each unchanged candidate in deterministic order:
  delete owned entries bottom-up without following reparse points
  for TerminalAttempt only:
    DeleteCleanupEligibleAttemptAsync after filesystem deletion
collect deleted items/failures
return result
```

Do not call recursive `Directory.Delete` on an uninspected path.

- [ ] **Step 5: Add `extractions cleanup`**

```text
s1atlas extractions cleanup
s1atlas extractions cleanup --older-than 30d
s1atlas extractions cleanup --older-than 30d --apply
s1atlas extractions cleanup --json
```

Human preview reports cutoff, eligible counts by category, blocked count, estimated files/bytes, “No files were deleted,” and the `--apply` instruction. Human apply reports deleted counts/bytes plus blocked/failure summaries.

JSON data:

```json
{
  "applied": false,
  "olderThan": "30d",
  "cutoffUtc": "...",
  "eligibleFileCount": 0,
  "eligibleByteCount": 0,
  "eligibleItems": [],
  "blockedItems": [],
  "deletedItems": [],
  "failures": []
}
```

Preview exits `0` even with blocked evidence. Apply exits `0` only with no blocked/failure items, `1` when any remain, and `2` on cancellation.

- [ ] **Step 6: Wire composition**

`CliApplication` constructs planner/service from `_paths.RootDirectory`, `sqliteRepository`, `recoveryService`, and `TimeProvider`. `ExtractionsCommand.Create` receives the service and adds the fourth subcommand.

- [ ] **Step 7: Add integration tests**

Use temporary `S1ATLAS_HOME` and real SQLite. Prove default preview deletes nothing; `--apply` deletes only an old Failed attempt/retained output; newer failure, ProcessCompleted candidate, validated extraction, input snapshot, and current tool remain; old recognized tool quarantine deletes; unknown quarantine is blocked; JSON is one document; errors have no stack trace; no HTTP request occurs; second apply is idempotent.

- [ ] **Step 8: Run focused regressions**

```powershell
dotnet test tests\S1Atlas.Extraction.Tests\S1Atlas.Extraction.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~Cleanup|FullyQualifiedName~ExtractionRecoveryServiceTests"

dotnet test tests\S1Atlas.IntegrationTests\S1Atlas.IntegrationTests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~Phase5CleanupCliTests|FullyQualifiedName~Phase4ExtractionCliTests"
```

- [ ] **Step 9: Commit Task 4**

```powershell
git add `
  src/S1Atlas.Extraction/Cleanup/ExtractionCleanupService.cs `
  src/S1Atlas.Cli/Commands/CleanupDurationParser.cs `
  src/S1Atlas.Cli/Commands/ExtractionsCleanupCommand.cs `
  src/S1Atlas.Cli/Commands/ExtractionsCommand.cs `
  src/S1Atlas.Cli/Configuration/AtlasPaths.cs `
  src/S1Atlas.Cli/Output/ExtractionCleanupOutputModels.cs `
  src/S1Atlas.Cli/CliApplication.cs `
  tests/S1Atlas.Extraction.Tests/Cleanup/ExtractionCleanupServiceTests.cs `
  tests/S1Atlas.Extraction.Tests/Attempts/ExtractionRecoveryServiceTests.cs `
  tests/S1Atlas.IntegrationTests/Extraction/Phase5CleanupCliTests.cs

git commit -m "feat: expose safe extraction cleanup"
```

---

### Task 5: Add Explicit Archived-Only Retry and Replay Certification

**Files:**
- Modify: `src/S1Atlas.Extraction/Inputs/InputSnapshotDocumentStore.cs`
- Modify: `src/S1Atlas.Extraction/Inputs/ExtractionInputResolver.cs`
- Modify: `src/S1Atlas.Extraction/ExtractionOrchestrator.cs`
- Modify: `src/S1Atlas.Extraction/ValidatedExtractionWorkflow.cs`
- Modify: `src/S1Atlas.Core/Extraction/ExtractionWorkflowResult.cs`
- Modify: `src/S1Atlas.Cli/Commands/ExtractCommand.cs`
- Modify: `src/S1Atlas.Cli/Output/ExtractionOutputModels.cs`
- Modify: `src/S1Atlas.Cli/CliApplication.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Inputs/ExtractionInputResolverTests.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Inputs/InputSnapshotServiceTests.cs`
- Test: `tests/S1Atlas.Extraction.Tests/ExtractionOrchestratorTests.cs`
- Test: `tests/S1Atlas.Extraction.Tests/ValidatedExtractionWorkflowTests.cs`
- Test: `tests/S1Atlas.IntegrationTests/Extraction/Phase5ArchivedReplayCliTests.cs`

**Interfaces:**

Extend `ExtractionOptions` with trailing:

```csharp
string? InputSnapshotId
```

Change input resolution:

```csharp
Task<ResolvedExtractionInput> ResolveAsync(
    GameBuild build,
    string? explicitGamePath,
    string? explicitInputSnapshotId,
    ExtractionProfile profile,
    CancellationToken cancellationToken);
```

Add workflow dependency:

```csharp
Func<
    string,
    string,
    string,
    DateTimeOffset,
    CancellationToken,
    Task> markInputSnapshotReplayVerifiedAsync
```

- [ ] **Step 1: Write failing option-validation tests**

Prove `--input-snapshot` requires 64 lower-case hex and `--retry`; conflicts with `--game-path` and `--snapshot-inputs`; never permits network; existing options remain source-compatible; help lists the option/constraints.

- [ ] **Step 2: Write failing resolver tests**

```text
unknown explicit ID -> ArchivedInputInvalid, no live fallback
different build -> reject
DB/strict manifest mismatch -> reject
missing/changed/reparse file -> reject
unverified intact explicit snapshot -> allowed for certification
implicit resolution ignores unverified snapshots
replay-verified implicit snapshot selected only after live candidates fail
Cpp2IL root is <snapshot.RootPath>\game-root
explicit selection never probes stored live observations or Steam
```

Add the regression proving `GameAssembly.dll` lives below `game-root`, never directly below the snapshot document root.

- [ ] **Step 3: Expose strict snapshot byte verification**

Expose a contained `game-root` helper from `InputSnapshotDocumentStore`. Keep strict manifest/marker schema, exact path set, no-reparse, size/hash, build/snapshot/digest verification. Filesystem documents remain immutable and do not gain replay flags.

- [ ] **Step 4: Implement explicit and implicit archived resolution**

Explicit:

```text
load snapshot by exact ID
require selected build match
strictly verify documents/bytes
require DB manifest == strict manifest
construct input from <snapshot-root>\game-root
return ArchivedSnapshot + exact ID
never inspect live candidates
```

Implicit continues querying only `ListReplayVerifiedInputSnapshotsAsync`, strictly verifies each candidate, uses `game-root`, skips corrupt candidates, and reports `ArchivedInputInvalid` when certified evidence exists but none remains valid.

- [ ] **Step 5: Write failing workflow certification tests**

Prove explicit snapshot + retry always reaches process path even with reusable output; process receives archived source/ID; authoritative valid output certifies; byte-identical result links and still certifies; invalid/process failure/cancellation/tamper do not certify; certification DB failure reports operational failure while preserving the valid extraction; repeated certification preserves first timestamp; normal no-op remains process/validation-free.

- [ ] **Step 6: Implement certification after authoritative validation**

```text
run prepared Phase 3 process
validate/promote or link candidate
if result.IsAuthoritative:
  MarkInputSnapshotReplayVerifiedAsync(
      snapshot ID,
      build ID,
      process attempt pre-input manifest digest,
      current UTC)
  return result with InputSnapshotId and InputSnapshotReplayVerified=true
```

Do not certify from no-op, policy revalidation, existing candidate, or non-authoritative result. `--input-snapshot` requires `--retry`, so certification always has fresh process evidence.

- [ ] **Step 7: Add CLI output facts**

Human output adds input source, snapshot ID, and replay-verified status. JSON adds:

```json
{
  "inputSnapshotId": "...",
  "inputSnapshotReplayVerified": true,
  "inputSource": "ArchivedSnapshot"
}
```

Keep schema version 1. Every process-backed result propagates source/ID; certification sets the replay flag only after the DB commit succeeds.

- [ ] **Step 8: Add integration coverage**

Use temporary Atlas root, unverified snapshot, no live game, source-built fake Cpp2IL, rejecting HTTP.

First:

```text
extract --build <id> --input-snapshot <id> --retry --json
```

Must run process from archive, validate authoritatively, certify snapshot, and touch no live path.

Second:

```text
extract --build <id> --retry --json
```

With no explicit snapshot and no live candidate, `--retry` forces process execution and must implicitly select the now replay-verified archive. A third invocation without retry must reuse authoritative output without input resolution. Failed fake output leaves `replay_verified = 0`.

- [ ] **Step 9: Commit Task 5**

```powershell
git add `
  src/S1Atlas.Extraction/Inputs/InputSnapshotDocumentStore.cs `
  src/S1Atlas.Extraction/Inputs/ExtractionInputResolver.cs `
  src/S1Atlas.Extraction/ExtractionOrchestrator.cs `
  src/S1Atlas.Extraction/ValidatedExtractionWorkflow.cs `
  src/S1Atlas.Core/Extraction/ExtractionWorkflowResult.cs `
  src/S1Atlas.Cli/Commands/ExtractCommand.cs `
  src/S1Atlas.Cli/Output/ExtractionOutputModels.cs `
  src/S1Atlas.Cli/CliApplication.cs `
  tests/S1Atlas.Extraction.Tests/Inputs `
  tests/S1Atlas.Extraction.Tests/ExtractionOrchestratorTests.cs `
  tests/S1Atlas.Extraction.Tests/ValidatedExtractionWorkflowTests.cs `
  tests/S1Atlas.IntegrationTests/Extraction/Phase5ArchivedReplayCliTests.cs

git commit -m "feat: certify archived input replay"
```

---

### Task 6: Harden Recovery, Source-Control Privacy, and CI

**Files:**
- Modify: `src/S1Atlas.Extraction/Attempts/ExtractionRecoveryService.cs`
- Modify: `tests/S1Atlas.Extraction.Tests/Attempts/ExtractionRecoveryServiceTests.cs`
- Modify: `.gitignore`
- Create: `scripts/verify-repository-hygiene.ps1`
- Modify: `.github/workflows/ci.yml`
- Create: `tests/S1Atlas.IntegrationTests/Repository/RepositoryHygieneScriptTests.cs`

- [ ] **Step 1: Add recovery/cleanup convergence tests**

Prove Phase 4 promotion recovery precedes cleanup; recoverable complete promotion registers and is not eligible; live/malformed/changed lock preserves evidence; repeated recovery+preview is deterministic; repeated apply converges; orphan promotion journal blocked; ProcessCompleted stays resumable; stale nonterminal staging becomes eligible only after recovery terminalizes it.

Only modify production recovery when a failing test demonstrates a gap.

- [ ] **Step 2: Expand `.gitignore` narrowly**

Add exact generated names:

```text
artifact-manifest.json
validation.json
extraction.json
*.promotion.json
GameAssembly.dll
global-metadata.dat
Assembly-CSharp.dll
```

Keep `docs/smoke-tests/*.md` trackable. Do not add broad `*.dll` or `*.json` rules.

- [ ] **Step 3: Write repository hygiene script**

`scripts/verify-repository-hygiene.ps1` accepts optional `TrackedPathsFile`; otherwise reads `git ls-files -z`. Normalize `/`, print all violations, exit 0 clean/1 dirty.

Prohibited basenames:

```text
Cpp2IL.exe
GameAssembly.dll
global-metadata.dat
Assembly-CSharp.dll
atlas.db
atlas.db-wal
atlas.db-shm
installation.json
tool-manifest.json
attempt.json
input-manifest.json
artifact-manifest.json
validation.json
extraction.json
complete.marker
extraction.lock
stdout.log
stderr.log
```

Prohibited path segments:

```text
candidate-output
retained-output
reconstructed
decompiled
.staging
```

Allow source config such as `config/validation/managed-assemblies-v1.json`.

- [ ] **Step 4: Test the script**

Test clean synthetic list, every prohibited basename/segment, documentation text irrelevance, and real repository clean result.

- [ ] **Step 5: Harden CI**

```yaml
on:
  push:
    branches: [ main ]
  pull_request:
    branches: [ main ]
```

Add after tests:

```yaml
- name: Format
  run: dotnet format S1Atlas.sln --verify-no-changes --no-restore

- name: Repository hygiene
  shell: pwsh
  run: ./scripts/verify-repository-hygiene.ps1
```

Do not add `upload-artifact`.

- [ ] **Step 6: Run full security/regression checks**

```powershell
dotnet restore S1Atlas.sln
dotnet build S1Atlas.sln --configuration Release --no-restore
dotnet test S1Atlas.sln --configuration Release --no-build --verbosity normal
dotnet format S1Atlas.sln --verify-no-changes --no-restore
pwsh ./scripts/verify-repository-hygiene.ps1
git diff --check
```

- [ ] **Step 7: Commit Task 6**

```powershell
git add `
  src/S1Atlas.Extraction/Attempts/ExtractionRecoveryService.cs `
  tests/S1Atlas.Extraction.Tests/Attempts/ExtractionRecoveryServiceTests.cs `
  .gitignore `
  scripts/verify-repository-hygiene.ps1 `
  .github/workflows/ci.yml `
  tests/S1Atlas.IntegrationTests/Repository/RepositoryHygieneScriptTests.cs

git commit -m "chore: harden extraction recovery and repository privacy"
```

---

### Task 7: Complete the Real Retry/Replay Gate and Publish the Smoke Report

**Files:**
- Modify: `README.md`
- Create: `docs/smoke-tests/2026-08-13-schedule-i-cpp2il-extraction.md`
- Verify: every Phase 5 source/test/CI file

- [ ] **Step 1: Update README**

Document cleanup grammar/default/preview/apply/never-deleted categories; `--input-snapshot` and retry requirement; certification state; output fields; Phase 5 completion; next ILSpy/symbol milestone. Do not describe cleanup as automatic.

- [ ] **Step 2: Run the complete automated gate**

```powershell
dotnet restore S1Atlas.sln
dotnet build S1Atlas.sln --configuration Release --no-restore
dotnet test S1Atlas.sln --configuration Release --no-build --verbosity normal
dotnet format S1Atlas.sln --verify-no-changes --no-restore
pwsh ./scripts/verify-repository-hygiene.ps1
git diff --check
git status --short
```

Record exact per-project and total counts.

- [ ] **Step 3: Record real pre-smoke baseline**

Parse `status --json`, `tools status cpp2il --json`, `extractions list --json`, preferred `extractions show --json`, and cleanup preview. Capture full live GameAssembly/metadata SHA-256 and deterministic sorted game inventory `(relative path, length, SHA-256)` without committing the path list. Abort if preferred integrity or managed pin is not verified.

- [ ] **Step 4: Run real live retry while creating snapshot**

```powershell
$liveRetryJson = dotnet run --configuration Release --no-build --project src\S1Atlas.Cli -- `
  extract --snapshot-inputs --retry --json
$liveRetry = $liveRetryJson | ConvertFrom-Json
$snapshotId = $liveRetry.data.inputSnapshotId
if ([string]::IsNullOrWhiteSpace($snapshotId)) {
    throw "The live retry did not return an input snapshot ID."
}
```

Require process/validation/authoritative true, snapshot ID present, replay false, same extraction ID when bytes identical, no duplicate reconstructed directory, new attempt linked, preference stable. Different bytes retain a blocked distinct extraction and stop for review.

- [ ] **Step 5: Run real archived-only certification retry**

```powershell
$archiveRetryJson = dotnet run --configuration Release --no-build --project src\S1Atlas.Cli -- `
  extract --build $buildId `
  --input-snapshot $snapshotId `
  --retry --json
$archiveRetry = $archiveRetryJson | ConvertFrom-Json
```

Require process/validation/authoritative true, `ArchivedSnapshot`, replay true, no live game input resolution, same extraction ID when bytes identical, no duplicate output. Confirm DB certification timestamp.

If archived output differs, preserve the distinct extraction with `SameRecipeDifferentOutput`, leave preference unchanged, and stop for review. If the snapshot file set is insufficient, stop and open a separate profile design change; do not copy additional live files or edit the production profile here.

- [ ] **Step 6: Prove normal authoritative no-op**

Run normal `extract --json`; require process false, validation false, reuse true, same extraction ID, no new attempt/preference event.

- [ ] **Step 7: Prove controlled failures and cleanup apply on disposable data**

Run focused integration tests proving failed archived replay does not certify, failed retry preserves preferred extraction, and cleanup apply deletes only synthetic eligible data. On the real Atlas root run cleanup preview only; do not apply real historical deletion during the smoke.

- [ ] **Step 8: Compare post-smoke game state**

Recompute full GameAssembly/metadata hashes and deterministic inventory. Require exact equality. If Steam updates during the smoke, stop and report `InputChangedDuringExtraction`.

- [ ] **Step 9: Write non-proprietary report**

Create `docs/smoke-tests/2026-08-13-schedule-i-cpp2il-extraction.md` with actual commit/OS/.NET/build/tool hashes, live/archive attempt IDs and durations, snapshot ID/certification time, extraction ID/outcome/counts, dedup/preference/no-op results, cleanup preview and synthetic apply result, game inventory digest/count, automated totals, hygiene result, limitations, and next milestone.

Do not include bytes, decompiled source, absolute local paths, full inventory, long symbols, secrets, or signed URLs.

- [ ] **Step 10: Run final verification after report**

```powershell
dotnet build S1Atlas.sln --configuration Release --no-restore
dotnet test S1Atlas.sln --configuration Release --no-build --verbosity normal
dotnet format S1Atlas.sln --verify-no-changes --no-restore
pwsh ./scripts/verify-repository-hygiene.ps1
git diff --check
git status --short
git ls-files | Select-String -Pattern `
  "Cpp2IL\.exe|GameAssembly\.dll|global-metadata\.dat|Assembly-CSharp\.dll|atlas\.db|\.db-wal|\.db-shm|attempt\.json|input-manifest\.json|artifact-manifest\.json|validation\.json|extraction\.json|complete\.marker|promotion\.json|candidate-output|retained-output|reconstructed|stdout\.log|stderr\.log"
```

Tracked-file scan must return no generated/proprietary path.

- [ ] **Step 11: Commit Task 7**

```powershell
git add README.md docs/smoke-tests/2026-08-13-schedule-i-cpp2il-extraction.md
git commit -m "docs: finalize the validated extraction milestone"
```

---

## Final Review Checklist

### Cleanup

```text
[ ] Preview is default and mutates nothing
[ ] 30d default and strict d/h/m parser tested
[ ] Exactly-at-cutoff is not eligible
[ ] Only Failed/Canceled/Abandoned attempts delete
[ ] ProcessCompleted and Succeeded attempts preserved
[ ] Unassociated extraction staging and staging with complete markers preserved
[ ] Validated extractions and input snapshots never traversed/deleted
[ ] Reparse points and unknown evidence block deletion
[ ] Filesystem deletion precedes database deletion
[ ] Apply is idempotent and reports partial failures truthfully
[ ] No network access
```

### Archived replay

```text
[ ] Explicit selector requires --retry
[ ] Explicit selector never falls back live
[ ] Snapshot root is <snapshot-root>\game-root
[ ] Snapshot bytes/manifests strictly verified
[ ] Unverified snapshots usable only for explicit certification
[ ] Implicit resolution uses only replay-verified snapshots
[ ] Only authoritative process-backed results certify
[ ] First certification timestamp preserved
[ ] Recreating identical certified snapshot cannot conflict/downgrade
[ ] Byte-identical archived output links existing extraction
[ ] Failed/canceled/invalid replay cannot certify
```

### Recovery/privacy/CI

```text
[ ] Recovery runs before cleanup planning
[ ] Live lock blocks cleanup
[ ] Promotion journals and ambiguous evidence preserved
[ ] Narrow .gitignore generated-name rules
[ ] Hygiene script passes
[ ] CI runs format and hygiene
[ ] Feature PRs produce one PR CI run
[ ] CI uploads no artifacts
```

### Real milestone gate

```text
[ ] Release build 0 warnings/0 errors
[ ] All tests 0 failures/0 skips
[ ] Real live --retry validated
[ ] Real archived-only --retry certified snapshot
[ ] Identical output deduplicated
[ ] Normal extract process-free no-op
[ ] Controlled failure preserved preferred output
[ ] Game hashes/inventory unchanged
[ ] Cleanup preview safe; apply proven on disposable data
[ ] Non-proprietary report committed
[ ] No proprietary/generated files tracked
```

---

## Definition of Done

```text
[ ] PR starts from d9e889c628ecfa912a778d2afe84502f40f2ea6b
[ ] migrations 1–5 byte-for-byte unchanged
[ ] cleanup preview/apply works in human and JSON modes
[ ] cleanup defaults 30d and protects authoritative/history data
[ ] staging/quarantine cleanup is ownership- and age-verified
[ ] explicit archived snapshot retry implemented
[ ] replay certification process-backed, authoritative, idempotent
[ ] implicit resolver consumes replay-verified snapshot
[ ] live retry proves same-output deduplication
[ ] archived-only retry succeeds without live input
[ ] preferred extraction remains integrity verified
[ ] full suite/format gate pass
[ ] repository hygiene/CI privacy gates pass
[ ] game inputs/inventory unchanged
[ ] non-proprietary smoke report contains actual evidence
[ ] no ILSpy/source/symbol/portal/MCP/agent-skill work included
```

---

## Execution Handoff

The established execution mode is **inline/local TDD** because this chat cannot dispatch development subagents and the real replay gate requires the user’s Windows installation and local Atlas data.

After this plan passes QA and is merged:

```text
1. Create feature/cpp2il-phase5-hardening-replay-finalization from updated main.
2. Execute Tasks 1–7 locally in Codex.
3. Keep RED checkpoints local.
4. Push only coherent green commits.
5. Open a draft implementation PR.
6. Run real live retry and archived-only replay only after full automated green.
7. Leave implementation PR draft and unmerged for human QA.
```

The next independent design cycle begins only after Phase 5 is merged and adds ILSpy decompilation, normalized source/symbol metadata, and initial search/type/method/source commands over the preferred integrity-verified extraction.
