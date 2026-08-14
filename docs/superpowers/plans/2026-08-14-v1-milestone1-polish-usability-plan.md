# S1Atlas V1 Milestone 1 — Polish & Usability Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Turn the completed V1 foundation into a polished, trustworthy local developer-intelligence tool with deterministic symbol resolution, precise source evidence, honest body/relationship completeness, usable bounded CLI output, and the minimal installed/cached API indexing capability required by the approved V1 Milestone 1 acceptance matrix.

**Architecture:** Keep the existing normalized SQLite index and offline-first authority model. Add precision and usability by enriching persisted symbol/source metadata, centralizing exact symbol resolution in the shared query layer, adding targeted relationship/source lookups, polishing CLI rendering/error contracts, and introducing only the narrow S1API/S1MAPI indexing workflow needed for Installed/Release/Preview. Do not add Scene Intelligence, diffing, portal, MCP, semantic search, runtime probing, TUI, plugin frameworks, or generalized multi-game abstractions.

**Tech Stack:** .NET 8, C#, System.CommandLine, SQLite/Microsoft.Data.Sqlite, ILSpy/ICSharpCode.Decompiler, Roslyn/Microsoft.CodeAnalysis.CSharp, xUnit, GitHub Actions.

---

## Scope guardrails

This milestone is deliberately narrow. Every task must preserve the existing safety model:

- no game process launch for query/index inspection;
- no writes into the Schedule I installation;
- no assembly execution for body classification/indexing;
- no upstream execution; cached upstream source is data only;
- no automatic network fallback during ordinary queries;
- no generalized plugin/adapter framework;
- no Scene Intelligence or runtime scene inspection;
- no diffing, portal, MCP, semantic/vector search, or TUI;
- no authority claims beyond recorded extraction/source/hash facts.

The implementation must remain task-by-task TDD: RED, GREEN, focused commit, then the next task.

---

## Task 1: Persist precise Roslyn source spans

**Files:**
- Modify: `src/S1Atlas.Core/Indexing/NormalizedSymbol.cs`
- Modify: `src/S1Atlas.Core/Storage/IIndexRepository.cs`
- Modify: `src/S1Atlas.Indexing/Source/RoslynSourceIndexer.cs`
- Modify: `src/S1Atlas.Indexing/Workflow/IndexingWorkflow.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs`
- Modify: tests under `tests/S1Atlas.Indexing.Tests/Source/`, `tests/S1Atlas.Storage.Tests/Sqlite/`, `tests/S1Atlas.Indexing.Tests/Workflow/`

- [x] **Step 1: Write source-span tests**
- [x] **Step 2: Verify RED**
- [x] **Step 3: Persist end-line/end-column**
- [x] **Step 4: Verify focused + full suites**
- [x] **Step 5: Commit**

---

## Task 2: Add conservative callable body-recovery status

**Files:**
- Modify managed-decompilation/index models and SQLite schema/migrations
- Add tests proving `Recovered`, `StubOrUnavailable`, `NoBodyByDesign`, `Unknown`, and null for non-callables

- [x] **Step 1: Write classification tests**
- [x] **Step 2: Verify RED**
- [x] **Step 3: Implement conservative classifier and persistence**
- [x] **Step 4: Verify full gate**
- [x] **Step 5: Commit**

---

## Task 3: Make search exact, bounded, and deterministic

- [x] Exact total count separate from bounded retrieval
- [x] Database-side limit
- [x] Deterministic ranking and literal wildcard handling
- [x] Thousands-of-matches fixture
- [x] Full gate green

---

## Task 4: Centralize deterministic single-symbol resolution

- [x] Exact symbol ID strongest
- [x] Canonical key/signature resolution
- [x] Unique exact readable-name/signature resolution
- [x] Unique best-ranked textual resolution
- [x] Tied best rank -> structured ambiguity
- [x] Cross-channel deterministic merge
- [x] Full gate green

---

## Task 5: Add symbol-centric integrity-checked source queries

- [x] Resolve exactly one symbol
- [x] Return structured ambiguity/not-found
- [x] Read only the selected source file
- [x] Verify recorded SHA-256 before returning text
- [x] Honor exact spans and bounded context
- [x] Preserve body-recovery status semantics
- [x] Full gate green

---

## Task 6: Clarify refs/callers/callees semantics

**Exact behavior:**
- `refs`: all indexed incoming/outgoing relationship kinds for the resolved symbol.
- `callers`: incoming normalized `Calls`/`Constructs` whose target resolves to the selected symbol.
- `callees`: outgoing normalized `Calls`/`Constructs` from the selected symbol, preserving unresolved raw target text.

- [x] Targeted storage lookups
- [x] Enriched endpoint metadata
- [x] Unresolved target preservation
- [x] Explicit body-recovery/completeness notices
- [x] Full gate green

---

### Task 7: Polish CLI rendering, limits, ambiguity, and safe source-file escape hatch

**Files:**
- Modify: `src/S1Atlas.Cli/Commands/IndexQueryCommandFactory.cs`
- Modify: `src/S1Atlas.Cli/Commands/SearchCommand.cs`
- Modify: `src/S1Atlas.Cli/Commands/TypeCommand.cs`
- Modify: `src/S1Atlas.Cli/Commands/MethodCommand.cs`
- Modify: `src/S1Atlas.Cli/Commands/SourceCommand.cs`
- Modify: `src/S1Atlas.Cli/Commands/RefsCommand.cs`
- Modify: `src/S1Atlas.Cli/Commands/CallersCommand.cs`
- Modify: `src/S1Atlas.Cli/Commands/CalleesCommand.cs`
- Modify: `src/S1Atlas.Cli/Output/IndexQueryOutputModels.cs`
- Create: `tests/S1Atlas.IntegrationTests/IndexingCliUsabilityTests.cs`

**Exact CLI behavior:**
- `--limit <n>` default `50`; reject `n <= 0` with `InvalidLimit`.
- `source --context <n>` default `5`; reject negative values with `InvalidContext`.
- `source --file` requests the complete selected source file.
- Full-file stdout is capped at **1,048,576 bytes**. If larger and `--output` is absent, return `SourceTooLargeForTerminal` and instruct the user to provide `--output <path>`.
- `source --output <path>` writes only hash-verified Atlas source content. Resolve the destination with `Path.GetFullPath` and compare it against the detected Schedule I installation root using platform-appropriate path comparison. If destination is the installation root or any descendant, fail with `ReadOnlyGameInstallation`; there is no force/override switch.
- `--output` does not alter Atlas authority or source metadata.
- Stable outcome codes include `AmbiguousSymbol`, `SymbolNotFound`, `NoCompletedIndex`, `SourceUnavailable`, `SourceIntegrityFailure`, `InvalidCodebaseChannel`, `InstalledDependencyMissing`, `UpstreamUnavailable`, `ReadOnlyGameInstallation`.

- [x] **Step 1: Write CLI integration tests for search counts/limit and ambiguity**

Assert human output contains readable names and exact symbol IDs; JSON contains `totalCount`, `returnedCount`, and structured candidates.

- [x] **Step 2: Write CLI source tests, including the hard read-only guard**

Assert focused snippet output contains exact ID/signature/path/hash/range/body-recovery status; test `--context 0`; test oversized `--file` refusal and successful safe `--output` write. Add a test that supplies a path under a fixture Schedule I installation root and assert `ReadOnlyGameInstallation` with **no file created**.

- [x] **Step 3: Write relationship output tests**

Human output must contain readable source/target names **and** exact symbol IDs, relationship ID, kind, evidence, and unresolved target text when applicable.

- [x] **Step 4: Implement thin renderers/options**

Keep semantic decisions in the shared query layer. Do not duplicate ranking, symbol resolution, body classification, or relationship filtering in command classes.

- [x] **Step 5: Run integration tests and commit**

The Task 7 head passed Release build, the full solution test suite, formatting, and repository hygiene on CI after the final cleanup.

---

### Task 8: Complete the minimal installed S1API/S1MAPI indexing capability required by V1 Milestone 1

This is real new capability, not presentation polish. It is included because the approved V1 acceptance matrix cannot be exercised otherwise. Keep it intentionally narrow: S1API/S1MAPI only, existing normalized index model only, no generic plugin/adapter framework.

**Files:**
- Create: `src/S1Atlas.Indexing/Workflow/ApiIndexingWorkflow.cs`
- Modify: `src/S1Atlas.Cli/Commands/IndexCommand.cs`
- Modify: composition/wiring in `src/S1Atlas.Cli/CliApplication.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Workflow/ApiIndexingWorkflowTests.cs`
- Modify: `tests/S1Atlas.IntegrationTests/IndexingCliUsabilityTests.cs`

**Interfaces:**

```text
Task<IndexingWorkflowResult> RunInstalledAsync(
    CodebaseKind codebase,
    string environmentSnapshotId,
    string assemblyPath,
    bool force,
    CancellationToken cancellationToken);
```

Only `S1Api` and `S1MApi` are accepted. The caller passes the path discovered in the current environment snapshot. The workflow hashes/decompiles that exact binary and writes into the existing normalized model using `CodeChannel.Installed` and the same ILSpy adapter/body recovery classification.

- [ ] **Step 1: Write installed API workflow tests**

Use a fixture managed assembly and assert codebase/channel/environment-snapshot provenance, symbols, source, spans, and body recovery persist correctly.

- [ ] **Step 2: Verify failure before implementation**

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter FullyQualifiedName~ApiIndexingWorkflowTests
```

- [ ] **Step 3: Implement minimal installed workflow by reusing existing primitives**

Do not copy Schedule I extraction-authority logic; installed API authority is the discovered binary from the current environment snapshot.

- [ ] **Step 4: Extend `s1atlas index` options**

Preserve:

```text
s1atlas index
```

as Schedule I / Installed. Add:

```text
s1atlas index --codebase s1api --channel installed
s1atlas index --codebase s1mapi --channel installed
```

Resolve the binary only from the current environment snapshot. If absent, return `InstalledDependencyMissing`; never fetch an upstream substitute.

- [ ] **Step 5: Run workflow/integration tests and commit**

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter FullyQualifiedName~ApiIndexingWorkflowTests
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj -c Release --filter FullyQualifiedName~IndexingCliUsabilityTests
```

```powershell
git add src/S1Atlas.Indexing/Workflow/ApiIndexingWorkflow.cs src/S1Atlas.Cli tests/S1Atlas.Indexing.Tests/Workflow/ApiIndexingWorkflowTests.cs tests/S1Atlas.IntegrationTests/Indexing/IndexingCliUsabilityTests.cs
git commit -m "feat: index installed Schedule I APIs"
```

---

### Task 9: Complete the minimal cached Release/Preview API indexing capability

This is the second half of the small API-indexing capability required by the approved channel matrix. It remains cache-only and never executes upstream code.

Continue with the approved plan after Task 8 is green.
