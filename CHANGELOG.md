# Changelog

All notable changes to S1Atlas are documented here. The format is loosely based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). S1Atlas ships on a rolling
`main`; dated entries mark notable milestones rather than formal released packages.

## [Unreleased]

### Added

- **Scene intelligence on stripped-type-tree builds** (AT-47) — Schedule I's
  release containers carry no embedded Unity type tree, so the scene parser now
  falls back to a pinned Unity class database: `config/tools/unity-classdata.win-x64.json`
  pins UABEA's `classdata.tpk` (commit `5adb448`, 289,605 bytes, SHA-256
  `129e1f80…`, MIT) as a data-only managed tool, installed with
  `tools install unity-classdata` and re-hashed against the pin before every
  read. RectTransform is decoded as a Transform so UI hierarchies stay in the
  graph, and asset-level MonoBehaviours (ScriptableObjects, explicit null
  GameObject) are no longer treated as corrupt component attachments. Each scene
  snapshot records its type-tree source in a new `type_tree_source` column
  (migration 13) — `embedded`, or the class database with its version, hash, and
  the dump version that stood in for the container's Unity version — surfaced as
  `typeTreeSource` by `index --scene`, `scenes`, `scene`, `gameobject`, `prefab`,
  `component`, and the MCP scene tools, and the class database identity is part
  of the scene snapshot identity. `gameobject` now returns the selected object's
  transform (position, rotation, scale, parent, sibling index). A stripped build
  without an installed class database still fails with
  `SceneTypeTreeUnavailable`, now naming the install command. Verified on build
  `0b86d6d8…`: 7 scenes, 211,409 game objects, 589,372 components. See
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
- **Empty scene index published as Completed** (AT-46) — Schedule I's release
  containers strip their Unity type trees (`TypeTreeEnabled=false`), and the
  pinned `assetstools-net` parser decodes GameObject, Transform, MonoBehaviour,
  MonoScript, and BuildSettings fields only from an embedded type tree, so every
  object became a nameless stub and `index --scene` published a `Completed`,
  `StubOrUnavailable` snapshot with 0 game objects, 0 roots, and no resolvable
  names. The parser now records whether each container embeds its type tree;
  the workflow fails a run whose containers cannot be decoded with
  `SceneTypeTreeUnavailable` (naming the containers) and, as a defense in depth,
  fails any write set that has object-table entries but no recovered GameObject
  with `NoRecoverableSceneObjects`; both codes are persisted as the snapshot's
  `failure_code`. A previously completed empty snapshot is no longer reused by
  `index --scene`, and `scenes`/`scene`/`gameobject`/`prefab`/`component` and
  the MCP scene tools report `NoRecoverableSceneObjects` for it instead of an
  empty `Resolved` result. Recovering names and hierarchy from stripped
  containers needs a Unity class database, which S1Atlas does not ship.

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
