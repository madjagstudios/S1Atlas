# Task 5 — CLI reference indexing and query options report

## Changed files

- `src/S1Atlas.Cli/CliApplication.cs` — registers the reference command tree and federated query service.
- `src/S1Atlas.Cli/Commands/ReferenceCommand.cs` — adds `reference` command registration.
- `src/S1Atlas.Cli/Commands/ReferenceIndexCommand.cs` — adds offline manifest-backed reference indexing with counts and phase timings.
- `src/S1Atlas.Cli/Commands/ReferenceCollectionsCommand.cs` — adds collection command registration.
- `src/S1Atlas.Cli/Commands/ReferenceCollectionsValidateCommand.cs` — validates and hashes manifests with stable counts and warnings.
- `src/S1Atlas.Cli/Commands/ReferenceCollectionsListCommand.cs` — lists completed collections with path-free output.
- `src/S1Atlas.Cli/Output/ReferenceOutputModels.cs` — path-free reference CLI output records.
- `src/S1Atlas.Cli/Commands/IndexQueryCommandFactory.cs` — adds scope/collection parsing and validation while leaving type/method/callable surfaces unchanged.
- `src/S1Atlas.Cli/Commands/SearchCommand.cs` — routes reference/all search through federation.
- `src/S1Atlas.Cli/Commands/SourceCommand.cs` — adds scoped source queries and reference source-root handling.
- `src/S1Atlas.Cli/Commands/CallersCommand.cs` — adds scoped caller queries.
- `src/S1Atlas.Cli/Commands/CalleesCommand.cs` — adds scoped callee queries.
- `src/S1Atlas.Cli/Commands/RefsCommand.cs` — adds scoped reference queries.
- `src/S1Atlas.Core/Storage/IIndexRepository.cs` — adds the completed-reference-index listing seam.
- `src/S1Atlas.Indexing/Workflow/ReferenceModIndexWorkflow.cs` — persists the normalized collection ID as reference snapshot identity so CLI collection selectors resolve by name/id.
- `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs` — implements completed reference-index listing.
- `src/S1Atlas.Storage/Sqlite/ReadOnlySqliteAtlasRepository.cs` — implements the same read-only listing seam.
- `tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj` — references the small managed interop fixture used by real offline CLI indexing tests.
- `tests/S1Atlas.IntegrationTests/Indexing/ReferenceModCliTests.cs` — CLI integration coverage.

## Test-first evidence

Initial RED command, after adding the CLI integration tests and before the Task 5 CLI implementation:

```text
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter "FullyQualifiedName~ReferenceModCliTests"
Failed! - Failed: 9, Passed: 6, Skipped: 0, Total: 15
```

The failures were the expected missing `reference` command, missing scoped options, and missing stable validation/list/index behavior; existing commands returned help or could not produce the requested envelopes.

GREEN focused verification:

```text
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter "FullyQualifiedName~ReferenceModCliTests" --no-restore
Passed! - Failed: 0, Passed: 16, Skipped: 0, Total: 16
```

Affected-suite verification:

```text
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --no-restore
Passed! - Failed: 0, Passed: 235, Skipped: 0, Total: 235

dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj --no-restore
Passed! - Failed: 0, Passed: 142, Skipped: 0, Total: 142

dotnet test tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj --no-restore
Passed! - Failed: 0, Passed: 127, Skipped: 0, Total: 127

dotnet test S1Atlas.sln --no-restore
Passed! - Failed: 0, Passed: 1,310 total across Core 127, Docs 8, Indexing 235, Storage 142, Extraction 551, Integration 177, and MCP 70.
```

Formatting and hygiene:

```text
Scoped dotnet format --verify-no-changes checks for the changed CLI/Core/Storage/Indexing/workflow/test projects: exit 0.
git diff --check: exit 0.
```

The required whole-solution `dotnet format S1Atlas.sln --verify-no-changes --no-restore` check remains blocked by four pre-existing whitespace diagnostics in `src/S1Atlas.Indexing/ReferenceMods/ReferenceModFileSelector.cs` (lines 44–47), outside Task 5. The unrelated file was not changed.

## Coverage and contract decisions

- Added `reference collections validate <manifest>`, `reference index <manifest> [--force] [--json]`, and `reference collections list [--json]`.
- Reference indexing uses only the local manifest loader, selector, hasher, workflow, and completed Schedule I authority; the rejecting HTTP fixture observed zero requests across validation, first index, reuse, and force rebuild.
- Added `--scope game|reference|all` and `--collection` to search, source, callers, callees, and refs. `reference`/`all` require a collection; `game` rejects one. API game queries continue through the existing service.
- `type`, `method`, and `callable` remain their existing game/API and Schedule-I-only surfaces; they do not accept the new scope options.
- JSON outputs report counts, stable provenance, and phase timings without manifest/root paths. Collection list mod records contain no absolute root path.
- Force rebuild receives a new candidate identity through the reviewed workflow; an unchanged non-force run reuses its completed index.

## Review fix round 1

Peirce's three Important findings were addressed as follows:

- `ReferenceModIndexWorkflow` now measures the initial and post-read input hash passes and returns their real combined duration. `ReferenceIndexCommand` populates `InputHashMilliseconds` and includes `hash ...ms` in human output; other workflow callers retain the nullable timing field and unchanged behavior.
- `ReferenceModQueryService` resolves the completed snapshot before building selection provenance and reports its persisted `SourceIdentity` for both collection-name and index-ID selectors. The selector remains lookup-only.
- CLI integration coverage now exercises successful `reference` and `all` source, callers, callees, and refs routes against the real command tree, asserting origin, canonical collection, mod provenance, source hash/path where a location exists, and relationship endpoint provenance.

Review fix TDD evidence:

```text
RED: dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter "FullyQualifiedName~ReferenceModCliTests" --no-restore
Failed! - Failed: 4, Passed: 15, Skipped: 0, Total: 19

GREEN: dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter "FullyQualifiedName~ReferenceModCliTests" --no-restore
Passed! - Failed: 0, Passed: 19, Skipped: 0, Total: 19

dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --no-restore
Passed! - Failed: 0, Passed: 180, Skipped: 0, Total: 180

dotnet test S1Atlas.sln --no-restore --no-build -m:1 --verbosity minimal
Passed! - Failed: 0, Passed: 1,313 total across Core 127, Docs 8, Indexing 235, Storage 142, Extraction 551, Integration 180, and MCP 70.

dotnet build S1Atlas.sln --no-restore
Build succeeded. 0 Warning(s), 0 Error(s).
```

Changed-file format verification passed, and `git diff --check` passed. Whole-solution format verification still reports only the four pre-existing `ReferenceModFileSelector.cs` whitespace diagnostics. A parallel all-project test invocation also surfaced three existing extraction-only concurrency flakes; each passed on isolated serial rerun, and the serial solution run passed.

## Concerns

- Whole-solution format verification still reports the existing Task 2 whitespace issue noted above; scoped verification for this change is clean.
- The repository's existing code-map check also reports nine generator-version mismatches (`INDEX.md`, `GUIDE.md`, and seven generated topic maps). No code-map files were regenerated because those changes are unrelated to Task 5.
- The collection list currently exposes the normalized collection ID and selected mod metadata, but not the optional manifest display name because the reviewed storage schema does not persist that display name independently.
