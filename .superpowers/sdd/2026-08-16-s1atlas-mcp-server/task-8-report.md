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

## Round-2 review fix

Restored `compare_symbol` to return the planned `ToolEnvelope<SymbolDiff>` type. `ToolEnvelope` now has optional `BuildA` and `BuildB` properties, leaving existing `Build` behavior unchanged for other tools. Compare success and `NotFound` responses now include FACT authority provenance for both builds as well as DERIVED comparison provenance; right-build authority failures preserve the left context and explicit failure context/status.

Verification: 5 passed, 0 failed, 0 skipped.

## Round-3 review fix

Corrected authority failure context mapping. A left-build failure now retains `BuildA` for the failed request and exposes an unresolved `BuildB` context for the separately requested right build. A right-build failure retains the resolved left context and its FACT authority provenance while preserving the right failure status, error, context, and provenance.

Verification: 6 passed, 0 failed, 0 skipped.
