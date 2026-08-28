# Task 4 — Reference-mod query and federation report

## Changed files

- `src/S1Atlas.Core/Indexing/QueryModels.cs` — additive query scope and nullable provenance fields.
- `src/S1Atlas.Core/Storage/IIndexRepository.cs` — additive completed-reference collection lookup seam.
- `src/S1Atlas.Indexing/Query/ReferenceModQueryService.cs` — completed reference collection selection, symbol/search/source/document/relationship queries, provenance, bounded document reads, and hash verification.
- `src/S1Atlas.Indexing/Query/FederatedIndexQueryService.cs` — game/reference/all aggregation, ambiguity preservation, cross-origin relationship federation, and exact-identity deduplication.
- `src/S1Atlas.Indexing/Query/IndexQueryService.cs` — public resolver seam and game provenance on source/relationship results.
- `src/S1Atlas.Indexing/Query/SymbolResolver.cs` — additive provenance projection parameters.
- `src/S1Atlas.Storage/Sqlite/ReadOnlySqliteAtlasRepository.cs` — read-only completed reference collection lookup.
- `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs` — writable-repository completed reference collection lookup.
- `tests/S1Atlas.Indexing.Tests/Query/ReferenceModQueryServiceTests.cs` — focused real-SQLite query/federation coverage.

## Test-first evidence

Initial RED command, before the Task 4 production types existed:

```text
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter "FullyQualifiedName~ReferenceModQueryServiceTests"
...
ReferenceModQueryServiceTests.cs(...): error CS0246: The type or namespace name 'ReferenceModQueryService' could not be found
ReferenceModQueryServiceTests.cs(...): error CS0103: The name 'IndexQueryScope' does not exist in the current context
```

GREEN focused query verification:

```text
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter "FullyQualifiedName~ReferenceModQueryServiceTests"
Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7
```

Additional requested suite verification:

```text
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --no-restore
Passed!  - Failed:     0, Passed:   223, Skipped:     0, Total:   223

dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj --no-restore
Passed!  - Failed:     0, Passed:   142, Skipped:     0, Total:   142

dotnet test tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj --no-restore
Passed!  - Failed:     0, Passed:   127, Skipped:     0, Total:   127

dotnet test S1Atlas.sln --no-restore
Implementation-session run reported Core 127, Docs 8, Indexing 223, Extraction 551, Storage 142, Integration 161, MCP 70 (1,282 total) passed in that run; this is not a stability claim for parallel execution.

git diff --check
Exit code: 0
```

## Coverage

- Game, reference, and all scopes use additive `IndexQueryOptions` fields; reference/all calls require an explicit collection selector.
- Only completed `ReferenceMod`/`Installed` runs with a valid recorded completed Schedule I base index are queried. Empty completed collections return `NotFound`; missing/incomplete collections return `NoCompletedIndex`/empty results.
- Reference symbol, source, document, and relationship results retain origin, collection, mod ID, display metadata, relative path, and SHA-256 where persisted evidence provides them.
- Search uses the existing escaped case-insensitive SQLite matching and deterministic ordinal tie-breaks. Same-name reference candidates remain ambiguous and ordered by provenance.
- Generated reference source is hash-verified before returning; documents are hash-verified against persisted input bytes and capped at `MaxDocumentExcerptCharacters`.
- Callers/callees and references cross from reference symbols to the recorded game index and preserve unresolved target text without guessing.
- Federation retains game/reference ambiguity, aggregates both origins without score suppression, and deduplicates only exact `(origin, referenceModId, symbolId)` identities.

## Concern

Migration 10 does not persist a standalone collection-name column. The query seam therefore resolves a collection by its stable reference index ID or persisted snapshot source identity. The current Task 3 workflow persists collection identity in the reference index identity/source-identity path; adding a human-readable collection-name query would require a later schema/workflow change outside Task 4.

## Review fix round 1 — RED/GREEN evidence

Zeno findings were reproduced with focused tests before production changes. The RED run showed the multi-assembly source query returning the first generated file without a persisted location, `GetDocumentsAsync` returning two rows for `Limit: 1`, and federation returning reference-origin relationships for game-scoped options; the initial test edit also exposed the existing game-caller fixture changing the expected federation candidate count.

The GREEN fix set:

- rejects `ReferenceCollection` with `Scope=Game` and only federates reference relationships for `Scope=All`;
- returns source unavailable when a reference symbol has no persisted location, while selecting the correct generated file/hash/content when a persisted location identifies a second assembly;
- adds bounded completed-reference-document repository reads with SQL `LIMIT` before content materialization;
- derives origin from `CodebaseKind` (`game` only for Schedule I, null for S1API/S1MAPI), including source and relationship endpoints;
- covers source-identity collection lookup, running/empty collections, escaped wildcards, exact-identity federation deduplication, cross-origin callers/references, and a reparse-point ancestor;
- reuses `OwnedIndexPaths.ForReferenceMod` for reference source-root safety.

Verification after the fixes:

```text
Focused query/provenance tests: 29/29 passed
Indexing: 232/232 passed
Storage: 142/142 passed
Core: 127/127 passed
Full solution: 1,291/1,291 passed
```

## Review fix round 2 — RED/GREEN evidence

The scoped re-review reproduced the remaining provenance defect: a genuine two-assembly fixture exposed the second generated file as symbol provenance even though no symbol-to-source location was persisted. It also added failing coverage for second-assembly relationship provenance, cross-origin same-ID federation, federated callees, API source/relationship origins, and second-assembly hash failure.

The fix now derives reference symbol and relationship source path/hash only from exactly one persisted symbol location whose source file exists; absent or ambiguous ownership leaves those fields null. The second-assembly fixture persists a location to the second file to verify correct path/hash/content and then verifies tampering fails hash validation.

Current verification:

```text
Focused query/provenance tests: 32/32 passed
Indexing: 235/235 passed
Storage: 142/142 passed
Core: 127/127 passed
Full solution run: 1,294/1,294 passed
Isolated IntegrationTests rerun: 161/161 passed
```

The full-suite result above is the successful run from this fix round. The scoped re-review also recorded an existing intermittent `ManagedToolCli` integration failure under parallel full-suite execution; the isolated 161-test integration rerun passed, so that flake is not attributed to Task 4.
