# Changelog

All notable changes to S1Atlas are documented here. The format is loosely based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). S1Atlas ships on a rolling
`main`; dated entries mark notable milestones rather than formal released packages.

## [Unreleased]

### Added

- **Serialized field values for game scripts** (AT-50) — scene indexes now decode
  the serialized fields of Schedule I MonoBehaviours and ScriptableObjects
  (property prices, employee wages, special-customer order sizes) instead of
  stopping at `GraphOnly`. A new extraction profile,
  `cpp2il-reconstructed-assemblies-v2` (now the default), keeps the
  `[SerializeField]` attributes Cpp2IL otherwise drops. Layouts built from them are
  trusted per object only when they decode it byte-exact; anything else stays
  `GraphOnly` with a stored reason. `get_component` / `component` return the
  values with FACT provenance, and the new `get_scriptable_object` tool /
  `scriptable-object` command covers ScriptableObjects such as
  `SpecialCustomerData`. Every scene snapshot records its script-layout source.
  Existing builds need a v2 extraction, `extractions promote`, a code-index
  rebuild, and `index --scene` to gain values.
- **Named targets for object-reference fields** (AT-53) — a decoded `PPtr` field
  now names what it points to (the GameObject, component, ScriptableObject, script
  or other asset, with its name, type and indexed ID) alongside its raw
  `fileId:localFileId`. Null and unresolvable pointers are labelled as such. The
  scene parser version is now `3.0.5+script-layouts.2`, so `index --scene`
  rebuilds existing snapshots to gain targets.

## [1.4.0] — 2026-09-20 — Scene intelligence on release builds

Scene intelligence now works on the game as it actually ships. Schedule I strips
the Unity type trees from its scene containers, which left every earlier scene
index empty; 1.4.0 decodes those containers through a pinned Unity class database,
records the type-tree source on every snapshot, and fails loudly instead of
publishing an empty index when it cannot decode. Three gate and lookup bugs found
on the way are fixed in the same release.

### Added

- **Scene intelligence on release builds** (AT-46, AT-47) — Schedule I ships its
  scene containers without embedded Unity type trees, so the scene parser could
  not name a single object and `index --scene` published a completed, empty
  snapshot. The parser now decodes stripped containers through a pinned Unity
  class database, installed with `tools install unity-classdata` and re-hashed
  against the pin before every read; each scene snapshot records its type-tree
  source and surfaces it on every scene command and MCP scene tool. When a
  stripped build has no matching class database the run fails with
  `SceneTypeTreeUnavailable` instead of completing empty, and a snapshot that
  recovered no GameObject fails with `NoRecoverableSceneObjects` and is not
  served as a completed index. `gameobject` now returns the selected object's
  transform. Pin, licence, and version-substitution details are in
  `docs/dependencies/unity-classdata-uabea-5adb448.md`.

### Fixed

- **Scene index prerequisite gate** (AT-44) — the Schedule I Installed code
  snapshot never recorded its environment snapshot id, so `index --scene` always
  failed with `CrossBuildCodeIndex` even when the preferred extraction was
  replay-verified and the code index was current. The code snapshot now records
  the build-matching environment snapshot id, and a pre-existing null is healed
  in place on the next `index` run without overwriting a populated value.
- **`gameobject <scene-id>/<name>` lookup** (AT-45) — the exact-name query joined
  `scene_snapshots` with an unqualified select list, so SQLite rejected it with
  `ambiguous column name: recovery_status`. The select list is now table-qualified.
- **MCP `get_gameobject` with a `<scene-id>/<name>` selector** (AT-49) — the read-only
  repository the MCP server uses carried its own copy of that query, so the same
  lookup that AT-45 fixed on the CLI still failed on MCP with `UnexpectedToolFailure`.
  Both copies are now qualified.

## [1.3.0] — 2026-09-10 — Native-body recovery

The targeted native-body recovery that 1.2.0 could only *plan* is now
implemented. For a game method exposed as nothing but a `throw null` stub, S1Atlas
can map the managed symbol to its native `GameAssembly.dll` address and recover
bounded, provenance-stamped evidence of what the method actually calls and reads —
without executing the game.

### Added

- **`recover-native-body`** — a CLI command that maps a stubbed IL2CPP managed
  method to its native address, decodes bounded direct-call and field-access
  evidence, and persists a provenance-stamped record. Runs are deterministic and
  idempotent. Recovered pseudocode is static evidence and requires runtime
  validation before being treated as behavioral fact.
- **Native evidence on `investigate_seam`** — the persisted record is surfaced
  read-only on both the CLI and the MCP server through the same bounded evidence
  model (`--native-symbol-id` / `--native-traversal-budget`).
- **Pinned-library provenance** — the recovery provider is an in-process build on
  `Samboy063.LibCpp2IL` and `Iced` (both MIT), pinned with a committed lockfile and
  a CI lockfile-drift check; records are stamped with the library tool identity, the
  build/index/GameAssembly provenance, and never carry a game binary, raw
  disassembly, or a filesystem path.

### Notes

- The provider is read-only with respect to the game — it reads `GameAssembly.dll`
  and `global-metadata.dat` and never launches or mutates the game.

## [1.2.0] — 2026-08-30 — Evidence-first agent parity

This release makes S1Atlas more decisive and safer for Schedule I mod
investigation by sharing deterministic evidence packets across the CLI and
read-only MCP server.

### Added

- Behavior-ownership seam investigation with deterministic candidate ordering,
  explicit coverage states, negative seam results, and bounded next actions.
- S1API/S1MAPI index, symbol, source, and relationship queries through MCP with
  build/index provenance and stale-index visibility.
- Targeted native-body recovery planning with build, tool, and input-integrity
  provenance; unsupported or failed recovery remains explicit.
- Runtime-proof planning scoped to one execution boundary with
  `PASS`/`INCONCLUSIVE`/`STOP` outcomes and no invented telemetry.
- Shared agent guidance and contract tests covering the evidence-first workflow.
- A reproducible MCP launch benchmark and direct Release-DLL registration
  guidance.

### Improved

- MCP host documentation now explains one server process per independent stdio
  client, protocol-only stdout, stderr diagnostics, and stale-session process
  inspection.

## [1.1.0] — 2026-08-28 — Agent usability

This release adds the evidence surfaces that reduce repeated manual decompilation
and make the boundary between static code evidence and live-game behavior clear.

### Added

- Body-recovery classification for generated interop wrappers.
- Callable-surface queries for directly callable game members.
- Reference-mod collections with cross-assembly relationships resolved against a
  pinned game index.
- Static call-site and field-reference queries with explicit resolution and
  completeness details.
- Focused source queries with deterministic runtime-verification hints, bounded
  caller/callee neighborhoods, and explicit containing-type source spans.
- Public usage and agent-skill guidance for the new query surfaces.

### Maintained

- Public-repository hygiene files, issue/PR templates, and release documentation.

## [1.0.0] — 2026-08-20 — V1

First complete version. All V1 "Definition of Done" criteria met.

### Added
- **Build fingerprinting** and immutable, version-aware scan tracking.
- **Cpp2IL + ILSpy extraction pipeline** (verified, provenance-tracked) for the
  IL2CPP game assemblies.
- **Code index** — types, methods, fields, and relationships, searchable by name,
  with decompiled source, callers, callees, and references.
- **Upstream S1API / S1MAPI deep-indexing** so the modding API can be checked before
  patching the game directly.
- **Scene intelligence** — scenes, prefabs, GameObjects, and components (CLI + MCP).
- **Build diffing** — see exactly what a game update changed.
- **Read-only MCP server** for coding agents.
- **Deterministic static HTML portal** for human browsing.
- **Agent skill** (`skills/s1atlas/`) for evidence-first modding workflows.
- Provenance labeling throughout — `FACT` (extracted) and `DERIVED` (computed).

[Unreleased]: ../../compare/main...HEAD
