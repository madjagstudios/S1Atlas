# Changelog

All notable changes to S1Atlas are documented here. The format is loosely based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). S1Atlas ships on a rolling
`main`; dated entries mark notable milestones rather than formal released packages.

## [Unreleased]

- Public-repo hygiene: `SECURITY.md`, issue/PR templates, this changelog.

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
