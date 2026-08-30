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

`extract` is always offline and never installs or downloads a tool. It reports
the input, extraction, validation, trust, preference, and reuse state. Exit `0`
means an authoritative validated extraction; failed candidates remain
non-authoritative. A matching validated extraction is reused after an integrity
check unless `--retry` requests a new process. See
**[docs/REFERENCE.md](REFERENCE.md)** for the lifecycle and integrity rules.

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

`extractions` commands never issue a network request. `list` and `show` report
validated extraction history and attempt state; `promote` explicitly verifies
and selects a validated extraction. Known states exit `0`, invalid or failed
operations exit `1`, and cancellation exits `2`.

`cleanup` is preview-first and never automatic. Without `--apply` it reports
eligible data without deleting anything; `--apply` removes only proven
Atlas-owned stale attempts and staging. It never removes validated extractions,
input snapshots, current tools, or uncertain evidence. See
**[docs/REFERENCE.md](REFERENCE.md)** for the full safety and recovery rules.

Build and query the code index once the current build has a preferred,
integrity-verified extraction. The index decompiles the reconstructed assemblies
with ILSpy, records normalized symbols and relationships with Roslyn, and answers
queries entirely offline:

```powershell
dotnet run --project src/S1Atlas.Cli -- index
dotnet run --project src/S1Atlas.Cli -- index --interop-path "C:\path\to\MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll"
dotnet run --project src/S1Atlas.Cli -- index --codebase s1api --channel installed
dotnet run --project src/S1Atlas.Cli -- search "<name-fragment>" --limit 25
dotnet run --project src/S1Atlas.Cli -- type "<Namespace.TypeName>"
dotnet run --project src/S1Atlas.Cli -- method "<TypeName.MethodName>"
dotnet run --project src/S1Atlas.Cli -- source "<TypeName.MethodName>" --context 6
dotnet run --project src/S1Atlas.Cli -- source "<TypeName.MethodName>" --file --output symbol.cs
dotnet run --project src/S1Atlas.Cli -- refs "<TypeName.MethodName>" --json
dotnet run --project src/S1Atlas.Cli -- callers "<TypeName.MethodName>"
dotnet run --project src/S1Atlas.Cli -- callees "<TypeName.MethodName>"
dotnet run --project src/S1Atlas.Cli -- callsites "UnityEngine.AI.NavMeshAgent.CompleteOffMeshLink"
dotnet run --project src/S1Atlas.Cli -- fieldrefs "Demo.State.Value" --readers
dotnet run --project src/S1Atlas.Cli -- callable "<TypeName.MethodName>"
```

Source queries are focused by default. For a resolved method or constructor,
the result includes bounded direct callers and callees from the selected index.
`--related-limit` defaults to `10`, accepts `0` through `50`, and `0` disables
the neighborhood lookup. The neighborhood is callable-only; fields, properties,
events, and type selections do not include one. Caller and callee totals remain separate
and complete even when their row lists are limited, and each direction keeps its
own completeness notice. If an optional relationship lookup fails, the verified
source still succeeds with the neighborhood omitted and a notice explaining
that the evidence was unavailable.

Use `--full-type` to return the containing type's verified source span for a
member selection. This is a type span, not the complete source file, and it
cannot be combined with `--file` or `--output`:

```powershell
dotnet run --project src/S1Atlas.Cli -- source "<TypeName.MethodName>" --context 6 --related-limit 20 --json
dotnet run --project src/S1Atlas.Cli -- source "<TypeName.MethodName>" --full-type --json
dotnet run --project src/S1Atlas.Cli -- source "<TypeName.MethodName>" --file --output symbol.cs
```

When the selected member's source span or canonical signature contains a
recognized physics, navmesh, or trigger-state signal, the result may include a
deterministic runtime-verification hint. The heuristic scans only that selected
span and signature; context lines requested with `--context` are never scanned.
The message format is `Static guidance only: the selected source suggests
<signal names> runtime behavior; verify it in-game.` It is a prompt to test in
the game, not evidence that the runtime behavior occurs.

Source results include a `Body recovery` status for callable symbols. `Recovered`
means the indexed IL provided affirmative body evidence; `NoBodyByDesign` means an
implementation body is not expected; `StubOrUnavailable` means the displayed text
must not be used as behavioral evidence. The latter includes Il2CppInterop
runtime-invoke wrappers, whose generated managed body forwards through
`IL2CPP.il2cpp_runtime_invoke` instead of containing the game's behavior. Schedule I
V1 indexes validated Cpp2IL `dll_il_recovery` reconstructed assemblies and decompiles
them with ILSpy. V1 does not retain a separate ISIL fallback artifact, so a genuinely
unrecovered body is reported as unavailable rather than presented as authoritative.

`callable` answers whether a Schedule I game member is directly callable through
the locally observed Il2CppInterop projection. Public game members are reported
as direct callables even when no interop assembly is present. Private or protected
members require a resolved wrapper; an ambiguous or missing wrapper remains
explicitly unavailable. The interop input is local-only and is not cross-validated
to the selected game build. A resolved runtime-invoke wrapper is an invocation
route, not behavioral evidence: its body forwards through `il2cpp_runtime_invoke`.
The optional `--interop-path` override is valid only for the default installed
Schedule I index; otherwise the standard path is derived from the persisted
installation root.

`callsites` finds static recovered-IL call-site edges for either a resolved
game member selector or canonical raw target text such as
`UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()`. `fieldrefs` resolves one
field and reports incoming `ReadsField` and/or `WritesField` relationships; use
`--readers` or `--writers` to filter, and never both together. Both commands are
bounded, deterministic, and preserve unresolved raw target text and
reference-collection provenance. Call-site queries fall back to raw-target
matching when selector resolution is not resolved, so they do not expose symbol
ambiguity as a separate call-site result state; field-selector ambiguity remains
explicit. They are static relationship evidence only:
they do not prove runtime behavior, scene or geometry behavior, lifecycle
ordering, or call order.

## Investigate seams

Use `investigate_seam` when the question is which exact code seam owns a
behavior, not whether that behavior has already been proved at runtime:

```powershell
dotnet run --project src/S1Atlas.Cli -- investigate_seam "Game.Seams.Target.Run" --question "Which seam owns settlement clearing?"
dotnet run --project src/S1Atlas.Cli -- investigate_seam "Game.Seams.Target.Run" --question "Which seam owns settlement clearing?" --relationship-limit 3 --owner-limit 5 --context 0 --native-symbol-id <native-id> --native-traversal-budget 25 --json
```

The CLI surface requires `<selector>` and `--question`, and also accepts
`--codebase`, `--channel`, `--build`, `--scope`, `--collection`,
`--relationship-limit`, `--owner-limit`, `--context`, repeated
`--native-symbol-id`, `--native-traversal-budget` from `0` to `500`,
`--details`, and `--json`. A zero native budget disables native lookup.

When MCP is registered, call the same investigation through the read-only
`investigate_seam` tool. The MCP surface accepts `selector`,
`behavioralQuestion`, `buildId`, `scope`, `collection`, `relationshipLimit`,
`ownerLimit`, `context`, `details`, `nativeSymbolIds`, and
`nativeTraversalBudget`. `nativeTraversalBudget: 0` (the default) disables
native evidence lookup; a positive budget performs a read-only lookup only for
the explicitly supplied native symbol IDs. MCP already returns the structured
result, so there is no extra `json` argument on the MCP tool.

The CLI JSON envelope and the MCP tool share the same payload contract:
`conclusion`, resolved `candidate`, ordered `ownerCandidates`,
`coverageWarnings`, `unknownDimensions`, and `nextActions`. Candidate and owner
candidate ordering is deterministic owner-candidate order, so the same seeded
request yields the same preferred candidate and owner list on both surfaces.
S1Atlas does not emit a confidence score. Instead, interpret the
returned `FACT`/`DERIVED` claims and separate `unknownDimensions`.

The complete decision packet also carries `pinnedProvenance`,
`authorityEntityAttribution`, `alternateGenericCallersAndExclusivity`,
`lifecyclePositionAndBeforeAfterState`, and `apiBeforePatchResult`. With
`details` off, `claims` and `evidenceSections` are empty while the complete
decision packet and all five gate records remain present; with `details` on,
only those two evidence arrays are populated. The CLI reports resolved research
as `success: true` with exit code `0`; MCP reports the same packet with
`status: resolved`.

CLI JSON has one intentional adapter-specific field: CLI-only
`referenceCollectionBaseProvenance`. It is `null` for game-only results. For a
reference result it records the installed Schedule I build, extraction, and
index that anchor the selected reference collection, while `pinnedProvenance`
records the selected reference index. MCP carries that base authority in its
top-level `build` and `provenance` entries instead of duplicating the CLI-only
field inside `data`.

`investigate_seam` is a read-only investigation: it does not patch code, run
native recovery automatically, or prove runtime behavior. When explicitly
requested, it may attach a matching stored native-evidence summary containing
status, mapping evidence, direct native edges, field accesses, tool identity,
and an output hash. A no-body or failed record remains visible and does not
become a positive seam claim.

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

The `scan`, `extract`, and `index` commands also accept `--performance`, which
writes one phase-timing and counter diagnostics object as JSON to standard
error. It is opt-in, never changes the command's result or exit code, and is
independent of `--json` (which controls the stdout result).

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

## Reference collections

Reference mods are user-supplied local inputs. A manifest is the explicit
selection boundary: S1Atlas reads only the declared roots and selected files,
does not discover or download mods, and does not certify compatibility, safety,
or redistribution rights. Validate and index a collection from the CLI:

```powershell
dotnet run --project src/S1Atlas.Cli -- reference collections validate <manifest>
dotnet run --project src/S1Atlas.Cli -- reference index <manifest>
dotnet run --project src/S1Atlas.Cli -- reference collections list --json
```

Reference indexing is an explicit offline CLI operation. Query commands accept
`--scope game|reference|all` and `--collection <name-or-id>` for `search`,
`source`, `refs`, `callers`, `callees`, `callsites`, and `fieldrefs`:

```powershell
dotnet run --project src/S1Atlas.Cli -- search "ModEntry" --scope reference --collection qol
dotnet run --project src/S1Atlas.Cli -- source "ModEntry.Run" --scope all --collection qol
dotnet run --project src/S1Atlas.Cli -- callers "Game.Target.Run" --scope all --collection qol
dotnet run --project src/S1Atlas.Cli -- callees "ModEntry.Run" --scope reference --collection qol
dotnet run --project src/S1Atlas.Cli -- callsites "UnityEngine.AI.NavMeshAgent.CompleteOffMeshLink" --scope reference --collection qol
dotnet run --project src/S1Atlas.Cli -- fieldrefs "qol/Qol.Config.Setting" --scope reference --collection qol --writers
dotnet run --project src/S1Atlas.Cli -- refs "ModEntry.Run" --scope reference --collection qol
```

The default scope is `game`, preserving the Schedule I behavior. `reference`
and `all` require a collection; `game` rejects one. `type`, `method`, and
`callable` remain their existing game/API convenience surfaces. Reference
scope never falls through to the recorded game index for a game-only selector;
`all` is the explicit cross-origin mode. Reference results preserve their
collection and mod provenance, recorded Schedule I base index, ambiguity,
unresolved targets, and incomplete/no-completed states. Federated MCP queries
use that recorded base index; an explicit `buildId` that differs from the
collection base is rejected deterministically.
Source and indexed document content remain bounded and are returned only after
the recorded content hash is checked.

Body recovery, callable-surface evidence, and reference evidence are orthogonal.
Body recovery describes whether decompiled text is
behavioral evidence; callable surface describes how a Schedule I game member
can be reached through the local interop projection; reference collections are
local prior-art evidence. None of these labels certifies a reference mod's
compatibility, safety, or licensing.

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

The read-only server exposes the Schedule I `Installed` surface and completed
local reference collections through these tools:

`search_symbols`, `get_type`, `get_method`, `get_source`, `find_callers`,
`find_callees`, `find_call_sites`, `find_field_references`,
`find_references`, `find_related_types`, `compare_symbol`, `list_builds`,
`get_environment`, `list_scenes`, `get_scene`, `get_gameobject`, `get_prefab`,
`get_component`, and `list_reference_collections`.

`search_symbols`, `get_source`, `find_callers`, `find_callees`,
`find_call_sites`, `find_field_references`, `find_references`, and
`find_related_types` accept optional `scope` and `collection` arguments.
`scope` defaults to `game`; `reference` and `all` require `collection`, while
`game` rejects it. `find_field_references` also accepts `readers` and `writers`
filters, which are mutually exclusive. `list_reference_collections` reports
completed collections, their recorded base index/build, and local-only mod
metadata. `investigate_seam` accepts the same selector/question/limit options as
the CLI and returns the same ordered candidate, warning, unknown-dimension, and
next-action payload fields. `get_type`, `get_method`, and `get_callable_surface` retain their
Schedule I-only behavior.

`get_source` also accepts `fullType` (default `false`) and `relatedLimit`
(default `10`, bounded to `0`–`50`). `fullType` returns the containing type's
verified source span rather than the complete file; `relatedLimit: 0` disables
the callable neighborhood. `fullType` cannot be combined with full-file output
modes. Source results use the same static runtime-verification heuristic as the
CLI: only the selected member span and canonical signature are scanned, never
context. Its message format is `Static guidance only: the selected source
suggests <signal names> runtime behavior; verify it in-game.` The JSON fields
are `runtimeVerification`, `neighborhood`, and `neighborhoodNotice`.

For callable members, `get_source` can include bounded callers and callees with
separate complete totals and direction-specific completeness notices. Fields,
properties, events, and type selections omit this neighborhood. If the optional
relationship lookup fails, the source response still succeeds with the
neighborhood omitted and a source-level notice; cancellation still cancels the
request.

Queries use the current environment when `buildId` is omitted and honor an
explicit build ID exactly. The selected build must have a preferred,
integrity-verified extraction and a completed matching Installed index. Responses
include status, build and index context, provenance, data, candidates where
applicable, and structured errors. `compare_symbol` requires two explicit build
IDs; `get_environment` reports only the current snapshot and returns
`NoMatchingEnvironmentSnapshot` for a historical request. Facts are labeled
`FACT`, while deterministic selections and counts are labeled `DERIVED`.
Expected failures use stable domain codes; unexpected failures are logged to
stderr and returned without stack traces or raw storage details.

`find_call_sites` and `find_field_references` return recovered-IL static
relationship evidence. `find_call_sites` falls back to raw-target matching when
selector resolution is not resolved, while `find_field_references` preserves
field-selector ambiguity. Both preserve unresolved raw target text, bounded
totals, and reference collection provenance, but they do not prove runtime
behavior, scene or geometry behavior, lifecycle ordering, or call order.

MCP has no write, patch, network, indexing, or game-execution capability. It does not
install tools, run extraction, launch a game or external process, or sync
upstream data. Read-only S1API/S1MAPI catalog, symbol, and source queries use
already-indexed local API snapshots; reference indexing remains a CLI-only
operation. Source and scene results read only already-indexed
Atlas-owned files with existing integrity checks; reference source/document
results are bounded and retain local-only provenance. Native evidence is
read-only, hash-keyed to the selected build/index/GameAssembly identity, and
never stores proprietary bodies, disassembly, paths, or binary artifacts.

The read-only MCP API parity tools are `list_api_indexes`, `search_api_symbols`,
`get_api_source`, `find_api_callers`, `find_api_callees`,
`find_api_references`, `find_api_related_types`, `find_api_call_sites`, and
`find_api_field_references`. They query only completed S1API/S1MAPI indexes and
preserve the selected codebase, channel, build/index, and source-snapshot
authority. Installed-current queries are bound to the exact current environment
snapshot; a stale index is reported as stale/unavailable rather than silently
treated as current.

For runtime questions, use the read-only MCP `plan_runtime_proof` tool after the
static ownership gate. It produces competing hypotheses, positive and negative
controls, declared observables, lifecycle checks, bounded duration/sample-rate
limits, cleanup requirements, and `PASS`/`INCONCLUSIVE`/`STOP` outcomes. The
plan is scoped to exactly one `singlePlayer`, `listenHost`, `dedicatedServer`,
or `client` execution boundary; authority and observability assumptions must
not be transferred between host roles. S1Atlas does not launch the game or
claim runtime proof automatically.

## Agent skill

The methodology skill is versioned at [`skills/s1atlas/SKILL.md`](../skills/s1atlas/SKILL.md).
Install it using the skill mechanism supported by your agent host, keeping the
repository copy as the source of truth. Verify the installed skill has identical
bytes to the repository copy before relying on it. When MCP is registered, launch
the read-only server over stdio with
`dotnet run --project src/S1Atlas.Mcp -- mcp serve`; otherwise the skill's CLI
commands remain the fallback. The skill adds no capability and requires agents
to cite FACT/DERIVED evidence and build/extraction/index or API commit/index
identifiers in their own output.

For host registration, point each host's local configuration at the same
read-only server entry point using that operator's checkout root, for example:

```text
dotnet run --project <local-S1Atlas-root>/src/S1Atlas.Mcp/S1Atlas.Mcp.csproj -- mcp serve
```

Host configuration and reference manifests stay outside the repository. Keep
local paths, manifests, generated indexes, credentials, and host-private
timeouts in user-level configuration rather than public repo content.
Each host registration should enable the read-only server and use bounded
startup/tool timeouts, with those settings kept in user-level config.

The skill is the canonical source for the full parity, trust, provenance, and
efficient-query contract. Use MCP only when the registered read-only server is
available; otherwise use the skill's CLI commands as the fallback. Never treat
a missing server as an empty index, and remember that S1Atlas does not download
mods.

## Command reference

| Command | Purpose |
|---|---|
| `scan [--game-path <path>] [--performance]` | Discover and persist the current local environment |
| `status [--json]` | Show the current indexed build and installation observation |
| `env [--json]` | Show the current build, installation paths, and tracked dependencies |
| `builds [--json]` | List content-derived builds, newest first-seen first |
| `tools status [tool-id] [--json]` | Inspect pinned managed-tool state offline |
| `tools install <tool-id> [--repair] [--json]` | Explicitly download, verify, install, or repair a managed tool |
| `extract [--build <id>] [--game-path <path>] [--cpp2il-path <path>] [--profile <id>] [--retry] [--snapshot-inputs] [--input-snapshot <id>] [--keep-failed-artifacts] [--performance] [--json]` | Run offline extraction (from live input or an archived snapshot), then validate and immutably promote an authoritative extraction (or reuse an existing one) |
| `extractions list [--build <id>] [--include-failed] [--json]` | List validated extractions newest first, optionally with failed attempts |
| `extractions show <extraction-or-attempt-id> [--json]` | Show a validated extraction (full integrity) or an attempt's facts |
| `extractions promote <extraction-id> [--json]` | Explicitly make a validated extraction the preferred output for its build |
| `extractions cleanup [--older-than <duration>] [--apply] [--json]` | Preview (default) or, with `--apply`, delete only proven Atlas-owned, age-eligible failure, staging, and quarantine data |
| `index [--codebase <id>] [--channel <id>] [--commit <sha>] [--force] [--performance] [--json]` | Build the installed Schedule I code index (no options) or an S1API/S1MAPI code index |
| `search <query> [--codebase <id>] [--channel <id>] [--limit <n>] [--json]` | Query the normalized code index across symbols, types, and methods |
| `type <query> [--codebase <id>] [--channel <id>] [--limit <n>] [--json]` | Resolve and inspect indexed type definitions |
| `method <query> [--codebase <id>] [--channel <id>] [--limit <n>] [--json]` | Resolve and inspect indexed method definitions |
| `source <query> [--codebase <id>] [--channel <id>] [--context <n>] [--file] [--output <path>] [--full-type] [--related-limit <0-50>] [--limit <n>] [--json]` | Show focused, integrity-checked decompiled source and optional callable neighborhood for one resolved symbol |
| `refs <query> [--codebase <id>] [--channel <id>] [--limit <n>] [--json]` | List indexed references to a resolved symbol |
| `callers <query> [--codebase <id>] [--channel <id>] [--limit <n>] [--json]` | List indexed callers of a resolved method |
| `callees <query> [--codebase <id>] [--channel <id>] [--limit <n>] [--json]` | List indexed callees of a resolved method |
| `callsites <query> [--build <id>] [--limit <n>] [--scope game\|reference\|all] [--collection <name-or-id>] [--json]` | Find static recovered-IL call-site edges for a resolved target symbol or canonical raw target text |
| `fieldrefs <query> [--build <id>] [--limit <n>] [--readers\|--writers] [--scope game\|reference\|all] [--collection <name-or-id>] [--json]` | Find static recovered-IL field readers and writers for one resolved field |
| `investigate_seam <selector> --question <text> [--codebase <id>] [--channel <id>] [--build <id>] [--scope game\|reference\|all] [--collection <name-or-id>] [--relationship-limit <1-50>] [--owner-limit <1-50>] [--context <n>] [--native-symbol-id <id>] [--native-traversal-budget <0-500>] [--details] [--json]` | Investigate a supportable ownership seam with deterministic candidate ordering, coverage warnings, unknown dimensions, and bounded next actions |
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
