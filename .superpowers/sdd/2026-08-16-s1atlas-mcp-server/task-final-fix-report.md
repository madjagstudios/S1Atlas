# Final review fix wave report

Date: 2026-08-16
Branch: `codex/s1atlas-mcp`

## Scope completed

- Routed every Schedule I CLI query, including `--channel all`, through the shared preferred verified Installed authority path. S1API/S1MAPI cross-channel behavior remains unchanged.
- Updated the approved MCP schemas to `get_type(selector, buildId?, limit?)`, `get_method(selector, buildId?, limit?)`, and `find_related_types(selector, buildId?, relationKinds?, limit?)`; omitted nullable arguments have defaults, and supplied limits/relation filters are applied.
- Serialized MCP `ToolStatus` values as lowercase snake case and provenance classifications as uppercase without changing the internal enum values used by comparisons.
- Revalidated the selected completed index against its loaded code snapshot (Schedule I, Installed, preferred extraction source identity) and any persisted environment/build association; mismatches return `IndexBuildMismatch` without index data.
- Separated preferred verified-extraction availability from completed preferred-index availability in `list_builds`.
- Mapped unexpected MCP storage/query exceptions to stable `Unavailable` / `AtlasUnavailable` data without raw exception/path details, while rethrowing cancellation. Source integrity and missing-source responses also use stable safe messages.
- Moved code-symbol selector, limit, and kind validation behind authority resolution so invalid responses retain available build context; blank search queries now return `InvalidArguments`, and relation-kind validation is explicit.

The previously recorded minor deferrals were not changed: repository SELECT duplication, performance work, the README `ComponentNotFound` example, the lexical dependency guard, and the no-match assertion.

No plan or specification document was modified.

## Focused verification

The regression tests were observed failing for the intended pre-fix behavior before their corresponding production changes. Final focused results:

```text
dotnet test tests\S1Atlas.IntegrationTests --configuration Release --filter "FullyQualifiedName~CliQueryParityTests.Search_ScheduleI_AllChannels_UsesPreferredVerifiedIndex"
PASS — 1 passed, 0 failed, 0 skipped

dotnet test tests\S1Atlas.Indexing.Tests --configuration Release --filter "FullyQualifiedName~InstalledBuildAuthorityResolverTests.Resolve_PreferredIndexAssociatedWithDifferentBuild_ReturnsIndexBuildMismatch"
PASS — 1 passed, 0 failed, 0 skipped

dotnet test tests\S1Atlas.Mcp.Tests --configuration Release --filter "FullyQualifiedName~BuildEnvironmentToolTests.ListBuilds_PreferredVerifiedExtractionWithoutIndex_ReportsIndependentAvailability"
PASS — 1 passed, 0 failed, 0 skipped

dotnet test tests\S1Atlas.Mcp.Tests --configuration Release --filter "FullyQualifiedName~McpTrustBoundaryTests.StdioHost_CallTool_ReturnsSerializedAuthorityEnvelope|FullyQualifiedName~McpTrustBoundaryTests.StdioHost_CodeSymbolSchemasMatchApprovedContractsAndAllowOmittedOptions"
PASS — 2 passed, 0 failed, 0 skipped

dotnet test tests\S1Atlas.Mcp.Tests --configuration Release --filter "FullyQualifiedName~CodeSymbolToolTests"
PASS — 21 passed, 0 failed, 0 skipped

dotnet test tests\S1Atlas.Mcp.Tests --configuration Release --filter "FullyQualifiedName~McpTrustBoundaryTests.CorruptAtlas_ReturnsStableUnavailableWithoutStorageDetails"
PASS — 1 passed, 0 failed, 0 skipped

git diff --check
PASS — no whitespace errors (only Git line-ending conversion warnings)
```

## Full-solution verification status

The requested full commands were not run after the final wave because the user subsequently directed the agent to stop broad test runs and commit immediately:

```text
dotnet build S1Atlas.sln --configuration Release
NOT RUN for the final wave

dotnet test S1Atlas.sln --configuration Release --no-build
NOT RUN for the final wave
```

Each focused `dotnet test` command built its affected project dependency graph successfully before executing the selected tests.
