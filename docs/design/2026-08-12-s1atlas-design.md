# S1Atlas V1 Design Specification

**Status:** Approved design  
**Date:** 2026-08-12  
**Target platform:** Windows  
**Primary language:** C# / .NET

## 1. Purpose

S1Atlas is a local developer-intelligence platform for Schedule I mod development. Its purpose is to eliminate repeated reverse-engineering work by creating a durable, searchable, version-aware map of the installed game and its primary modding APIs.

S1Atlas serves two audiences:

1. Human developers who want to explore Schedule I internals, understand how systems work, and learn C# from real game code.
2. Development agents that need accurate, current, structured knowledge of Schedule I before designing or modifying mods.

S1Atlas is read-only with respect to the Schedule I installation. It analyzes the game but never modifies it.

## 2. V1 Scope

S1Atlas V1 will:

- Run locally on the Windows PC containing the Schedule I installation.
- Detect and fingerprint the installed Schedule I build.
- Extract and reconstruct IL2CPP metadata and assemblies.
- Produce readable C# decompilation.
- Index namespaces, types, methods, fields, properties, parameters, inheritance, references, callers, and callees.
- Preserve immutable snapshots of every indexed build indefinitely.
- Store normalized searchable metadata in one SQLite database keyed by build ID.
- Store raw extraction and decompiled source artifacts separately on disk by build.
- Deep-index S1API.
- Deep-index S1MAPI.
- Track installed MelonLoader and Sideload versions.
- Compare Schedule I builds and identify structural and method-body changes.
- Provide a CLI.
- Generate a local static HTML exploration portal.
- Include plain-English context for human users.
- Expose Atlas knowledge to agents through a read-only MCP server.
- Support an accompanying agent skill defining how agents should use S1Atlas responsibly.

## 3. Explicit V1 Exclusions

V1 does not require:

- Cross-platform scanning.
- Remote hosting.
- A React, SPA, or other full application frontend.
- Vector or embedding-based semantic search.
- Automatic patch generation.
- Automatic Harmony patch creation.
- Automatic mod breakage prediction.
- Automatic game modification.
- MCP write operations.
- A general-purpose AI agent embedded in S1Atlas.

These can be evaluated after the core index proves reliable.

## 4. Architectural Approach

S1Atlas will use a modular .NET solution rather than a monolithic executable or distributed service architecture.

```text
S1Atlas.sln

src/
  S1Atlas.Core/
  S1Atlas.Extraction/
  S1Atlas.Storage/
  S1Atlas.Cli/
  S1Atlas.Docs/
  S1Atlas.Mcp/

tests/
  S1Atlas.Core.Tests/
  S1Atlas.Extraction.Tests/
  S1Atlas.Storage.Tests/
  S1Atlas.IntegrationTests/
```

### 4.1 S1Atlas.Core

Core owns the S1Atlas domain model and abstractions. Representative concepts include `GameBuild`, `EnvironmentSnapshot`, `Assembly`, `Namespace`, `Type`, `Method`, `Parameter`, `Field`, `Property`, `SourceLocation`, `Reference`, `CallRelationship`, `InheritanceRelationship`, `DependencyVersion`, and `SymbolFingerprint`.

Core must not depend directly on Cpp2IL, ILSpy, SQLite, HTML generation, or MCP. The domain model belongs to S1Atlas rather than any extraction vendor.

### 4.2 S1Atlas.Extraction

Extraction owns integration with reverse-engineering and decompilation tooling. Responsibilities include:

- Discovering the Schedule I installation.
- Validating required input files.
- Determining available build/version identifiers.
- Hashing important source artifacts.
- Interfacing with Cpp2IL/LibCpp2IL.
- Interfacing with ILSpy decompilation.
- Preserving extraction artifacts.
- Normalizing extracted information into Core models.
- Deep-indexing S1API and S1MAPI.
- Detecting MelonLoader and Sideload versions.

Third-party extraction details are isolated behind interfaces so underlying tools can be replaced later without redesigning the rest of S1Atlas.

### 4.3 S1Atlas.Storage

Storage owns persistence and querying. V1 uses SQLite. A single Atlas database stores metadata for all indexed builds, keyed by build ID.

Representative persisted data includes builds, environment snapshots, assemblies, namespaces, types, methods, parameters, fields, properties, source locations, relationships, dependency versions, and symbol fingerprints.

Large raw and decompiled source artifacts remain on disk rather than being stored as database blobs. Storage exposes domain-level queries rather than leaking SQL into other projects.

### 4.4 S1Atlas.Cli

The CLI is the first operational interface.

Representative commands:

```text
s1atlas scan
s1atlas scan --force
s1atlas status
s1atlas env
s1atlas builds
s1atlas search <query>
s1atlas type <symbol>
s1atlas method <symbol>
s1atlas source <symbol>
s1atlas refs <symbol>
s1atlas callers <symbol>
s1atlas diff
s1atlas diff --symbol <symbol>
s1atlas docs
s1atlas serve-mcp
```

Queries that return structured information should support JSON output for automation.

### 4.5 S1Atlas.Docs

Docs generates a static local HTML portal from the same query layer used by the CLI and MCP server.

The portal should support search, namespace browsing, type browsing, method browsing, inheritance navigation, caller/callee navigation, references, decompiled source, build history, symbol history, build diffs, environment/dependency information, plain-English explanations, modding-oriented context, and C# learning context.

V1 should avoid requiring a JavaScript application framework.

### 4.6 S1Atlas.Mcp

The MCP project exposes Atlas data to development agents. MCP is a thin adapter over the same Core and Storage query interfaces used by the CLI and documentation generator.

Representative V1 tools:

- `search_symbols`
- `get_type`
- `get_method`
- `get_source`
- `find_callers`
- `find_references`
- `find_related_types`
- `compare_symbol`
- `list_builds`
- `get_environment`

MCP is read-only in V1. It does not patch Schedule I, modify mods, alter Atlas source facts, or execute arbitrary game operations.

## 5. Extraction Pipeline

The V1 extraction path is:

```text
Schedule I installation
        |
        v
Environment discovery
        |
        v
Build fingerprinting
        |
        v
Staging snapshot
        |
        v
Cpp2IL / LibCpp2IL
        |
        v
Managed reconstruction
        |
        v
ILSpy decompilation
        |
        v
S1Atlas normalization
        |
        v
SQLite indexing
        |
        v
Validation
        |
        v
Atomic promotion
        |
        v
Diff generation
        |
        v
HTML documentation
```

S1Atlas owns the orchestration and normalized data model. It does not reimplement IL2CPP reverse engineering from scratch.

## 6. Immutable Build Snapshots

Every successfully indexed Schedule I build is retained indefinitely.

```text
data/
  atlas.db

  builds/
    <build-id>/
      metadata.json
      raw/
      reconstructed/
      decompiled/

  docs/
```

Each snapshot records, where available:

- Schedule I version.
- Steam/build identifier.
- Scan timestamp.
- Relevant file hashes.
- S1API version.
- S1MAPI version.
- MelonLoader version.
- Sideload version.
- S1Atlas version.
- Cpp2IL/LibCpp2IL version.
- ILSpy/decompiler version.

Tracking tool versions allows S1Atlas to distinguish changes in the game from changes caused by an upgraded extraction/decompilation toolchain.

## 7. Transactional Scan Model

A scan must never corrupt an existing Atlas. New builds are initially written to a staging area:

```text
data/
  builds/
    .staging/
      <candidate-build-id>/
```

The authoritative Atlas is not updated until extraction, normalization, indexing, and validation succeed.

If a scan fails:

- The previous current build remains authoritative.
- Existing Atlas queries continue to work.
- The failed candidate never becomes a valid build.
- Failure diagnostics are retained where useful.

Promotion occurs only after validation succeeds.

## 8. Validation

Before promotion, S1Atlas should verify conditions such as:

- Build metadata exists.
- Required assemblies were extracted.
- Types were indexed.
- Methods were indexed.
- Decompiled source files exist.
- Recorded source locations resolve.
- Relationship targets are valid.
- SQLite changes are internally consistent.
- Dependency versions were captured when available.

S1Atlas should record extraction statistics such as numbers of assemblies, namespaces, types, methods, fields, properties, and relationships. Large unexplained deviations from previous successful scans should generate warnings or fail validation where appropriate.

## 9. Symbol and Relationship Model

V1 indexing depth includes:

- Namespaces
- Classes
- Structs
- Interfaces
- Enums
- Methods
- Constructors
- Fields
- Properties
- Parameters
- Return types
- Accessibility
- Static/instance state
- Virtual/override metadata where available
- Inheritance
- Interface implementation
- Type references
- Method calls
- Callers
- Callees
- Source locations
- Decompiled method bodies
- Stable fingerprints

A representative method record includes build, assembly, namespace, declaring type, name, fully-qualified signature, return type, parameters, accessibility, static flag, virtual/override information, decompiled source location, and fingerprint.

Relationships are stored separately from symbols so they can be navigated and compared across builds.

## 10. Build Diffing

Once two valid snapshots exist, S1Atlas compares their symbols and fingerprints.

V1 change classifications include:

- Added
- Removed
- Signature changed
- Method body changed
- Relationships changed
- Unchanged

A method may retain the same signature while still being reported as behaviorally relevant because its body or call relationships changed. The human portal should permit source comparison between builds for changed methods.

V1 reports factual changes but does not automatically decide that a specific mod is broken.

## 11. External Dependency Strategy

### Schedule I

Full deep index.

### S1API

Deep index. S1Atlas should understand the API surface and implementations sufficiently to help determine whether mod functionality can use S1API rather than patching raw Schedule I internals.

### S1MAPI

Deep index using the same principles as S1API.

### MelonLoader

V1 tracks installed version and compatibility metadata. Deep indexing is not required unless a future use case demonstrates a need.

### Sideload

V1 tracks installed version and compatibility metadata. Deep indexing is not required unless a future use case demonstrates a need.

Local installed state is authoritative for V1. Internet/GitHub latest-version checking is not part of the critical scan path.

## 12. Human Portal

The human portal is a first-class S1Atlas feature, not merely raw decompiler output rendered as HTML.

A symbol page should provide several levels of understanding.

### Plain-English Overview

Explains what the symbol appears to represent and its role in the game. V1 must be able to produce useful plain-English context without requiring an embedded general-purpose AI runtime. Deterministic summaries derived from indexed symbols and relationships satisfy this requirement. Richer AI-assisted explanations may be added later or used as optional enrichment, but they must be labeled as `INTERPRETATION` and must not become a dependency of the trusted indexing pipeline.

### Why a Modder Might Care

Highlights related systems and potential relevance to mod features.

### C# Learning Context

Identifies useful language concepts visible in the selected code, such as inheritance, interfaces, generics, enums, properties, instance methods, static methods, null checking, object references, delegates/events, LINQ, and method invocation.

### Evidence

Shows the underlying signatures, fields, properties, relationships, callers, callees, references, decompiled source, and build provenance.

## 13. Provenance Model

S1Atlas must distinguish facts from interpretation.

### FACT

Directly extracted from source or metadata.

Example: `Employee.Fire()` calls `EmployeeManager.RemoveEmployee()`.

### DERIVED

Computed deterministically from indexed facts.

Example: `EmployeeManager` participates in multiple employee lifecycle relationships.

### INTERPRETATION

A human- or AI-oriented explanation derived from the available evidence.

Example: `Fire()` appears to remove the employee from active employment without necessarily destroying the NPC object.

Interpretation is useful and encouraged, but must never masquerade as extracted truth. All information should remain traceable to the relevant Schedule I build.

## 14. Agent Skill (Shipped)

An S1Atlas agent skill complements MCP rather than replacing it. MCP provides information; the skill defines methodology.

The V1 skill is versioned at `skills/s1atlas/SKILL.md`. It is methodology-only:
it requires evidence-first use of the read-only CLI/MCP surfaces, labels
FACT/DERIVED/INTERPRETATION, and does not add capabilities or permit bypassing
the integrity boundary.

The skill should instruct agents working on Schedule I projects to:

1. Query S1Atlas before making claims about game internals.
2. Prefer the currently indexed build unless another build is explicitly targeted.
3. Check S1API and S1MAPI before recommending direct game patches.
4. Inspect decompiled source when behavior matters.
5. Distinguish FACT, DERIVED, and INTERPRETATION.
6. Record or cite relevant symbols/builds supporting implementation decisions.
7. Avoid assuming historical Schedule I modding knowledge remains valid after an update.
8. Recheck affected symbols after game or dependency updates.

## 15. Safety and Trust Boundaries

S1Atlas follows these rules:

- The Schedule I installation is read-only input.
- Scanning does not modify game files.
- MCP is read-only in V1.
- Failed scans cannot replace valid snapshots.
- Human/AI interpretations cannot overwrite extracted facts.
- Third-party reverse-engineering tools are isolated behind adapters.
- Agents receive the same underlying Atlas truth as human-facing interfaces.

## 16. Testing Strategy

### Unit Tests

Cover S1Atlas-owned logic such as symbol identities, fingerprints, diff classification, normalization, query parsing, path handling, and provenance classification.

### Integration Tests

Exercise representative fixture assemblies through extraction adapters, normalization, SQLite persistence, query APIs, and documentation generation. Integration coverage must include an intentionally failed or interrupted staged scan and verify that it cannot replace or corrupt the previously promoted build or database state.

### Real-Game Smoke Tests

Against the locally installed Schedule I build, verify that extraction completes, symbol counts are sane, method/source references resolve, relationships are populated, dependency discovery behaves correctly, generated documentation is browsable, and MCP can query the resulting Atlas.

## 17. V1 Milestones

Implementation order:

1. **Foundation** — .NET solution structure, configuration, environment discovery, build detection, SQLite schema.
2. **Extraction** — Cpp2IL/LibCpp2IL adapter, ILSpy adapter, source artifact preservation, normalized symbol generation.
3. **Relationships** — inheritance, references, callers, callees, source locations.
4. **CLI** — scan, status, search, symbol inspection, source, references, callers, builds, environment.
5. **Snapshots and Diffing** — immutable snapshots, fingerprints, build comparison, symbol comparison, source comparison.
6. **Human Portal** — static HTML generation, navigation, search, build history, source viewing, plain-English context, provenance, C# learning context.
7. **Dependency Indexing** — S1API, S1MAPI, MelonLoader tracking, Sideload tracking.
8. **MCP** — read-only MCP server, core query tools, build-aware queries.
9. **Agent Skill** — S1Atlas usage methodology, evidence/provenance rules, API-before-direct-patching guidance.
10. **Hardening** — integration tests, real-game smoke tests, interrupted scan recovery, documentation, local installation/use workflow.

## 18. Definition of V1 Complete

S1Atlas V1 is complete when it can:

- Discover the local Schedule I environment.
- Detect and fingerprint an installed game build.
- Extract and decompile that build.
- Preserve its artifacts immutably.
- Index its symbols and relationships.
- Search and navigate those symbols.
- Display decompiled source.
- Compare at least two indexed builds.
- Deep-index S1API and S1MAPI.
- Track MelonLoader and Sideload versions.
- Generate a useful static human portal.
- Provide plain-English modding and C# learning context with clear provenance.
- Expose the same trusted knowledge through a read-only MCP server.
- Provide an agent skill describing correct Atlas usage.
- Survive an interrupted or failed scan without damaging the last valid Atlas.

## 19. Design Principles

The implementation should favor:

- Correctness over cleverness.
- Readability over premature abstraction.
- Small, understandable C# components.
- Strong boundaries between responsibilities.
- Replaceable external-tool integrations.
- Evidence-backed explanations.
- Stable source provenance.
- Local-first operation.
- Incremental usefulness before V1 is fully complete.

Because S1Atlas is also intended to support learning, code should remain approachable enough for a developing C# programmer to follow, inspect, and discuss.
