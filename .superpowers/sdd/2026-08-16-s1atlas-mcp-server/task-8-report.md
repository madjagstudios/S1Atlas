# Task 8 report: `compare_symbol`

Implemented the read-only MCP `compare_symbol` tool.

- Requires a non-blank selector and two explicit, distinct build IDs; blank arguments return `InvalidArguments`.
- Resolves authority independently for both builds and compares their verified Schedule I Installed indexes.
- Returns derived provenance for both build contexts and maps an absent diff to `SymbolNotFound`/`NotFound`.
- Added coverage for missing build IDs, unchanged symbols, and method-body changes.
- Updated the two-build test fixture with unique persisted IDs and canonical compare-symbol data.

## Verification

Command:

```powershell
dotnet test tests/S1Atlas.Mcp.Tests --filter CompareToolTests --no-restore
```

Result: 3 passed, 0 failed, 0 skipped.

## Review fix

Replaced the single-build `ToolEnvelope<SymbolDiff>` return with a comparison-specific envelope that preserves the standard status, error, candidates, data, and provenance conventions while exposing full `BuildA` and `BuildB` contexts. Both contexts are populated on successful comparisons and symbol-not-found results; when right-build authority fails, the resolved left context remains available alongside the right failure context. Added assertions for both contexts, a no-match `NotFound` case, and right-build failure preservation.

Verification after the fix: 5 passed, 0 failed, 0 skipped.
