# Task 6 report — read-only MCP reference surface

## Scope

Task 6 extends the existing read-only MCP surface with completed reference-
collection listing and federated symbol/source/relationship queries. The
default remains the verified Schedule I Installed index. `reference` and
`all` require an explicit collection or completed reference index selector;
`game` rejects a collection. `get_type`, `get_method`, and
`get_callable_surface` remain Schedule-I-only convenience tools. Indexing,
manifest validation, file selection, and all mutation remain CLI-only.

## Implementation

- Added `list_reference_collections` with collection, completed index,
  recorded base-index, and local-only mod provenance.
- Added optional `scope` and `collection` arguments to `search_symbols`,
  `get_source`, `find_callers`, `find_callees`, `find_references`, and
  `find_related_types`.
- Routed reference and `all` queries through the existing federated read-only
  query service; game defaults continue to use the existing authority-bound
  Schedule I query path.
- Preserved bounded limits, source hash/integrity handling, ambiguity,
  incomplete/no-index states, relationship direction, symbol origin, and
  collection/mod/base-index provenance in the existing envelopes.
- Updated README, usage documentation, and the S1Atlas skill with local-only
  collection workflow and the orthogonality of AT-24 body recovery, AT-25
  callable-surface evidence, and AT-26 reference evidence.

## TDD evidence

### RED

Before the production surface was added, the focused MCP contract command
failed at compile time because `ReferenceCollectionTools`, `find_callees`, and
the new scope/collection overloads were absent. The failure was confined to
the new contract tests; this established the expected failing state.

### GREEN

Focused command:

```text
dotnet test tests/S1Atlas.Mcp.Tests/S1Atlas.Mcp.Tests.csproj --filter "FullyQualifiedName~ReferenceCollectionToolTests|FullyQualifiedName~McpTrustBoundaryTests.StdioHost_UsesProtocolOnlyStdoutAndRegistersEveryReadOnlyTool|FullyQualifiedName~McpTrustBoundaryTests.StdioHost_CodeSymbolSchemasMatchApprovedContractsAndAllowOmittedOptions|FullyQualifiedName~McpTrustBoundaryTests.ScopeValidation_PreservesScheduleIDefaultsAndRejectsInvalidCollectionCombinations|FullyQualifiedName~McpTrustBoundaryTests.CallableSurface_RemainsScheduleIOnly" --no-restore --logger "console;verbosity=minimal"
```

Result: **Passed 7/7; failed 0; skipped 0.**

Affected projects:

- Core: **127/127 passed**
- Storage: **142/142 passed**
- Indexing: **235/235 passed**
- MCP: included in the full run below at **75/75 passed**

Full solution command:

```text
dotnet test S1Atlas.sln --no-restore --logger "console;verbosity=minimal"
```

Result: **1,318/1,318 passed; failed 0; skipped 0.** This included Docs
8/8, Extraction 551/551, Integration 180/180, and all affected projects.

## Verification and concerns

- `git diff --check` passed.
- The repository hygiene script passed with no tracked proprietary or
  generated paths.
- Whole-solution format verification reports the four pre-existing whitespace
  diagnostics in `ReferenceModFileSelector.cs` (Task 2, lines 44–47); no
  unrelated formatter cleanup was included.
- No network, download, game execution, raw SQLite query in MCP adapters, or
  mutation tool registration was added.
- No push was performed.

## Review fix round 1

Cicero's three Important findings were addressed:

1. The collection catalog now preserves the repository's completion ordering
   and selects the first (newest) completed run per collection, matching
   query-by-name selection instead of sorting content-derived index IDs.
2. `reference` resolution, source, and relationship dispatch no longer fall
   through to the recorded game index. Game endpoint lookup is performed only
   when the explicit federation scope is `all`; `reference` targets that are
   not in the reference index remain unresolved.
3. Federated game legs use the selected collection's recorded base run.
   MCP binds envelope authority to that base and rejects an explicit build ID
   mismatch with `ReferenceCollectionBuildMismatch`.

### RED

The new review regressions failed before the fixes: the catalog advertised
`reference-z-stale` instead of the newer `reference-a-new`; the multi-build
query reported the latest `build-b` instead of the collection's `build-a`;
the stdio game-only reference-scope case returned `resolved`; and the query
layer returned a game endpoint under `reference`.

### GREEN

- New MCP review regressions: **3/3 passed**.
- Isolated reference relationship regression: **1/1 passed**.
- Full MCP suite: **78/78 passed**.
- Full Indexing suite: **235/235 passed**.
- Affected CLI scoped-query regression: **1/1 passed**.
- Previously flaky extraction installer test, rerun in isolation: **1/1 passed**.
- Final full solution: **1,321/1,321 passed** — Core 127, Docs 8,
  Indexing 235, Extraction 551, Storage 142, Integration 180, MCP 78.

An intermediate MCP rerun encountered orphaned stdio test processes holding a
test assembly; only those exact test/MCP PIDs were terminated before rerunning.
The earlier full-solution run also had the known temporary-directory
installer promotion flake and an outdated CLI expectation; both were isolated
and the installer test passed alone. The repository-wide format baseline still
has the four pre-existing whitespace diagnostics in
`ReferenceModFileSelector.cs` lines 44–47.
