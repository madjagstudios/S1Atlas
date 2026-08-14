# S1Atlas V1 Milestone 1 — Polish & Usability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the merged code index into a precise, bounded, developer-friendly query experience while preserving exact technical identity and proving the S1API/S1MAPI Installed/Release/Preview model against real inputs.

**Architecture:** Keep the existing extraction/index authority model. Add only the missing persisted facts: complete source spans and a conservative method-body recovery classification that distinguishes bodies absent by design, meaningfully recovered bodies, concrete bodies that are absent/verified stubs, and cases where Atlas cannot safely decide. Then enrich the shared query layer with deterministic symbol resolution, bounded SQLite-backed search, focused source retrieval, and precise relationship semantics. The CLI remains a thin human/JSON renderer over those shared results. API channel validation uses the same normalized storage model; it does not introduce a second index or execute upstream repositories.

**Tech Stack:** C# / .NET 8, xUnit v3, Microsoft.Data.Sqlite, Microsoft.CodeAnalysis.CSharp, ICSharpCode.Decompiler, System.CommandLine.

## Global Constraints

- Follow `docs/superpowers/specs/2026-08-14-v1-milestone1-polish-usability-design.md` exactly, including its review clarifications.
- Preserve the principle: **progressive readability, not progressive disclosure of truth**.
- Normal local queries make no network calls. GitHub access remains explicit through `upstream sync` unless a user has explicitly opted into on-use checking.
- Schedule I Installed facts come only from the preferred integrity-verified extraction.
- Release/Preview never substitute for Installed.
- Do not add Scene Intelligence, diffing, the HTML portal, MCP, the agent skill, semantic/vector search, runtime probing, a TUI, a plugin architecture, or generalized multi-game support.
- Do not execute or build upstream S1API/S1MAPI repositories. Parse cached source as data only.
- Preserve existing database migrations 1–6 byte-for-byte. Append database migration 7 only.
- `IndexingWorkflow.IndexSchemaVersion` is a separate recipe-identity counter: it moves from 7 to 8 in this milestone. Never call that database migration 8.
- A missing installed S1API/S1MAPI binary is a valid `Not present` outcome and does not block Release/Preview validation.
- Empty callers/callees must never be presented as proof of no calls when recovered body data is unavailable/unknown or target resolution is incomplete.
- A method is never called “recovered” merely because its RVA is non-zero or because it has at least one body byte.
- A method is never called “stubbed” merely because it has zero recovered references; valid methods can contain useful logic without calls.
- The Schedule I installation remains read-only. `source --output` must refuse any destination that resolves under the detected game installation root, even when explicitly supplied by the user.
- Every task ends with a focused green test gate and a small commit.

---

## File Structure

### Core / storage contracts

- Modify `src/S1Atlas.Core/Indexing/NormalizedSymbol.cs` — retain the existing source start-line field and add precise start/end columns/line range.
- Modify `src/S1Atlas.Core/Indexing/DecompilerModels.cs` — add conservative method-body recovery facts/classification for managed methods.
- Modify `src/S1Atlas.Core/Storage/IIndexRepository.cs` — add nullable persisted body-recovery status and bounded symbol-query contracts.
- Modify `src/S1Atlas.Core/Indexing/QueryModels.cs` — richer symbol selection, source, relationship, counts, and body-recovery results.

### Indexing

- Modify `src/S1Atlas.Indexing/Decompilation/IlSpyManagedDecompiler.cs` — collect metadata/body facts and classify recovery conservatively.
- Create `src/S1Atlas.Indexing/Decompilation/BodyRecoveryClassifier.cs` — pure, fixture-testable body classification; no UI wording or confidence scoring.
- Modify `src/S1Atlas.Indexing/Source/RoslynSourceIndexer.cs` — capture real Roslyn start/end positions.
- Modify `src/S1Atlas.Indexing/Workflow/IndexingWorkflow.cs` — persist complete source spans and body-recovery status; bump **index schema version** from 7 to 8 so old completed indexes are not silently reused as if they contain the new facts.
- Create `src/S1Atlas.Indexing/Query/SymbolResolver.cs` — deterministic single-symbol resolution and ambiguity reporting.
- Create `src/S1Atlas.Indexing/Query/SourceSnippetReader.cs` — hash-verified bounded source reads and snippet extraction.
- Modify `src/S1Atlas.Indexing/Query/IndexQueryService.cs` — bounded search, focused source, refs/callers/callees semantics, endpoint enrichment, availability status.
- Create `src/S1Atlas.Indexing/Workflow/ApiIndexingWorkflow.cs` — minimal installed-binary and cached-upstream indexing into the existing normalized model. This is a small new capability required to satisfy the already-approved API-channel acceptance criteria; it is intentionally not generalized beyond S1API/S1MAPI.

### Storage

- Modify `src/S1Atlas.Storage/Migrations/SqliteMigrations.cs` — append **database migration 7** adding `symbols.body_recovery_status`; add query-support indexes only if measurement shows they are needed.
- Modify `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs` — read/write body recovery, exact lookup, SQL count, ranked limited symbol search, and targeted relationship/source lookups.

### CLI

- Modify `src/S1Atlas.Cli/Commands/IndexQueryCommandFactory.cs` — shared `--limit` and precise errors where applicable.
- Modify `src/S1Atlas.Cli/Commands/SourceCommand.cs` — `--context`, `--file`, safe `--output`, and installation-root refusal.
- Modify `src/S1Atlas.Cli/Commands/RefsCommand.cs`, `CallersCommand.cs`, `CalleesCommand.cs` — call the distinct query operations.
- Modify `src/S1Atlas.Cli/Commands/IndexCommand.cs` — minimal codebase/channel/commit selection for API indexing while preserving `s1atlas index` as Schedule I Installed by default.
- Modify `src/S1Atlas.Cli/Output/IndexQueryOutputModels.cs` and the existing command renderer path — totals, candidates, readable endpoints plus exact IDs/signatures/evidence.

### Tests / docs

- Modify `tests/S1Atlas.Indexing.Tests/Source/RoslynSourceIndexerTests.cs`.
- Modify `tests/S1Atlas.Indexing.Tests/Decompilation/IlSpyManagedDecompilerTests.cs`.
- Create `tests/S1Atlas.Indexing.Tests/Decompilation/BodyRecoveryClassifierTests.cs`.
- Modify or create focused workflow tests under `tests/S1Atlas.Indexing.Tests/Workflow/`.
- Create `tests/S1Atlas.Indexing.Tests/Query/SymbolResolverTests.cs`.
- Create `tests/S1Atlas.Indexing.Tests/Query/SourceSnippetReaderTests.cs`.
- Create `tests/S1Atlas.Indexing.Tests/Query/IndexQueryServiceUsabilityTests.cs`.
- Modify `tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryIndexingTests.cs`.
- Create `tests/S1Atlas.Storage.Tests/Migrations/SqliteMigrationRunnerMilestone1Tests.cs`.
- Update every existing storage migration checksum/count/pinning/backup expectation affected solely by appending database migration 7; do not rewrite old migration SQL.
- Create `tests/S1Atlas.IntegrationTests/IndexingCliUsabilityTests.cs`.
- Create `docs/smoke-tests/2026-08-14-v1-milestone1-polish-usability.md` only after the real smoke is run.

---

### Task 1: Capture precise source spans

**Files:**
- Modify: `src/S1Atlas.Core/Indexing/NormalizedSymbol.cs`
- Modify: `src/S1Atlas.Indexing/Source/RoslynSourceIndexer.cs`
- Modify: `src/S1Atlas.Indexing/Workflow/IndexingWorkflow.cs`
- Test: `tests/S1Atlas.Indexing.Tests/Source/RoslynSourceIndexerTests.cs`
- Test: `tests/S1Atlas.Indexing.Tests/Workflow/IndexingWorkflowTests.cs`

**Interfaces:**
- Extend `NormalizedSymbol` with `int? SourceColumn`, `int? SourceEndLine`, and `int? SourceEndColumn`; retain `SourceLine` as the 1-based start line for compatibility.
- `BuildSourceLocations` must populate all six `IndexSourceLocationRecord` fields from those values.

- [ ] **Step 1: Write failing Roslyn span tests**

Add a multiline fixture and assert exact 1-based positions:

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
Assert.Equal(5, method.SourceColumn);
Assert.Equal(7, method.SourceEndLine);
Assert.NotNull(method.SourceEndColumn);
```

Also test a one-line member and a type declaration.

- [ ] **Step 2: Run the focused test and verify failure**

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

### Task 2: Persist honest method-body recovery status with database migration 7

**Files:**
- Modify: `src/S1Atlas.Core/Indexing/DecompilerModels.cs`
- Modify: `src/S1Atlas.Core/Storage/IIndexRepository.cs`
- Modify: `src/S1Atlas.Indexing/Decompilation/IlSpyManagedDecompiler.cs`
- Create: `src/S1Atlas.Indexing/Decompilation/BodyRecoveryClassifier.cs`
- Modify: `src/S1Atlas.Indexing/Workflow/IndexingWorkflow.cs`
- Modify: `src/S1Atlas.Storage/Migrations/SqliteMigrations.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs`
- Test: `tests/S1Atlas.Indexing.Tests/Decompilation/BodyRecoveryClassifierTests.cs`
- Test: `tests/S1Atlas.Indexing.Tests/Decompilation/IlSpyManagedDecompilerTests.cs`
- Test: `tests/S1Atlas.Storage.Tests/Migrations/SqliteMigrationRunnerMilestone1Tests.cs`
- Test: `tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryIndexingTests.cs`
- Test: existing migration pinning/checksum/count tests under `tests/S1Atlas.Storage.Tests/Migrations/`
- Test: workflow tests under `tests/S1Atlas.Indexing.Tests/Workflow/`

**Interfaces:**

Add a small core enum:

```csharp
public enum BodyRecoveryStatus
{
    NoBodyByDesign,
    Recovered,
    StubOrUnavailable,
    Unknown
}
```

`IndexSymbolRecord` ends with `BodyRecoveryStatus? BodyRecoveryStatus = null`. Non-method symbols use `null`. New method/constructor rows always persist one of the four values. Legacy rows migrated from database migration 6 remain SQL `NULL` and are surfaced by the query layer as `Unknown`.

Add only the method facts required for classification, such as:

```csharp
public sealed record ManagedMethodBodyFacts(
    bool HasPhysicalBody,
    bool NoBodyByDesign,
    int IlByteCount,
    int InstructionCount,
    int RecoveredReferenceCount,
    bool MatchesVerifiedStubPattern);
```

`BodyRecoveryClassifier.Classify(ManagedMethodBodyFacts facts)` is pure and deterministic.

Classification rules are deliberately conservative:

```text
NoBodyByDesign
  metadata says the missing body is intentional (for example abstract, P/Invoke,
  runtime/internal-call), with no physical body expected.

StubOrUnavailable
  a concrete method has no physical body, an empty IL body, or matches a stub
  pattern that has been explicitly represented by a regression fixture.

Recovered
  a physical body contains affirmative non-trivial recovered IL evidence.
  A recovered reference is sufficient evidence but is NOT required; arithmetic,
  branches, locals, constants/returns, field operations, and other real IL may
  establish recovery without calls.

Unknown
  a physical body exists but the classifier cannot safely distinguish meaningful
  recovery from a stub. Unknown is preferred over false confidence.
```

Do **not** classify `Recovered` from RVA alone. Do **not** classify `StubOrUnavailable` from `RecoveredReferenceCount == 0` alone. Do not add a numerical confidence score.

- [ ] **Step 1: Write classifier tests before persistence work**

Cover at minimum:

```csharp
Assert.Equal(BodyRecoveryStatus.NoBodyByDesign,
    classifier.Classify(new(false, true, 0, 0, 0, false)));

Assert.Equal(BodyRecoveryStatus.StubOrUnavailable,
    classifier.Classify(new(false, false, 0, 0, 0, false)));

Assert.Equal(BodyRecoveryStatus.Recovered,
    classifier.Classify(new(true, false, 8, 3, 0, false)));

Assert.Equal(BodyRecoveryStatus.Unknown,
    classifier.Classify(new(true, false, 1, 1, 0, false)));
```

Also prove a zero-reference method with several non-trivial IL instructions can be `Recovered`, and a physical body cannot become `StubOrUnavailable` only because `RecoveredReferenceCount` is zero.

- [ ] **Step 2: Add decompiler fixture coverage**

Use fixture methods for: normal arithmetic/return logic, an abstract method, an intentionally empty/trivial method, and a method containing calls. Assert metadata/body facts are read without `Assembly.Load` and classifications match the conservative rules.

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter "FullyQualifiedName~BodyRecoveryClassifierTests|FullyQualifiedName~IlSpyManagedDecompilerTests"
```

Expected: FAIL before implementation, then PASS after the classifier/decompiler changes.

- [ ] **Step 3: Write database migration-7 tests before changing migration text**

Create tests that migrate a **database at migration level 6** and assert:

```sql
PRAGMA table_info(symbols);
```

contains `body_recovery_status`; existing symbol rows are preserved; old rows have `body_recovery_status IS NULL`; and SQLite accepts the new-column `CHECK` constraint with existing NULL rows.

- [ ] **Step 4: Append database migration 7 only**

Append SQL equivalent to:

```sql
ALTER TABLE symbols
ADD COLUMN body_recovery_status TEXT NULL
CHECK (
    body_recovery_status IS NULL OR
    body_recovery_status IN ('NoBodyByDesign','Recovered','StubOrUnavailable','Unknown')
);
```

Register it after database migration 6 without modifying migrations 1–6.

- [ ] **Step 5: Update migration-ledger pinning tests**

Update the existing committed/golden expectations that mechanically change when a migration is appended: expected migration count/range `1..6` → `1..7`, committed checksum list with the exact migration-7 checksum, any `Take(6)`/equivalent assertions, and any backup/schema-level fixture/glob names that now refer to the database reaching migration level 7. Preserve every checksum for migrations 1–6 exactly.

Run:

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj -c Release --filter FullyQualifiedName~Migration
```

Expected: PASS.

- [ ] **Step 6: Extend repository round-trip tests**

Write a completed index containing method rows for all four statuses and a type row with null status. Round-trip exactly. Verify legacy SQL NULL maps to query-level `Unknown` only for callable symbols, not to a fabricated status on types/fields/properties/events.

- [ ] **Step 7: Update Schedule I workflow symbol construction and index recipe identity**

Persist the exact decompiler classification for methods/constructors; use null for non-callable symbols. Increment `IndexingWorkflow.IndexSchemaVersion` from `7` to `8`. This is **index schema version 8**, not database migration 8.

- [ ] **Step 8: Verify storage/workflow gates and commit**

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj -c Release
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter "FullyQualifiedName~Workflow|FullyQualifiedName~Decompilation"
```

```powershell
git add src/S1Atlas.Core src/S1Atlas.Indexing/Decompilation src/S1Atlas.Indexing/Workflow/IndexingWorkflow.cs src/S1Atlas.Storage tests/S1Atlas.Storage.Tests tests/S1Atlas.Indexing.Tests
git commit -m "feat: persist method body recovery status"
```

---

### Task 3: Add bounded SQLite-backed symbol search and exact lookup

**Files:**
- Modify: `src/S1Atlas.Core/Storage/IIndexRepository.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs`
- Test: `tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryIndexingTests.cs`

**Interfaces:**

```csharp
Task<IndexSymbolRecord?> GetCompletedSymbolByIdAsync(string indexId, string symbolId, CancellationToken cancellationToken);
Task<int> CountCompletedSymbolMatchesAsync(string indexId, string query, CancellationToken cancellationToken);
Task<IReadOnlyList<IndexSymbolRecord>> SearchCompletedSymbolsAsync(string indexId, string query, int limit, CancellationToken cancellationToken);
```

`SearchCompletedSymbolsAsync` must apply filtering, ranking, deterministic tie-breaks, and `LIMIT` in SQLite. Do not implement `GetCompletedSymbolsAsync(...).Take(limit)`.

- [ ] **Step 1: Write storage tests for exact ID, count, ranking, and limit**

Seed at least 100 matching rows plus exact and prefix matches. Assert exact count, a 50-row page, exact match first, and deterministic equal-rank ordering.

- [ ] **Step 2: Run and verify failure**

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj -c Release --filter FullyQualifiedName~SqliteAtlasRepositoryIndexingTests
```

- [ ] **Step 3: Implement count/search SQL**

Use case-insensitive `qualified_name`/`signature` matching and a SQL `CASE` rank: exact name/signature, exact terminal/member segment, prefix, substring, signature-only. Finish with binary `qualified_name`, `signature`, `symbol_id` tie-breaks. Validate `limit > 0`.

- [ ] **Step 4: Prove bounded retrieval**

Add a test with thousands of matching fixture symbols and assert only `limit` records are returned/materialized while `CountCompletedSymbolMatchesAsync` remains exact.

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
4. unique best-ranked textual match; any tie at the best rank returns `Ambiguous`.

- [ ] **Step 1: Write resolver tests for all four rungs and tied best rank**

Include two `Dealer` candidates at the same best rank and assert `Ambiguous`, never first-row-wins.

- [ ] **Step 2: Run and verify failure**

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter FullyQualifiedName~SymbolResolverTests
```

- [ ] **Step 3: Implement `SymbolResolver` over Task-3 repository APIs**

Do not make CLI classes responsible for resolution.

- [ ] **Step 4: Change `SearchAsync` to return `SymbolSearchResult`**

For `--channel all`:
- `totalCount` is the sum of matching counts across the selected channels;
- results are merged into one **global rank order**, not channel-then-rank;
- apply one total `Limit` after deterministic global comparison of rank, qualified name, signature, channel, and symbol ID;
- fetch only enough bounded candidates from each channel to produce the global page; never materialize every symbol.

Add a test where a better-ranked Preview result beats a worse-ranked Release result even if Release is enumerated first.

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

Expose the detailed persisted status rather than reducing it to a misleading binary body flag:

```csharp
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
    BodyRecoveryStatus? BodyRecoveryStatus,
    string Provenance);
```

For a callable symbol from a legacy index where SQL status is null, return `BodyRecoveryStatus.Unknown`. For a non-callable symbol, keep the property null/not-applicable.

`SourceSnippetReader.ReadAsync(...)` receives an Atlas-owned absolute path, expected SHA-256, recorded location, and context count. It verifies the hash before returning content.

- [ ] **Step 1: Write snippet-reader tests**

Cover exact span, five-line context, clipping at first/last line, CRLF/LF handling, invalid context, missing file, and hash mismatch.

- [ ] **Step 2: Run and verify failure**

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --filter FullyQualifiedName~SourceSnippetReaderTests
```

- [ ] **Step 3: Implement the reader without loading unrelated source files**

It is acceptable to read the selected generated file once; do not read every indexed source file for a symbol query.

- [ ] **Step 4: Replace file-wide `SourceAsync` behavior**

Resolve exactly one symbol, load its one `IndexSourceLocationRecord` and source file, resolve the final Atlas-owned path, verify SHA-256, then return one `SourceSnippetQueryResult`.

- [ ] **Step 5: Test ambiguity/not-found/integrity/body-status behavior**

Assert an ambiguous selector returns candidates and never a merged file list. Assert `NoBodyByDesign`, `Recovered`, `StubOrUnavailable`, and `Unknown` are rendered/serialized distinctly; `Unknown` carries the warning that an empty call set is not definitive.

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

Expose `RefsAsync`, `CallersAsync`, `CalleesAsync`. Call-like kinds are normalized `Calls` and `Constructs`; do not use substring heuristics.

- [ ] **Step 1: Write fixture tests proving distinct semantics**

Seed inheritance, parameter type, call, construct, and field-read edges. Assert `refs` returns relevant incoming/outgoing edges of all kinds, `callers` only incoming call-like edges targeting the selected symbol, and `callees` only outgoing call-like edges sourced by it.

- [ ] **Step 2: Add targeted storage lookups**

Do not load every relationship and every symbol for the index. Add source/target filtered queries and batch endpoint lookup sufficient for the selected result set.

- [ ] **Step 3: Preserve unresolved targets**

When `target_symbol_id` is null, return `Resolved=false` with exact `target_text`. Never guess a symbol.

- [ ] **Step 4: Carry honest completeness notices**

For callers/callees, carry the selected callable's `BodyRecoveryStatus` plus an explicit note/flag that incoming callers are limited to call sites whose target resolved to the selected symbol. Human output rules:
- `Recovered`: Atlas has affirmative recovered-body evidence, but caller completeness is still bounded by target resolution.
- `StubOrUnavailable` or `Unknown`: zero calls are explicitly non-definitive.
- `NoBodyByDesign`: explain that no implementation body is expected for that declaration.

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

- [ ] **Step 1: Write CLI integration tests for search counts/limit and ambiguity**

Assert human output contains readable names and exact symbol IDs; JSON contains `totalCount`, `returnedCount`, and structured candidates.

- [ ] **Step 2: Write CLI source tests, including the hard read-only guard**

Assert focused snippet output contains exact ID/signature/path/hash/range/body-recovery status; test `--context 0`; test oversized `--file` refusal and successful safe `--output` write. Add a test that supplies a path under a fixture Schedule I installation root and assert `ReadOnlyGameInstallation` with **no file created**.

- [ ] **Step 3: Write relationship output tests**

Human output must contain readable source/target names **and** exact symbol IDs, relationship ID, kind, evidence, and unresolved target text when applicable.

- [ ] **Step 4: Implement thin renderers/options**

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

### Task 8: Complete the minimal installed S1API/S1MAPI indexing capability required by V1 Milestone 1

This is real new capability, not presentation polish. It is included because the approved V1 acceptance matrix cannot be exercised otherwise. Keep it intentionally narrow: S1API/S1MAPI only, existing normalized index model only, no generic plugin/adapter framework.

**Files:**
- Create: `src/S1Atlas.Indexing/Workflow/ApiIndexingWorkflow.cs`
- Modify: `src/S1Atlas.Cli/Commands/IndexCommand.cs`
- Modify: composition/wiring in `src/S1Atlas.Cli/CliApplication.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Workflow/ApiIndexingWorkflowTests.cs`
- Modify: `tests/S1Atlas.IntegrationTests/IndexingCliUsabilityTests.cs`

**Interfaces:**

```csharp
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
git add src/S1Atlas.Indexing/Workflow/ApiIndexingWorkflow.cs src/S1Atlas.Cli tests/S1Atlas.Indexing.Tests/Workflow/ApiIndexingWorkflowTests.cs tests/S1Atlas.IntegrationTests/IndexingCliUsabilityTests.cs
git commit -m "feat: index installed Schedule I APIs"
```

---

### Task 9: Complete the minimal cached Release/Preview API indexing capability

This is the second half of the small API-indexing capability required by the approved channel matrix. It remains cache-only and never executes upstream code.

**Files:**
- Modify: `src/S1Atlas.Indexing/Workflow/ApiIndexingWorkflow.cs`
- Modify: `src/S1Atlas.Cli/Commands/IndexCommand.cs`
- Use existing: `src/S1Atlas.Indexing/Upstream/UpstreamSnapshotCache.cs`
- Use existing: `src/S1Atlas.Indexing/Source/RoslynSourceIndexer.cs`
- Modify: `tests/S1Atlas.Indexing.Tests/Workflow/ApiIndexingWorkflowTests.cs`
- Modify: `tests/S1Atlas.IntegrationTests/IndexingCliUsabilityTests.cs`

**Interfaces:**

```csharp
Task<IndexingWorkflowResult> RunCachedSourceAsync(
    CodebaseKind codebase,
    CodeChannel channel,
    string commitSha,
    bool force,
    CancellationToken cancellationToken);
```

Accept only `S1Api`/`S1MApi` and `Release`/`Preview`. The commit must already exist in `UpstreamSnapshotCache`; this method makes no network request. Parse cached `.cs` files as data with Roslyn. No `dotnet restore`, MSBuild, source generators, scripts, or repository code execution.

- [ ] **Step 1: Write cached-source workflow tests**

Seed a fake cached commit with multiple `.cs` files plus non-C# files. Assert only C# source is indexed; file provenance and exact commit SHA are retained; Release and Preview are distinct channels even if they reference the same commit.

- [ ] **Step 2: Add CLI behavior**

Support:

```text
s1atlas index --codebase s1api --channel release --commit <40-char-sha>
s1atlas index --codebase s1api --channel preview --commit <40-char-sha>
s1atlas index --codebase s1mapi --channel release --commit <40-char-sha>
s1atlas index --codebase s1mapi --channel preview --commit <40-char-sha>
```

Require an exact cached commit SHA. `upstream sync ... --commit` remains the explicit network operation. Reject Schedule I Release/Preview with `InvalidCodebaseChannel`.

- [ ] **Step 3: Prove offline behavior**

Integration-test Release/Preview indexing with an HTTP path/client that throws if invoked; indexing must still pass because it reads cache only.

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
- Use the representative baseline queries embedded in the approved Milestone 1 design.
- Do not install S1API or S1MAPI merely to satisfy the matrix unless the operator explicitly chooses to do so.

- [ ] **Step 1: Rebuild/reuse Schedule I with index schema version 8**

```powershell
dotnet run -c Release --project src\S1Atlas.Cli -- index
```

Expected: an **index schema version 8** index is created once, then a repeat run reuses it. Do not call this “database schema 8”; the database reached migration level 7 in Task 2.

- [ ] **Step 2: Run representative usability queries**

```powershell
dotnet run -c Release --project src\S1Atlas.Cli -- search Property --limit 10
dotnet run -c Release --project src\S1Atlas.Cli -- source "ScheduleOne.Property.Property::SetOwned()" --context 5
dotnet run -c Release --project src\S1Atlas.Cli -- refs "ScheduleOne.Property.Property"
dotnet run -c Release --project src\S1Atlas.Cli -- callers "ScheduleOne.Property.Property::SetOwned()"
dotnet run -c Release --project src\S1Atlas.Cli -- callees "ScheduleOne.Property.Property::SetOwned()"
```

Record exact result counts, resolution/ambiguity, body recovery status, and relationship-completeness messaging. Do not commit proprietary source text; record metadata/counts and sanitized examples only.

- [ ] **Step 3: Validate Installed API channels only where binaries are present**

Run `env`. For each actually installed API, index Installed. If absent, record `Not present`; that is not a Milestone 1 failure.

- [ ] **Step 4: Validate real Release/Preview commits**

Resolve the official release-tag commit and current configured preview/default-branch commit for S1API and S1MAPI. Explicitly sync each exact SHA, then index cached Release/Preview. After sync, repeated index/query operations must succeed from cached state without network access.

- [ ] **Step 5: Prove channel separation**

Query at least one symbol in Installed/Release/Preview where available and record codebase, channel, snapshot/index ID, and commit/binary source identity. Confirm no Release/Preview result is labeled Installed.

- [ ] **Step 6: Write smoke report**

Include a matrix containing only `PASS`, `Not present`, or `FAIL`, plus sanitized evidence. Explicitly state known Cpp2IL caller/callee limitations and report the observed counts of `Recovered`, `StubOrUnavailable`, `NoBodyByDesign`, and `Unknown` methods so the classifier's real behavior is visible.

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

- [ ] **Step 2: Run complete test suite**

```powershell
dotnet test S1Atlas.sln -c Release --no-build
```

Expected: 0 failed, 0 skipped unless a pre-existing explicitly documented skip exists.

- [ ] **Step 3: Run format verification**

```powershell
dotnet format S1Atlas.sln --verify-no-changes
```

Expected: exit 0.

- [ ] **Step 4: Run the authoritative repository hygiene gate**

```powershell
git diff --check
powershell -ExecutionPolicy Bypass -File scripts/verify-repository-hygiene.ps1
git status --short
```

Expected: hygiene script exit 0 and clean worktree after commits. Do not replace the maintained hygiene script with a parallel hand-written filename regex.

- [ ] **Step 5: Re-run highest-risk focused tests**

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj -c Release --no-build
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Query|FullyQualifiedName~ApiIndexingWorkflow|FullyQualifiedName~RoslynSourceIndexer|FullyQualifiedName~BodyRecovery"
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~IndexingCliUsabilityTests
```

Expected: PASS.

- [ ] **Step 6: Verify approved non-goals remain absent**

Review the diff and reject any accidental Scene Intelligence, diffing, portal, MCP, agent-skill, semantic-search, runtime-probe, TUI, plugin, or generalized multi-game implementation.

- [ ] **Step 7: Final commit only if gate required fixes**

```powershell
git add -A
git commit -m "fix: close Milestone 1 verification gaps"
```

Do not create an empty commit.

---

## Plan Self-Review Checklist

Before implementation begins, verify:

- Every approved spec goal maps to at least one task.
- Task 1 captures complete source spans instead of retaining hardcoded columns/null ends.
- Task 2 does **not** equate RVA/non-empty body with meaningful recovery and does **not** equate zero references with a stub.
- `NoBodyByDesign`, `Recovered`, `StubOrUnavailable`, and `Unknown` have fixture-tested, conservative meanings.
- Database migration 7 is additive and migrations 1–6 remain byte-identical with their old checksums unchanged.
- All migration count/checksum/pinning/backup-level expectations are updated for database migration 7.
- `IndexingWorkflow.IndexSchemaVersion` changes 7→8, and wording never conflates that with database migration numbering.
- Search uses SQL count/ranked limit rather than load-all-then-truncate.
- `--channel all` sums `totalCount` across channels and globally rank-orders the bounded merged results.
- Rung-4 symbol selection treats tied best rank as ambiguity.
- `refs`, `callers`, and `callees` are distinct and call-like filtering uses normalized kinds.
- Relationship output retains exact IDs/signatures/evidence and unresolved target text.
- `source` verifies expected SHA before showing or exporting content.
- Oversized full-file source cannot accidentally dump multi-megabyte output to a terminal.
- `source --output` refuses every path under the detected Schedule I installation root with no override.
- Installed API absence is reported honestly and never filled by Release/Preview.
- Tasks 8–9 remain the smallest S1API/S1MAPI-specific indexing capability needed for the approved matrix; they do not create a generalized subsystem/framework.
- Release/Preview indexing reads only previously cached immutable source and executes no upstream code.
- Final verification invokes `scripts/verify-repository-hygiene.ps1` directly.
- The real smoke report contains no proprietary source content or absolute private paths.
- No later V1 milestone has leaked into this plan.
