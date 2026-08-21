# S1Atlas usage

Full command walkthrough and reference for the CLI, the read-only MCP server, and
the agent skill. For an overview and quick start, see the [README](../README.md).
Deep internals (data layout, the Cpp2IL pin, validation policy, build identity)
live in [REFERENCE.md](REFERENCE.md).

## Build and test

From the repository root:

```powershell
dotnet restore S1Atlas.sln
dotnet build S1Atlas.sln --configuration Release
dotnet test S1Atlas.sln --configuration Release --no-build
```

## Run the CLI

Using an explicit game path is the most reliable first run:

```powershell
dotnet run --project src/S1Atlas.Cli -- scan --game-path "C:\Program Files (x86)\Steam\steamapps\common\Schedule I"
```

Then explore the stored environment:

```powershell
dotnet run --project src/S1Atlas.Cli -- status
dotnet run --project src/S1Atlas.Cli -- env
dotnet run --project src/S1Atlas.Cli -- builds
```

Inspect or explicitly install the managed Cpp2IL pin:

```powershell
dotnet run --project src/S1Atlas.Cli -- tools status cpp2il
dotnet run --project src/S1Atlas.Cli -- tools install cpp2il
dotnet run --project src/S1Atlas.Cli -- tools install cpp2il --repair
```

`tools status` is always offline. Of the implemented commands, only
`tools install cpp2il` can access the network, and installation never happens
implicitly during a scan or status query.

Run an extraction against the current indexed build and verified managed pin:

```powershell
dotnet run --project src/S1Atlas.Cli -- extract
dotnet run --project src/S1Atlas.Cli -- extract --json
```

Use an explicit build, game root, custom tool, or input snapshot request when
needed:

```powershell
dotnet run --project src/S1Atlas.Cli -- extract --build <64-character-build-id>
dotnet run --project src/S1Atlas.Cli -- extract --game-path "C:\Games\Schedule I"
dotnet run --project src/S1Atlas.Cli -- extract --cpp2il-path "C:\Tools\Cpp2IL.exe"
dotnet run --project src/S1Atlas.Cli -- extract --profile cpp2il-reconstructed-assemblies-v1 --retry
dotnet run --project src/S1Atlas.Cli -- extract --snapshot-inputs
dotnet run --project src/S1Atlas.Cli -- extract --input-snapshot <64-character-snapshot-id> --retry
dotnet run --project src/S1Atlas.Cli -- extract --keep-failed-artifacts
```

`--input-snapshot` runs Cpp2IL from a stored input snapshot instead of live game
input. It requires `--retry` (so it always runs a new process from the archive),
never falls back to live input, and cannot be combined with `--game-path` or
`--snapshot-inputs`. The snapshot's immutable `game-root` is the Cpp2IL root. Only
after Cpp2IL runs from that exact snapshot and Phase 4 returns an authoritative
validated extraction does S1Atlas certify the snapshot `replay_verified = 1`;
certification is idempotent and preserves the first certification timestamp. Every
process-backed `extract` reports the input source, the input snapshot ID (when
one applies), and whether that snapshot is replay-verified.

`extract` is always offline: it never installs or downloads a tool. Without
`--cpp2il-path`, it requires the exact managed pin to be freshly verified and
reports `ManagedPinned` trust. An explicit executable is freshly hashed and
capability-probed, remains outside the managed tools root, and reports
`CustomOverride` trust.

A successful `extract` reports an **authoritative validated extraction**: it
prints the extraction ID and root, the validation outcome, tool trust, whether
the output is the preferred one for the build, and whether the process,
validation, and reuse ran. It exits `0` only when the extraction is authoritative
(validation outcome `Valid` or `ValidWithWarnings` and full integrity proven). A
candidate that runs the process but fails validation is never authoritative and
exits `1` without a `complete.marker` or preference change. When a matching
validated extraction already exists, `extract` reuses it after a full integrity
check without rerunning Cpp2IL (`reusedExistingExtraction` is `true`,
`processWasRun` is `false`); `--retry` is the only path that deliberately forces a
new Cpp2IL process.

Inspect validated extraction history and manage the preferred output:

```powershell
dotnet run --project src/S1Atlas.Cli -- extractions list
dotnet run --project src/S1Atlas.Cli -- extractions list --build <64-character-build-id>
dotnet run --project src/S1Atlas.Cli -- extractions list --include-failed
dotnet run --project src/S1Atlas.Cli -- extractions show <extraction-id-or-attempt-id>
dotnet run --project src/S1Atlas.Cli -- extractions promote <extraction-id>
dotnet run --project src/S1Atlas.Cli -- extractions cleanup
dotnet run --project src/S1Atlas.Cli -- extractions cleanup --older-than 30d
dotnet run --project src/S1Atlas.Cli -- extractions cleanup --older-than 30d --apply
dotnet run --project src/S1Atlas.Cli -- extractions cleanup --json
```

`extractions` commands never issue a network request. `list` shows validated
extractions newest first and, with `--include-failed`, also folds in failed,
canceled, abandoned, and candidate attempts. `show` accepts a 64-character
extraction ID (and performs a fresh full integrity check, reporting an
operational failure without exposing the root if the output no longer matches) or
a 32-character attempt ID (returning its lifecycle, validation, and result
facts). `promote` is explicit and non-interactive: it verifies integrity and the
current policy, records a `ManualPromotion` audit, is idempotent when the
extraction is already preferred, and rejects attempt IDs. Known history states
exit `0`; unknown IDs and integrity failures exit `1`; cancellation exits `2`.

`cleanup` is **preview-first and never automatic**: without `--apply` it only
reports what it would remove and deletes nothing. `--older-than` takes a positive
lower-case integer followed by `m`, `h`, or `d` (default `30d`, maximum `36500d`),
and an item is eligible only when its controlling timestamp is strictly earlier
than the cutoff. Cleanup may remove only proven Atlas-owned, age-eligible data:
`Failed`, `Canceled`, or `Abandoned` attempts and their bounded logs, validation
documents, and retained output; recoverably stale extraction, input, and tool
staging; and old quarantined managed-tool installations. It **never** removes a
`ProcessCompleted` candidate, a `Succeeded` attempt or one referenced by a
validated extraction, a validated extraction, an input snapshot (verified or not),
the current managed-tool installation, or any active or ambiguous evidence, and it
never follows a reparse point or enumerates inside a validated extraction or input
snapshot. It runs recovery first, refuses to run while an extraction lock is held,
re-observes every candidate immediately before deletion (a changed candidate is
preserved), and deletes files before the matching database row so an interrupted
run stays truthful and idempotently retryable. Preview exits `0` even with blocked
evidence; `--apply` exits `0` only when nothing remained blocked or failed,
`1` when any did, and `2` on cancellation. `extractions cleanup` issues no network
request.

Build and query the code index once the current build has a preferred,
integrity-verified extraction. The index decompiles the reconstructed assemblies
with ILSpy, records normalized symbols and relationships with Roslyn, and answers
queries entirely offline:

```powershell
dotnet run --project src/S1Atlas.Cli -- index
dotnet run --project src/S1Atlas.Cli -- index --codebase s1api --channel installed
dotnet run --project src/S1Atlas.Cli -- search "<name-fragment>" --limit 25
dotnet run --project src/S1Atlas.Cli -- type "<Namespace.TypeName>"
dotnet run --project src/S1Atlas.Cli -- method "<TypeName.MethodName>"
dotnet run --project src/S1Atlas.Cli -- source "<TypeName.MethodName>" --context 6
dotnet run --project src/S1Atlas.Cli -- source "<TypeName.MethodName>" --file --output symbol.cs
dotnet run --project src/S1Atlas.Cli -- refs "<TypeName.MethodName>" --json
dotnet run --project src/S1Atlas.Cli -- callers "<TypeName.MethodName>"
dotnet run --project src/S1Atlas.Cli -- callees "<TypeName.MethodName>"
```

Upstream S1API/S1MAPI channels are cached explicitly before a release/preview
index; `upstream status` is always offline and `upstream sync` is the only
networked upstream command:

```powershell
dotnet run --project src/S1Atlas.Cli -- upstream status --codebase s1api
dotnet run --project src/S1Atlas.Cli -- upstream sync s1api --commit <40-character-sha>
dotnet run --project src/S1Atlas.Cli -- index --codebase s1api --channel release --commit <40-character-sha>
```

Build and query the static scene intelligence index after the same build has a
preferred integrity-verified extraction, a replay-verified input snapshot, and a
completed Schedule I Installed code index:

```powershell
dotnet run --project src/S1Atlas.Cli -- index --scene
dotnet run --project src/S1Atlas.Cli -- index --scene --build <64-character-build-id> --json
dotnet run --project src/S1Atlas.Cli -- scenes --kind scene --limit 50
dotnet run --project src/S1Atlas.Cli -- scenes --kind prefab --limit 50 --json
dotnet run --project src/S1Atlas.Cli -- scene <scene-id-or-exact-name> --children --components --refs
dotnet run --project src/S1Atlas.Cli -- gameobject <game-object-id-or-scene-id/name> --children --components --refs
dotnet run --project src/S1Atlas.Cli -- prefab <prefab-id-or-exact-name> --objects --components
dotnet run --project src/S1Atlas.Cli -- component <component-id-or-exact-type> --refs --code --json
```

Compare two indexed builds to see what changed:

```powershell
dotnet run --project src/S1Atlas.Cli -- diff <build-id-before> <build-id-after>
dotnet run --project src/S1Atlas.Cli -- diff <build-id-before> <build-id-after> --kind Method --json
dotnet run --project src/S1Atlas.Cli -- diff <build-id-before> <build-id-after> --limit 100
```

`diff` compares existing indexed data and classifies each symbol as Added,
Removed, MethodBodyChanged, RelationshipsChanged, or Unchanged. It requires both
builds to have a completed, preferred, integrity-verified index. The command is
entirely offline.

Scene indexing is static, offline, and read-only with respect to the game install.
It parses only the supported Unity 2022.3 SerializedFile containers and sidecars;
it never launches the game, Unity, a managed game assembly, or a parser subprocess,
and it makes no network request. Inputs are hashed before and after parsing. A
completed immutable snapshot is written beneath the local Atlas data root, with
its marker written last; failed imports are not queryable. List and nested queries
are counted in SQLite and bounded to 50 rows by default unless `--limit` is given.

### Scene fidelity boundary

Scene intelligence reports only facts proven by the selected serialized files:

- `FullyRecovered`, `PartiallyRecovered`, `GraphOnly`, `StubOrUnavailable`, and
  `Unknown` are categorical availability states, not confidence scores.
- A custom MonoBehaviour without a reviewed field schema is `GraphOnly` when its
  identity and attachment graph are available. S1Atlas does not invent custom
  fields or values and v1 has no general serialized-field value table.
- MonoBehaviour-to-code links require one exact same-build Schedule I Installed
  `SymbolIdentity` match. Missing, ambiguous, unavailable, and not-indexed links
  remain explicit; no fuzzy match is substituted.
- PPtrs resolve only to exact objects in the verified parsed container set.
  External or missing targets retain unresolved evidence rather than inferred
  destinations.
- A prefab document requires parser-certified prefab/PrefabInstance class-ID
  evidence. Marker text and ordinary asset-file roots are not prefab proof.
- Scene names come from recovered build-settings scene paths. When unavailable,
  the raw container basename is an explicit fallback, not a fabricated name.
- UnityFS/AssetBundle-only content, YAML scenes, runtime behavior, visual or world
  reconstruction, spatial inference, and complete prefab coverage are outside v1.

An empty query or zero recovered graph rows is therefore not proof that the game
contains no matching runtime objects. Inspect each row's recovery and resolution
statuses and treat the recorded counts as measured coverage denominators.

For live input, S1Atlas re-hashes the selected build inputs before process
execution and again afterward. A mismatch before execution requires a new
`scan`; a change during execution rejects the output. `--snapshot-inputs`
copies and re-hashes the approved profile inputs into an immutable snapshot,
but records that snapshot with `replay_verified = false`. It does not become
eligible for archived replay merely because it was copied successfully — a
snapshot is certified only by a later explicit `extract --input-snapshot <id>
--retry` that runs Cpp2IL from the archive and produces an authoritative
extraction. Implicit historical input resolution uses only replay-verified
snapshots; an explicit `--input-snapshot` run may select an unverified snapshot
precisely so it can certify it.

For machine-readable output, add `--json` to the query commands:

```powershell
dotnet run --project src/S1Atlas.Cli -- status --json
dotnet run --project src/S1Atlas.Cli -- env --json
dotnet run --project src/S1Atlas.Cli -- builds --json
dotnet run --project src/S1Atlas.Cli -- tools status cpp2il --json
dotnet run --project src/S1Atlas.Cli -- tools install cpp2il --json
dotnet run --project src/S1Atlas.Cli -- extract --json
```

Each JSON invocation writes exactly one top-level envelope to stdout with `schemaVersion`, `command`, `success`, `exitCode`, `data`, and `error`. Schema version 1 defines that top-level contract. Later command-specific error objects may add fields, so consumers should ignore error properties they do not recognize.

Without `--game-path`, S1Atlas checks the standard Steam locations under `Program Files (x86)` and `Program Files`.

## Generate the static portal

```powershell
dotnet run --project src/S1Atlas.Cli -- docs generate
dotnet run --project src/S1Atlas.Cli -- docs generate --build <build-id> --output .\portal
```

`--build` pins only the Schedule I Installed pages through the preferred,
integrity-verified extraction authority. S1API and S1MAPI pages independently
use each codebase/channel's latest completed index and show its source commit and
index ID; they are not governed by the game-build pin. Scene pages are deferred
from the portal in V1 and remain available through the CLI and MCP. The command
is offline and read-only, writes outside the Atlas data root, and reports a
scan-or-migration-first error for a missing or wrong-schema database without
creating or migrating it. The default output is `./s1atlas-docs/`; open its
`index.html` in any browser.

## Read-only MCP server

Launch the MCP server as a separate executable over stdio:

```powershell
dotnet run --project src/S1Atlas.Mcp -- mcp serve
```

Standard output is reserved for MCP protocol messages; diagnostics and logs go
to standard error. MCP uses the same Atlas data root as the CLI: `%LOCALAPPDATA%\S1Atlas`
by default, or the root supplied through `S1ATLAS_HOME`. The variable moves the
database and all Atlas-owned data together. MCP opens the existing database in
read-only mode; it does not create the root or database, run migrations, or
change stored data.

V1 exposes only the Schedule I `Installed` surface through these tools:

`search_symbols`, `get_type`, `get_method`, `get_source`, `find_callers`,
`find_references`, `find_related_types`, `compare_symbol`, `list_builds`,
`get_environment`, `list_scenes`, `get_scene`, `get_gameobject`, `get_prefab`,
and `get_component`.

Every symbol and scene query resolves an omitted `buildId` from the current
environment, while an explicit build ID is used exactly and never silently
replaced. The selected build must have a preferred, integrity-verified
extraction and a completed matching Installed index; MCP never returns a Phase
3 candidate, retained failure output, unverified extraction, or unchecked index
row. `compare_symbol` requires two explicit build IDs. Tool responses include
status, requested and resolved build context, extraction/index provenance,
integrity state, data, candidates where applicable, and structured errors.
`get_environment` returns only the current environment snapshot when `buildId`
is omitted. With an explicit non-current build ID it returns `unavailable` with
`NoMatchingEnvironmentSnapshot`; it never returns historical environment facts.
Facts are labeled `FACT`; deterministic selection, ranking, counts,
relationship direction, completeness boundaries, and diff classifications are
labeled `DERIVED`. Expected failures use stable domain codes such as
`AtlasUnavailable`, `InvalidArguments`, `InvalidLimit`, `InvalidKind`,
`NoAtlasState`, `NoCurrentBuild`, `BuildNotFound`,
`NoPreferredVerifiedExtraction`, `ExtractionIntegrityFailure`,
`NoCompletedIndex`, `IndexBuildMismatch`, `SymbolNotFound`,
`AmbiguousSymbol`, `SourceUnavailable`, `SourceIntegrityFailure`,
`NoMatchingEnvironmentSnapshot`, `NoCompletedSceneIndex`,
`SceneSnapshotNotFound`, `SceneNotFound`, `GameObjectNotFound`,
`UnresolvedCodeSymbol`, `PartialRecovery`, and `UnresolvedSceneReference`.
Unexpected failures are logged to stderr and
returned as safe MCP tool errors without stack traces or raw storage details.

MCP has no write, patch, network, or game-execution capability. It does not
install tools, run extraction, launch a game or external process, sync
upstream data, or expose S1API/S1MAPI channels. Source and scene results read
only already-indexed Atlas-owned files with existing integrity checks.

## Agent skill

The methodology skill is versioned at [`skills/s1atlas/SKILL.md`](../skills/s1atlas/SKILL.md).
The verified Claude Code path convention for this repository is a
project-scoped `.claude/skills/s1atlas/` install or a user-scoped
`%USERPROFILE%/.claude/skills/s1atlas/` install. From the repo root, a
project-scoped junction can be created with:

```powershell
New-Item -ItemType Directory -Force .claude\skills | Out-Null
New-Item -ItemType Junction -Path .claude\skills\s1atlas -Target (Resolve-Path .\skills\s1atlas)
```

The repository verification includes identical-byte path resolution and a
fresh-agent load/trigger check; this host has no `claude` executable, so it does
not claim a live Claude CLI invocation. When MCP is registered, launch the
read-only server over stdio with
`dotnet run --project src/S1Atlas.Mcp -- mcp serve`; otherwise the skill's CLI
commands remain the fallback. The skill adds no capability and requires agents
to cite FACT/DERIVED evidence and build/extraction/index or API commit/index
identifiers in their own output.

## Command reference

| Command | Purpose |
|---|---|
| `scan [--game-path <path>]` | Discover and persist the current local environment |
| `status [--json]` | Show the current indexed build and installation observation |
| `env [--json]` | Show the current build, installation paths, and tracked dependencies |
| `builds [--json]` | List content-derived builds, newest first-seen first |
| `tools status [tool-id] [--json]` | Inspect pinned managed-tool state offline |
| `tools install <tool-id> [--repair] [--json]` | Explicitly download, verify, install, or repair a managed tool |
| `extract [--build <id>] [--game-path <path>] [--cpp2il-path <path>] [--profile <id>] [--retry] [--snapshot-inputs] [--input-snapshot <id>] [--keep-failed-artifacts] [--json]` | Run offline extraction (from live input or an archived snapshot), then validate and immutably promote an authoritative extraction (or reuse an existing one) |
| `extractions list [--build <id>] [--include-failed] [--json]` | List validated extractions newest first, optionally with failed attempts |
| `extractions show <extraction-or-attempt-id> [--json]` | Show a validated extraction (full integrity) or an attempt's facts |
| `extractions promote <extraction-id> [--json]` | Explicitly make a validated extraction the preferred output for its build |
| `extractions cleanup [--older-than <duration>] [--apply] [--json]` | Preview (default) or, with `--apply`, delete only proven Atlas-owned, age-eligible failure, staging, and quarantine data |
| `index [--codebase <id>] [--channel <id>] [--commit <sha>] [--force] [--json]` | Build the installed Schedule I code index (no options) or an S1API/S1MAPI code index |
| `search <query> [--codebase <id>] [--channel <id>] [--limit <n>] [--json]` | Query the normalized code index across symbols, types, and methods |
| `type <query> [--codebase <id>] [--channel <id>] [--limit <n>] [--json]` | Resolve and inspect indexed type definitions |
| `method <query> [--codebase <id>] [--channel <id>] [--limit <n>] [--json]` | Resolve and inspect indexed method definitions |
| `source <query> [--codebase <id>] [--channel <id>] [--context <n>] [--file] [--output <path>] [--limit <n>] [--json]` | Show integrity-checked decompiled source for one resolved symbol |
| `refs <query> [--codebase <id>] [--channel <id>] [--limit <n>] [--json]` | List indexed references to a resolved symbol |
| `callers <query> [--codebase <id>] [--channel <id>] [--limit <n>] [--json]` | List indexed callers of a resolved method |
| `callees <query> [--codebase <id>] [--channel <id>] [--limit <n>] [--json]` | List indexed callees of a resolved method |
| `upstream status [--codebase <s1api\|s1mapi>] [--json]` | Show cached upstream API status without network access |
| `upstream sync <s1api\|s1mapi> --commit <sha> [--json]` | Fetch and cache one exact upstream commit for later indexing |
| `index --scene [--build <id>] [--force] [--json]` | Build or reuse an offline, integrity-verified scene snapshot for the selected build |
| `scenes [--build <id>] [--snapshot <id>] [--kind scene\|prefab] [--query <text>] [--limit <n>] [--json]` | List counted, bounded scene or proven-prefab documents |
| `scene <id\|exact-name> [--children] [--components] [--refs] [--limit <n>] [--json]` | Inspect one scene and optionally its bounded graph pages |
| `gameobject <id\|scene-id/name> [--children] [--components] [--refs] [--limit <n>] [--json]` | Inspect one GameObject and optionally its bounded graph pages |
| `prefab <id\|exact-name> [--objects] [--components] [--limit <n>] [--json]` | Inspect one parser-proven prefab document |
| `component <id\|exact-type> [--refs] [--code] [--limit <n>] [--json]` | Inspect one component, serialized references, and an exact code-symbol handoff |
| `diff <id-a> <id-b> [--codebase <id>] [--channel <id>] [--kind <kind>] [--limit <n>] [--json]` | Compare two indexed builds and report per-symbol changes |
| `docs generate [--build <id>] [--output <dir>]` | Generate the deterministic, offline static human portal (default `./s1atlas-docs/`) |
| `S1Atlas.Mcp mcp serve` | Launch the read-only Schedule I Installed MCP server over stdio |
