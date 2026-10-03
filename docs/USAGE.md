# S1Atlas usage

Full command walkthrough and reference for the CLI, the read-only MCP server, and
the agent skill. For an overview and quick start, see the [README](../README.md).
Deep internals (data layout, the Cpp2IL pin, validation policy, build identity)
live in [REFERENCE.md](REFERENCE.md).

## Install the tools

`s1atlas` and `s1atlas-mcp` ship as .NET tools. From the repository root,
pack them and install both packages to a directory on your `PATH`:

```powershell
dotnet pack S1Atlas.sln --configuration Release --output ./.tool-dev/packages
dotnet tool install S1Atlas.Cli --tool-path ./.tool-dev/tools --add-source ./.tool-dev/packages
dotnet tool install S1Atlas.Mcp --tool-path ./.tool-dev/tools --add-source ./.tool-dev/packages
```

Then verify the install:

```powershell
s1atlas --version
s1atlas-mcp --version
s1atlas tools status
```

`tools status` is always offline, so it confirms the install without touching
the network or the game. Building from source and running the test suite are
covered in [CONTRIBUTING.md](../CONTRIBUTING.md).

## First run

Four commands take a fresh install from an empty atlas to a first answer:

```powershell
s1atlas setup
s1atlas doctor
s1atlas status
s1atlas search "Player" --limit 20
```

`setup` plans the missing pipeline steps (scan, managed-tool installs,
extract, index) from the same readiness report `doctor` renders, prints the
numbered plan, and asks before changing anything. Tool downloads get a second
confirmation because they reach the network; `--yes` answers both, and piped
input without `--yes` fails fast instead of hanging. Each step re-checks
readiness first, so work that is already satisfied is skipped, and the run
stops at the first failure with the next step to take. `--include-optional`
adds the scene snapshot; without it, an otherwise-ready atlas prints
`Already ready` and touches nothing.

`doctor` is the read-only version of the same checklist: it never writes the
atlas, migrates the database, or touches the network, and it exits `0` only
when the atlas is ready. `doctor --json` renders the machine-readable report.
Consoles without Unicode support get `[ok]`/`[missing]` marks instead of
glyphs. `status` keeps its scan lines and appends the same Ready-or-Next
summary, plus a readiness object under `--json`.

Setup errors carry the fix with them. CLI `--json` failures, MCP error
envelopes, and serve error payloads include a `hint` field with the exact
runnable command when one exists (`s1atlas scan`, `s1atlas extract`, and
similar); a missing hint means the fix needs input only the operator can
provide. Read the hint, or run `s1atlas doctor` to see the full checklist.

## Run the CLI

Using an explicit game path is the most reliable first run:

```powershell
s1atlas scan --game-path "C:\Program Files (x86)\Steam\steamapps\common\Schedule I"
```

Then explore the stored environment:

```powershell
s1atlas status
s1atlas env
s1atlas builds
```

Inspect or explicitly install the managed Cpp2IL pin:

```powershell
s1atlas tools status cpp2il
s1atlas tools install cpp2il
s1atlas tools install cpp2il --repair
```

Install the pinned Unity class database that `index --scene` needs on release
builds whose containers strip their type trees (see
[Scene intelligence](#scene-intelligence) below):

```powershell
s1atlas tools status unity-classdata
s1atlas tools install unity-classdata
```

`tools status` is always offline. Of the implemented commands, only
`tools install <tool-id>` can access the network, and installation never happens
implicitly during a scan, index, or status query.

Run an extraction against the current indexed build and verified managed pin:

```powershell
s1atlas extract
s1atlas extract --json
```

Use an explicit build, game root, custom tool, or input snapshot request when
needed:

```powershell
s1atlas extract --build <64-character-build-id>
s1atlas extract --game-path "C:\Games\Schedule I"
s1atlas extract --cpp2il-path "C:\Tools\Cpp2IL.exe"
s1atlas extract --profile cpp2il-reconstructed-assemblies-v1 --retry
s1atlas extract --snapshot-inputs
s1atlas extract --input-snapshot <64-character-snapshot-id> --retry
s1atlas extract --keep-failed-artifacts
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
s1atlas extractions list
s1atlas extractions list --build <64-character-build-id>
s1atlas extractions list --include-failed
s1atlas extractions show <extraction-id-or-attempt-id>
s1atlas extractions promote <extraction-id>
s1atlas extractions cleanup
s1atlas extractions cleanup --older-than 30d
s1atlas extractions cleanup --older-than 30d --apply
s1atlas extractions cleanup --json
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
s1atlas index
s1atlas index --interop-path "C:\path\to\MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll"
s1atlas index --codebase s1api --channel installed
s1atlas search "<name-fragment>" --limit 25
s1atlas type "<Namespace.TypeName>"
s1atlas method "<TypeName.MethodName>"
s1atlas source "<TypeName.MethodName>" --context 6
s1atlas source "<TypeName.MethodName>" --file --output symbol.cs
s1atlas refs "<TypeName.MethodName>" --json
s1atlas callers "<TypeName.MethodName>"
s1atlas callees "<TypeName.MethodName>"
s1atlas call-sites "UnityEngine.AI.NavMeshAgent.CompleteOffMeshLink"
s1atlas field-refs "Demo.State.Value" --readers
s1atlas overrides "<TypeName.MethodName>"
s1atlas overridden-by "<TypeName.MethodName>" --depth 5
s1atlas derived "<Namespace.TypeName>" --limit 20 --offset 20
s1atlas callable "<TypeName.MethodName>"
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
s1atlas source "<TypeName.MethodName>" --context 6 --related-limit 20 --json
s1atlas source "<TypeName.MethodName>" --full-type --json
s1atlas source "<TypeName.MethodName>" --file --output symbol.cs
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

`call-sites` finds static recovered-IL call-site edges for either a resolved
game member selector or canonical raw target text such as
`UnityEngine.AI.NavMeshAgent::CompleteOffMeshLink()`. `field-refs` resolves one
field and reports incoming `ReadsField` and/or `WritesField` relationships; use
`--readers` or `--writers` to filter, and never both together. Both commands are
bounded, deterministic, and preserve unresolved raw target text and
reference-collection provenance. Call-site queries fall back to raw-target
matching when selector resolution is not resolved, so they do not expose symbol
ambiguity as a separate call-site result state; field-selector ambiguity remains
explicit. They are static relationship evidence only:
they do not prove runtime behavior, scene or geometry behavior, lifecycle
ordering, or call order.

`overrides` lists the base-class and interface slots one method fills, up to
the root, marking the direct step. `overridden-by` lists the methods that
override or implement one method, transitively, with `--depth` (default `10`)
bounding the walk. `derived` lists the subclasses and implementers of one
type, transitively, with `--depth` (default `10`) and `--offset` paging over
true totals. All three are metadata facts, so they work from reconstructed
assemblies without method bodies.

## Investigate seams

Use `investigate-seam` when the question is which exact code seam owns a
behavior, not whether that behavior has already been proved at runtime:

```powershell
s1atlas investigate-seam "Game.Seams.Target.Run" --question "Which seam owns settlement clearing?"
s1atlas investigate-seam "Game.Seams.Target.Run" --question "Which seam owns settlement clearing?" --relationship-limit 3 --owner-limit 5 --context 0 --native-symbol-id <native-id> --native-traversal-budget 25 --json
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

With a reference collection in scope, the detailed packet also carries a
`Patches` evidence section counting the reference-mod Harmony patches that
target the resolved symbol, plus a prior-art claim when at least one patch
exists. Game scope reports an empty section since patches live in reference
collections.

CLI JSON has one intentional adapter-specific field: CLI-only
`referenceCollectionBaseProvenance`. It is `null` for game-only results. For a
reference result it records the installed Schedule I build, extraction, and
index that anchor the selected reference collection, while `pinnedProvenance`
records the selected reference index. MCP carries that base authority in its
top-level `build` and `provenance` entries instead of duplicating the CLI-only
field inside `data`.

`investigate-seam` is a read-only investigation: it does not patch code, run
native recovery automatically, or prove runtime behavior. When explicitly
requested, it may attach a matching stored native-evidence summary containing
status, mapping evidence, direct native edges, field accesses, tool identity,
and an output hash. A no-body or failed record remains visible and does not
become a positive seam claim.

## Recover native method bodies

A stubbed IL2CPP managed method (`BodyRecoveryStatus.StubOrUnavailable`) has no
recovered managed source. `recover-native-body` is a separate, explicit,
CLI-only step that maps such a method to its native `GameAssembly.dll` address
and decodes bounded, static evidence around it:

```powershell
s1atlas recover-native-body --symbol-id <symbol-id> --native-traversal-budget 100
```

It accepts one or more repeated `--symbol-id` values (at least one is
required; full IDs or unique short-ID prefixes), `--native-traversal-budget` from
`1` to `500` (default `100`), an optional `--build` (full ID or unique
short-ID prefix), and `--json`. It resolves the current (or selected) build
authority, locates the Schedule I installation, and requires a completed
Schedule I `Installed` index; it fails with a precise error rather than a
partial result if authority resolution, the installation, or the execution
context is unavailable.

The command reads only `GameAssembly.dll` and `global-metadata.dat` bytes; it
never launches, patches, or otherwise mutates the game. Recovery is a
read-only mapping and bounded x86-64 decode: it does not execute the native
code and does not prove runtime behavior. Recovered pseudocode, edges, and
field accesses are static evidence only and require runtime validation
before being treated as a behavioral fact.

Each method is decoded from its entry to the start of the next function in
`GameAssembly.dll`, including code after an early return; a jump out of the
method is recorded as a tail call. A method whose end cannot be determined is
reported incomplete.

Each run persists a provenance-stamped `NativeRecoveryRecord` keyed by build
ID, index ID, `GameAssembly.dll` SHA-256, the selected symbol IDs, and the
traversal budget. Running the same request again against unchanged inputs
reproduces the identical `RecoveryId` and `OutputSha256` (idempotent), rather
than creating a duplicate record. The result reports one of six statuses:
`Recovered`, `NoBody`, `AmbiguousMapping`, `InputChanged`, `Failed`, or
`Unsupported`. This is a separate axis from the existing managed-side
`BodyRecoveryStatus.StubOrUnavailable`: a managed body being unavailable only
means the managed source could not be recovered, and is independent of whether
native recovery was ever attempted for that symbol.

The persisted record does not contain a game binary, raw disassembly, or a
local filesystem path; stored evidence strings are bounded and sanitized (a
string is rejected if it carries a path or a binary/disassembly marker). Provenance also
records the tool identity: this build ships an in-process, pinned-library
provider (`Samboy063.LibCpp2IL` 2022.1.0-pre-release.21 + `Iced` 1.21.0, both
MIT), not a subprocess, so `ToolName`/`ToolVersion`/`ToolSha256` describe the
pinned libraries rather than an executable. See
[Native recovery provenance](design/2026-08-29-native-recovery-provenance.md)
for the full evidence and sanitization contract.

`recover-native-body` is the only write path for native evidence; reading it
back afterward goes through `investigate-seam` (see above), which surfaces the
persisted record as read-only `nativeEvidence` on both the CLI and the MCP
tool, using the same bounded evidence model.

Upstream S1API/S1MAPI channels are cached explicitly before a release/preview
index; `upstream status` is always offline and `upstream sync` is the only
networked upstream command:

```powershell
s1atlas upstream status --codebase s1api
s1atlas upstream sync --codebase s1api --commit <40-character-sha>
s1atlas index --codebase s1api --channel release --commit <40-character-sha>
```

Build and query the static scene intelligence index after the same build has a
preferred integrity-verified extraction, a replay-verified input snapshot, and a
completed Schedule I Installed code index:

```powershell
s1atlas index --scene
s1atlas index --scene --build <64-character-build-id> --json
s1atlas scenes --kind scene --limit 50
s1atlas scenes --kind prefab --limit 50 --json
s1atlas scene <scene-id-or-exact-name> --children --components --refs
s1atlas game-object <game-object-id-or-scene-id/name> --children --components --refs
s1atlas prefab <prefab-id-or-exact-name> --objects --components
s1atlas component <component-id-or-exact-type> --refs --code --json
s1atlas scriptable-object <asset-id-or-exact-name-or-Namespace.Class> --json
```

Compare two indexed builds to see what changed:

```powershell
s1atlas diff <build-id-before> <build-id-after>
s1atlas diff <build-id-before> <build-id-after> --kind Method --json
s1atlas diff <build-id-before> <build-id-after> --limit 100
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
  fields or values. Game-script field values are stored only when decoded through
  a verified script layout (see "Game-script field values" below); such a
  component is `FullyRecovered` and its values are FACT.
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

The pinned parser decodes GameObject, Transform (including RectTransform),
MonoBehaviour, MonoScript, and BuildSettings fields from one of two type-tree
sources: the type tree embedded in the SerializedFile, or, when a container
strips its type tree (`TypeTreeEnabled=false`, the case for every Schedule I
release container), the pinned Unity class database installed by
`tools install unity-classdata` (see
[docs/dependencies/unity-classdata-uabea-5adb448.md](dependencies/unity-classdata-uabea-5adb448.md)).
The class database is a hash-pinned download that never ships in this
repository; the parser re-verifies its SHA-256 before reading it and resolves the
class layouts for the container's Unity version. Every snapshot records which
source decoded it in `typeTreeSource`, shown by `index --scene`, `scenes`,
`scene`, `game-object`, `prefab`, `component`, and the MCP scene tools:
`embedded`, or
`class-database unity-classdata <version> sha256:<hash> (<dump> layouts for <unity>; nearest earlier dump)`
when the package holds no dump for the exact Unity version and the newest earlier
one stood in (`(<dump> exact)` otherwise). That substitution is the fidelity
boundary of a class-database snapshot: the engine classes S1Atlas decodes did not
change layout between the resolved dump and the container version on the
verified build, but a future engine update must be re-verified end to end. The
class database identity is part of the scene snapshot identity, so changing the
pin produces a new snapshot rather than altering an existing one. Asset-level
MonoBehaviours (ScriptableObjects) carry an explicit null GameObject and are not
recorded as component attachments; those with a script are recorded as scriptable
assets instead (`scriptable-object`, MCP `get_scriptable_object`).

#### Game-script field values

The class database describes only Unity's own classes. Game scripts' serialized
fields (property prices, employee wages, special-customer order sizes) are decoded
from layouts generated from the preferred extraction's reconstructed
`Assembly-CSharp.dll`, and only when that reconstruction kept its
`[SerializeField]` attributes. Cpp2IL keeps them only when its attribute
processors run, which the default extraction profile
`cpp2il-reconstructed-assemblies-v2` does. Each object is accepted only if its
generated layout decodes it byte-exact; anything else stays `GraphOnly` with the
reason stored, never guessed values.

Every snapshot records its script-layout source, shown by `index --scene`
(`Script layouts: …`) and the scene commands (`script layouts …`). An older
extraction without restored attributes reports
`unavailable: extraction <id> (profile …) has no restored [SerializeField] attributes; …`.
To get field values for such a build:

```powershell
s1atlas extract --input-snapshot <replay-verified-input-id> --retry
s1atlas extractions promote <new-extraction-id>
s1atlas index
s1atlas index --scene
```

The new extraction does not replace an existing preferred one automatically
(same Cpp2IL tool instance), so it must be promoted, and the code index must be
rebuilt for it before `index --scene`. `component` and `scriptable-object` then
print `Field: <path> (<type>) = <value>` lines, and the MCP tools return the field
set with a FACT provenance entry `serialized-script-fields`. Field sets are capped
at 256 leaves, 32 elements per array, 512-character strings, and 256-byte byte
arrays (shown as hex); a capped set is marked `truncated`. Values are serialized
defaults: anything the game computes at load time still needs in-game verification.

An object-reference field (`PPtr<…>`) keeps its raw `fileId:localFileId` value
and also says what it points to, resolved within the same scene snapshot:

```text
Field: Leader.Data (PPtr<$BaseNPCDataObject>) = 0:6973 -> ScriptableAsset "Diesel" (ScheduleOne.NPCs.Framework.NPCDataObject) | id <asset-id>
Field: Backup (PPtr<$Widget>) = 0:0 -> null
Field: Icon (PPtr<$Sprite>) = 3:12 -> unresolved (fileId=3;localFileId=12;external=<path>)
```

The target kind is `GameObject`, `Component`, `ScriptableAsset`, `MonoScript`,
or `Asset` (any other Unity object, named by class ID). An indexed target carries
its record ID, which `game-object`, `component` and `scriptable-object` accept;
anything else carries its container and local file ID. A target is `unresolved`
when its external file is not one of the indexed containers or the object does
not exist there. JSON and MCP output carry the same data as a `target` object on
each pointer field.

Without an installed, matching class database a stripped build still yields
nameless object-table stubs and nothing else, so `index --scene` refuses to
complete such a run: it records the snapshot as `Failed` with failure code
`SceneTypeTreeUnavailable` and a message naming every stripped container and the
`tools install unity-classdata` remedy. A completed snapshot that holds
object-table entries but no recovered GameObject is likewise not usable scene
intelligence: `index --scene` will not reuse it, and the scene commands and MCP
scene tools report `NoRecoverableSceneObjects` (with the snapshot identity)
instead of an empty `Resolved` result.

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
s1atlas status --json
s1atlas env --json
s1atlas builds --json
s1atlas tools status cpp2il --json
s1atlas tools install cpp2il --json
s1atlas extract --json
```

Each JSON invocation writes exactly one top-level envelope to stdout with `schemaVersion`, `command`, `success`, `exitCode`, `data`, and `error`. Schema version 1 defines that top-level contract. Later command-specific error objects may add fields, so consumers should ignore error properties they do not recognize.

The `scan`, `extract`, and `index` commands also accept `--performance`, which
writes one phase-timing and counter diagnostics object as JSON to standard
error. It is opt-in, never changes the command's result or exit code, and is
independent of `--json` (which controls the stdout result).

Without `--game-path`, S1Atlas checks the standard Steam locations under `Program Files (x86)` and `Program Files`.

## Serve the local web app

```powershell
s1atlas serve
s1atlas serve --port 5217 --open
```

`serve` starts a loopback-only, read-only web app over the current Atlas data
and prints the listening URL. The landing page shows the resolved build and
per-index symbol counts; `/search` queries one codebase scope with paging
(`q`, `kind`, `codebase`, `page`, plus `build` to scope game results to one
build). Search ranks exact simple-name matches first, then name prefixes,
then substring matches ordered by relevance; queries of one or two characters
match simple-name prefixes only. A member's simple name is its declared
name, so a method named `Up` matches `q=Up` while its return type never
does. Substring matching needs the search index built by schema migration
v16: on an older atlas the page says so and falls back to the
slower unranked search until any `s1atlas` write command upgrades the database.
`/symbol/<id>` shows one symbol's members, integrity-checked source,
callers, callees, and references, plus Overrides and Overridden by sections
on methods and a Derived types section on types. Symbol pages list exact and
may-dispatch callers with the dispatch route and split totals; `?exact=1` on
the page and on `/api/symbol/<id>/callers` restricts to statically bound
callers. Relationship rows credit compiler-generated bodies to the declaring
method with an `in ...` detail; `?generated=1` shows the raw generated rows
instead. `callers`/`callees` exclude delegate creation unless `?delegates=1`
is set on the page or on `/api/symbol/<id>/callers|callees`; included rows
read `delegate created (not called)`. `/builds` lists every known build newest
first with human-readable status labels; `/builds/<id>` shows one build's
facts, per-codebase symbol counts with links into search filtered to that
build, adjacent diffs, and the environment when it is the current build.
`/environment` shows the current environment snapshot with the install root
redacted. `/diff?from=<id>&to=<id>` compares any two builds with
classification counts, per-symbol signatures before and after, true totals
and paging (`codebase`, `kind`, `page`); `/diff` without parameters shows a
build picker. The mirrored `/api/*` endpoints (`/api/status`, `/api/search`,
`/api/symbol/<id>`, `/api/symbol/<id>/callers|callees|references`,
`/api/builds`, `/api/builds/<id>`, `/api/environment`, `/api/diff`) return
the same tool envelopes and JSON shapes as the matching MCP tools. The server binds only to loopback,
answers only loopback `Host` values on its own bound port, accepts only `GET`
and `HEAD`, sends no CORS headers, and never writes the Atlas. `--port 0`
picks an ephemeral port; `--open` opens the default browser once the server
is listening. Stop the server with Ctrl+C.

```powershell
s1atlas open "Demo.Widget"
s1atlas open "Demo.Widget" --port 5217
```

`open` resolves a symbol selector exactly like `type`/`method` (ambiguity and
not-found behave the same), then probes `http://127.0.0.1:<port>/api/status`
(default port 5217) with a short timeout. When serve is listening there over
the same build and index, `open` launches `/symbol/<id>` in the default
browser and prints the URL. When nothing listens, it prints the URL and tells
you to start it with `s1atlas serve`. When the server answers for a different
build or index, it prints a warning and does not open. `open` only ever
contacts loopback; every other query command stays fully offline.

## Reference collections

Reference mods are user-supplied local inputs. A manifest is the explicit
selection boundary: S1Atlas reads only the declared roots and selected files,
does not discover or download mods, and does not certify compatibility, safety,
or redistribution rights. Validate and index a collection from the CLI:

```powershell
s1atlas reference collections validate <manifest>
s1atlas reference index <manifest>
s1atlas reference collections list --json
```

Reference indexing is an explicit offline CLI operation. Query commands accept
`--scope game|reference|all` and `--collection <name-or-id>` for `search`,
`source`, `refs`, `callers`, `callees`, `call-sites`, `field-refs`,
`overrides`, `overridden-by`, `derived`, and `patched-by`:

```powershell
s1atlas search "ModEntry" --scope reference --collection qol
s1atlas source "ModEntry.Run" --scope all --collection qol
s1atlas callers "Game.Target.Run" --scope all --collection qol
s1atlas callees "ModEntry.Run" --scope reference --collection qol
s1atlas call-sites "UnityEngine.AI.NavMeshAgent.CompleteOffMeshLink" --scope reference --collection qol
s1atlas field-refs "qol/Qol.Config.Setting" --scope reference --collection qol --writers
s1atlas refs "ModEntry.Run" --scope reference --collection qol
s1atlas patched-by "Game.Target.Run" --scope reference --collection qol
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

`patched-by` answers which reference mods patch one game method. Each row is a
patch edge from a patch method to the game method: `generatedDetail` names the
patch kind (`Prefix`, `Postfix`, `Transpiler`, or `Finalizer`), `attribute`
labels declared `[HarmonyPatch]` patches, and `DERIVED` labels constant manual
`harmony.Patch(AccessTools.Method(...))` calls. Unlike the other reference
queries, `patched-by` falls back to the recorded game index when the selector
matches no reference symbol, since patch targets are game methods by
definition. Resolved rows carry the game target; unresolved rows carry the
`unresolved:<reason>:` target text instead, and rows whose text names the
queried method are included. Patch targets written against the `Il2Cpp`
interop view resolve to the same game symbols. Reference collections indexed
before this version have no patch edges; re-run
`s1atlas reference index <manifest>` to rebuild with patches.

Body recovery, callable-surface evidence, and reference evidence are orthogonal.
Body recovery describes whether decompiled text is
behavioral evidence; callable surface describes how a Schedule I game member
can be reached through the local interop projection; reference collections are
local prior-art evidence. None of these labels certifies a reference mod's
compatibility, safety, or licensing.

## Read-only MCP server

Install the tools once (see [Install the tools](#install-the-tools)), then
launch the installed server over stdio with no arguments:

```powershell
s1atlas-mcp
```

`s1atlas-mcp` with no arguments serves MCP over stdio; `mcp serve` is an
accepted alias for the same mode, and `--version` prints the installed
version. Any other argument exits `2` with usage text. The installed launch
does not invoke restore or build work during MCP startup. After pulling source
changes, reinstall the tools before restarting a registered host.

Register the installed `s1atlas-mcp` command (on your `PATH`, no arguments)
in user-level host configuration. The four common shapes are:

Claude Code (`~/.claude.json`):

```json
{
  "mcpServers": {
    "s1atlas": { "command": "s1atlas-mcp", "args": [] }
  }
}
```

Codex (`~/.codex/config.toml`):

```toml
[mcp_servers.s1atlas]
command = "s1atlas-mcp"
args = []
```

VS Code (`mcp.json`):

```json
{
  "servers": {
    "s1atlas": { "command": "s1atlas-mcp", "args": [] }
  }
}
```

Claude Desktop (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "s1atlas": { "command": "s1atlas-mcp", "args": [] }
  }
}
```

There is one MCP server process per independent stdio client. Multiple Codex or
Claude connections therefore produce multiple server processes; that is
expected and the server does not require or create a shared singleton. Session
cleanup remains the client/host's responsibility.

Standard output is reserved for MCP protocol messages. Diagnostics belong on
standard error. On Windows, inspect the parent/child relationship before
investigating a suspected stale session:

```powershell
Get-CimInstance Win32_Process |
  Where-Object { $_.CommandLine -match 's1atlas-mcp' } |
  Select-Object ProcessId, ParentProcessId, CommandLine
```

Match the `ParentProcessId` to the active Codex or Claude client before taking
any cleanup action; do not terminate a process that belongs to an active stdio
session.

MCP uses the same Atlas data root as the CLI: `%LOCALAPPDATA%\S1Atlas`
by default, or the root supplied through `S1ATLAS_HOME`. The variable moves the
database and all Atlas-owned data together. MCP opens the existing database in
read-only mode; it does not create the root or database, run migrations, or
change stored data.

The read-only server exposes the Schedule I `Installed` surface, completed
local reference collections, and completed S1API/S1MAPI indexes through 26
tools:

| Area | Tools |
|---|---|
| Symbol search | `search_symbols`, `get_type`, `get_method`, `get_source`, `get_callable_surface` |
| Relationships | `find_callers`, `find_callees`, `find_call_sites`, `find_field_references`, `find_patches`, `find_references`, `find_related_types` |
| Hierarchy | `find_overrides`, `find_overriders`, `find_derived_types` |
| Scenes | `list_scenes`, `get_scene`, `get_gameobject`, `get_component`, `get_scriptable_object` |
| Builds and collections | `list_builds`, `get_environment`, `list_api_indexes`, `list_reference_collections` |
| Analysis | `compare_symbol`, `investigate_seam`, `plan_runtime_proof` |

Fourteen code tools take an optional `codebase` (`scheduleI`, `s1api`, or
`s1mapi`; default `scheduleI`) and an optional `channel` (default
`Installed`); Schedule I has only the Installed channel. `find_patches`
rejects `s1api` and `s1mapi` since patches only target the game index.
`get_callable_surface` is game-only and takes neither. Fixed vocabularies
are advertised as schema enums: `codebase`,
`channel`, `scope` (`Game`, `Reference`, `All`), and the symbol-kind and
scene-kind filters. Binding is case-insensitive, and an invalid enum value
fails with a readable error naming the parameter and the allowed values.

`search_symbols`, `get_source`, `find_callers`, `find_callees`,
`find_call_sites`, `find_field_references`, `find_patches`, `find_references`,
`find_related_types`, `find_overrides`, `find_overriders`, and
`find_derived_types` accept optional `scope` and `collection` arguments.
`scope` defaults to `Game`; `Reference` and `All` require `collection`, while
`Game` rejects it. `find_field_references` also accepts `readers` and `writers`
filters, which are mutually exclusive. `find_overriders` and
`find_derived_types` accept `depth` (default `10`). `find_callers` accepts
`exact` (default `false`): by default it also returns may-dispatch callers
(call sites targeting an overridden base slot or interface method the
selected method fills), labeled `DERIVED` with the route taken plus
exact/derived split totals, while `exact: true` returns only statically
bound callers. The relationship and search tools also accept
`includeGenerated` (default `false`): by default generated bodies are
credited to the declaring method with an `in ...` detail, while `true`
shows the raw generated rows. `find_patches` takes no `includeGenerated`
since patch rows carry no generated sources. `find_callers` and `find_callees` accept
`includeDelegates` (default `false`): by default delegate creation is
excluded from callers/callees, while `true` includes it labeled `delegate
created (not called)`. Slots with no resolvable indexed symbol contribute no
routes, so overrides of external framework methods add no derived rows. `list_reference_collections` reports
completed collections, their recorded base index/build, and local-only mod
metadata. `investigate_seam` accepts the same selector/question/limit options as
the CLI and returns the same ordered candidate, warning, unknown-dimension, and
next-action payload fields.

The ten `find_*` tools and the four `list_*` tools page with an opaque
`cursor`. Responses carry `nextCursor` only when more rows remain; pass it
back with otherwise identical arguments to fetch the next page. Cursors are
bound to the tool, the effective arguments, the limit, and the resolved
build/index, so reuse with different arguments or after a re-index fails
with `invalid_cursor`. `find_patches` cursors additionally bind the resolved
reference index. `search_symbols` stays limit-only and takes no
cursor. Merged `all`-scope totals are exact when both sides fit the page
window and null when truncated.

Envelopes omit empty `candidates` and `suggestions` arrays. Provenance
entries omit build, extraction, and index IDs that duplicate the envelope
build; entries pointing at a different index keep theirs. Symbol
candidates slim to the fields a follow-up call needs, without the index
identity the envelope already carries. Call-site and field-reference
results serialize flat, without the doubled `page` object. Ambiguity
behavior is unchanged: `ambiguous` envelopes still carry candidates with
exact totals, and `not_found` envelopes still carry near-match
suggestions.

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
explicit build ID or unique short-ID prefix. The selected build must have a
preferred, integrity-verified extraction and a completed matching Installed
index. Responses include status, build and index context, provenance, data,
candidates with exact totals on ambiguity, near-match suggestions on unknown
selectors, and structured errors. `compare_symbol` requires two explicit build
IDs; `get_environment` reports only the current snapshot and returns
`snapshot_not_found` for a historical request. Facts are labeled
`FACT`, while deterministic selections and counts are labeled `DERIVED`.
Expected failures use ten stable snake_case domain codes;
unexpected failures are logged to stderr and returned without stack traces
or raw storage details. Setup errors such as `no_current_build` or
`no_completed_index` carry a `hint` field with the exact CLI fix command;
run it, or run CLI `s1atlas doctor` to see the full readiness checklist.

| Wire code | Meaning |
|---|---|
| `invalid_arguments` | A parameter value or combination is invalid. |
| `symbol_not_found` | No symbol, scene, GameObject, component, or asset matches the selector. |
| `no_completed_index` | No completed queryable index exists for the selection. |
| `source_unavailable` | The backing source snapshot is unavailable. |
| `source_integrity_failure` | A backing snapshot failed integrity verification. |
| `snapshot_not_found` | No environment snapshot or build matches the request. |
| `no_current_build` | No current environment snapshot exists. |
| `atlas_unavailable` | The Atlas store itself is unavailable. |
| `unexpected_tool_failure` | An internal failure; details are in the message, not the code. |
| `invalid_cursor` | A page cursor was malformed or bound to a different query. |

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

The read-only MCP API parity tools are the shared code tools with
`codebase: s1api` or `s1mapi`, plus `list_api_indexes`. They query only
completed S1API/S1MAPI indexes and preserve the selected codebase, channel,
build/index, and source-snapshot authority. Installed-current queries are
bound to the exact current environment snapshot; a stale index is reported
as stale/unavailable rather than silently treated as current.

Retired tools map to shared replacements with no behavior change:
`search_api_symbols` to `search_symbols`, `get_api_source` to `get_source`,
`find_api_callers` to `find_callers`, `find_api_callees` to `find_callees`,
`find_api_references` to `find_references`, `find_api_related_types` to
`find_related_types`, `find_api_call_sites` to `find_call_sites`, and
`find_api_field_references` to `find_field_references`, each with the
matching `codebase` and `channel`. `get_prefab` merged into `get_scene`
with `kind: Prefab`; omitting `kind` resolves scenes. `find_derived_types`
pages with `cursor` instead of `offset`.

For runtime questions, use the read-only MCP `plan_runtime_proof` tool after the
static ownership gate. It produces competing hypotheses, positive and negative
controls, declared observables, lifecycle checks, bounded duration/sample-rate
limits, cleanup requirements, and `PASS`/`INCONCLUSIVE`/`STOP` outcomes. The
plan is scoped to exactly one `SinglePlayer`, `ListenHost`, `DedicatedServer`,
or `Client` execution boundary; authority and observability assumptions must
not be transferred between host roles. S1Atlas does not launch the game or
claim runtime proof automatically.

## Agent skill

The methodology skill is versioned at [`skills/s1atlas/SKILL.md`](../skills/s1atlas/SKILL.md).
Install it using the skill mechanism supported by your agent host, keeping the
repository copy as the source of truth. Verify the installed skill has identical
bytes to the repository copy before relying on it. When MCP is registered, launch
the installed `s1atlas-mcp` server over stdio with no arguments; otherwise the skill's CLI
commands remain the fallback. The skill adds no capability and requires agents
to cite FACT/DERIVED evidence and build/extraction/index or API commit/index
identifiers in their own output.

For host registration, point each host's local configuration at the same
read-only server entry point, the installed `s1atlas-mcp` command on the
operator's `PATH` with no arguments; see the per-host snippets in
[Read-only MCP server](#read-only-mcp-server).

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

## Real-game golden facts

Synthetic fixtures cannot catch every real-build bug: decoder edge cases, field-layout misalignment, and data-shape drift only show up on the real build. The golden-facts suite pins a few facts from your own live atlas and re-checks them on demand. It runs only with `S1ATLAS_RUN_LOCAL_GAME_TESTS=1`, only against a temp copy of the live atlas, and its expected values live in a gitignored local file that must never be committed.

1. Copy `tests/S1Atlas.IntegrationTests/GoldenFacts/golden-facts.example.json` to `golden-facts.local.json` in the same directory.
2. Replace the placeholders with values read from your own atlas via the CLI query commands (see the command reference below). Supported fact kinds:
   - the serialized field value of a component or ScriptableObject, by selector plus field path;
   - the resolved target name of an object-reference field;
   - the exact callee-name set of a method, by selector.
   Keep facts few and stable: values that change with every build make bad goldens.
3. Run the suite:

```powershell
$env:S1ATLAS_RUN_LOCAL_GAME_TESTS = '1'
dotnet test S1Atlas.sln --configuration Release --no-build --filter "FullyQualifiedName~GoldenFacts"
Remove-Item Env:\S1ATLAS_RUN_LOCAL_GAME_TESTS
```

Without the local file the file-backed tests skip; the value-free structural invariants (resolver round-trips, recovery-edge hygiene) run regardless. After each game update, re-run the suite: changed values mean the local file needs refreshing, while failures against an unchanged file mean a product bug. Never commit the local file — the repository-hygiene gate blocks its name.

## Compiler-generated bodies

Calls and field accesses inside lambdas, `async`/iterator state machines,
and local functions are credited to the method that declares them. Credited
rows carry an `in ...` detail naming the body the call was found in:

- `in lambda`
- `in async state machine`
- `in iterator state machine`
- `in local function {Name}`

Nested generated bodies join details outermost-first (`in lambda, in local
function Scale`). A generated body that cannot be mapped stays on the
generated member with an `unmapped: {reason}` detail instead of a guess:
either the declaring method is not indexed, or the declaring overload is
ambiguous. Attribute-mapped `async` and iterator overloads resolve through
their state-machine attributes; `<>c` lambda bodies over ambiguous overloads
stay unmapped with a reason.

Every relationship surface can show the raw generated rows instead:

- CLI: `--include-generated` on `callers`, `callees`, `refs`, `field-refs`,
  and `search`. Relationship IDs are unchanged: the same edge keeps its ID
  whether it renders credited or raw.
- MCP: `includeGenerated` on the relationship and search tools.
- Serve: `?generated=1` on symbol and search pages and on the
  relationship/search APIs.

Symbol search hides compiler-generated members by default and reports `<n>
generated result(s) hidden` with the switch that reveals them. `field-refs`
likewise excludes compiler-captured fields unless asked.

## Delegate creation and field addresses

`callers` and `callees` report real calls only. Creating a delegate from a
method (`ldftn`/`ldvirtftn`) records a `ReferencesMethod` edge instead, and
taking a field's address (`ldflda`/`ldsflda`) records a `TakesFieldAddress`
edge; neither is a call, a read, or a write.

Delegate references stay out of `callers`/`callees` unless asked:

- CLI: `--include-delegates` on `callers` and `callees`.
- MCP: `includeDelegates` on `find_callers` and `find_callees`, including
  `codebase: s1api` and `s1mapi` queries.
- Serve: `?delegates=1` on symbol pages and on the callers/callees APIs.

Included delegate rows are labeled `delegate created (not called)`.

`field-refs` always shows address-taken sites in both readers and writers:
`possible write (address taken)` in writers (and in the combined view) and
`possible read (address taken)` in readers. `refs` always shows both new
kinds. `ldtoken` of a method or field records a metadata reference, never a
call or read; `constrained.` + `callvirt` stays a virtual call; `calli`
records nothing.

## Symbol selectors

Every symbol-taking command (`callers`, `callees`, `refs`, `field-refs`,
`call-sites`, `overrides`, `overridden-by`, `derived`, `callable`, `source`,
`open`, `investigate-seam`, plus the MCP `get_*` and `find_*` tools) accepts
the same selector forms, resolved in this order:

1. Full 64-character symbol ID (lowercase).
2. Unique short-ID prefix: 8 to 63 hex characters of either case.
3. Canonical key (`ScheduleI:Installed:Method:...`).
4. Exact signature.
5. Qualified name.
6. Fuzzy text, ranked; compiler-generated members stay hidden unless asked
   for with `--include-generated` / `includeGenerated`.

A prefix shared by several symbols fails listing the matches instead of
guessing; a prefix that matches nothing falls through to the text forms.
Short IDs display as 12 characters in human output (`search` rows and every
candidate table); `--json` keeps full IDs and adds a `shortId` field.

An ambiguous selector exits 1 with `AmbiguousSymbol` and a numbered candidate
table (number, kind, qualified name, signature, short ID, codebase) capped at
10 human rows with the exact total (`Found 12 candidates; showing 10.`), or
`Found at least N candidates; showing 10.` when the pool truncates past 50,
plus the narrowest working hint: the exact signature when signatures differ,
otherwise a short ID from the table. An unknown selector exits 1 with
`SymbolNotFound`: human output prints `Found 0 matches.`, plus up to 5
`Nearest matches` rows and a spelling/`search` hint when close names exist.
MCP `not_found` envelopes carry the same near matches in `suggestions`;
`ambiguous` envelopes carry candidates with `totalCandidateCount`.

Build, extraction, and attempt IDs accept unique short-ID prefixes anywhere
they are accepted: `--build`, `diff` build arguments,
`extractions show`, and `recover-native-body --symbol-id`. Ambiguous prefixes
fail listing labeled short IDs (builds show their first-seen time, history
entries their kind and creation time, symbols their signature) that can be
re-run directly. Prefixes accept either hex case everywhere, and full-length
build IDs do too; full symbol and extraction IDs must be lowercase. Scene,
GameObject, component, and asset ambiguity renders numbered candidate rows
with counts and exact-ID hints.

## Shell completion

`s1atlas completion <pwsh|bash|zsh>` prints a completion script for the named
shell. The script calls back into `s1atlas` itself, so completion needs no
extra tooling once `s1atlas` is on your `PATH`:

```powershell
# PowerShell: append once, then restart the shell (or run `. $PROFILE`).
s1atlas completion pwsh >> $PROFILE
```

```bash
# bash: evaluate from ~/.bashrc.
eval "$(s1atlas completion bash)"
```

```zsh
# zsh: evaluate from ~/.zshrc, after compinit.
eval "$(s1atlas completion zsh)"
```

## Command reference

<!-- cli-table:begin -->
| Command | Purpose |
|---|---|
| `s1atlas builds [--json]` | List all indexed Schedule I builds. |
| `s1atlas call-sites <query> [--codebase] [--channel] [--build] [--limit] [--scope] [--collection] [--json]` | Find static call-site edges for a resolved target symbol or raw target text. |
| `s1atlas callable <query> [--codebase] [--channel] [--build] [--limit] [--json]` | Show the callable surface of a resolved symbol. |
| `s1atlas callees <query> [--codebase] [--channel] [--build] [--limit] [--scope] [--collection] [--include-generated] [--include-delegates] [--json]` | List indexed callees of a resolved method. |
| `s1atlas callers <query> [--codebase] [--channel] [--build] [--limit] [--scope] [--collection] [--exact] [--include-generated] [--include-delegates] [--json]` | Find callers of one resolved symbol, including may-dispatch callers reached through overrides and interface implementations. |
| `s1atlas completion <shell>` | Print a shell completion script that completes s1atlas commands and options. |
| `s1atlas component <component-id|exact-type-selector> [--refs] [--code] [--limit] [--json]` | Query one indexed component. |
| `s1atlas derived <query> [--codebase] [--channel] [--build] [--limit] [--scope] [--collection] [--depth] [--offset] [--json]` | Show the subclasses and implementers of a type, transitively. |
| `s1atlas diff <id-a> <id-b> [--codebase] [--channel] [--kind] [--limit] [--json]` | Compare two indexed builds and report per-symbol changes. |
| `s1atlas doctor [--json]` | Check atlas pipeline readiness. |
| `s1atlas env [--json]` | Show the current game and modding dependency environment. |
| `s1atlas extract [--build] [--game-path] [--cpp2il-path] [--profile] [--retry] [--snapshot-inputs] [--input-snapshot] [--keep-failed-artifacts] [--performance] [--json]` | Extract, validate, and promote an authoritative reconstructed assembly set. |
| `s1atlas extractions cleanup [--older-than] [--apply] [--json]` | Preview or delete only proven Atlas-owned, age-eligible failure and staging data. Preview is the default. |
| `s1atlas extractions list [--build] [--include-failed] [--json]` | List validated extractions newest first, optionally with failed attempts. |
| `s1atlas extractions promote <extraction-id> [--json]` | Explicitly make a validated extraction the preferred output for its build. |
| `s1atlas extractions show <id> [--json]` | Show a validated extraction (full integrity) or an attempt's facts. |
| `s1atlas field-refs <query> [--codebase] [--channel] [--build] [--limit] [--scope] [--collection] [--readers] [--writers] [--include-generated] [--json]` | Find field read/write relationships for one resolved symbol. |
| `s1atlas game-object <game-object-id|scene-id/name> [--children] [--components] [--refs] [--limit] [--json]` | Query one indexed game object. |
| `s1atlas index [--force] [--scene] [--build] [--codebase] [--channel] [--commit] [--interop-path] [--performance] [--json]` | Build the installed Schedule I source and symbol index. |
| `s1atlas investigate-seam <selector> --question [--codebase] [--channel] [--build] [--scope] [--collection] [--relationship-limit] [--owner-limit] [--context] [--details] [--native-symbol-id] [--native-traversal-budget] [--json]` | Investigate whether a resolved symbol is a supportable ownership seam. |
| `s1atlas method <query> [--codebase] [--channel] [--build] [--limit] [--scope] [--collection] [--include-generated] [--json]` | Resolve and inspect indexed method definitions. |
| `s1atlas open <selector> [--port]` | Open one resolved symbol in the local serve web app. |
| `s1atlas overridden-by <query> [--codebase] [--channel] [--build] [--limit] [--scope] [--collection] [--depth] [--json]` | Show the methods that override or implement a method, transitively. |
| `s1atlas overrides <query> [--codebase] [--channel] [--build] [--limit] [--scope] [--collection] [--json]` | Show the base and interface slots a method fills, up to the root. |
| `s1atlas patched-by <query> [--codebase] [--channel] [--build] [--limit] [--scope] [--collection] [--json]` | Find reference-mod Harmony patches targeting one game method, including unresolved patches that name the method. |
| `s1atlas prefab <prefab-id|exact-name> [--objects] [--components] [--refs] [--limit] [--json]` | Query one proven prefab document. |
| `s1atlas recover-native-body [--symbol-id] [--native-traversal-budget] [--build] [--json]` | Recover native method bodies for the selected symbols and persist the result. |
| `s1atlas reference collections list [--json]` | List completed local reference-mod collections. |
| `s1atlas reference collections validate <manifest> [--json]` | Validate and hash a local reference-mod collection manifest. |
| `s1atlas reference index <manifest> [--force] [--json]` | Build a local reference-mod index. |
| `s1atlas refs <query> [--codebase] [--channel] [--build] [--limit] [--scope] [--collection] [--include-generated] [--json]` | List indexed references to a resolved symbol. |
| `s1atlas scan [--game-path] [--performance]` | Discover the local Schedule I environment and save a build snapshot. |
| `s1atlas scene <scene-id|exact-name> [--children] [--components] [--refs] [--limit] [--json]` | Query one indexed scene document. |
| `s1atlas scenes [--build] [--snapshot] [--kind] [--query] [--limit] [--json]` | List indexed scene and proven prefab documents. |
| `s1atlas scriptable-object <asset-id|exact-name|namespace.class> [--json]` | Query one indexed scriptable asset (ScriptableObject) and its decoded fields. |
| `s1atlas search <query> [--codebase] [--channel] [--build] [--limit] [--scope] [--collection] [--include-generated] [--json]` | Query the normalized code index across symbols, types, and methods. |
| `s1atlas serve [--port] [--open]` | Start the local read-only web app on loopback. |
| `s1atlas setup [--yes] [--include-optional]` | Run the missing pipeline steps in order. |
| `s1atlas source <query> [--codebase] [--channel] [--build] [--scope] [--collection] [--candidate-limit] [--context] [--file] [--output] [--full-type] [--related-limit] [--json]` | Show integrity-checked source for one resolved symbol. |
| `s1atlas status [--json]` | Show the current Atlas build status. |
| `s1atlas tools install <tool-id> [--repair] [--json]` | Download, verify, and register a repository-pinned tool. |
| `s1atlas tools status [<tool-id>] [--json]` | Inspect managed tool installations without network access. |
| `s1atlas type <query> [--codebase] [--channel] [--build] [--limit] [--scope] [--collection] [--include-generated] [--json]` | Resolve and inspect indexed type definitions. |
| `s1atlas upstream status [--codebase] [--json]` | Show cached upstream status without network access. |
| `s1atlas upstream sync [--codebase] [--commit] [--json]` | Fetch and cache one exact upstream commit. |
<!-- cli-table:end -->

Additional forms:

| Command | Purpose |
|---|---|
| `index --scene [--build <id>] [--force] [--json]` | Build or reuse an offline, integrity-verified scene snapshot for the selected build |
| `s1atlas-mcp` | Launch the read-only Schedule I Installed MCP server over stdio (no arguments; `mcp serve` is an alias, `--version` prints the version) |
