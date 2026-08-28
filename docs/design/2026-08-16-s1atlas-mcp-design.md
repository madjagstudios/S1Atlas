# S1Atlas V1 Read-Only MCP Server Design

Status: shipped (PR #23, merged on 2026-08-16)
Work item: S1Atlas V1 read-only MCP server

## 1. Purpose

Add `S1Atlas.Mcp`, a local read-only Model Context Protocol server that exposes
the same integrity-verified Atlas knowledge used by the human CLI. The server
is an adapter over existing query and authority services. It is not a second
query engine, extraction pipeline, patching surface, or game integration.

The milestone includes code-symbol queries, build comparison, build and
environment discovery, and scene-intelligence queries. It does not include
interpretation-generation tools, write tools, network transport, game
execution, or mutation of Schedule I, mods, Atlas facts, or Atlas storage.

## 2. Decisions

### 2.1 Process and transport

`S1Atlas.Mcp` is a separate `net8.0` executable. Its explicit launch shape is:

```text
S1Atlas.Mcp mcp serve
```

The server uses MCP stdio transport. Standard output is reserved for MCP
protocol messages. Diagnostics and logs go to standard error. The existing
human CLI remains a separate executable and command surface.

### 2.2 MCP SDK

Use the official `ModelContextProtocol` NuGet package with
`Microsoft.Extensions.Hosting`. This package is the SDK's hosted stdio-server
option and provides dependency injection, hosting, cancellation, and
attribute-based tool discovery. `ModelContextProtocol.Core` is not sufficient
as the primary package for this server, and `ModelContextProtocol.AspNetCore`
is out of scope because V1 has no HTTP transport.

### 2.3 Shared composition

The CLI and MCP must share the composition of read-only authority and query
services. Create a new `S1Atlas.Application` library that owns this shared
composition, then have both executables consume it without duplicating
integrity policy. The library will expose the read-only service bundle needed
by MCP; extraction, indexing, cleanup, installation, process, HTTP, and
game-discovery workflows will not be registered in the MCP host.

The shared bundle includes:

- `ValidatedExtractionIntegrityVerifier`.
- `PreferredVerifiedExtractionResolver`.
- `IndexQueryService`.
- `BuildDiffService`.
- `SceneQueryService`.
- Read-only Atlas build/environment access through `IAtlasRepository`.
- Read-only extraction-history access for validated/preferred build metadata.

### 2.4 Authority parity between CLI and MCP

The Schedule I Installed build-authority resolution described in section 3.2
(current-or-explicit build → preferred verified extraction → matching completed
Installed index by source identity → full integrity verification) is owned by a
single shared component in `S1Atlas.Application` and consumed by **both** the CLI
Schedule I query commands and the MCP host. There must be exactly one authority
path for the game surface, so an agent (MCP) and a human (CLI) cannot receive a
different answer for the same Schedule I query.

Today the CLI query commands resolve a Schedule I index by `(codebase, channel)`
alone, without proving the preferred verified extraction or re-checking
integrity. This milestone routes the CLI's Schedule I Installed queries through
the shared authority path, so they gain the same preferred-and-verified guarantee
and optional `--build` selection. The default behavior (no build specified →
current build) is preserved.

This parity requirement is scoped to the Schedule I Installed surface, which is
all MCP V1 exposes (section 4.1). The CLI retains its separate S1API/S1MAPI
codebase and release/preview channel resolution unchanged: those indexes are
built from cached upstream source commits, not Cpp2IL extractions, and have no
preferred-verified-extraction authority to share. MCP V1 does not expose the API
codebases at all.

## 3. Trust and read-only boundary

### 3.1 Storage opening

The current `SqliteAtlasRepository.InitializeAsync` runs migrations, and its
normal connections use `ReadWriteCreate`. MCP cannot use that path. Storage
must gain an explicit read-only opening mode that:

- Opens an existing database with SQLite read-only access.
- Does not create the database or parent directories.
- Does not run migrations.
- Fails explicitly when the Atlas database is missing, inaccessible, or has an
  incompatible schema.
- Rejects write operations if a write-capable repository object is reached
  accidentally.

The MCP composition will never call repository initialization and will not
construct write-oriented services. Add a `ReadOnlySqliteAtlasRepository`
adapter for the MCP composition; it delegates supported reads to the
read-only SQLite connection and throws on every mutation method exposed by the
existing repository interfaces.

### 3.2 Authority resolution

Every symbol and scene query goes through a build authority resolver before
calling a query service:

1. If `buildId` is omitted, read the current environment snapshot and use its
   build ID. If no current snapshot exists, return `NoCurrentBuild`.
2. If `buildId` is explicit, use it exactly; never silently substitute the
   current or newest build.
3. Resolve the preferred extraction with
   `PreferredVerifiedExtractionResolver`. That resolver must prove the
   preferred extraction's identity, build association, and full artifact
   integrity through `ValidatedExtractionIntegrityVerifier`.
4. Resolve the completed Schedule I installed index whose source identity is
   the verified extraction ID.
5. Assert that the selected index and any scene snapshot belong to the
   requested build. A mismatch returns `IndexBuildMismatch` or the equivalent
   scene status and no authoritative data.

The server never returns a Phase 3 candidate, retained failed output, an
unverified extraction, or a database row that has not passed the existing
authority checks.

### 3.3 No network or execution

The MCP host does not create managed-tool HTTP clients, upstream sync clients,
process extractors, game locators, or game execution services. It performs no
network request and starts no external process. Querying source or scene data
reads already-indexed Atlas-owned files only, with existing hash verification.

## 4. Build context and response envelope

### 4.1 Build selection

V1 tool schemas target the authoritative Schedule I installed channel. Every
symbol and scene tool accepts an optional `buildId`. Omitted IDs resolve to the
current build as described above; explicit IDs are exact. The response always
echoes the resolved build context.

`compare_symbol` requires two explicit build IDs, `buildIdA` and `buildIdB`.
Neither side may silently default to the current build.

### 4.2 Envelope

Every tool returns a structured result with this conceptual shape:

```json
{
  "status": "resolved",
  "build": {
    "requestedBuildId": null,
    "resolvedBuildId": "…",
    "extractionId": "…",
    "indexId": "…",
    "codebase": "ScheduleI",
    "channel": "Installed",
    "integrityVerified": true
  },
  "data": {},
  "candidates": [],
  "provenance": [
    {
      "classification": "FACT",
      "source": "preferred-verified-extraction",
      "buildId": "…",
      "extractionId": "…",
      "indexId": "…"
    }
  ],
  "error": null
}
```

`status` is explicit and uses `resolved`, `not_found`, `ambiguous`,
`unavailable`, or `invalid` according to the underlying query result. Empty
arrays are not treated as a successful answer when the query was unresolved.

Direct extracted/indexed facts are classified as `FACT`. Deterministic query
selection, ranking, counts, relationship direction, completeness boundaries,
and diff classifications are classified as `DERIVED`. V1 does not generate
interpretations, but the provenance model supports `INTERPRETATION` for a
future explicitly labeled surface. No interpretation may be labeled as fact.

## 5. Tool surface

Limits use the existing service semantics, default to 50, and have a bounded
server-side maximum. Negative or zero limits are invalid. Tool methods use
JSON-friendly names and enums while mapping to the existing Core models.

### 5.1 Code-symbol tools

- `search_symbols(query, buildId?, kind?, limit?)`
  - Delegates to `IndexQueryService.SearchAsync` using the resolved build
    index. `kind` accepts the existing symbol kinds.
- `get_type(selector, buildId?, limit?)`
  - Mirrors the CLI `type` query using type resolution semantics.
- `get_method(selector, buildId?, limit?)`
  - Mirrors the CLI `method` query using method resolution semantics.
- `get_source(selector, buildId?, context?)`
  - Delegates to `IndexQueryService.SourceAsync`.
  - Returns the resolved symbol, integrity-verified relative source path,
    SHA-256, byte count, location, body-recovery status, provenance string,
    and bounded snippet text.
  - Has no output path and cannot write a source file.
- `find_callers(selector, buildId?, limit?)`
  - Delegates to the caller relationship query and preserves the completeness
    notice when target resolution bounds the result.
- `find_callees(selector, buildId?, limit?, scope?, collection?)`
  - Defaults to the authoritative Schedule I game index (`scope=game`).
  - `scope=reference` or `scope=all` requires `collection` to name an
    explicitly selected completed local reference collection or its completed
    reference index, bound to the selected game build; the authority envelope
    preserves that collection binding and rejects missing or mismatched
    authority.
  - Delegates to the callee relationship query and preserves unresolved
    endpoints and evidence. The recovered-IL relationships are static
    evidence only; they do not establish runtime behavior, lifecycle ordering,
    or call order.
- `find_call_sites(selector, buildId?, limit?, scope?, collection?)`
  - Delegates to the static recovered-IL call-site query.
  - Accepts either a resolved game-member selector or canonical raw target text
    when the target has no indexed symbol row.
  - Preserves unresolved raw target text, bounded totals, and reference
    collection provenance.
  - Does not claim runtime behavior, scene/geometry behavior, lifecycle
    ordering, or call order.
- `find_field_references(selector, buildId?, readers?, writers?, limit?, scope?, collection?)`
  - Delegates to the static recovered-IL field read/write query.
  - Resolves one field and reports incoming `ReadsField` and/or `WritesField`
    relationships, with mutually exclusive direction filters.
  - Preserves ambiguity, bounded totals, and cross-origin reference
    provenance.
  - Does not claim runtime behavior, scene/geometry behavior, lifecycle
    ordering, or call order.
- `find_references(selector, buildId?, limit?)`
  - Delegates to the reference relationship query and preserves unresolved
    endpoints and evidence.
- `find_related_types(selector, buildId?, relationKinds?, limit?)`
  - Delegates to relationship queries, then deterministically filters type
    relationships such as `Inherits`, `ImplementsInterface`, `FieldType`,
    `PropertyType`, `EventType`, `ParameterType`, and `ReturnType`.

The existing `IndexQueryService` will gain a build-aware selection path or
equivalent overload so these calls remain on the service rather than having
the MCP adapter reimplement repository queries. Per section 2.4 this build-aware
Schedule I path is the shared authority path consumed by both the CLI Schedule I
query commands and MCP; the storage lookups it builds on
(`GetLatestCompletedIndexBySourceIdentityAsync` /
`GetLatestCompletedIndexForBuildAsync`) already exist. The CLI's no-build default
(current build) and its separate S1API/S1MAPI resolution remain compatible.

### 5.2 Build comparison

- `compare_symbol(selector, buildIdA, buildIdB)`
  - Resolves both builds through the same preferred verified extraction path.
  - Uses a symbol-scoped method on `BuildDiffService`.
  - Returns the canonical key, before/after symbol details, index IDs, and one
    classification: `Added`, `Removed`, `MethodBodyChanged`,
    `RelationshipsChanged`, or `Unchanged`.
  - The symbol-scoped method owns classification logic; the MCP layer only
    maps the result.

### 5.3 Build and environment tools

- `list_builds(limit?)`
  - Reads builds from `IAtlasRepository` and validated/preferred history
    metadata through the read-only extraction-history surface.
  - Marks the current build and indicates whether a preferred verified
    extraction and usable indexed output are available.
- `get_environment(buildId?)`
  - Returns the current environment snapshot when no build is supplied.
  - For an explicit build, returns that build's matching environment facts if
    available; otherwise returns `NoMatchingEnvironmentSnapshot` rather than
    another build's environment.

### 5.4 Scene tools

- `list_scenes(buildId?, sceneSnapshotId?, kind?, query?, limit?)`
- `get_scene(selector, buildId?, sceneSnapshotId?, kind?, includeChildren?, includeComponents?, includeReferences?, limit?)`
- `get_gameobject(selector, buildId?, sceneSnapshotId?, includeChildren?, includeComponents?, includeReferences?, limit?)`
- `get_prefab(selector, buildId?, sceneSnapshotId?, includeObjects?, includeComponents?, includeReferences?, limit?)`
- `get_component(selector, buildId?, sceneSnapshotId?, includeReferences?, includeCode?, limit?)`

These tools delegate to `SceneQueryService`. A supplied scene snapshot ID is
verified against the supplied build ID when both are present; a mismatch is
invalid. When only a build ID is supplied, the latest completed scene snapshot
for that exact build is selected. Scene statuses, partial recovery, unresolved
references, bounded pages, scene containers, and code-symbol handoff data are
preserved.

## 6. Error semantics

Expected domain outcomes are returned as structured tool results. Initial V1
error codes include:

- `InvalidArguments`
- `NoAtlasState`
- `NoCurrentBuild`
- `BuildNotFound`
- `NoPreferredVerifiedExtraction`
- `ExtractionIntegrityFailure`
- `NoCompletedIndex`
- `IndexBuildMismatch`
- `SymbolNotFound`
- `AmbiguousSymbol`
- `SourceUnavailable`
- `SourceIntegrityFailure`
- `NoCompletedSceneIndex`
- `SceneSnapshotNotFound`
- `PartialRecovery`
- `UnresolvedSceneReference`

Ambiguous resolution returns candidates. An integrity or authority failure
returns no data. Unexpected exceptions are logged to stderr and surfaced as an
MCP tool error with a safe message; stack traces and raw storage details are
not included in normal tool data.

## 7. Testing strategy

### 7.1 Unit tests

Cover:

- Default and explicit build resolution.
- Preferred extraction and completed-index identity matching.
- Provenance envelope mapping.
- Invalid, unavailable, missing, and ambiguous result mapping.
- Related-type filtering.
- Symbol-scoped diff classifications, including unchanged symbols.
- Scene snapshot/build mismatch handling.
- Tool registration and the absence of mutation tool names.

### 7.2 Integration tests

Use the existing managed assembly, indexing, scene, and SQLite fixtures. Add a
focused `S1Atlas.Mcp.Tests` project to the solution for MCP unit and host-level
integration coverage. Tests must use generated or repository-owned fixtures
only, make no network request, and execute no game or external extraction
process.

Required integration cases:

1. Resolve the default current build and an explicit historical build.
2. Return symbol, relationship, source, diff, build, environment, and scene
   data through MCP-shaped results.
3. Prove ambiguous and missing selectors are explicit and include candidates
   where applicable.
4. Seed a preferred verified extraction alongside a Phase 3 candidate,
   retained failure output, or unverified row; prove only the preferred
   integrity-verified extraction is returned.
5. Corrupt a preferred extraction or indexed source file; prove the tool
   returns an integrity failure with no authoritative payload.
6. Snapshot all Atlas database/data-root file paths and hashes before and after
   exercising every tool; prove no file is created, deleted, or modified.
7. Prove the read-only storage opening does not create missing state or run a
   migration and that mutation calls are rejected.
8. Exercise the stdio host or SDK tool registration with stdout reserved for
   protocol traffic and stderr used for diagnostics.

## 8. Documentation and milestone update

Update `README.md` to:

- Add the `S1Atlas.Mcp mcp serve` launch command.
- Document stdio usage and the `S1ATLAS_HOME` data-root behavior.
- Add the complete V1 tool list and the build/provenance/error rules.
- State that MCP has no write, patch, network, or game-execution capability.
- Move the read-only MCP server from outstanding to completed in the next
  milestone section while leaving the agent skill milestone outstanding.

## 9. Acceptance criteria

The milestone is complete when:

- `S1Atlas.Mcp` builds and launches through `mcp serve` over stdio.
- The published tool list contains only the approved read-only tools.
- All symbol and scene results identify the exact build and verified source
  context used.
- All data comes through the existing integrity-verified authority and query
  services; no raw DB re-query exists in the MCP adapter.
- The Schedule I build-authority path is shared: the CLI Schedule I query
  commands and MCP resolve the same preferred, integrity-verified index, so the
  two surfaces cannot diverge for a Schedule I query (section 2.4).
- Source results are hash-verified and never written by MCP.
- Missing, ambiguous, unavailable, partial, and integrity-invalid states are
  explicit.
- No MCP invocation mutates Atlas state, accesses the network, or executes the
  game or extraction tools.
- Unit and integration tests pass using non-proprietary offline fixtures.
- README documents the command, tools, trust boundary, and completed
  milestone.
