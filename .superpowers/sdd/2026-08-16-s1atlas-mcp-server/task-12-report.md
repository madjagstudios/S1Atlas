# Task 12 Report: Host Trust-Boundary Tests

## Scope delivered

- Added `McpTrustBoundaryTests` with real stdio-host tool discovery, exact V1 tool registration, protocol-only stdout coverage, dependency-boundary checks, whole-Atlas SHA-256 mutation detection, preferred verified-index isolation, corrupted-source integrity failure, absent-DB read-only behavior, default/historical authority resolution, and explicit missing/ambiguous/unavailable statuses.
- Extended only the existing MCP test fixture with synthetic Phase 3 candidate, retained-failure-output, unverified-input, and non-authoritative-index records. No proprietary game bytes or network fixtures are used.
- Added the narrow required host behavior: a missing Atlas DB maps to `Unavailable` / `AtlasUnavailable` rather than escaping as an exception. The read-only open path still does not create or migrate the DB.

## Test status

Final focused command:

```powershell
dotnet test tests/S1Atlas.Mcp.Tests --filter McpTrustBoundaryTests --no-restore
```

Result: **PASS** — 8 passed, 0 failed, 0 skipped (7 seconds; command exit code 0).

The broader `dotnet test tests/S1Atlas.Mcp.Tests` integration run was intentionally not run after the focused test, per the request to stop long-running integration testing. No test process remained running; the only observed `dotnet` process was the Roslyn compiler server.
