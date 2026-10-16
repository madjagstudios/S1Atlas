# AT-149: Scene query errors point at `s1atlas setup --scene` when no scene snapshot exists

Attach the `s1atlas setup --scene` hint to every `NoCompletedSceneIndex` scene-query failure that targets the current build, on both the MCP and CLI surfaces. The approach is one shared rule plus thin per-surface plumbing: add `ReadinessFixCommands.HintForNoCompletedSceneIndex` next to `HintForAuthorityStatus` so both surfaces apply the identical current-build rule and cannot drift, pass the resolved build id from MCP (prefix-safe, since MCP build ids accept prefixes) and the raw `--build` value from CLI (exact-match only per the `WHERE build_id = $build` scene lookup), and resolve the current build id only on the `NoCompletedSceneIndex` failure path. When no explicit build is given the request targets the current build by construction, so no lookup is needed; an explicit build equal to the current one gets the hint, any other explicit build gets none.

### Task 1: MCP scene tools hint at `setup --scene` on `NoCompletedSceneIndex`

Goal: every MCP scene tool (`list_scenes`, `get_scene`, `get_gameobject`, `get_component`, `get_scriptable_object`) returns `NoCompletedSceneIndex` with hint `s1atlas setup --scene` when the request targets the current build and no scene snapshot exists, and with no hint when an explicit other build is requested.

Files:

- `src/S1Atlas.Application/Readiness/ReadinessFixCommands.cs`: add the shared rule helper `public static string? HintForNoCompletedSceneIndex(string? buildId, string? currentBuildId)`, returning the existing `SetupScene` constant when `buildId is null` or `string.Equals(buildId, currentBuildId, StringComparison.Ordinal)`, else null. Document that MCP callers pass the resolved build id and CLI callers pass the requested `--build` value.
- `src/S1Atlas.Mcp/Tools/SceneTools.cs`: thread the hint through both `NoCompletedSceneIndex` branches, the one in `SnapshotError` (no completed snapshot for the authority's build) and the one in `FromResult` (service-level `SceneQueryStatus.NoCompletedSceneIndex`). Both helpers are static and synchronous, so add a hint parameter to each and compute the value in the async callers: fetch the current build id via the read-only repository's `GetCurrentSnapshotAsync` (the same call `InstalledBuildAuthorityResolver` uses for its own hints) only on the `NoCompletedSceneIndex` path, then apply `HintForNoCompletedSceneIndex(authority.ResolvedBuildId, currentBuildId)`. When `authority.RequestedBuildId` is null the resolved build is the current build by construction, so the lookup may be skipped. Pass `ReadinessFixCommands.SetupScene` (the constant, not a string literal) so the text cannot drift. Use the existing third `ToolError` constructor parameter (`Hint`), following the `NoCompletedIndex` precedent in `src/S1Atlas.Mcp/Mapping/EnvelopeMapper.cs`. All six current `FromResult` call sites plus the one `SnapshotError` call site must pass the hint; leave every other status branch untouched.
- `tests/S1Atlas.Mcp.Tests/SceneToolTests.cs`: add one test method covering acceptance criteria 1 and 3 together. Seed with the existing `McpTestAtlas.SeedTwoInstalledBuildsAsync` helper (two healthy installed builds, neither with a scene snapshot). Call `list_scenes` with no `buildId` and assert status `NotFound`, `error.code == "NoCompletedSceneIndex"`, and `error.hint == ReadinessFixCommands.SetupScene`; then call `list_scenes` with the explicit non-current build id and assert the same code with a null hint. Check which of `BuildIdA`/`BuildIdB` the helper leaves current (existing `SeedTwoInstalledBuildsAsync` consumers show the convention) and name the builds explicitly in the test.

Out of scope: the `NoCompletedSceneIndex` to `no_completed_index` mapping in `tests/S1Atlas.Mcp.Tests/McpErrorCodeTests.cs` is unaffected by hints; do not touch it.

Checks (run from the repo root, PowerShell):

- `dotnet build S1Atlas.sln --configuration Release`
- `dotnet test tests/S1Atlas.Mcp.Tests --configuration Release --no-build` (must include the new test and the existing `GetScene_NoSceneIndex_ReturnsNoCompletedSceneIndex`, which asserts the code only and must keep passing)
- `dotnet format S1Atlas.sln --verify-no-changes --no-restore` (if it reports changes in touched files, run `dotnet format S1Atlas.sln --no-restore` and re-verify)

The task ends when the code change, the new test, and all three checks above are in place.

### Task 2: CLI scene commands carry the same hint in JSON and human output

Goal: the six CLI scene commands (`scenes`, `scene`, `game-object`, `prefab`, `component`, `scriptable-object`) report `NoCompletedSceneIndex` with hint `s1atlas setup --scene` when the request targets the current build, and with no hint for an explicit other build. Only `scenes` accepts `--build`/`--snapshot` (`src/S1Atlas.Cli/Commands/ScenesCommand.cs`); the other five commands always query the current build, so they always attach the hint on this status.

Files:

- `src/S1Atlas.Application/Readiness/ReadinessFixCommands.cs`: reuse `HintForNoCompletedSceneIndex` added in Task 1 (`public static string? HintForNoCompletedSceneIndex(string? buildId, string? currentBuildId)`; `SetupScene` when `buildId` is null or ordinal-equal to `currentBuildId`, else null). If it is missing, add it exactly as specified here rather than inlining the rule.
- `src/S1Atlas.Cli/Commands/SceneCommandSupport.cs`: extend the failure path so `NoCompletedSceneIndex` can carry a hint. `FailureFor` currently returns `(code, message)?` and `Write` forwards to `CommandOutput.FailureWithData`, which has no hint channel. Implement the smallest change that lets all six commands pass a hint for this status (for example, a hint-aware `FailureFor` overload plus a hint parameter on `Write`), resolving the current build id via `repository.GetCurrentSnapshotAsync` only when a failure with an explicit `--build` needs the comparison (established pattern in `src/S1Atlas.Cli/Commands/IndexCommand.cs` and `src/S1Atlas.Cli/Commands/SourceCommand.cs`), then applying `HintForNoCompletedSceneIndex(requestedBuild, currentBuildId)`. The `--build` value compares with ordinal string equality: the scene lookup is exact-match (`WHERE build_id = $build` in `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Scene.cs`), so there is no prefix case to handle.
- `src/S1Atlas.Cli/Output/CommandOutput.cs`: only if needed for the hint channel chosen above (for example, an optional `hint` parameter on `FailureWithData` that flows into `CliError` for `--json` and prints the `Next:` line in human output, matching `Failure`). Do not change the `Failure` methods or the envelope shape.
- The five single-item commands (`src/S1Atlas.Cli/Commands/SceneCommand.cs`, `GameObjectCommand.cs`, `PrefabCommand.cs`, `ComponentCommand.cs`, `ScriptableObjectCommand.cs`) and `ScenesCommand.cs`: pass the requested build (or null where the command has no `--build` option) through the new hint path. No other behavior changes.
- `tests/S1Atlas.IntegrationTests/Scene/SceneCliTests.cs`: add one test. Invoke `scenes --json` via `CliApplication` on a fresh temp data directory (no scene snapshot, mirroring the existing `Scene_query_commands_have_human_and_json_stable_failures` theory setup), parse stdout as JSON, and assert `error.code == "NoCompletedSceneIndex"` and `error.hint == "s1atlas setup --scene"`. Assert against the `ReadinessFixCommands.SetupScene` constant.

The existing `Scene_query_commands_have_human_and_json_stable_failures` theory asserts `Contains(code)` only, so the added hint must not break it; run it as part of the checks.

Checks (run from the repo root, PowerShell):

- `dotnet build S1Atlas.sln --configuration Release`
- `dotnet test tests/S1Atlas.IntegrationTests --configuration Release --no-build --filter "FullyQualifiedName~SceneCliTests"` (must include the new test and the existing scene failure theory)
- `dotnet format S1Atlas.sln --verify-no-changes --no-restore` (if it reports changes in touched files, run `dotnet format S1Atlas.sln --no-restore` and re-verify)

The task ends when the code change, the new test, and all three checks above are in place.

### Task 3: CHANGELOG entry and final verification

Goal: record the user-visible behavior change and prove the acceptance gate is green: `dotnet format S1Atlas.sln --verify-no-changes --no-restore` clean and the full suite green.

Files:

- `CHANGELOG.md`: add one bullet under `## [Unreleased]` in the `### Fixed` section (it is a guidance fix on an existing error, matching the style of the AT-133/AT-142 bullets). State that scene queries (`scenes`, `scene`, `game-object`, `prefab`, `component`, `scriptable-object`, and the matching MCP scene tools) now point at `s1atlas setup --scene` when no scene snapshot exists for the current build, and that an explicit request for a different build carries no hint. End the bullet with `(AT-149)`. Use plain hyphens and ASCII quotes only: no em dashes in CHANGELOG prose. Do not touch any other section.
- No other files. In particular, do not commit `docs/worknotes`, and do not add or change tests in this task; Tasks 1 and 2 own the one-test-per-criterion budget.

Checks (run from the repo root, PowerShell, in this order):

- `dotnet format S1Atlas.sln --verify-no-changes --no-restore` (must be clean; if it reports changes, run `dotnet format S1Atlas.sln --no-restore` and re-verify)
- `dotnet build S1Atlas.sln --configuration Release`
- `dotnet test S1Atlas.sln --configuration Release --no-build` (full suite, green; `LocalGameRequired` tests stay skipped by default per `CONTRIBUTING.md`)

The task ends when the CHANGELOG entry and all three checks above are in place.
