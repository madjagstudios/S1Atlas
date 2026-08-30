# Changelog

All notable changes to S1Atlas are documented here. The format is loosely based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). S1Atlas ships on a rolling
`main`; dated entries mark notable milestones rather than formal released packages.

## [Unreleased]

No changes yet.

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
