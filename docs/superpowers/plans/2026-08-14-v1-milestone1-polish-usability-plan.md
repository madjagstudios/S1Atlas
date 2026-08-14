# S1Atlas V1 Milestone 1 — Polish & Usability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the merged code index into a precise, bounded, developer-friendly query experience while preserving exact technical identity and proving the S1API/S1MAPI Installed/Release/Preview model against real inputs.

**Architecture:** Keep the existing extraction/index authority model. Add only the missing persisted facts (`has_body` and complete source spans), then enrich the shared query layer with deterministic symbol resolution, bounded SQLite-backed search, focused source retrieval, and precise relationship semantics. The CLI remains a thin human/JSON renderer over those shared results. API channel validation uses the same normalized storage model; it does not introduce a second index or execute upstream repositories.

**Tech Stack:** C# / .NET 8, xUnit v3, Microsoft.Data.Sqlite, Microsoft.CodeAnalysis.CSharp, ICSharpCode.Decompiler, System.CommandLine.

## Global Constraints

- Follow `docs/superpowers/specs/2026-08-14-v1-milestone1-polish-usability-design.md` exactly.
- Preserve the principle: **progressive readability, not progressive disclosure of truth**.
- Normal local queries make no network calls. GitHub access remains explicit through `upstream sync` unless a user has explicitly opted into on-use checking.
- Schedule I Installed facts come only from the preferred integrity-verified extraction.
- Release/Preview never substitute for Installed.
- Do not add Scene Intelligence, diffing, the HTML portal, MCP, the agent skill, semantic/vector search, runtime probing, a TUI, a plugin architecture, or generalized multi-game support.
- Do not execute or build upstream S1API/S1MAPI repositories. Parse cached source as data only.
- Preserve existing migrations byte-for-byte. Append the Milestone 1 migration only.
- A missing installed S1API/S1MAPI binary is a valid `Not present` outcome and does not block Release/Preview validation.
- Empty callers/callees must never be presented as proof of no calls when recovered body data is unavailable/unknown or target resolution is incomplete.
- Every task ends with a focused green test gate and a small commit.

---

## File Structure

### Core / storage contracts

- Modify `src/S1Atlas.Core/Indexing/NormalizedSymbol.cs` — retain the existing source start-line field and add precise start/end columns/line range.
- Modify `src/S1Atlas.Core/Storage/IIndexRepository.cs` — add nullable persisted body availability and bounded symbol-query contracts.
- Modify `src/S1Atlas.Core/Indexing/QueryModels.cs` — richer symbol selection, source, relationship, counts, and availability results.

### Indexing

- Modify `src/S1Atlas.Indexing/Source/RoslynSourceIndexer.cs` — capture real Roslyn start/end positions.
- Modify `src/S1Atlas.Indexing/Workflow/IndexingWorkflow.cs` — persist complete source spans and `ManagedMemberFacts.HasBody`; bump index recipe/schema identity so old completed indexes are not silently reused as if they contain the new facts.
- Create `src/S1Atlas.Indexing/Query/SymbolResolver.cs` — deterministic single-symbol resolution and ambiguity reporting.
- Create `src/S1Atlas.Indexing/Query/SourceSnippetReader.cs` — hash-verified bounded source reads and snippet extraction.
- Modify `src/S1Atlas.Indexing/Query/IndexQueryService.cs` — bounded search, focused source, refs/callers/callees semantics, endpoint enrichment, availability status.
- Create `src/S1Atlas.Indexing/Workflow/ApiIndexingWorkflow.cs` — minimal installed-binary and cached-upstream indexing into the existing normalized model.

### Storage

- Modify `src/S1Atlas.Storage/Migrations/SqliteMigrations.cs` — append migration 7 adding `symbols.has_body` and query-support indexes only if measurement shows they are needed.
- Modify `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs` — read/write `has_body`, exact lookup, SQL count, ranked limited symbol search, and targeted relationship/source lookups.

### CLI

- Modify `src/S1Atlas.Cli/Commands/IndexQueryCommandFactory.cs` — shared `--limit` and precise errors where applicable.
- Modify `src/S1Atlas.Cli/Commands/SourceCommand.cs` — `--context`, `--file`, and `--output` behavior.
- Modify `src/S1Atlas.Cli/Commands/RefsCommand.cs`, `CallersCommand.cs`, `CalleesCommand.cs` — call the distinct query operations.
- Modify `src/S1Atlas.Cli/Commands/IndexCommand.cs` — minimal codebase/channel/commit selection for API indexing while preserving `s1atlas index` as Schedule I Installed by default.
- Modify `src/S1Atlas.Cli/Output/IndexQueryOutputModels.cs` and the existing command renderer path — totals, candidates, readable endpoints plus exact IDs/signatures/evidence.

### Tests / docs

- Modify `tests/S1Atlas.Indexing.Tests/Source/RoslynSourceIndexerTests.cs`.
- Modify or create focused workflow tests under `tests/S1Atlas.Indexing.Tests/Workflow/`.
- Create `tests/S1Atlas.Indexing.Tests/Query/SymbolResolverTests.cs`.
- Create `tests/S1Atlas.Indexing.Tests/Query/SourceSnippetReaderTests.cs`.
- Create `tests/S1Atlas.Indexing.Tests/Query/IndexQueryServiceUsabilityTests.cs`.
- Modify `tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryIndexingTests.cs`.
- Create `tests/S1Atlas.Storage.Tests/Migrations/SqliteMigrationRunnerMilestone1Tests.cs`.
- Create `tests/S1Atlas.IntegrationTests/IndexingCliUsabilityTests.cs`.
- Create `docs/smoke-tests/2026-08-14-v1-milestone1-polish-usability.md` only after the real smoke is run.

---

### Task 1: Capture precise source spans

**Files:**
- Modify: `src/S1Atlas.Core/Indexing/NormalizedSymbol.cs`
- Modify: `src/S1Atlas.Indexing/Source/RoslynSourceIndexer.cs`
- Modify: `src/S1Atlas.Indexing/Workflow/IndexingWorkflow.cs`
- Test: `tests/S1Atlas.Indexing.Tests/Source/RoslynSourceIndexerTests.cs`
- Test: `tests/S1Atlas.Indexing.Tests/Workflow/IndexingWorkflowTests.cs` if present; otherwise create `tests/S1Atlas.Indexing.Tests/Workflow/SourceLocationWorkflowTests.cs`

**Interfaces:**
- Extend `NormalizedSymbol` with `int? SourceColumn`, `int? SourceEndLine`, and `int? SourceEndColumn`; retain `SourceLine` as the 1-based start line for compatibility.
- `BuildSourceLocations` must populate all six `IndexSourceLocationRecord` fields from those values.

- [ ] **Step 1: Write failing Roslyn span tests**

Add a multiline fixture and assert exact 1-based positions, for example:

```csharp
var source = """
namespace Demo;
public class Worker
{
    public int Add(int x)
    {
        return x + 1;
    }
}
""";

var method = Assert.Single(indexer.Index(source, CodebaseKind.S1Api, CodeChannel.Release)
    .Where(x => x.Kind == SymbolKind.Method));
Assert.Equal(4, method.SourceLine);
Assert.NotNull(method.SourceColumn);
Assert.Equal(7, method.SourceEndLine);
Assert.NotNull(method.SourceEndColumn);
```

Also test a one-line member and a type declaration.

- [ ] **Step 2: Run the focused test and verify failure**

Run:

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter FullyQualifiedName~RoslynSourceIndexerTests
```

Expected: FAIL because end positions/columns are not captured.

- [ ] **Step 3: Implement Roslyn span capture**

Use one `GetLineSpan()` per node and store `StartLinePosition.Line + 1`, `StartLinePosition.Character + 1`, `EndLinePosition.Line + 1`, `EndLinePosition.Character + 1`. Do not infer columns later in the workflow.

- [ ] **Step 4: Thread the span into `BuildSourceLocations`**

Replace the hardcoded start column `1` and null end positions with the values captured by `NormalizedSymbol`.

- [ ] **Step 5: Add workflow persistence coverage and run it**

Verify a completed fixture index returns non-null end positions and real columns from `GetCompletedSourceLocationsAsync`.

Run:

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter FullyQualifiedName~SourceLocation
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/S1Atlas.Core/Indexing/NormalizedSymbol.cs src/S1Atlas.Indexing/Source/RoslynSourceIndexer.cs src/S1Atlas.Indexing/Workflow/IndexingWorkflow.cs tests/S1Atlas.Indexing.Tests
git commit -m "feat: capture precise indexed source spans"
```

---

### Task 2: Persist method-body availability with migration 7

**Files:**
- Modify: `src/S1Atlas.Core/Storage/IIndexRepository.cs`
- Modify: `src/S1Atlas.Indexing/Workflow/IndexingWorkflow.cs`
- Modify: `src/S1Atlas.Storage/Migrations/SqliteMigrations.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs`
- Test: `tests/S1Atlas.Storage.Tests/Migrations/SqliteMigrationRunnerMilestone1Tests.cs`
- Test: `tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryIndexingTests.cs`
- Test: workflow tests under `tests/S1Atlas.Indexing.Tests/Workflow/`

**Interfaces:**
- Change `IndexSymbolRecord` to end with `bool? HasBody = null`.
- Persist SQL `symbols.has_body` as `NULL`, `0`, or `1`.
- Type/field/property/event records use `null`; method/constructor records use the exact `ManagedMemberFacts.HasBody` value.
- Bump `IndexingWorkflow.IndexSchemaVersion` from `7` to `8` so an unchanged game extraction creates a new index that actually contains the new required facts.

- [ ] **Step 1: Write migration tests before changing migration text**

Create tests that migrate a schema-6 DB and assert:

```sql
PRAGMA table_info(symbols);
```

contains `has_body`, with existing symbol rows preserved and `has_body IS NULL` for pre-migration rows.

- [ ] **Step 2: Run the migration test and verify failure**

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj -c Release --filter FullyQualifiedName~Milestone1
```

Expected: FAIL because migration 7 does not exist.

- [ ] **Step 3: Append migration 7 only**

Append SQL equivalent to:

```sql
ALTER TABLE symbols
ADD COLUMN has_body INTEGER NULL
CHECK (has_body IS NULL OR has_body IN (0, 1));
```

Register it after migration 6 without modifying migrations 1–6.

- [ ] **Step 4: Extend repository read/write tests**

Write a completed index containing a method with `HasBody=true`, a method with `HasBody=false`, and a type with `HasBody=null`; round-trip all three exactly.

- [ ] **Step 5: Update workflow symbol construction**

When building each member record, pass `member.HasBody` only for `Method`/`Constructor`; pass null otherwise. Increment `IndexSchemaVersion` to `8`.

- [ ] **Step 6: Verify migration, storage, and workflow gates**

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj -c Release
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter FullyQualifiedName~Workflow
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/S1Atlas.Core/Storage/IIndexRepository.cs src/S1Atlas.Indexing/Workflow/IndexingWorkflow.cs src/S1Atlas.Storage tests/S1Atlas.Storage.Tests tests/S1Atlas.Indexing.Tests/Workflow
git commit -m "feat: persist indexed method body availability"
```

---

### Task 3: Add bounded SQLite-backed symbol search and exact lookup

**Files:**
- Modify: `src/S1Atlas.Core/Storage/IIndexRepository.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs`
- Test: `tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryIndexingTests.cs`

**Interfaces:**
Add repository methods with these semantics:

```csharp
Task<IndexSymbolRecord?> GetCompletedSymbolByIdAsync(string indexId, string symbolId, CancellationToken cancellationToken);
Task<int> CountCompletedSymbolMatchesAsync(string indexId, string query, CancellationToken cancellationToken);
Task<IReadOnlyList<IndexSymbolRecord>> SearchCompletedSymbolsAsync(string indexId, string query, int limit, CancellationToken cancellationToken);
```

`SearchCompletedSymbolsAsync` must apply filtering, ranking, deterministic tie-breaks, and `LIMIT` in SQLite. Do not implement `GetCompletedSymbolsAsync(...).Take(limit)`.

- [ ] **Step 1: Write storage tests for exact ID, count, ranking, and limit**

Seed at least 100 matching rows plus exact and prefix matches. Assert:

```csharp
Assert.Equal(102, await repository.CountCompletedSymbolMatchesAsync(indexId, "Dealer", ct));
var page = await repository.SearchCompletedSymbolsAsync(indexId, "Dealer", 50, ct);
Assert.Equal(50, page.Count);
Assert.Equal(exactSymbolId, page[0].SymbolId);
```

Also assert deterministic ordering for equal-rank candidates.

- [ ] **Step 2: Run and verify failure**

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj -c Release --filter FullyQualifiedName~SqliteAtlasRepositoryIndexingTests
```

Expected: FAIL because the methods do not exist.

- [ ] **Step 3: Implement count/search SQL**

Use case-insensitive `qualified_name`/`signature` matching and a SQL `CASE` rank that preserves the current intent: exact name/signature first, exact terminal/member segment next, prefix next, substring next, signature-only last. Finish with binary `qualified_name`, `signature`, `symbol_id` tie-breaks. Validate `limit > 0`.

- [ ] **Step 4: Prove bounded retrieval**

Add a test with thousands of matching fixture symbols and assert only `limit` records are materialized by the returned API while `CountCompletedSymbolMatchesAsync` remains exact.

- [ ] **Step 5: Run storage suite and commit**

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj -c Release
```

```powershell
git add src/S1Atlas.Core/Storage/IIndexRepository.cs src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryIndexingTests.cs
git commit -m "feat: add bounded indexed symbol search"
```

---

### Task 4: Add deterministic single-symbol resolution and query result counts

**Files:**
- Modify: `src/S1Atlas.Core/Indexing/QueryModels.cs`
- Create: `src/S1Atlas.Indexing/Query/SymbolResolver.cs`
- Modify: `src/S1Atlas.Indexing/Query/IndexQueryService.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Query/SymbolResolverTests.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Query/IndexQueryServiceUsabilityTests.cs`

**Interfaces:**
Introduce simple shared records/enums, keeping names stable for later portal/MCP reuse:

```csharp
public enum SymbolResolutionStatus { Resolved, NotFound, Ambiguous }

public sealed record SymbolResolutionResult(
    SymbolResolutionStatus Status,
    SymbolQueryResult? Symbol,
    IReadOnlyList<SymbolQueryResult> Candidates);

public sealed record SymbolSearchResult(
    int TotalCount,
    int ReturnedCount,
    IReadOnlyList<SymbolQueryResult> Results);
```

`IndexQueryOptions` gains `int Limit = 50`.

Resolution order:
1. exact symbol ID;
2. exact canonical key/signature;
3. unique exact qualified-name/signature match;
4. unique best-ranked textual match; if two or more candidates share the best rank, return `Ambiguous`.

- [ ] **Step 1: Write resolver tests for all four rungs and tied best rank**

Include a case where two `Dealer` candidates have the same best rank and assert `Ambiguous`, never “first row wins”.

- [ ] **Step 2: Run and verify failure**

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter FullyQualifiedName~SymbolResolverTests
```

- [ ] **Step 3: Implement `SymbolResolver` over the repository APIs from Task 3**

Do not make CLI classes responsible for resolution.

- [ ] **Step 4: Change `SearchAsync` to return `SymbolSearchResult`**

Aggregate per-channel counts correctly when `--channel all` is requested; apply the requested total limit deterministically across ordered channel results rather than materializing every symbol.

- [ ] **Step 5: Run query tests and commit**

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter FullyQualifiedName~Query
```

```powershell
git add src/S1Atlas.Core/Indexing/QueryModels.cs src/S1Atlas.Indexing/Query tests/S1Atlas.Indexing.Tests/Query
git commit -m "feat: add deterministic symbol resolution"
```

---

### Task 5: Make `source` symbol-centric and integrity-checked

**Files:**
- Create: `src/S1Atlas.Indexing/Query/SourceSnippetReader.cs`
- Modify: `src/S1Atlas.Core/Indexing/QueryModels.cs`
- Modify: `src/S1Atlas.Indexing/Query/IndexQueryService.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Query/SourceSnippetReaderTests.cs`
- Modify: `tests/S1Atlas.Indexing.Tests/Query/IndexQueryServiceUsabilityTests.cs`

**Interfaces:**
Add:

```csharp
public enum MethodBodyAvailability { Available, UnavailableOrStubbed, Unknown }

public sealed record SourceSnippetQueryResult(
    SymbolQueryResult Symbol,
    string IndexId,
    string RelativePath,
    string Sha256,
    long ByteCount,
    SourceLocationQueryResult Location,
    int ContextBefore,
    int ContextAfter,
    string Text,
    MethodBodyAvailability BodyAvailability,
    string Provenance);
```

`SourceSnippetReader.ReadAsync(...)` receives an Atlas-owned absolute path, expected SHA-256, recorded location, and context count. It verifies hash before returning content.

- [ ] **Step 1: Write snippet-reader tests**

Cover exact span, five-line context, clipping at first/last line, CRLF/LF handling, invalid context, missing file, and hash mismatch.

- [ ] **Step 2: Run and verify failure**

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter FullyQualifiedName~SourceSnippetReaderTests
```

- [ ] **Step 3: Implement the reader without loading unrelated source files**

It is acceptable to read the selected generated file once; do not read every indexed source file for a symbol query.

- [ ] **Step 4: Replace `IndexQueryService.SourceAsync` file-wide behavior**

Resolve exactly one symbol via `SymbolResolver`, load its one `IndexSourceLocationRecord` and source file, resolve the final Atlas-owned path, verify SHA-256, then return one `SourceSnippetQueryResult`.

Map persisted `HasBody` as:

```csharp
true  => MethodBodyAvailability.Available
false => MethodBodyAvailability.UnavailableOrStubbed
null  => MethodBodyAvailability.Unknown
```

- [ ] **Step 5: Test ambiguity/not-found/integrity/body-status behavior**

Assert an ambiguous selector returns candidates and never a merged file list. Assert a stubbed method returns source declaration text plus `UnavailableOrStubbed`.

- [ ] **Step 6: Run query suite and commit**

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter FullyQualifiedName~Query
```

```powershell
git add src/S1Atlas.Core/Indexing/QueryModels.cs src/S1Atlas.Indexing/Query tests/S1Atlas.Indexing.Tests/Query
git commit -m "feat: add focused integrity-checked source queries"
```

---

### Task 6: Give `refs`, `callers`, and `callees` distinct semantics with enriched endpoints

**Files:**
- Modify: `src/S1Atlas.Core/Indexing/QueryModels.cs`
- Modify: `src/S1Atlas.Indexing/Query/IndexQueryService.cs`
- Modify: `src/S1Atlas.Core/Storage/IIndexRepository.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs`
- Modify: `tests/S1Atlas.Indexing.Tests/Query/IndexQueryServiceUsabilityTests.cs`
- Modify: `tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryIndexingTests.cs`

**Interfaces:**
Replace opaque-only relationship query results with an enriched record that retains the exact fields:

```csharp
public sealed record RelationshipEndpointQueryResult(
    string? SymbolId,
    string? QualifiedName,
    string? Signature,
    string? RawText,
    bool Resolved);

public sealed record RelationshipQueryResult(
    string RelationshipId,
    string Kind,
    string Evidence,
    string Direction,
    RelationshipEndpointQueryResult Source,
    RelationshipEndpointQueryResult Target);
```

Expose three service operations: `RefsAsync`, `CallersAsync`, `CalleesAsync`. Call-like kinds are the normalized `Calls` and `Constructs` kinds; do not use substring heuristics.

- [ ] **Step 1: Write fixture tests proving distinct semantics**

Seed inheritance, parameter type, call, construct, and field-read edges. Assert:
- `refs` returns incoming and outgoing relevant edges of all kinds;
- `callers` returns only incoming `Calls`/`Constructs` edges targeting the selected symbol;
- `callees` returns only outgoing `Calls`/`Constructs` edges sourced by the selected symbol.

- [ ] **Step 2: Add targeted storage lookups**

Do not load every relationship and every symbol for the index. Add source/target filtered queries and batch endpoint symbol lookup sufficient for the selected result set.

- [ ] **Step 3: Preserve unresolved targets**

When `target_symbol_id` is null, return `Resolved=false` with exact `target_text`. Never guess a symbol.

- [ ] **Step 4: Add availability/completeness notices to the query result model**

For callers/callees, carry selected-symbol body availability plus a boolean/note that callers are limited to call sites whose targets resolved to the selected symbol. Do not convert zero edges to a definitive “none call this” claim.

- [ ] **Step 5: Run query/storage suites and commit**

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj -c Release
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter FullyQualifiedName~Query
```

```powershell
git add src/S1Atlas.Core src/S1Atlas.Indexing/Query src/S1Atlas.Storage tests/S1Atlas.Indexing.Tests/Query tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryIndexingTests.cs
git commit -m "feat: clarify indexed relationship queries"
```

---

### Task 7: Polish CLI rendering, limits, ambiguity, and source-file escape hatch

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

**Interfaces / exact CLI behavior:**
- `--limit <n>` default `50`; reject `n <= 0` with `InvalidLimit`.
- `source --context <n>` default `5`; reject negative values with `InvalidContext`.
- `source --file` requests the complete selected source file.
- Full-file stdout is capped at **1,048,576 bytes**. If larger and `--output` is absent, return `SourceTooLargeForTerminal` and instruct the user to provide `--output <path>`.
- `source --output <path>` writes the verified source content to that user-selected path; it never writes inside the Schedule I installation unless the user explicitly supplies such a path, and it does not alter Atlas authority.
- Use stable error codes including `AmbiguousSymbol`, `SymbolNotFound`, `NoCompletedIndex`, `SourceUnavailable`, `SourceIntegrityFailure`, `InvalidCodebaseChannel`, `InstalledDependencyMissing`, and `UpstreamUnavailable` where those outcomes occur.

- [ ] **Step 1: Write CLI integration tests for search counts/limit and ambiguity**

Assert human output contains both readable names and symbol IDs; JSON contains `totalCount`, `returnedCount`, and structured candidates.

- [ ] **Step 2: Write CLI source tests**

Assert focused snippet output contains exact ID/signature/path/hash/range/body availability; test `--context 0`; test oversized `--file` refusal and successful `--output` write.

- [ ] **Step 3: Write relationship output tests**

Human output must contain readable source/target names **and** exact symbol IDs, relationship ID, kind, evidence, and unresolved target text when applicable.

- [ ] **Step 4: Implement the thin renderers/options**

Keep semantic decisions in the shared query layer. Do not duplicate ranking, symbol resolution, body classification, or relationship filtering in command classes.

- [ ] **Step 5: Run integration tests and commit**

```powershell
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj -c Release --filter FullyQualifiedName~IndexingCliUsabilityTests
```

```powershell
git add src/S1Atlas.Cli tests/S1Atlas.IntegrationTests/IndexingCliUsabilityTests.cs
git commit -m "feat: polish Atlas index query CLI"
```

---

### Task 8: Complete minimal installed S1API/S1MAPI indexing

**Files:**
- Create: `src/S1Atlas.Indexing/Workflow/ApiIndexingWorkflow.cs`
- Modify: `src/S1Atlas.Cli/Commands/IndexCommand.cs`
- Modify: composition/wiring in `src/S1Atlas.Cli/CliApplication.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Workflow/ApiIndexingWorkflowTests.cs`
- Modify: `tests/S1Atlas.IntegrationTests/IndexingCliUsabilityTests.cs`

**Interfaces:**
`ApiIndexingWorkflow` exposes an installed-binary operation conceptually equivalent to:

```csharp
Task<IndexingWorkflowResult> RunInstalledAsync(
    CodebaseKind codebase,
    string environmentSnapshotId,
    string assemblyPath,
    bool force,
    CancellationToken cancellationToken);
```

Only `S1Api` and `S1MApi` are accepted. The caller must pass the path discovered in the current environment snapshot. The workflow hashes/decompiles that exact binary and writes into the existing `code_snapshots/index_runs/symbols/source_files/source_locations/...` model using `CodeChannel.Installed` and the same ILSpy adapter.

- [ ] **Step 1: Write installed API workflow tests**

Use a fixture managed assembly and assert codebase/channel/environment snapshot provenance, symbols, source, spans, and `HasBody` persist correctly.

- [ ] **Step 2: Verify failure before implementation**

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter FullyQualifiedName~ApiIndexingWorkflowTests
```

- [ ] **Step 3: Implement the minimal installed workflow by reusing existing decompiler/writer/fingerprint primitives**

Do not copy Schedule I extraction-authority logic into the API workflow; installed API authority is the discovered binary from the current environment snapshot.

- [ ] **Step 4: Extend `s1atlas index` options**

Preserve this exact compatibility rule:

```text
s1atlas index
```

still means Schedule I / Installed.

Add:

```text
s1atlas index --codebase s1api --channel installed
s1atlas index --codebase s1mapi --channel installed
```

Resolve the binary only from the current environment snapshot. If absent, return `InstalledDependencyMissing`; do not fetch an upstream substitute.

- [ ] **Step 5: Run workflow/integration tests and commit**

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter FullyQualifiedName~ApiIndexingWorkflowTests
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj -c Release --filter FullyQualifiedName~IndexingCliUsabilityTests
```

```powershell
git add src/S1Atlas.Indexing/Workflow/ApiIndexingWorkflow.cs src/S1Atlas.Cli tests/S1Atlas.Indexing.Tests/Workflow/ApiIndexingWorkflowTests.cs tests/S1Atlas.IntegrationTests/IndexingCliUsabilityTests.cs
git commit -m "feat: index installed Schedule I APIs"
```

---

### Task 9: Index cached Release/Preview source without executing upstream code

**Files:**
- Modify: `src/S1Atlas.Indexing/Workflow/ApiIndexingWorkflow.cs`
- Modify: `src/S1Atlas.Cli/Commands/IndexCommand.cs`
- Use existing: `src/S1Atlas.Indexing/Upstream/UpstreamSnapshotCache.cs`
- Use existing: `src/S1Atlas.Indexing/Source/RoslynSourceIndexer.cs`
- Modify: `tests/S1Atlas.Indexing.Tests/Workflow/ApiIndexingWorkflowTests.cs`
- Modify: `tests/S1Atlas.IntegrationTests/IndexingCliUsabilityTests.cs`

**Interfaces / behavior:**
Add a cached-source operation:

```csharp
Task<IndexingWorkflowResult> RunCachedSourceAsync(
    CodebaseKind codebase,
    CodeChannel channel,
    string commitSha,
    bool force,
    CancellationToken cancellationToken);
```

Accept only `S1Api`/`S1MApi` and only `Release`/`Preview`. The commit must already exist in `UpstreamSnapshotCache`; this method makes no network request. Each cached `.cs` file is parsed as data with Roslyn. No `dotnet restore`, MSBuild, source generators, scripts, or repository code execution.

- [ ] **Step 1: Write cached-source workflow tests**

Seed a fake cached commit with multiple `.cs` files, plus non-C# files. Assert only C# source is indexed; file provenance and exact commit SHA are retained; Release and Preview become different `CodeSnapshotRecord` channels even if they reference the same cached commit.

- [ ] **Step 2: Add CLI behavior**

Support:

```text
s1atlas index --codebase s1api --channel release --commit <40-char-sha>
s1atlas index --codebase s1api --channel preview --commit <40-char-sha>
s1atlas index --codebase s1mapi --channel release --commit <40-char-sha>
s1atlas index --codebase s1mapi --channel preview --commit <40-char-sha>
```

Require an exact cached commit SHA for Release/Preview in this milestone; `upstream sync ... --commit` remains the explicit network operation. Reject Schedule I Release/Preview with `InvalidCodebaseChannel`.

- [ ] **Step 3: Prove offline behavior**

Integration-test Release/Preview indexing with an HTTP client that would throw if invoked; indexing must still pass because it reads cache only.

- [ ] **Step 4: Run tests and commit**

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter FullyQualifiedName~ApiIndexingWorkflowTests
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj -c Release --filter FullyQualifiedName~IndexingCliUsabilityTests
```

```powershell
git add src/S1Atlas.Indexing/Workflow/ApiIndexingWorkflow.cs src/S1Atlas.Cli/Commands/IndexCommand.cs tests/S1Atlas.Indexing.Tests/Workflow/ApiIndexingWorkflowTests.cs tests/S1Atlas.IntegrationTests/IndexingCliUsabilityTests.cs
git commit -m "feat: index cached API release and preview source"
```

---

### Task 10: Run the reproducible usability/API smoke and document only proven coverage

**Files:**
- Create after execution: `docs/smoke-tests/2026-08-14-v1-milestone1-polish-usability.md`
- Modify only if needed for truthful usage docs: `README.md`

**Prerequisites:**
- Work from the real Windows Schedule I environment and Atlas data.
- Use the representative baseline queries embedded in the approved Milestone 1 design (Property, Employee, Delivery, Storage, and transit/routing examples).
- Do not install S1API or S1MAPI merely to satisfy the matrix unless the operator explicitly chooses to do so.

- [ ] **Step 1: Rebuild/reuse Schedule I with the new index schema**

```powershell
dotnet run -c Release --project src\S1Atlas.Cli -- index
```

Expected: a schema-8 index is created once, then a repeat run reuses it.

- [ ] **Step 2: Run representative usability queries**

At minimum:

```powershell
dotnet run -c Release --project src\S1Atlas.Cli -- search Property --limit 10
dotnet run -c Release --project src\S1Atlas.Cli -- source "ScheduleOne.Property.Property::SetOwned()" --context 5
dotnet run -c Release --project src\S1Atlas.Cli -- refs "ScheduleOne.Property.Property"
dotnet run -c Release --project src\S1Atlas.Cli -- callers "ScheduleOne.Property.Property::SetOwned()"
dotnet run -c Release --project src\S1Atlas.Cli -- callees "ScheduleOne.Property.Property::SetOwned()"
```

Record exact result counts, whether the selector resolved or required disambiguation, body availability, and whether relationship incompleteness is clearly explained. Do not commit proprietary source text; record metadata/counts and sanitized examples only.

- [ ] **Step 3: Validate Installed API channels only where binaries are present**

Run `env`. For each actually installed API:

```powershell
dotnet run -c Release --project src\S1Atlas.Cli -- index --codebase s1api --channel installed
```

or the S1MAPI equivalent. If absent, record `Not present`; that is not a Milestone 1 failure.

- [ ] **Step 4: Validate real Release/Preview commits**

Resolve the official release-tag commit and current default-branch commit for S1API and S1MAPI, then explicitly sync each exact SHA and index the cached channels:

```powershell
dotnet run -c Release --project src\S1Atlas.Cli -- upstream sync s1api --commit <release-sha>
dotnet run -c Release --project src\S1Atlas.Cli -- index --codebase s1api --channel release --commit <release-sha>
dotnet run -c Release --project src\S1Atlas.Cli -- upstream sync s1api --commit <preview-sha>
dotnet run -c Release --project src\S1Atlas.Cli -- index --codebase s1api --channel preview --commit <preview-sha>
```

Repeat for S1MAPI. After sync, prove repeated index/query operations make no network request by disconnecting/guarding network or using cached state only.

- [ ] **Step 5: Prove channel separation**

Query at least one symbol in Installed/Release/Preview where available and record codebase, channel, snapshot/index ID, and commit/binary source identity. Confirm no query labels Release/Preview as Installed.

- [ ] **Step 6: Write the smoke report**

Include a matrix with only `PASS`, `Not present`, or `FAIL`, plus sanitized evidence. Explicitly state the known Cpp2IL caller/callee limitations.

- [ ] **Step 7: Commit smoke/docs**

```powershell
git add docs/smoke-tests/2026-08-14-v1-milestone1-polish-usability.md README.md
git commit -m "docs: record Milestone 1 usability smoke"
```

If README required no changes, omit it from `git add`.

---

### Task 11: Final Milestone 1 verification and hygiene gate

**Files:**
- No new production scope. Fix only defects revealed by the gate.

- [ ] **Step 1: Run Release build with warnings as errors**

```powershell
dotnet build S1Atlas.sln -c Release -warnaserror
```

Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Run the complete test suite**

```powershell
dotnet test S1Atlas.sln -c Release --no-build
```

Expected: 0 failed, 0 skipped unless a pre-existing explicitly documented skip exists.

- [ ] **Step 3: Run format verification**

```powershell
dotnet format S1Atlas.sln --verify-no-changes
```

Expected: exit 0.

- [ ] **Step 4: Run repository hygiene checks**

```powershell
git diff --check
git status --short
git ls-files | Select-String -Pattern '\.(dll|exe|db|db-shm|db-wal|pdb)$|(^|/)(data|artifacts|logs|\.staging)/'
```

Expected: clean worktree after commits; no generated/proprietary binaries, DBs, logs, cached upstream source, or Schedule I source tracked.

- [ ] **Step 5: Re-run the highest-risk focused tests**

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj -c Release --no-build
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Query|FullyQualifiedName~ApiIndexingWorkflow|FullyQualifiedName~RoslynSourceIndexer"
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~IndexingCliUsabilityTests
```

Expected: PASS.

- [ ] **Step 6: Verify the approved non-goals remain absent**

Review the diff and reject any accidental Scene Intelligence, diffing, portal, MCP, agent-skill, semantic-search, runtime-probe, TUI, plugin, or generalized multi-game implementation.

- [ ] **Step 7: Final commit only if the verification gate required fixes**

```powershell
git add -A
git commit -m "fix: close Milestone 1 verification gaps"
```

Do not create an empty commit.

---

## Plan Self-Review Checklist

Before implementation begins, verify:

- Every approved spec goal maps to at least one task.
- Tasks 1–2 explicitly capture the two facts the current pipeline discards: precise source spans and method-body availability.
- Migration 7 is additive and migrations 1–6 remain byte-identical.
- Index schema version changes so a stale schema-7 completed index is not reused as though it contains the new facts.
- Search uses SQL count/ranked limit rather than load-all-then-truncate.
- Rung-4 symbol selection treats tied best rank as ambiguity.
- `refs`, `callers`, and `callees` are distinct and call-like filtering uses normalized kinds.
- Relationship output retains exact IDs/signatures/evidence and unresolved target text.
- `source` verifies the expected SHA before showing content.
- Oversized full-file source cannot accidentally dump multi-megabyte output to a terminal.
- Installed API absence is reported honestly and never filled by Release/Preview.
- Release/Preview indexing reads only previously cached immutable source and executes no upstream code.
- The real smoke report contains no proprietary source content or absolute private paths.
- No later V1 milestone has leaked into this plan.
