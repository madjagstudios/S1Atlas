# Task 10 report: Scene MCP tools

Implemented the five read-only scene MCP tools in `SceneTools`:

- `list_scenes`
- `get_scene`
- `get_gameobject`
- `get_prefab`
- `get_component`

Each tool resolves the optional build through `InstalledBuildAuthorityResolver`, defaults an omitted build to the current build, restricts authority to Schedule I Installed, resolves or validates the completed scene snapshot through the read-only repository, and queries only through `SceneQueryService`. Snapshot/build mismatches return `Invalid` with `SceneSnapshotNotFound`; unavailable, not-found, and ambiguous scene states have explicit envelopes. Partial recovery and unresolved-reference results remain data-carrying `Resolved` envelopes.

Focused fixture support seeds replay-verified, same-build scene authorities and uses repository APIs only. No raw database rows or proprietary bytes were added.

Verification run:

```text
dotnet test tests/S1Atlas.Mcp.Tests --filter SceneToolTests

Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4
```

The initial red run failed as expected because `SceneTools` did not yet exist (`CS0246`). No full-suite run was performed, per task scope.

## Review fix

Refactored scene tools so the shared installed-build authority resolves before selector, limit, and kind validation. Invalid argument envelopes now retain the verified resolved `BuildContext` and include fact/derived provenance. `get_scene` now accepts only `Scene` (or an omitted kind, which resolves to `Scene`); `get_prefab` remains fixed to `Prefab`.

Added focused assertions for resolved build context and fact/derived provenance on bounded and partial scene results, plus invalid-selector context and rejected `get_scene(kind: "Prefab")` coverage.

Verification run:

```text
dotnet test tests/S1Atlas.Mcp.Tests --filter SceneToolTests

Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7
```
