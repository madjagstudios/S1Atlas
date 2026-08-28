# Reference-Mod Indexing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a local, user-curated reference-mod collection index that lets S1Atlas search prior-art across Schedule I and selected mods with explicit provenance.

**Architecture:** Add a `ReferenceMod` installed codebase and a reference index built from a validated local manifest of managed assemblies and text files. Reuse the completed Schedule I index's persisted symbol IDs as cross-index relationship targets; do not re-decompile or copy the game assembly. Persist mod/document ownership and hashes beside the existing symbol/relationship data, then add a federated query scope for game, reference, and all results. Keep indexing in the CLI; keep MCP read-only.

**Tech Stack:** .NET 8, `System.CommandLine`, `Microsoft.Data.Sqlite`, ILSpy `IManagedDecompiler`, existing Roslyn source/relationship indexers, xUnit, JSON CLI/MCP envelopes.

**Spec:** `docs/design/2026-08-27-reference-mod-indexing-design.md`

## Global Constraints

- Reference input is local/offline only; AT-26 does not download or discover mods.
- The manifest is the explicit selection boundary; collection names are stable user-curated profiles such as `qol`.
- Index identity hashes normalized manifest content, declared metadata, selected file hashes, game extraction identity, tool versions, and schema; local paths do not participate.
- Reference files are user-supplied and `LocalOnly`; S1Atlas does not certify compatibility, safety, or redistribution rights.
- Hash every selected file before indexing and again after reading/decompiling; fail on drift.
- Search/source/callers/callees expose collection/mod/path/hash provenance and bounded excerpts.
- Matching and cross-origin relationship resolution are dictionary-keyed, not O(n²).
- `callable` remains a Schedule I game-member query; AT-24 body recovery and AT-25 callable surface remain orthogonal evidence.
- Existing constructors and old indexes remain source-compatible and queryable through nullable trailing fields and migration defaults.

## Delivery sequence

Land this as four reviewable PRs: Task 1 (storage and migration), Tasks 2–3 (manifest and indexing workflow), Task 4 (query/federation), and Tasks 5–6 (CLI, MCP, and documentation). Run Task 7 before each PR handoff and again before the final merge. The sequence is intentionally serial because each later surface consumes the previous task's persisted contract.

---

### Task 1: Add reference-domain models and migration 10

**Files:**
- Modify: `src/S1Atlas.Core/Indexing/CodebaseKind.cs`
- Modify: `src/S1Atlas.Core/Indexing/QueryModels.cs`
- Create: `src/S1Atlas.Core/ReferenceMods/ReferenceModModels.cs`
- Modify: `src/S1Atlas.Core/Storage/IIndexRepository.cs`
- Create: `src/S1Atlas.Core/Storage/ReferenceIndexModels.cs`
- Modify: `src/S1Atlas.Storage/Migrations/SqliteMigrations.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/ReadOnlySqliteAtlasRepository.cs`
- Test: `tests/S1Atlas.Storage.Tests/Migrations/ReferenceModMigrationTests.cs`
- Test: `tests/S1Atlas.Storage.Tests/Sqlite/ReferenceModRepositoryTests.cs`

**Interfaces:**
- Produces `ReferenceCollectionDefinition`, `ReferenceModDefinition`, `ReferenceIndexContextRecord`, `IndexReferenceModRecord`, and `IndexReferenceDocumentRecord` records.
- Extends `IndexWriteSet` with nullable trailing `ReferenceIndexContext`, `ReferenceMods`, and `ReferenceDocuments` lists; reference symbols use the existing `Symbols` list and a mod-ownership table.
- Adds repository methods `GetReferenceIndexContextAsync`, `GetCompletedReferenceModsAsync`, `GetCompletedReferenceDocumentsAsync`, and `SearchCompletedReferenceDocumentsAsync`.

- [ ] **Step 1: Write migration and compatibility tests first.** Assert that migration 10 is sequentially named `reference-mods-v10`, old v9 databases migrate, `ReferenceMod` is accepted only with the `Installed` channel, and all new tables enforce index/snapshot ownership and unique `(index_id, mod_id, relative_path)` document identity. Build the v9 fixture with an environment snapshot, code snapshot, index run, symbol, source file, fingerprint, relationship, and a scene row, then assert all rows and every foreign-key check survive the parent-table rebuild.

```csharp
[Fact]
public async Task ReferenceMigration_PreservesPopulatedV9DatabaseAndForeignKeys()
{
    await CreatePopulatedV9DatabaseAsync();
    await new SqliteMigrationRunner(_databasePath, _backupDirectory, SqliteMigrations.All).MigrateAsync(TestContext.Current.CancellationToken);

    Assert.Equal(1, await CountAsync("code_snapshots"));
    Assert.Equal(1, await CountAsync("symbols"));
    Assert.Equal(1, await CountAsync("relationships"));
    Assert.Equal(1, await CountAsync("scenes"));
    Assert.Equal(0, await QueryIntAsync("SELECT COUNT(*) FROM pragma_foreign_key_check;"));
}
```

- [ ] **Step 2: Add the reference records with additive constructor tails.** Use `ReferenceMod` for the index codebase and `Installed` for its channel. Model the base game index context (`ReferenceIndexId`, `GameIndexId`, `BuildId`), mod metadata (`ModId`, `DisplayName`, `Version`, `License`, `RootPath`, `ContentSha256`), selected documents (`ModId`, `RelativePath`, `Kind`, `Sha256`, `ByteCount`, `Content`), and reference symbol ownership (`SymbolId`, `ModId`). Keep `RootPath` for local provenance only; never feed it to `CreateIndexId`.

- [ ] **Step 3: Add migration 10 tables and indexes.** Add `reference_index_context` with foreign keys from the reference index to its base game index, plus `reference_mods`, `reference_documents`, and `reference_symbol_owners`; add the new `ReferenceMod` value to the `code_snapshots.codebase` check and require `ReferenceMod` to use `Installed`. Rebuild `code_snapshots` in a transaction with foreign-key enforcement disabled only for the copy/drop/rename window, recreate its indexes, restore enforcement, and run `PRAGMA foreign_key_check`. Do not add a reference copy of game symbols: relationship target IDs may reference the existing game `symbols` row in another snapshot.

- [ ] **Step 4: Implement transactional persistence and ownership validation.** Insert reference context, mod metadata, documents, and mod-symbol ownership in `CompleteIndexRunAsync` after symbols and source files. Require every reference source symbol to belong to the running reference snapshot; permit a relationship target symbol to belong either to the same snapshot or to the recorded base game index, and reject any other external target. Add read-only joins that return mod/document provenance and preserve old indexes when no reference rows exist.

- [ ] **Step 5: Run focused storage tests.**

Run: `dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj --filter "FullyQualifiedName~ReferenceMod"`

Expected: migration and repository tests pass, including populated legacy v9 migration with dependent scene/FK rows, ReferenceMod channel enforcement, duplicate document rejection, valid cross-snapshot game targets, invalid external-target rejection, and rollback on ownership failure.

- [ ] **Step 6: Commit the storage slice.**

```powershell
git add src/S1Atlas.Core/Indexing/CodebaseKind.cs src/S1Atlas.Core/Indexing/QueryModels.cs src/S1Atlas.Core/Storage/ReferenceIndexModels.cs src/S1Atlas.Core/Storage/IIndexRepository.cs src/S1Atlas.Storage/Migrations/SqliteMigrations.cs src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs src/S1Atlas.Storage/Sqlite/ReadOnlySqliteAtlasRepository.cs tests/S1Atlas.Storage.Tests/Migrations/ReferenceModMigrationTests.cs tests/S1Atlas.Storage.Tests/Sqlite/ReferenceModRepositoryTests.cs
git commit -m "feat: persist reference mod index data"
```

### Task 2: Validate manifests and build deterministic local input snapshots

**Files:**
- Create: `src/S1Atlas.Indexing/ReferenceMods/ReferenceModManifestLoader.cs`
- Create: `src/S1Atlas.Indexing/ReferenceMods/ReferenceModFileSelector.cs`
- Create: `src/S1Atlas.Indexing/ReferenceMods/ReferenceModInputHasher.cs`
- Test: `tests/S1Atlas.Indexing.Tests/ReferenceMods/ReferenceModManifestLoaderTests.cs`
- Test: `tests/S1Atlas.Indexing.Tests/ReferenceMods/ReferenceModFileSelectorTests.cs`

**Interfaces:**
- `ReferenceModManifestLoader.LoadAsync(string manifestPath, CancellationToken)` returns a normalized `ReferenceCollectionDefinition`.
- `ReferenceModFileSelector.Select(ReferenceModDefinition)` returns sorted `ReferenceModInputFile` records with mod ID, full path, safe relative path, kind, and declared document kind.
- `ReferenceModInputHasher.HashAsync(IReadOnlyList<ReferenceModInputFile>, CancellationToken)` returns sorted file hashes and a collection content hash.

- [ ] **Step 1: Write failing manifest-validation tests.** Cover duplicate collection/mod IDs, missing roots, rooted relative-path escapes, missing `id`/`displayName`/`license`, empty include sets, invalid glob patterns, and a valid `qol` manifest. Assert paths are not present in the collection content hash input.

- [ ] **Step 2: Implement strict JSON loading and normalization.** Normalize collection/mod IDs to lower-case stable identifiers, preserve display metadata, canonicalize separators, sort include/exclude patterns, and reject network-like paths or URLs. Treat `license: "unknown"` as an explicit declaration that produces a warning, not as permission.

- [ ] **Step 3: Write failing file-selection tests.** Assert managed `.dll`, `.cs`, `.md`, `.markdown`, and `.txt` files are selected; `bin`, `obj`, caches, symlink/reparse escapes, and excluded globs are omitted; and the returned order is `(modId, relativePath)` ordinal.

- [ ] **Step 4: Implement bounded local selection and hashing.** Resolve each root once, reject a file outside its root, read/hash each selected file, classify DLLs as managed-assembly candidates and text files as source/document candidates, and return a manifest hash over metadata plus file hashes—not absolute paths.

- [ ] **Step 5: Add drift tests and implementation.** Hash a file, mutate it between the pre-read and post-read pass, and assert `InvalidDataException` identifies an input drift without publishing partial records. Add cancellation coverage.

- [ ] **Step 6: Run focused indexing tests and commit.**

Run: `dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter "FullyQualifiedName~ReferenceMod"`

Expected: all manifest, selection, path-safety, hash, drift, and cancellation tests pass.

```powershell
git add src/S1Atlas.Indexing/ReferenceMods src/S1Atlas.Core/ReferenceMods/ReferenceModModels.cs tests/S1Atlas.Indexing.Tests/ReferenceMods
git commit -m "feat: validate reference mod collections"
```

### Task 3: Build the composite reference index

**Files:**
- Create: `src/S1Atlas.Indexing/Workflow/ReferenceModIndexWorkflow.cs`
- Create: `src/S1Atlas.Indexing/Workflow/ReferenceModIndexSource.cs`
- Create: `src/S1Atlas.Indexing/Workflow/ReferenceGameSymbolLoader.cs`
- Create: `src/S1Atlas.Indexing/Relationships/ReferenceRelationshipResolver.cs`
- Modify: `src/S1Atlas.Indexing/Workflow/IndexingWorkflow.cs` only to share index-ID/hash helpers without changing Schedule I behavior
- Modify: `src/S1Atlas.Indexing/Relationships/RelationshipExtractor.cs`
- Modify: `src/S1Atlas.Indexing/Decompilation/IlSpyManagedDecompiler.cs` only if source provenance needs assembly-level metadata
- Test: `tests/S1Atlas.Indexing.Tests/Workflow/ReferenceModIndexWorkflowTests.cs`
- Test: `tests/S1Atlas.Indexing.Tests/Relationships/ReferenceRelationshipResolverTests.cs`

**Interfaces:**
- `ReferenceModIndexWorkflow.RunAsync(string buildId, ReferenceCollectionDefinition collection, bool force, CancellationToken)` returns `IndexingWorkflowResult` with reference counts and warnings.
- Extend `IndexingWorkflowResult` with nullable trailing `ReferenceModCount`, `ReferenceDocumentCount`, and `ReferenceSymbolCount` values so existing Schedule I/API callers remain source-compatible.
- `ReferenceModIndexSource.ReadModAssemblyAsync(ReferenceModInputFile, CancellationToken)` returns a decompilation; it has no game-assembly read method.
- `ReferenceGameSymbolLoader.LoadAsync(string gameIndexId, CancellationToken)` loads persisted game `IndexSymbolRecord` rows from the completed Schedule I index without decompiling the game assembly.
- `ReferenceRelationshipResolver.Resolve(...)` accepts mod decompilations plus a dictionary keyed by `(origin, type, name, arity, signature)` and returns relationships whose game targets retain the existing Schedule I `SymbolId`.

- [ ] **Step 1: Write the failing workflow tests.** Build a fixture with a verified game extraction, a completed Schedule I index containing a target method, and two local mods: one selected in `qol`, one omitted. Assert only the selected mod appears, the workflow loads the existing game symbol row by ID, the decompiler is called only for selected mod assemblies, generated source paths are under `reference/<index-id>/<mod-id>/`, collection metadata is persisted, and no network client or game decompiler call is made.

- [ ] **Step 2: Implement reference index identity and reuse.** Compute the ID from the base game index ID, verified extraction identity, normalized manifest metadata/file hashes, decompiler package/version, settings, and `IndexSchemaVersion`; exclude every absolute path. Reuse only a completed matching reference index; `--force` creates a new candidate as existing indexing does.

- [ ] **Step 3: Implement source ingestion.** Decompile each selected managed assembly once, write generated C# under an owned final root, index its symbols/fingerprints, and read selected text files into bounded local document records. Classify `README`, `CHANGELOG`, `DEVLOG`, and other text by filename/path for provenance.

- [ ] **Step 4: Add game/mod symbol identity and relationships.** Preserve Schedule I canonical keys and `SymbolId` values for game targets loaded from the base index; prefix reference symbol keys with the normalized mod ID and persist their ownership. Feed a combined lookup of persisted game symbols and selected mod symbols to relationship extraction so calls, field reads, and field writes crossing game/mod boundaries resolve when signatures agree. Retain unresolved raw targets and mark them unresolved rather than guessing.

- [ ] **Step 5: Add post-read drift validation and atomic publication.** Re-hash every assembly/document after processing, fail and delete staging on mismatch, complete the database transaction before moving the staging root, and return counts for mods, assemblies, documents, symbols, and relationships.

- [ ] **Step 6: Run workflow/relationship tests and commit.**

Run: `dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter "FullyQualifiedName~ReferenceModIndexWorkflow|FullyQualifiedName~ReferenceRelationshipResolver"`

Expected: selected-only indexing, hash-only identity, cross-origin resolution, unresolved-target honesty, drift failure, reuse, force rebuild, and atomic cleanup all pass.

```powershell
git add src/S1Atlas.Indexing/Workflow src/S1Atlas.Indexing/Relationships tests/S1Atlas.Indexing.Tests/Workflow tests/S1Atlas.Indexing.Tests/Relationships
git commit -m "feat: index selected reference mods"
```

### Task 4: Add scoped and federated query services

**Files:**
- Create: `src/S1Atlas.Indexing/Query/ReferenceModQueryService.cs`
- Create: `src/S1Atlas.Indexing/Query/FederatedIndexQueryService.cs`
- Modify: `src/S1Atlas.Core/Indexing/QueryModels.cs`
- Modify: `src/S1Atlas.Indexing/Query/IndexQueryService.cs`
- Modify: `src/S1Atlas.Indexing/Query/SymbolResolver.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/ReadOnlySqliteAtlasRepository.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs`
- Test: `tests/S1Atlas.Indexing.Tests/Query/ReferenceModQueryServiceTests.cs`
- Test: `tests/S1Atlas.Indexing.Tests/Query/FederatedIndexQueryServiceTests.cs`

**Interfaces:**
- `IndexQueryOptions` gains `IndexQueryScope Scope` and `string? ReferenceCollection`.
- `ReferenceModQueryService.SearchAsync`, `SourceAsync`, and `RelationshipsAsync` operate on a completed reference collection index and its recorded base game index.
- `FederatedIndexQueryService` aggregates game/reference results, keeps source provenance, and returns `Ambiguous` when a selector resolves to multiple owners.

- [ ] **Step 1: Write failing query tests.** Cover `game`, `reference`, and `all`; collection-required validation; selected collection isolation; same-name symbols from two mods returning ambiguity with both provenance records; document substring hits with bounded excerpts; callers/callees crossing game/mod ownership; and empty/no-completed-index behavior.

- [ ] **Step 2: Add provenance-bearing result fields.** Append nullable `Origin`, `Collection`, `ReferenceModId`, `ReferenceModDisplayName`, `ReferenceModVersion`, `License`, `RelativePath`, and `Sha256` fields to query results. Keep existing game result construction valid through defaults.

- [ ] **Step 3: Implement reference repository queries.** Search symbol names/signatures and document content with escaped case-insensitive matching, order exact/prefix/contains hits deterministically, and return only bounded document excerpts. Verify content hashes against the indexed local copy before returning source/document evidence.

- [ ] **Step 4: Implement federated resolution and relationships.** Query the preferred verified game index and the selected reference index, resolve external `target_symbol_id` values through the reference index's recorded base game index, merge results with origin labels, deduplicate only identical `(origin, modId, symbolId)` rows, and preserve ambiguity rather than selecting by score.

- [ ] **Step 5: Run query tests and commit.**

Run: `dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter "FullyQualifiedName~ReferenceModQueryService|FullyQualifiedName~FederatedIndexQueryService"`

Expected: scope, collection, provenance, ambiguity, document search, source integrity, and cross-origin relationship tests pass.

```powershell
git add src/S1Atlas.Core/Indexing/QueryModels.cs src/S1Atlas.Indexing/Query src/S1Atlas.Storage/Sqlite/ReadOnlySqliteAtlasRepository.cs src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs tests/S1Atlas.Indexing.Tests/Query
git commit -m "feat: query reference mod prior art"
```

### Task 5: Add CLI manifest/index and query controls

**Files:**
- Create: `src/S1Atlas.Cli/Commands/ReferenceCommand.cs`
- Create: `src/S1Atlas.Cli/Commands/ReferenceCollectionsCommand.cs`
- Create: `src/S1Atlas.Cli/Commands/ReferenceIndexCommand.cs`
- Modify: `src/S1Atlas.Cli/CliApplication.cs`
- Modify: `src/S1Atlas.Cli/Commands/IndexQueryCommandFactory.cs`
- Modify: `src/S1Atlas.Cli/Commands/SearchCommand.cs`
- Modify: `src/S1Atlas.Cli/Commands/SourceCommand.cs`
- Modify: `src/S1Atlas.Cli/Commands/RefsCommand.cs`
- Modify: `src/S1Atlas.Cli/Commands/CallersCommand.cs`
- Modify: `src/S1Atlas.Cli/Commands/CalleesCommand.cs`
- Modify: `src/S1Atlas.Cli/Output/IndexQueryOutput.cs`
- Test: `tests/S1Atlas.IntegrationTests/ReferenceModCliTests.cs`
- Test: `tests/S1Atlas.IntegrationTests/CliQueryParityTests.cs`

**Interfaces:**
- `reference collections validate <manifest>` validates without indexing.
- `reference index <manifest> [--force] [--json]` builds a local collection index.
- `reference collections list [--json]` lists completed local collections and their evidence identities.
- Query commands accept `--scope game|reference|all` and `--collection <name>`; default remains `game`.

- [ ] **Step 1: Write failing CLI tests.** Assert manifest validation and index JSON output, collection list output, required `--collection`, rejection of `--scope reference` with no collection, default game behavior, and rejection of network/API/scene options on reference indexing.

- [ ] **Step 2: Register the reference workflow and command tree.** Construct it with the existing decompiler, repository, authority resolver, and local data root. Keep index writes out of MCP composition.

- [ ] **Step 3: Extend query option parsing and human/JSON output.** Validate scope/collection combinations before authority resolution; show `Origin`, collection/mod, license declaration, relative path, hash, and evidence classification in reference results.

`--scope` and `--collection` are added only to `search`, `source`, `refs`, `callers`, and `callees`. `type` and `method` retain their existing game/API-only option surface in AT-26; users can use scoped `search` for reference symbol discovery.

- [ ] **Step 4: Add CLI parity tests for all scopes.** Verify human and `--json` outputs represent the same resolved/ambiguous/not-found status and provenance.

- [ ] **Step 5: Run integration tests and commit.**

Run: `dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter "FullyQualifiedName~ReferenceMod|FullyQualifiedName~CliQueryParity"`

Expected: command registration, option validation, indexing, collection selection, scope federation, and output parity pass.

```powershell
git add src/S1Atlas.Cli tests/S1Atlas.IntegrationTests/ReferenceModCliTests.cs tests/S1Atlas.IntegrationTests/CliQueryParityTests.cs
git commit -m "feat: expose reference mod indexing in the CLI"
```

### Task 6: Add read-only MCP tools and agent documentation

**Files:**
- Create: `src/S1Atlas.Mcp/Tools/ReferenceModTools.cs`
- Modify: `src/S1Atlas.Mcp/Tools/CodeSymbolTools.cs`
- Modify: `src/S1Atlas.Mcp/McpServerComposition.cs`
- Modify: `src/S1Atlas.Mcp/Mapping/EnvelopeMapper.cs`
- Test: `tests/S1Atlas.Mcp.Tests/ReferenceModToolTests.cs`
- Test: `tests/S1Atlas.Mcp.Tests/CodeSymbolToolTests.cs`
- Modify: `docs/USAGE.md`
- Modify: `docs/REFERENCE.md`
- Modify: `skills/s1atlas/SKILL.md`

**Interfaces:**
- Add read-only `list_reference_collections` with collection name, mod metadata, completed index ID, and local-only trust.
- Add optional `scope` and `collection` arguments to symbol/source/relationship tools where the CLI supports them.
- Do not add an MCP indexing or download tool.

- [ ] **Step 1: Write failing MCP tests.** Assert collection listing, scope validation, missing collection errors, ambiguous same-name results, provenance, bounded documentation excerpts, and no source redistribution field containing an entire document.

- [ ] **Step 2: Implement read-only service composition and envelope mapping.** Reuse authority mapping for the game leg, mark reference evidence local-only, and preserve the existing tool error/status schema.

- [ ] **Step 3: Update the skill evidence loop.** Add collection selection before search, provenance/license review before using prior-art, and the rule that reference source is evidence of how another mod was written—not proof that the same approach is compatible with the current build.

- [ ] **Step 4: Document offline indexing and examples.** Include the manifest shape, `qol` collection workflow, rebuild-after-update rule, path/hash identity behavior, and the separation between indexing and future download/discovery work.

- [ ] **Step 5: Run MCP/docs tests and commit.**

Run: `dotnet test tests/S1Atlas.Mcp.Tests/S1Atlas.Mcp.Tests.csproj --filter "FullyQualifiedName~ReferenceMod"; dotnet test tests/S1Atlas.Docs.Tests/S1Atlas.Docs.Tests.csproj`

Expected: MCP status/provenance tests and documentation validation pass.

```powershell
git add src/S1Atlas.Mcp tests/S1Atlas.Mcp.Tests docs/USAGE.md docs/REFERENCE.md skills/s1atlas/SKILL.md
git commit -m "docs: teach agents to use reference mod prior-art"
```

### Task 7: Full verification and public-repository close-out

**Files:**
- Modify: any implementation/test/docs files required by verification failures only
- Test: repository-wide test and hygiene gates

- [ ] **Step 1: Run the complete verification gates.**

Run:

```powershell
dotnet build S1Atlas.sln -c Release
dotnet test S1Atlas.sln -c Release --no-build
dotnet format S1Atlas.sln --verify-no-changes
pwsh -NoProfile -File scripts/verify-repository-hygiene.ps1
git diff --check
```

Expected: zero build warnings/errors, all tests pass, format is clean, hygiene is clean, and whitespace validation is clean.

- [ ] **Step 2: Exercise the real local workflow.** Validate a manifest, index a small two-mod fixture collection, run `search`, `source`, `callers`, and `callees` with `--scope all --collection qol`, confirm provenance and bounded excerpts, mutate an input, and confirm the next run fails with drift rather than publishing.

- [ ] **Step 3: Run the going-public scan before any push or PR.** Scan the diff and tracked tree for secrets, private machine paths, generated/proprietary artifacts, AI/process attribution, Jira residue, and accidental manifest/source inclusion. Keep only the documented `claude.skills-dir` finding if it remains the scanner’s expected agent-skill exception; do not add local manifests or absolute paths to the public repository.

- [ ] **Step 4: Review the final diff against the design.** Confirm collection selection is explicit, no downloader exists, index identity excludes paths, local-only trust is visible, old indexes remain readable, and AT-24/AT-25 semantics are not conflated.

- [ ] **Step 5: Commit verification-only fixes, then hand off for PR review.** Use a public-safe commit message with no agent-attribution trailers; do not claim AT-26 complete until CI and the public scan are green.
