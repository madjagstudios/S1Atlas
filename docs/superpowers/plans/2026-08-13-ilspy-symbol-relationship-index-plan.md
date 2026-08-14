# S1Atlas ILSpy, Symbol, and Relationship Index Implementation Plan

> **For Codex:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Turn the preferred integrity-verified Schedule I extraction plus installed/cached S1API and S1MAPI inputs into a safe, searchable source/symbol/relationship index with Installed, Release, and Preview authority kept separate.

**Architecture:** Add a focused `S1Atlas.Indexing` project between trusted inputs and the existing Core/Storage/CLI layers. Indexing owns the ILSpy and Roslyn adapters, source generation, normalized symbol construction, relationship extraction, and indexing workflow. Core owns vendor-neutral domain models and canonical identity rules. Storage adds one v6 schema and repository surface. CLI exposes indexing/query/upstream commands. Schedule I authority is resolved through the existing preferred-pointer → validated extraction → fresh integrity verification sequence; indexing does not create a second extraction trust framework.

**Tech Stack:** C# / .NET 8; SQLite; `ICSharpCode.Decompiler` **10.1.1.8388**; `Microsoft.CodeAnalysis.CSharp` **5.6.0**; xUnit; existing `CliEnvelope`, `AtlasPaths`, migration runner, hashing, and repository-hygiene conventions.

## Implementation rules

- Follow TDD: add or update a focused test first, verify it fails for the intended reason, then implement the minimum production code to pass it.
- Keep commits small and phase-oriented. Do not combine unrelated refactors.
- Do not run or load reconstructed game assemblies with `Assembly.Load`/`AssemblyLoadContext`.
- Do not build, restore, execute, or evaluate arbitrary upstream S1API/S1MAPI repositories.
- Do not add a graph database, plugin framework, semantic search, numerical confidence engine, background service, or full diff engine.
- Normal queries default to Installed. Release/Preview must be explicitly requested.
- Network access is manual by default. Optional auto-check happens only during an already-running relevant command.
- Never add generated Schedule I source, reconstructed DLLs, or local Atlas runtime data to Git.
- Use existing divergent validated Cpp2IL outputs for fingerprint-stability smoke checks if they still exist. Do **not** rerun Cpp2IL merely to create another comparison sample unless explicitly approved.

---

# Phase 1 — Trusted Schedule I input and ILSpy capability

## Task 1: Add the indexing project and pinned analysis dependencies

**Files:**
- Create: `src/S1Atlas.Indexing/S1Atlas.Indexing.csproj`
- Create: `tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj`
- Create: `tests/S1Atlas.Indexing.Tests/BootstrapTests.cs`
- Modify: `S1Atlas.sln`

**Step 1 — Write the failing bootstrap test**

Add a test that references a trivial public type from `S1Atlas.Indexing` (for example `IndexingAssemblyMarker`). The test must fail because the project/type does not yet exist.

Run:

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release
```

Expected: compile failure referencing the missing project/type.

**Step 2 — Add the projects and exact package pins**

`S1Atlas.Indexing.csproj` references:
- `S1Atlas.Core`
- `S1Atlas.Extraction`
- `ICSharpCode.Decompiler` version `10.1.1.8388`
- `Microsoft.CodeAnalysis.CSharp` version `5.6.0`

The test project references `S1Atlas.Indexing` and the same xUnit/test packages already used by the solution. Add both projects to `S1Atlas.sln`.

**Step 3 — Make the bootstrap test pass**

Run:

```powershell
dotnet restore S1Atlas.sln
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release
```

Expected: pass.

**Step 4 — Commit**

```powershell
git add S1Atlas.sln src/S1Atlas.Indexing tests/S1Atlas.Indexing.Tests
git commit -m "feat: add indexing project"
```

## Task 2: Centralize preferred verified extraction resolution

**Files:**
- Create: `src/S1Atlas.Indexing/Authority/PreferredVerifiedExtractionResolver.cs`
- Create: `src/S1Atlas.Indexing/Authority/PreferredVerifiedExtraction.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Authority/PreferredVerifiedExtractionResolverTests.cs`
- Reuse: `src/S1Atlas.Core/Storage/IValidatedExtractionRepository.cs`
- Reuse: `src/S1Atlas.Extraction/Manifests/ValidatedExtractionIntegrityVerifier.cs`

**Required behavior:**
1. read the preferred extraction pointer for a build;
2. resolve the corresponding validated extraction row;
3. perform a fresh full integrity verification;
4. return a single verified value object only if all three agree;
5. fail closed for missing preference, missing validated row, mismatched build/extraction IDs, failed integrity, or preference invalidation.

Do not add new preference semantics. `PolicyInvalidated` and `IntegrityInvalidated` remain existing extraction concerns.

**TDD cases:**
- preferred + validated + integrity pass → returns verified input;
- preferred pointer alone → rejected;
- validated row exists but is not preferred → rejected;
- integrity failure → rejected;
- preference changes between initial read and final authority check → rejected or retried once, but never silently indexes the old output.

Run:

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter PreferredVerifiedExtractionResolver
```

Expected: all pass.

Commit:

```powershell
git add src/S1Atlas.Indexing/Authority tests/S1Atlas.Indexing.Tests/Authority
git commit -m "feat: resolve preferred verified extraction"
```

## Task 3: Add owned index paths without a second promotion framework

**Files:**
- Modify: `src/S1Atlas.Cli/Configuration/AtlasPaths.cs`
- Create: `src/S1Atlas.Indexing/Paths/OwnedIndexPaths.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Paths/OwnedIndexPathsTests.cs`
- Modify as needed: `tests/S1Atlas.IntegrationTests/Repository/RepositoryHygieneScriptTests.cs`
- Modify only if needed: `.gitignore`
- Modify only if needed: `scripts/verify-repository-hygiene.ps1`

**Required roots:**

```text
builds/<build-id>/indexes/<index-id>/...
installed/s1api/<binary-sha256>/indexes/<index-id>/...
installed/s1mapi/<binary-sha256>/indexes/<index-id>/...
upstream/s1api/commits/<commit-sha>/...
upstream/s1mapi/commits/<commit-sha>/...
```

Each index has a sibling `.staging` path during construction and a final `complete.marker`. Paths must reject traversal and reparse-point escape using the same defensive style as extraction-owned paths.

Do not build journals/quarantine trees unless a failing test proves they are necessary.

Run:

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter OwnedIndexPaths
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj -c Release --filter RepositoryHygiene
```

Commit:

```powershell
git add src/S1Atlas.Cli/Configuration/AtlasPaths.cs src/S1Atlas.Indexing/Paths tests/S1Atlas.Indexing.Tests/Paths .gitignore scripts/verify-repository-hygiene.ps1 tests/S1Atlas.IntegrationTests/Repository
git commit -m "feat: define owned index paths"
```

## Task 4: Implement the pinned ILSpy adapter on fixture assemblies

**Files:**
- Create: `src/S1Atlas.Core/Indexing/DecompilerModels.cs`
- Create: `src/S1Atlas.Indexing/Decompilation/IManagedDecompiler.cs`
- Create: `src/S1Atlas.Indexing/Decompilation/IlSpyManagedDecompiler.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Decompilation/IlSpyManagedDecompilerTests.cs`
- Modify: `tests/Fixtures/S1Atlas.ManagedAssemblyFixture/FixtureRoot.cs`

The adapter reads DLLs as metadata/decompiler inputs and returns S1Atlas-owned facts plus generated C# text. No ILSpy type may appear in Core/Storage/CLI public contracts.

Fixture coverage must include:
- class + inheritance/interface;
- overloaded methods;
- constructor;
- field/property/event;
- generic method/type;
- calls, object construction, field read/write in method bodies.

**Tests first:** verify readable source contains expected declarations and metadata facts are populated without executing the fixture assembly.

Run:

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter IlSpyManagedDecompiler
```

Commit:

```powershell
git add src/S1Atlas.Core/Indexing src/S1Atlas.Indexing/Decompilation tests/Fixtures/S1Atlas.ManagedAssemblyFixture tests/S1Atlas.Indexing.Tests/Decompilation
git commit -m "feat: add ILSpy decompiler adapter"
```

## Task 5: Real Schedule I ILSpy capability smoke

**Files:**
- Create: `docs/smoke-tests/2026-08-13-schedule-i-ilspy-indexing.md`
- Add a temporary/internal diagnostic entry point only if necessary; remove it before phase completion if it is not part of the product CLI.

On the operator Windows machine:
1. resolve the preferred verified extraction using Task 2;
2. decompile representative assemblies, especially `Assembly-CSharp.dll`;
3. record whether readable source is produced;
4. sample representative methods and record how many have useful recoverable bodies versus declaration-only output;
5. hash authoritative extraction inputs before/after and confirm no changes;
6. if the known divergent same-recipe validated outputs still exist, process the same representative symbols from both and record whether normalized declaration/structural/method representations can plausibly be made stable despite raw-byte differences. Do not claim full semantic equivalence.

This is a **measurement gate**, not a reason to invent fallback relationships.

Commit the smoke observations only; never commit generated source or DLLs.

---

# Phase 2 — Normalized symbols, identities, persistence, and `index`

## Task 6: Define the shared normalized symbol/type model and canonical renderer

**Files:**
- Create: `src/S1Atlas.Core/Indexing/CodebaseKind.cs`
- Create: `src/S1Atlas.Core/Indexing/CodeChannel.cs`
- Create: `src/S1Atlas.Core/Indexing/NormalizedTypeReference.cs`
- Create: `src/S1Atlas.Core/Indexing/SymbolKind.cs`
- Create: `src/S1Atlas.Core/Indexing/NormalizedSymbol.cs`
- Create: `src/S1Atlas.Core/Indexing/CanonicalSignatureRenderer.cs`
- Create: `src/S1Atlas.Core/Indexing/SymbolIdentity.cs`
- Create tests under: `tests/S1Atlas.Core.Tests/Indexing/`

**Hard rules:**
- Schedule I supports `Installed` only.
- S1API/S1MAPI support Installed/Release/Preview.
- One renderer produces canonical keys for both ILSpy and Roslyn frontends.
- Normalize primitive aliases (`int` → `System.Int32`), nested types, generic arity, arrays, pointers, nullable annotations, tuple shapes, and ref/in/out modifiers consistently.
- If upstream syntax cannot be confidently normalized to the same logical identity, mark the identity as source-text/best-effort instead of pretending it is equivalent.

**TDD examples:**
- ILSpy-style `System.Int32` and Roslyn-style `int` inputs render identically;
- overload-significant differences produce different keys;
- generic arity is stable;
- `ref`, `in`, and `out` differ;
- ScheduleI/Preview construction is rejected.

Run:

```powershell
dotnet test tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj -c Release --filter Indexing
```

Commit.

## Task 7: Add the minimal SQLite v6 index schema

**Files:**
- Modify: `src/S1Atlas.Storage/Migrations/SqliteMigrations.cs`
- Modify if recognizer requires it: `src/S1Atlas.Storage/Migrations/FoundationSchemaRecognizer.cs`
- Create: `tests/S1Atlas.Storage.Tests/Migrations/IndexingMigrationTests.cs`

Migration **v6** should initially create only what this milestone requires:

```text
code_snapshots
index_runs
symbols
source_files
source_locations
symbol_fingerprints
relationships
upstream_repositories
upstream_snapshots
upstream_state
```

Use foreign keys and indexes for actual query paths. Do not create table-per-symbol-kind hierarchies unless a focused query/test demonstrates the need.

Include `environment_snapshot_id` on Installed snapshots so cross-codebase binding is explicitly scoped to the observed current environment.

Tests must prove:
- clean v5 → v6 migration;
- v1 fixture can still migrate through all migrations;
- migration checksum protection still works;
- channel/codebase constraints reject ScheduleI/Release and ScheduleI/Preview;
- deleting/invalidating extraction records does not cascade-delete unrelated historical source indexes accidentally.

Run:

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj -c Release --filter IndexingMigration
```

Commit.

## Task 8: Add index repository persistence and one-transaction completion semantics

**Files:**
- Create: `src/S1Atlas.Core/Storage/IIndexRepository.cs`
- Create: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs`
- Create: `tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryIndexingTests.cs`

Required operations:
- create/read code snapshot;
- start/complete/fail index run;
- replace/write symbols, source metadata, fingerprints, relationships inside one DB transaction for a candidate run;
- query only completed indexes;
- retrieve latest completed snapshot by codebase/channel/environment.

Failure must leave the prior completed index queryable. No extra promotion-policy engine.

Run:

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj -c Release --filter SqliteAtlasRepositoryIndexing
```

Commit.

## Task 9: Generate Schedule I symbols/source/fingerprints and finalize an index

**Files:**
- Create: `src/S1Atlas.Indexing/Workflow/IndexingWorkflow.cs`
- Create: `src/S1Atlas.Indexing/Workflow/ScheduleOneIndexSource.cs`
- Create: `src/S1Atlas.Indexing/Source/GeneratedSourceWriter.cs`
- Create: `src/S1Atlas.Indexing/Fingerprints/SymbolFingerprintService.cs`
- Create tests under: `tests/S1Atlas.Indexing.Tests/Workflow/`

Workflow:
1. obtain `PreferredVerifiedExtraction`;
2. compute deterministic index identity from extraction ID + exact decompiler package/version/settings + index schema version;
3. if a completed matching index exists and `--force` is false, reuse it;
4. write generated source under `.staging`;
5. normalize symbols and source locations;
6. compute declaration/structural/source fingerprints and method-body fingerprints only when evidence is usable;
7. validate source hashes/basic referential integrity;
8. finalize owned source directory and complete DB run;
9. write `complete.marker` last.

If any step fails, the new run is not queryable and the previous completed index remains intact.

Tests cover idempotent reuse, `--force` rebuild, failed staging cleanup/retention behavior, source hash mismatch, and preference change during indexing.

Commit.

## Task 10: Add `s1atlas index`

**Files:**
- Create: `src/S1Atlas.Cli/Commands/IndexCommand.cs`
- Create: `src/S1Atlas.Cli/Output/IndexOutputModels.cs`
- Modify: `src/S1Atlas.Cli/CliApplication.cs`
- Create: `tests/S1Atlas.IntegrationTests/Indexing/IndexCliFixture.cs`
- Create: `tests/S1Atlas.IntegrationTests/Indexing/IndexCliTests.cs`

CLI:

```text
s1atlas index [--force] [--json]
```

For this phase, it must successfully index Schedule I from a test fixture authority path. Installed API/upstream handling arrives in Phase 4 without changing the command shape.

Human and JSON output include codebase/channel, source input identity, index ID, reused/rebuilt, symbol counts, source-file count, relationship count (may be zero until Phase 3), and warnings.

Run focused integration tests, then full solution tests.

---

# Phase 3 — Practical relationships and query CLI

## Task 11: Populate structural and recovered-IL relationships

**Files:**
- Create: `src/S1Atlas.Core/Indexing/RelationshipModels.cs`
- Create: `src/S1Atlas.Indexing/Relationships/RelationshipExtractor.cs`
- Extend: `src/S1Atlas.Indexing/Decompilation/IlSpyManagedDecompiler.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Relationships/RelationshipExtractorTests.cs`

Initial kinds only:

```text
Inherits
ImplementsInterface
FieldType
PropertyType
EventType
ParameterType
ReturnType
Calls
Constructs
ReadsField
WritesField
```

Evidence:

```text
Metadata
RecoveredIL
UpstreamSource
```

Resolved target if unambiguous; otherwise retain textual target evidence. No guessed edges and no numeric confidence.

Installed cross-codebase resolution is allowed only when snapshots share the same `environment_snapshot_id`.

Commit.

## Task 12: Add deterministic query service and CLI commands

**Files:**
- Create: `src/S1Atlas.Core/Indexing/QueryModels.cs`
- Create: `src/S1Atlas.Indexing/Query/IndexQueryService.cs`
- Create CLI commands:
  - `src/S1Atlas.Cli/Commands/SearchCommand.cs`
  - `TypeCommand.cs`
  - `MethodCommand.cs`
  - `SourceCommand.cs`
  - `RefsCommand.cs`
  - `CallersCommand.cs`
  - `CalleesCommand.cs`
- Create: `src/S1Atlas.Cli/Output/IndexQueryOutputModels.cs`
- Modify: `src/S1Atlas.Cli/CliApplication.cs`
- Create integration tests under: `tests/S1Atlas.IntegrationTests/Indexing/QueryCliTests.cs`

Every relevant command supports:

```text
--codebase schedule-i|s1api|s1mapi
--channel installed|release|preview|all
--json
```

Default channel is Installed. `all` must be explicit.

Search ranking remains deterministic:
1. exact name;
2. exact qualified-name segment;
3. prefix;
4. substring;
5. canonical signature;
6. namespace.

Ambiguous type/method inputs return candidates instead of guessing.

`source` returns only the relevant source slice plus provenance.

Tests must specifically prove Preview/Release results never appear in an unqualified Installed query.

Commit.

---

# Phase 4 — Installed S1API/S1MAPI and upstream intelligence

## Task 13: Capture installed API binaries as authoritative current snapshots

**Files:**
- Extend narrowly: `src/S1Atlas.Extraction/Discovery/InstalledDependencyDetector.cs`
- Add/modify Core dependency observation model only as needed to retain binary path + SHA-256.
- Extend indexing workflow: `src/S1Atlas.Indexing/Workflow/InstalledApiIndexSource.cs`
- Add tests in both `S1Atlas.Extraction.Tests/Discovery` and `S1Atlas.Indexing.Tests/Workflow`.

Requirements:
- locate installed S1API/S1MAPI DLL(s) without executing them;
- hash exact binary bytes;
- bind Installed snapshots to the persisted `environment_snapshot_id` that observed them;
- decompile/index via the same ILSpy adapter and canonical renderer as Schedule I;
- absent S1API or S1MAPI is a normal explicit unavailable state, not a whole-command failure.

The existing dependency version display must not regress.

Commit.

## Task 14: Add safe upstream snapshot cache and manual sync

**Files:**
- Create: `src/S1Atlas.Core/Indexing/UpstreamModels.cs`
- Create: `src/S1Atlas.Indexing/Upstream/IUpstreamClient.cs`
- Create: `src/S1Atlas.Indexing/Upstream/GitHubUpstreamClient.cs`
- Create: `src/S1Atlas.Indexing/Upstream/UpstreamSnapshotCache.cs`
- Create: `src/S1Atlas.Indexing/Upstream/UpstreamSyncService.cs`
- Add config model under: `src/S1Atlas.Cli/Configuration/`
- Add repository configuration under `config/` only after verifying the official S1API and S1MAPI repository identities from GitHub during implementation.
- Create tests under: `tests/S1Atlas.Indexing.Tests/Upstream/`

Rules:
- no network during ordinary search/source/query commands;
- `upstream sync` is the explicit network entry point;
- cache source keyed by repository + exact commit SHA;
- hash every cached file locally and validate before reuse;
- trust GitHub as the upstream transport/source; do not claim local verification of Git object cryptographic hashes unless actually implemented;
- never invoke `dotnet build`, MSBuild, package restore, source generators, tests, or repo scripts;
- GitHub failure preserves usable cached state and reports staleness.

Use an injectable fake client in CI; CI itself must remain network-free.

Commit.

## Task 15: Parse Release/Preview C# with Roslyn into the same canonical model

**Files:**
- Create: `src/S1Atlas.Indexing/Source/RoslynSourceIndexer.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Source/RoslynSourceIndexerTests.cs`
- Extend fixture source repository under: `tests/Fixtures/` as needed.

Roslyn parsing is source-only and non-executing. Do not require a fully buildable solution.

The parser must:
- recover declarations/source locations/comments where useful;
- feed the same `NormalizedTypeReference` + `CanonicalSignatureRenderer` used by ILSpy;
- mark keys/relationships best-effort when semantic binding is unavailable;
- preserve unresolved target text rather than guessing;
- never silently bind Preview symbols to Installed symbols.

Golden tests must feed equivalent fixture declarations through ILSpy and Roslyn and verify byte-identical canonical keys for cases we claim to normalize. Cases we cannot normalize reliably must be explicitly marked non-equivalent/best-effort.

Commit.

## Task 16: Match installed source conservatively and expose `upstream status/sync`

**Files:**
- Create: `src/S1Atlas.Indexing/Upstream/InstalledSourceMatcher.cs`
- Create CLI commands:
  - `src/S1Atlas.Cli/Commands/UpstreamCommand.cs`
  - `UpstreamStatusCommand.cs`
  - `UpstreamSyncCommand.cs`
- Create: `src/S1Atlas.Cli/Output/UpstreamOutputModels.cs`
- Modify: `src/S1Atlas.Cli/CliApplication.cs`
- Create integration tests: `tests/S1Atlas.IntegrationTests/Indexing/UpstreamCliTests.cs`

Match evidence order:
1. exact embedded/source commit metadata when available;
2. exact binary/package hash mapped to reviewed upstream artifact;
3. exact reviewed tag/release association;
4. semantic version/tag match;
5. unmatched.

Version-only matching must never be labeled cryptographic/exact-binary proof.

CLI:

```text
s1atlas upstream status [--json]
s1atlas upstream sync [s1api|s1mapi] [--json]
```

Add configuration:

```text
upstream.autoCheck = false
upstream.checkInterval = 24h
```

When `autoCheck=true`, only a relevant already-running scan/index/upstream operation may perform a due check. No daemon, service, scheduler, startup task, or timer.

Integration tests prove:
- default commands do zero network calls;
- manual sync calls network seam exactly as expected;
- opt-in due auto-check runs during relevant use;
- not-due auto-check does not call network;
- cached queries work when client throws;
- Installed/Release/Preview remain distinct.

Commit.

---

# Phase 5 — Real smoke, stability measurements, and release hardening

## Task 17: Run full real-environment smoke and document limitations

**Files:**
- Update: `docs/smoke-tests/2026-08-13-schedule-i-ilspy-indexing.md`
- Modify README/help docs only where user-facing commands need documentation.

Run on the operator Windows machine:

```powershell
dotnet run --project src/S1Atlas.Cli -c Release -- index --json
dotnet run --project src/S1Atlas.Cli -c Release -- search Dealer --json
dotnet run --project src/S1Atlas.Cli -c Release -- type <representative-type> --json
dotnet run --project src/S1Atlas.Cli -c Release -- method <representative-method> --json
dotnet run --project src/S1Atlas.Cli -c Release -- refs <representative-symbol> --json
dotnet run --project src/S1Atlas.Cli -c Release -- callers <representative-method> --json
dotnet run --project src/S1Atlas.Cli -c Release -- callees <representative-method> --json
dotnet run --project src/S1Atlas.Cli -c Release -- upstream status --json
```

Explicitly sync upstream only for the smoke step that tests network behavior:

```powershell
dotnet run --project src/S1Atlas.Cli -c Release -- upstream sync --json
```

Record:
- exact preferred extraction ID and build ID;
- decompiler version/settings identity;
- assembly/type/method/member/source counts;
- behavioral-body and relationship recovery rates for representative systems;
- installed S1API/S1MAPI status and hashes when present;
- Release/Preview exact repository/commit provenance;
- proof that Installed queries exclude Preview/Release;
- proof game inputs remain unchanged;
- proof generated proprietary files are outside Git;
- normalized fingerprint comparison across existing divergent same-recipe outputs, with exact limitations. If old divergent outputs no longer exist, document that this measurement could not be repeated; do not create new Cpp2IL outputs merely for the test.

## Task 18: Full verification gate and implementation PR

Run:

```powershell
dotnet restore S1Atlas.sln
dotnet build S1Atlas.sln -c Release --no-restore
dotnet test S1Atlas.sln -c Release --no-build
dotnet format S1Atlas.sln --verify-no-changes
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-repository-hygiene.ps1
```

Expected:
- Release build: 0 errors, ideally 0 warnings;
- all tests pass, 0 skipped unless an explicitly documented platform-only case already exists;
- format clean;
- hygiene clean;
- no generated Schedule I source/reconstructed DLL/local cache tracked.

Then inspect:

```powershell
git status --short
git diff --check
git ls-files | Select-String -Pattern 'Assembly-CSharp|GameAssembly|global-metadata|complete\.marker|\\source\\schedule-i'
```

Expected: clean working tree after commit; no proprietary/generated matches.

Before merge:
- request code review;
- address review technically, not mechanically;
- rerun the exact verification gate on final head;
- require green exact-head CI.

---

# Milestone completion criteria

The milestone is done only when:

- Schedule I indexing can consume only the preferred validated extraction after fresh integrity proof;
- ILSpy generates useful readable C# without executing managed inputs;
- normalized assemblies/namespaces/types/methods/constructors/fields/properties/events/parameters are persisted and searchable;
- one shared canonical renderer is used by ILSpy and Roslyn frontends;
- declaration/structural/source/method-body fingerprints exist only where supported and raw SHA-256 remains authoritative provenance;
- inheritance/interfaces/type references and recoverable calls/constructs/field reads/writes are indexed without guessing;
- `index`, `search`, `type`, `method`, `source`, `refs`, `callers`, and `callees` work in human and JSON forms;
- installed S1API/S1MAPI are indexed when present and tied to the observed environment;
- Release and Preview upstream snapshots are cached/indexed separately from Installed;
- ordinary commands perform no GitHub traffic by default;
- optional auto-check is on-use only;
- GitHub failure does not break cached/local queries;
- failed indexing never replaces a completed index;
- real smoke documents useful coverage and limitations rather than claiming completeness;
- the game installation remains read-only;
- all build/test/format/hygiene gates pass.
