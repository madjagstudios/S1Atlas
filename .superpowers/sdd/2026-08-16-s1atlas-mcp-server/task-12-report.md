# Task 12 Report: Host Trust-Boundary Tests

## Scope delivered

- Added `McpTrustBoundaryTests` with real stdio-host tool discovery and `tools/call`, exact V1 tool registration, protocol-only stdout coverage, source-wiring boundary checks, whole-Atlas SHA-256 mutation detection, preferred verified-index isolation, corrupted-source integrity failure, absent-DB read-only behavior, default/historical authority resolution, and explicit missing/ambiguous/unavailable statuses.
- Extended only the existing MCP test fixture with synthetic Phase 3 candidate, retained-failure-output, unverified-input, non-authoritative-index, and non-preferred scene-snapshot records. No proprietary game bytes or network fixtures are used.
- Added the narrow required host behavior: every direct V1 tool path that can open the Atlas (`list_builds`, `get_environment`, and `compare_symbol`, as well as authority-based tools) maps a missing Atlas DB to `Unavailable` / `AtlasUnavailable` rather than escaping as an exception. The read-only open path still does not create or migrate the DB.
- Scene selection now requires a completed snapshot's build ID, extraction ID, and code-index ID to match the resolved preferred authority; non-authoritative snapshots are rejected before query execution.

## Test status

Final focused command:

```powershell
dotnet test tests/S1Atlas.Mcp.Tests --filter McpTrustBoundaryTests --no-restore
```

Result: **PASS** — 11 passed, 0 failed, 0 skipped (13 seconds; command exit code 0).

Full-project command started:

```powershell
dotnet test tests/S1Atlas.Mcp.Tests --no-restore
```

Result: **INTERRUPTED** by the client after 5.9 seconds, before the test runner produced a pass/fail result. The spawned `dotnet test`, VSTest, testhost, and MCP child processes were then explicitly stopped; no test process remained running.
