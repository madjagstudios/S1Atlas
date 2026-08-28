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
