# S1Atlas V1 Milestone 1 — Polish & Usability Design

**Status:** Proposed design  
**Date:** 2026-08-14  
**Milestone:** V1 Completion — 1 of 6  
**Scope:** Query/source usability refinement and real API validation

## 1. Purpose

This milestone improves the existing S1Atlas query experience before later V1 work builds on it.

The current index is already useful: it can decompile the preferred validated Schedule I extraction, persist normalized symbols and relationships, and query Schedule I/S1API/S1MAPI code snapshots. The Organized Crime acceptance probe showed that this foundation can find likely implementation hooks, but also exposed recurring usability friction around source navigation, relationship output, sparse reconstructed method bodies, and real API-channel validation.

Milestone 1 fixes those usability gaps without changing the core extraction trust model or expanding into a new subsystem.

The governing principle is:

> **Progressive readability, not progressive disclosure of truth.**
>
> S1Atlas may make technical information easier to understand, but it must not achieve simplicity by hiding exact identities, evidence, provenance, hashes, signatures, or other technical facts.

This applies to the CLI now and later to the human portal and MCP interfaces.

## 2. Goals

Milestone 1 will:

- Make `source` symbol-centric instead of file-centric.
- Add useful source snippets with configurable context while preserving full file access.
- Make exact symbol IDs and canonical signatures first-class selectors.
- Make ambiguous single-symbol queries fail clearly with candidate choices rather than silently merging results.
- Give `refs`, `callers`, and `callees` distinct, documented semantics.
- Show readable source/target names in relationship output **in addition to** exact IDs and signatures.
- Distinguish empty relationship results from unavailable or sparse recovered method-body data.
- Add bounded result presentation with total/returned counts and `--limit` where appropriate.
- Improve command errors and status messages without weakening the structured JSON contract.
- Validate real S1API/S1MAPI Installed/Release/Preview behavior wherever legitimate inputs exist.
- Preserve offline/local-first behavior and explicit GitHub synchronization.

## 3. Non-Goals

This milestone does not add:

- Scene or world-object indexing.
- Build or symbol diffing.
- The static HTML portal.
- MCP.
- The agent skill.
- Semantic or vector search.
- Runtime game probing.
- A Cpp2IL replacement or new extraction algorithm.
- A TUI or interactive shell.
- Terminal themes or cosmetic color systems.
- A plugin architecture.
- Generalized multi-game support.
- A new Atlas authority or promotion framework.

Those either have separate mandatory V1 milestones or are outside V1.

## 4. Architectural Approach

Milestone 1 refines the existing shared query layer and lets the CLI consume richer results.

```text
existing completed index
        |
        v
IndexQueryService / shared query contracts
        |
        +--> symbol resolution
        +--> focused source retrieval
        +--> enriched relationships
        +--> availability/status information
        |
        v
CLI human renderer / JSON envelope
```

The existing index, symbol identities, relationship identities, extraction identity, snapshots, and trust model remain authoritative.

The milestone must not create a second source store, second symbol model, second relationship model, or presentation-only database.

### 4.1 Shared Query-Layer First

Usability facts that later interfaces will also need belong in the shared query layer rather than being recomputed by the CLI.

Examples include:

- Exact symbol resolution.
- Ambiguity candidates.
- Source location and snippet metadata.
- Resolved relationship endpoint names/signatures.
- Relationship evidence.
- Method-body/data-availability status.
- Total and returned result counts.

The CLI owns formatting. It does not own the meaning of the data.

## 5. Symbol Selection

S1Atlas currently supports broad textual matching. That remains useful for discovery, but commands that operate on one symbol need deterministic resolution.

### 5.1 Supported Selectors

Single-symbol commands should resolve, in strongest-to-weakest order:

1. Exact symbol ID.
2. Exact canonical signature/key.
3. Unique exact qualified-name/signature match.
4. Unique ranked textual match.

If more than one plausible symbol remains, the command returns an ambiguity result rather than guessing.

### 5.2 Ambiguity Output

Human output should show candidate names **and** technical identity:

```text
Ambiguous symbol: "Dealer"

6 matching symbols:
1. ScheduleOne.Economy.Dealer
   ID: <symbol-id>
   Signature: <canonical signature>
2. ...

Use the exact symbol ID or canonical signature to select one.
```

JSON should expose structured candidates rather than requiring consumers to parse the human message.

### 5.3 Broad Search Remains Broad

`search` is not a single-symbol command. It may return many ranked matches across names, qualified names, namespaces, and signatures.

Search must not silently collapse distinct symbols that happen to share a display name.

## 6. Search Results and Limits

Search and other potentially large list queries should support `--limit`.

Recommended default:

```text
50
```

The query result preserves both:

- `totalCount`
- `returnedCount`

Human output should make truncation explicit:

```text
Found 237 matches. Showing 50.
Use --limit <n> to change the result count.
```

A limit controls presentation/returned results; it must not cause Atlas to claim the underlying result set contains only the returned items.

The implementation may apply the bound before or after persistence retrieval depending on the existing repository shape, provided ranking and counts remain correct and memory use is reasonable.

## 7. Source Navigation

### 7.1 Symbol-Centric Default

`source <symbol>` should resolve one symbol and return the source range associated with that symbol instead of returning every source file for an index.

For example:

```text
s1atlas source "ScheduleOne.Property.Property::SetOwned()"
```

returns:

- Resolved symbol identity.
- Exact source file.
- Source SHA-256.
- Recorded start/end line and column.
- Source text for the recorded symbol span.
- A small amount of context before and after the span.
- Codebase/channel.
- Index/build/snapshot provenance available to the query layer.
- Best-effort/recovery status.

### 7.2 Default Context

Default source context is:

```text
5 lines before
5 lines after
```

`--context <n>` adjusts the context count.

Context must be clipped safely at file boundaries.

### 7.3 Full File Access

The focused snippet becomes the default, but full source access remains available through an explicit option such as:

```text
--file
```

The exact option name may follow established CLI conventions during implementation, but the behavior is required: experienced users must retain deliberate access to the complete generated/upstream source file.

### 7.4 Source Integrity

Before returning source content, Atlas must use the existing recorded path/hash facts to ensure it is reading the expected Atlas-owned source artifact.

If the file is missing or its recorded integrity cannot be trusted, Atlas reports a distinct source-integrity failure rather than returning potentially incorrect content.

No game-installation file is modified.

## 8. Method-Body/Data Availability

The real Schedule I smoke demonstrated that reconstructed Cpp2IL methods can be sparse or stubbed. S1Atlas must communicate this limitation explicitly.

Milestone 1 introduces a small shared availability classification for query consumers. The exact type name is implementation-defined, but the semantics should remain intentionally simple:

```text
Available
UnavailableOrStubbed
Unknown
```

This is not a numerical confidence model.

It exists so the CLI, future portal, and future MCP do not independently guess what an empty call set means.

Human output may say:

```text
Recovered body: unavailable/stubbed
Declaration metadata is available.
Call relationships may therefore be incomplete.
```

A zero-result caller/callee query must not be described as proof that no call exists when body data is unavailable or unknown.

## 9. Relationship Query Semantics

`refs`, `callers`, and `callees` must no longer be generic aliases for effectively the same query behavior.

### 9.1 `refs <symbol>`

`refs` is the general relationship explorer.

It returns relevant incoming and outgoing relationships across supported relationship kinds, grouped by direction and/or kind in human output.

Examples include:

- Inheritance.
- Interface implementation.
- Parameter types.
- Return types.
- Field/property/event types.
- Calls.
- Construction.
- Field reads/writes.
- Other existing normalized relationship kinds.

The command must preserve relationship evidence and unresolved target text.

### 9.2 `callers <symbol>`

`callers` returns incoming call-like relationships where the selected symbol is the target.

Call-like relationships include the existing precise relationship kinds that semantically represent invocation of the selected method/constructor. The implementation should use the normalized relationship taxonomy rather than string heuristics.

### 9.3 `callees <symbol>`

`callees` returns outgoing call-like relationships where the selected symbol is the source.

It is the directional inverse of `callers` for the same call-like relationship taxonomy.

### 9.4 Relationship Endpoint Enrichment

Resolved endpoints expose both readable and exact technical identity.

A relationship result should carry enough information to render conceptually:

```text
Calls
  Source: ScheduleOne.Economy.Dealer::ProcessSale(...)
  Source ID: <exact-symbol-id>
  Source Signature: <canonical-signature>

  Target: ScheduleOne.Economy.Customer::BuyProduct(...)
  Target ID: <exact-symbol-id>
  Target Signature: <canonical-signature>

  Relationship ID: <exact-relationship-id>
  Evidence: RecoveredIL
  Resolution: Resolved
```

For unresolved targets:

```text
Target: SomeExternal.Type.Member
Target ID: unavailable
Resolution: Unresolved
Evidence: <evidence-kind>
```

Atlas must preserve the raw target evidence rather than guessing a binding.

## 10. Human Output Philosophy

Human output should be compact, readable, and technically complete.

It may organize or abbreviate presentation, but the information model must retain:

- Exact symbol IDs.
- Exact relationship IDs where applicable.
- Canonical signatures.
- Qualified names.
- Codebase and channel.
- Evidence type.
- Resolution state.
- Build/index/snapshot provenance where relevant.
- Source location/hash where relevant.

Experienced users should not need a hidden developer mode to access exact identifiers.

The CLI does not need terminal colors, interactive navigation, or a TUI in V1 Milestone 1.

## 11. Empty and Partial Results

Atlas should distinguish meaningfully different outcomes.

Examples:

### 11.1 No Relationship Exists in Indexed Evidence

```text
No matching relationships found.
```

### 11.2 Body Data Is Insufficient

```text
No call edges found.
Recovered method-body data is unavailable or stubbed, so this is not evidence that the method has no callers/callees.
```

### 11.3 Unresolved Relationship Target

The relationship itself remains visible with its raw target text and `Unresolved` status.

### 11.4 No Completed Index

Return a distinct status/error indicating that the requested codebase/channel has no completed index.

### 11.5 Missing Installed Dependency

For example:

```text
S1MAPI Installed: Not present
```

A missing installed API must never be synthesized from Release or Preview data.

## 12. CLI Errors and JSON Contract

### 12.1 Existing Envelope

The current JSON envelope remains the base contract:

```text
schemaVersion
command
success
exitCode
data
error
```

Milestone 1 should evolve response payloads in backward-compatible ways where practical.

The schema version should only be bumped if implementation requires a genuinely breaking machine contract.

### 12.2 Distinct Error/Status Codes

Where useful to automation, separate outcomes should have separate stable codes rather than relying on English parsing.

Examples include:

- AmbiguousSymbol.
- SymbolNotFound.
- NoCompletedIndex.
- SourceUnavailable.
- SourceIntegrityFailure.
- InvalidCodebaseChannel.
- InstalledDependencyMissing.
- UpstreamUnavailable.

Exact names may be reconciled with existing CLI conventions during implementation, but the semantics must remain distinct.

## 13. S1API and S1MAPI Real-World Validation

The indexing/upstream infrastructure exists, but V1 needs real proof of the supported channel model.

Milestone 1 finishes with a documented smoke matrix:

```text
                     Installed    Release    Preview
Schedule I              PASS         N/A        N/A
S1API                    PASS         PASS       PASS
S1MAPI                   PASS*        PASS       PASS
```

`PASS*` means Installed is only claimable if a legitimate installed binary is actually present during validation.

If S1MAPI or S1API is not installed, the report must say `Not present` rather than fabricating coverage.

### 13.1 Authority Rules

- Schedule I supports Installed only.
- Installed API facts come from actual discovered installed binaries.
- Release facts come from the configured official released/tagged upstream snapshot.
- Preview facts come from the configured upstream development/default branch snapshot.
- Release/Preview never substitute for Installed.
- Matching GitHub source may enrich Installed, but installed binary facts remain authoritative.
- Channel identity must remain visible in query results.

### 13.2 Network Policy

Normal `search`, `source`, `refs`, `callers`, `callees`, and local index queries remain offline.

GitHub synchronization remains an explicit network operation by default.

Optional automatic checks, when implemented according to the approved upstream design, occur only during an already-running relevant command, never as background polling, and are not required to complete Milestone 1 if the opt-in configuration surface is not yet exposed.

A failed explicit upstream sync may continue using a verified cached snapshot where the existing upstream cache contract permits it. The result must make stale/cache fallback visible rather than pretending a fresh fetch succeeded.

## 14. Data Flow

### 14.1 Source

```text
symbol selector
        |
        v
resolve one symbol
        |
        v
resolve recorded source location/file
        |
        v
verify Atlas-owned source artifact
        |
        v
extract recorded range + context
        |
        v
return snippet + technical provenance
```

### 14.2 Relationships

```text
symbol selector
        |
        v
resolve one symbol
        |
        v
load relevant normalized relationships
        |
        v
resolve known endpoint symbols
        |
        +--> preserve unresolved target text
        |
        v
attach names + IDs + signatures + evidence
        |
        v
attach data-availability status
        |
        v
render human / JSON
```

### 14.3 Search

```text
broad query
        |
        v
existing ranking/matching semantics
        |
        v
compute total count
        |
        v
apply requested/default limit
        |
        v
return ranked results + counts
```

## 15. Persistence and Migration Guidance

A schema migration is not a goal of this milestone.

Prefer deriving new presentation/query data from existing persisted facts.

A migration is justified only if a required shared truth such as method-body availability cannot be represented or reliably derived from the current index. If a migration is required:

- append it using existing migration rules;
- preserve all prior migrations byte-for-byte;
- do not force destructive reindexing of valid data unless technically necessary;
- document any required reindex behavior explicitly.

Do not add generalized tables or abstractions for future portal/MCP needs.

## 16. Failure and Recovery Behavior

Milestone 1 must not jeopardize existing completed indexes.

- Query failures do not mutate index authority.
- Missing/corrupt source is reported, not silently ignored.
- Ambiguity never causes a guessed mutation or selection.
- Upstream network failure does not corrupt cached snapshots.
- Release/Preview cannot replace Installed truth.
- Existing completed indexes remain queryable when a non-authoritative upstream refresh fails.

No new promotion journal or extraction-style recovery subsystem is needed.

## 17. Testing Strategy

### 17.1 Unit Tests

Cover S1Atlas-owned logic including:

- Exact symbol-ID selection.
- Canonical signature selection.
- Ambiguity detection and candidate ordering.
- Search ranking and limits.
- Total/returned counts.
- Source range extraction.
- Context clipping at file boundaries.
- Relationship direction and call-like filtering.
- Endpoint enrichment.
- Resolved vs unresolved targets.
- Data-availability classification and messaging inputs.

### 17.2 Integration Tests

Fixture indexes should prove:

- `refs`, `callers`, and `callees` have distinct semantics.
- Exact IDs deterministically select one symbol.
- Ambiguous names do not silently merge single-symbol operations.
- Human and JSON outputs preserve exact IDs/signatures.
- Source snippets correspond to recorded locations.
- Source-integrity failures fail closed.
- Missing installed API data remains missing rather than falling through to Release/Preview.
- Existing query/index behavior remains backward-compatible where intended.

### 17.3 Real Smoke

Use the real Schedule I index and legitimately available API inputs.

Repeat representative property/employee/delivery/storage queries from the Organized Crime acceptance probe and record whether the refined workflow substantially reduces manual friction.

Exercise S1API/S1MAPI Installed/Release/Preview channels wherever real inputs exist.

The smoke report must record limitations honestly, especially sparse reconstructed method bodies and legitimately missing installed dependencies.

## 18. Definition of Done

Milestone 1 is complete when:

- `source` returns focused symbol source by default.
- Source context is adjustable and full-file access remains available.
- Source output retains exact technical provenance and integrity information.
- Exact symbol IDs and canonical signatures work as selectors.
- Ambiguous single-symbol queries require explicit resolution.
- Search/list queries expose total/returned counts and bounded output.
- `refs`, `callers`, and `callees` have distinct semantics.
- Relationship output contains readable endpoints **and** exact technical IDs/signatures.
- Unresolved relationships retain raw target evidence.
- Empty call results distinguish lack of indexed edges from unavailable/stubbed body data.
- CLI errors/statuses are actionable and machine-distinguishable.
- JSON remains a complete automation interface.
- Schedule I real indexing/query smoke remains green.
- S1API/S1MAPI channel behavior is exercised against legitimate real inputs where available.
- Missing Installed inputs are reported as missing rather than synthesized.
- Network access remains explicit by default and local queries remain offline.
- Automated tests and format/hygiene gates pass.
- A real smoke report documents the improved usability and remaining limitations.

## 19. Over-Engineering Guardrails

Implementation should stop when the requirements above are satisfied.

In particular:

- Do not redesign the extraction pipeline.
- Do not introduce a generic presentation framework.
- Do not build portal-specific navigation models yet.
- Do not build an MCP-specific DTO hierarchy yet.
- Do not add a graph database.
- Do not add numerical confidence scoring.
- Do not build semantic search.
- Do not solve sparse Cpp2IL bodies with speculative inference.
- Do not add scene indexing in this milestone.
- Do not generalize Atlas for other games yet.

Favor small extensions to the existing query contracts and CLI renderers over new subsystems.

## 20. V1 Roadmap Context

This is the first of six mandatory S1Atlas V1 completion milestones:

1. **Polish & Usability** — this document.
2. **Scene Intelligence** — offline scenes, prefabs, GameObjects, transforms, components, serialized data, references, and links to Atlas code symbols.
3. **Build & Symbol Diffing** — compare indexed game/API snapshots and classify meaningful changes.
4. **Human Portal** — static local HTML exploration over the trusted Atlas query layer.
5. **MCP + Agent Skill** — read-only agent access plus evidence/provenance methodology.
6. **V1 Hardening & Release** — installation/distribution, documentation, real smoke coverage, recovery/privacy/hygiene, and V1 release readiness.

All six milestones are required before S1Atlas is called V1 complete.
