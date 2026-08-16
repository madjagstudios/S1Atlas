# Task 6 Report

Date: 2026-08-16
Branch: `codex/s1atlas-mcp`
Task: `S1Atlas.Mcp` stdio host, read-only composition, and tool-catalog guard
Status: DONE

## Summary

Implemented the new `S1Atlas.Mcp` host project, including:

- a read-only composition root for authority, index query, diff, and scene query services
- a hosted `mcp serve` entrypoint with stderr-only console logging
- MCP SDK registration using the currently restorable `ModelContextProtocol` package
- focused MCP composition and tool-catalog guard tests

No MCP tool methods were added yet. The assembly is ready to host future `[McpServerToolType]` / `[McpServerTool]` tool classes without widening the service graph beyond the approved read-only surface.

## Files Changed

- Created `src/S1Atlas.Mcp/S1Atlas.Mcp.csproj`
- Created `src/S1Atlas.Mcp/Program.cs`
- Created `src/S1Atlas.Mcp/McpServerComposition.cs`
- Created `src/S1Atlas.Mcp/McpToolCatalog.cs`
- Modified `tests/S1Atlas.Mcp.Tests/S1Atlas.Mcp.Tests.csproj`
- Created `tests/S1Atlas.Mcp.Tests/HostCompositionTests.cs`
- Created `tests/S1Atlas.Mcp.Tests/McpTestAtlas.cs`
- Modified `S1Atlas.sln`

## Implementation Notes

- `McpServerComposition.BuildReadOnlyServices(string dataDirectory)` now constructs:
  - `ReadOnlySqliteAtlasRepository` over `ReadOnlySqliteConnectionFactory`
  - `PreferredVerifiedExtractionResolver`
  - `InstalledBuildAuthorityResolver`
  - `IndexQueryService`
  - `BuildDiffService`
  - `SceneQueryService`
- The composition creates no HTTP clients, process extractors, game locators, installers, or mutable repository services.
- `Program.cs` enforces the `mcp serve` command shape, resolves the Atlas home directory via `AtlasPaths.FromEnvironment()`, clears default logging providers, routes console logging to stderr, and registers the MCP server with:
  - `AddMcpServer()`
  - `WithStdioServerTransport()`
  - `WithToolsFromAssembly(...)`
- The currently restorable MCP SDK version was resolved and pinned with:
  - `ModelContextProtocol` `2.2.0`
- `McpToolCatalog.DiscoverToolNames()` reflects over `[McpServerTool]` methods by attribute name so the guard test stays valid even before concrete tool classes exist.
- The extraction integrity verifier is activated from the existing extraction assembly via reflection so Task 6 can reuse the same validated-extraction checks without expanding this task into extraction-surface changes.
- The seeded MCP test atlas now matches the actual `AtlasPaths` contract: the data root is the directory that directly contains `atlas.db`.

## TDD / Verification Log

1. Added the initial failing `HostCompositionTests` coverage for read-only authority resolution and the mutation-verb tool-catalog guard.
2. Verified the red state with `dotnet test tests/S1Atlas.Mcp.Tests --filter HostCompositionTests`, which failed because `McpServerComposition` and `McpToolCatalog` did not exist.
3. Implemented the `S1Atlas.Mcp` project skeleton, solution wiring, read-only service composition, tool-catalog discovery helper, and `mcp serve` host entrypoint.
4. Resolved the MCP SDK package version with `dotnet add src/S1Atlas.Mcp/S1Atlas.Mcp.csproj package ModelContextProtocol`, which pinned `2.2.0`.
5. Confirmed the hosted registration API from the installed package surface and wired `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly(...)`.
6. Investigated the first runtime failure systematically and traced it to the test fixture using the wrong Atlas data-root shape rather than to the host composition itself.
7. Corrected the seeded test atlas to place `atlas.db` at the real data root.
8. Re-ran the focused MCP tests successfully.
9. Built the full solution successfully.
10. Committed the work with the required trailer.

## Commit

- `TBD`

## Commands Run

```powershell
dotnet test tests/S1Atlas.Mcp.Tests --filter HostCompositionTests
dotnet add src\S1Atlas.Mcp\S1Atlas.Mcp.csproj package ModelContextProtocol
dotnet test tests/S1Atlas.Mcp.Tests --filter HostCompositionTests
dotnet build S1Atlas.sln
```

## Test Results

- `dotnet test tests/S1Atlas.Mcp.Tests --filter HostCompositionTests` -> PASS (2 tests)
- `dotnet build S1Atlas.sln` -> PASS

## Concerns

- `S1Atlas.Mcp` currently depends on reflection to instantiate the existing extraction integrity verifier and document store because that constructor surface is not publicly available to the new host project. The behavior is covered by the composition test, but this remains the main maintainability risk until a shared public factory or visibility decision is made in a later task.

## Review Fix Round 1

Addressed the review finding about MCP crossing the approved boundary:

- removed the direct `S1Atlas.Cli` project reference from `S1Atlas.Mcp`
- removed the direct `S1Atlas.Extraction` project reference from `S1Atlas.Mcp`
- moved Atlas-home/data-path resolution into shared application code via `AtlasDataPaths`
- moved read-only repository/service composition into `S1Atlas.Application.Composition.ReadOnlyAtlasComposition`
- replaced MCP-side reflection-based verifier activation with a supported extraction factory method used by the shared application composition
- added MCP boundary regression coverage to verify:
  - the MCP project file no longer references `S1Atlas.Cli` or `S1Atlas.Extraction`
  - `McpServerComposition` delegates to `ReadOnlyAtlasComposition` and no longer reflects into verifier construction

### Commands Run

```powershell
dotnet test tests/S1Atlas.Mcp.Tests --filter HostCompositionTests
dotnet build S1Atlas.sln
```

### Output

- `dotnet test tests/S1Atlas.Mcp.Tests --filter HostCompositionTests` -> PASS (4 tests)
- `dotnet build S1Atlas.sln` -> PASS

### Concerns

- None for this fix round. The reviewed host-level reflection and forbidden direct project references have been removed from `S1Atlas.Mcp`.
