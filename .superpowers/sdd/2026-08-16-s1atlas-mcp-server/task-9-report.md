# Task 9 report: `list_builds` and `get_environment`

Implemented the read-only MCP `list_builds` and `get_environment` tools.

- `list_builds` returns bounded Schedule I Installed build summaries, identifies the current build, and reports preferred verified-extraction and completed-index availability without exposing raw repository rows.
- `get_environment` maps only the current environment snapshot after resolving the existing verified Installed authority. An explicit build other than the current snapshot returns `Unavailable` with `NoMatchingEnvironmentSnapshot`; no historical build can receive another build's environment facts.
- Added focused tests for the non-current safety constraint, build availability/current designation, and current-environment resolution.
- Updated the existing MCP fixture so its seeded code snapshots carry the persisted environment snapshot ID required by `GetLatestCompletedIndexForBuildAsync`.

## TDD / verification

1. Added `GetEnvironment_ExplicitNonCurrentBuild_ReturnsNoMatchingSnapshot` before the tool existed. The focused command failed at compile time because `BuildEnvironmentTools` was absent.
2. Implemented the tools and reran the focused command: 1 passed, 0 failed.
3. Added the remaining focused behavior tests. This revealed the fixture's missing `environmentSnapshotId` linkage; the repository's build-scoped index query therefore correctly returned no index. Linked seeded indexes to their snapshots and reran the focused command.

Command:

```powershell
dotnet test tests/S1Atlas.Mcp.Tests --filter BuildEnvironmentToolTests
```

Result: 3 passed, 0 failed, 0 skipped.
