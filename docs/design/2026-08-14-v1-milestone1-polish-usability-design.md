# S1Atlas V1 Milestone 1 — Polish & Usability Design

**Status:** Proposed design  
**Date:** 2026-08-14  
**Milestone:** V1 Completion — 1 of 6  
**Scope:** Query/source usability refinement, small required index-data additions, and real API validation

## 1. Purpose

This milestone improves the existing S1Atlas query experience before later V1 work builds on it.

The current index is already useful: it can decompile the preferred validated Schedule I extraction, persist normalized symbols and relationships, and query Schedule I/S1API/S1MAPI code snapshots. A real mod-development acceptance probe showed that this foundation can find likely implementation hooks, but also exposed recurring usability friction around source navigation, relationship output, sparse reconstructed method bodies, and real API-channel validation.

Milestone 1 fixes those gaps without changing the extraction trust model or expanding into a new subsystem.

The governing principle is:

> **Progressive readability, not progressive disclosure of truth.**
>
> S1Atlas may make technical information easier to understand, but it must not achieve simplicity by hiding exact identities, evidence, provenance, hashes, signatures, or other technical facts.

This applies to the CLI now and later to the human portal and MCP interfaces.

## 2. Goals

Milestone 1 will:

- Make `source` symbol-centric instead of file-centric.
- Persist exact source spans needed to return focused source snippets.
- Persist a simple method-body availability signal rather than infer availability from missing call edges.
- Make exact symbol IDs and canonical signatures first-class selectors.
- Make ambiguous single-symbol queries fail clearly with candidate choices rather than silently merging results.
- Give `refs`, `callers`, and `callees` distinct, documented semantics.
- Show readable relationship endpoints **in addition to** exact IDs, signatures, evidence, and resolution state.
- Distinguish empty relationship results from unavailable or sparse recovered method-body data.
- Add bounded query results with accurate total/returned counts and `--limit` where appropriate.
- Improve command errors and status messages without weakening the structured JSON contract.
- Validate real S1API/S1MAPI Installed/Release/Preview behavior wherever legitimate inputs exist.
- Preserve offline/local-first behavior and explicit GitHub synchronization by default.

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

Milestone 1 refines the existing shared query layer and makes two small additive data-capture improvements that the query experience genuinely requires.

```text
existing trusted indexing pipeline
        |
        +--> capture full source span
        +--> preserve method-body availability
        |
        v
completed index
        |
        v
IndexQueryService / shared query contracts
        |
        +--> deterministic symbol resolution
        +--> focused source retrieval
        +--> enriched relationships
        +--> bounded search/counts
        +--> availability/status information
        |
        v
CLI human renderer / JSON envelope
```

The existing symbol identities, relationship identities, extraction identity, snapshots, and trust model remain authoritative.

The milestone must not create a second source store, second symbol model, second relationship model, presentation-only database, or extraction-style recovery framework.

### 4.1 Shared Query-Layer First

Facts that later interfaces also need belong in the shared query layer rather than being recomputed by the CLI.

Examples include:

- Exact symbol resolution.
- Ambiguity candidates.
- Source location and snippet metadata.
- Resolved relationship endpoint names/signatures.
- Relationship evidence and resolution state.
- Method-body/data-availability status.
- Total and returned result counts.

The CLI owns formatting. It does not own the meaning of the data.

## 5. Symbol Selection

Broad textual matching remains useful for discovery, but commands that operate on one symbol require deterministic resolution.

### 5.1 Supported Selectors

Single-symbol commands resolve in strongest-to-weakest order:

1. Exact symbol ID.
2. Exact canonical signature/key.
3. Unique exact qualified-name/signature match.
4. Unique best-ranked textual match.

For rung 4, a textual result is selected only when exactly one symbol holds the single best rank. A tie at the best rank is ambiguous even if lower-ranked candidates differ.

Atlas never silently chooses the first equally ranked candidate.

### 5.2 Ambiguity Output

Human output shows candidate names **and** technical identity:

```text
Ambiguous symbol: "Dealer"

6 matching symbols:
1. ScheduleOne.Economy.Dealer
   ID: <symbol-id>
   Signature: <canonical signature>
2. ...

Use the exact symbol ID or canonical signature to select one.
```

JSON exposes structured candidates rather than requiring consumers to parse the human message.

### 5.3 Broad Search Remains Broad

`search` is not a single-symbol command. It may return many ranked matches across names, qualified names, namespaces, and signatures.

Search must not silently collapse distinct symbols that happen to share a display name.

## 6. Search Results and Limits

Search and other potentially large list queries support `--limit`.

Recommended default:

```text
50
```

The result preserves both:

- `totalCount`
- `returnedCount`

Human output makes truncation explicit:

```text
Found 237 matches. Showing 50.
Use --limit <n> to change the result count.
```

A limit controls returned/presented results; it must not cause Atlas to claim the underlying result set contains only the returned items.

### 6.1 Bounded Repository Queries

Milestone 1 should not implement `--limit` by loading every symbol into memory and then calling `Take(n)`.

Where the current repository shape permits it, search should use bounded persistence queries with an accurate count-then-page/limit approach so Atlas can return correct `totalCount` while avoiding O(N) per-query materialization of the entire symbol set.

Ranking semantics must remain deterministic. If part of ranking cannot be expressed directly in SQLite without distorting results, the implementation plan should choose the smallest bounded candidate window that preserves documented ranking rather than reverting to unbounded full-index loads.

## 7. Source Navigation

### 7.1 Symbol-Centric Default

`source <symbol>` resolves one symbol and returns the source range associated with that symbol rather than every source file in the index.

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

### 7.2 Required Source-Span Capture

The current pipeline does not retain a complete source span for every symbol. Milestone 1 therefore intentionally extends indexing to capture and persist the full line/column span already available from Roslyn syntax locations.

This is a small additive data improvement, not a new source model.

Real start/end columns should be stored. The design does not accept permanently hard-coded column `1` while promising column precision.

### 7.3 Default Context

Default source context is:

```text
5 lines before
5 lines after
```

`--context <n>` adjusts the context count.

Context is clipped safely at file boundaries.

### 7.4 Full File Access

The focused snippet becomes the default, but experienced users retain deliberate access to the complete generated/upstream source file.

Because Schedule I may decompile into a multi-megabyte module source file, full-file access must not accidentally dump an unbounded file to an interactive terminal. The implementation should provide one of these equivalent safe behaviors:

- require an explicit output path for files above a documented byte threshold; or
- refuse terminal emission above that threshold and instruct the user to write the file to disk.

Small source files may still be printed directly when explicitly requested.

The exact option names are implementation details; the safety and full-access behavior are requirements.

### 7.5 Source Integrity

Before returning source content, Atlas verifies that it is reading the expected Atlas-owned source artifact using recorded path/hash facts.

If the file is missing or the hash does not match, Atlas reports a distinct source-integrity failure rather than returning potentially incorrect content.

No game-installation file is modified.

## 8. Method-Body/Data Availability

The real Schedule I smoke demonstrated that reconstructed Cpp2IL methods can be sparse or stubbed. S1Atlas must communicate this limitation explicitly.

Milestone 1 introduces a simple shared availability classification:

```text
Available
UnavailableOrStubbed
Unknown
```

This is not a numerical confidence model.

### 8.1 Required Body-Availability Capture

Body availability must not be inferred from the absence of `Calls` relationships. A real call-free method and a reconstructed stub can both have zero call edges.

The indexing/decompiler path therefore preserves an explicit body-availability fact derived from the decompiler's existing method/body knowledge (for example its existing `HasBody` signal plus the minimum stub check required to distinguish known reconstructed placeholders).

The exact persisted field name is implementation-defined, but it must be queryable without guessing from relationship absence.

Human output may say:

```text
Recovered body: unavailable/stubbed
Declaration metadata is available.
Call relationships may therefore be incomplete.
```

A zero-result caller/callee query must not be described as proof that no call exists when body data is unavailable or unknown.

## 9. Relationship Query Semantics

`refs`, `callers`, and `callees` must have distinct semantics.

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

The command preserves relationship evidence and unresolved target text.

### 9.2 `callers <symbol>`

`callers` returns incoming call-like relationships where the selected symbol is the resolved target.

Call-like relationships use the normalized relationship taxonomy rather than string heuristics.

Completeness is bounded by two independent factors:

1. whether caller method bodies were recoverable enough to produce call edges; and
2. whether those call sites resolved to the selected target symbol rather than remaining unresolved textual targets.

Atlas must communicate those limits rather than presenting a thin caller set as complete proof.

### 9.3 `callees <symbol>`

`callees` returns outgoing call-like relationships where the selected symbol is the source.

It uses the same call-like relationship taxonomy as `callers`.

### 9.4 Relationship Endpoint Enrichment

Resolved endpoints expose both readable and exact technical identity.

A relationship result carries enough information to render conceptually:

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

Atlas preserves the raw target evidence rather than guessing a binding.

## 10. Human Output Philosophy

Human output should be compact, readable, and technically complete.

It may organize or abbreviate presentation, but the information model retains:

- Exact symbol IDs.
- Exact relationship IDs where applicable.
- Canonical signatures.
- Qualified names.
- Codebase and channel.
- Evidence type.
- Resolution state.
- Build/index/snapshot provenance where relevant.
- Source location/hash where relevant.

Experienced users do not need a hidden developer mode to access exact identifiers.

The CLI does not need terminal colors, interactive navigation, or a TUI in Milestone 1.

## 11. Empty and Partial Results

Atlas distinguishes meaningfully different outcomes.

### 11.1 No Relationship in Indexed Evidence

```text
No matching relationships found.
```

### 11.2 Body Data Is Insufficient

```text
No call edges found.
Recovered method-body data is unavailable or stubbed, so this is not evidence that the method has no callers/callees.
```

### 11.3 Target Resolution Is Incomplete

If unresolved call sites exist that could not be bound to a symbol, Atlas surfaces that limitation separately from body availability.

### 11.4 Unresolved Relationship Target

The relationship remains visible with raw target text and `Unresolved` status.

### 11.5 No Completed Index

Return a distinct status/error indicating that the requested codebase/channel has no completed index.

### 11.6 Missing Installed Dependency

For example:

```text
S1MAPI Installed: Not present
```

A missing installed API is never synthesized from Release or Preview data.

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

Milestone 1 evolves response payloads in backward-compatible ways where practical.

The schema version is bumped only if implementation requires a genuinely breaking machine contract.

### 12.2 Distinct Error/Status Codes

Where useful to automation, separate outcomes have separate stable codes rather than relying on English parsing.

Examples include:

- AmbiguousSymbol.
- SymbolNotFound.
- NoCompletedIndex.
- SourceUnavailable.
- SourceIntegrityFailure.
- InvalidCodebaseChannel.
- InstalledDependencyMissing.
- UpstreamUnavailable.

Exact names may be reconciled with existing CLI conventions during implementation, but the semantics remain distinct.

## 13. S1API and S1MAPI Real-World Validation

The indexing/upstream infrastructure exists, but V1 needs real proof of the supported channel model.

Milestone 1 finishes with a documented smoke matrix:

```text
                     Installed      Release      Preview
Schedule I              PASS          N/A           N/A
S1API                 PASS/NP         PASS          PASS
S1MAPI                PASS/NP         PASS          PASS
```

`NP` means `Not present` and is an acceptable real-world result when a legitimate installed binary is not actually installed in the validation environment. Missing installed binaries do **not** block Milestone 1 completion, provided Atlas detects and reports that absence correctly and Release/Preview are validated independently.

The validation process must not modify the user's Schedule I installation merely to manufacture Installed coverage.

### 13.1 Authority Rules

- Schedule I supports Installed only.
- Installed API facts come from actual discovered installed binaries.
- Release facts come from the configured official released/tagged upstream snapshot.
- Preview facts come from the configured upstream development/default branch snapshot.
- Release/Preview never substitute for Installed.
- Matching GitHub source may enrich Installed, but installed binary facts remain authoritative.
- Channel identity remains visible in query results.

### 13.2 Network Policy

Normal `search`, `source`, `refs`, `callers`, `callees`, and local index queries remain offline.

GitHub synchronization remains an explicit network operation by default.

Optional automatic checks, when enabled, occur only during an already-running relevant command, never as background polling. The existing opt-in configuration model remains compatible with this milestone.

A failed explicit upstream sync may continue using a verified cached snapshot where the existing cache contract permits it. The result makes cache fallback/staleness visible rather than pretending a fresh fetch succeeded.

## 14. Data Flow

### 14.1 Source

```text
symbol selector
        |
        v
resolve one symbol
        |
        v
resolve persisted source span/file
        |
        v
verify Atlas-owned source artifact
        |
        v
extract span + context
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
attach body/target-resolution availability notes
        |
        v
render human / JSON
```

### 14.3 Search

```text
broad query
        |
        v
count matching results
        |
        v
bounded deterministic ranking/query
        |
        v
apply requested/default limit
        |
        v
return ranked results + counts
```

## 15. Persistence and Migration Guidance

A small additive schema migration is **expected** in this milestone because two required truths are not currently persisted with sufficient precision:

1. complete source spans (including real end positions/columns); and
2. method-body availability.

The implementation plan should use the smallest additive migration that fits existing schema conventions.

Migration rules remain:

- append using existing migration rules;
- preserve prior migrations byte-for-byte;
- preserve existing valid index/extraction history;
- do not perform destructive migration or reset the Atlas database;
- explicitly document whether existing completed indexes need a one-time reindex to populate the new fields.

If old completed indexes lack the new facts, query behavior must fail honestly (`Unknown`/source span unavailable) rather than fabricate them. A normal reindex may upgrade that snapshot's query richness.

Do not add generalized tables or abstractions for future portal/MCP needs.

## 16. Reproducible Acceptance Baseline

The design must not depend on an external private issue as its only acceptance definition.

Milestone 1 will carry a sanitized, repository-owned acceptance baseline containing representative queries and the friction they demonstrated. It may be a short doc under `docs/` or a committed smoke checklist, but it must not contain proprietary decompiled source.

At minimum the baseline includes representative investigations for:

```text
Property
Employee / AssignProperty
Delivery / LoadingDock
Storage / Grid
ITransitEntity / TransitRoute
```

The baseline records the pre-Milestone-1 friction categories:

- source returned file-level information rather than a focused symbol span;
- relationship output emphasized opaque IDs;
- `refs`/`callers`/`callees` semantics were not distinct enough;
- empty caller/callee results could not distinguish sparse bodies from true absence;
- broad queries materialized large result sets without user-facing limits/counts.

The final real smoke repeats these representative queries and records whether the usability friction was removed.

## 17. Failure and Recovery Behavior

Milestone 1 must not jeopardize existing completed indexes.

- Query failures do not mutate index authority.
- Missing/corrupt source is reported, not silently ignored.
- Ambiguity never causes a guessed selection.
- Upstream network failure does not corrupt cached snapshots.
- Release/Preview cannot replace Installed truth.
- Existing completed indexes remain queryable when a non-authoritative upstream refresh fails.
- Older indexes lacking new additive fields remain representable with honest `Unknown`/unavailable status until reindexed.

No new promotion journal or extraction-style recovery subsystem is needed.

## 18. Testing Strategy

### 18.1 Unit Tests

Cover S1Atlas-owned logic including:

- Exact symbol-ID selection.
- Canonical signature selection.
- Best-rank tie ambiguity.
- Search ranking and limits.
- Total/returned counts.
- Source span capture and range extraction.
- Real line/column precision.
- Context clipping at file boundaries.
- Full-file terminal size protection.
- Relationship direction and call-like filtering.
- Endpoint enrichment.
- Resolved vs unresolved targets.
- Body-availability classification.
- Target-resolution completeness messaging inputs.

### 18.2 Integration Tests

Fixture indexes prove:

- `refs`, `callers`, and `callees` have distinct semantics.
- Exact IDs deterministically select one symbol.
- Ambiguous names do not silently merge single-symbol operations.
- Human and JSON outputs preserve exact IDs/signatures.
- Source snippets correspond to persisted complete spans.
- Source-integrity failures fail closed.
- Missing installed API data remains missing rather than being filled from Release/Preview.
- Accurate counts and bounded result retrieval work without full-index materialization.
- Older rows/indexes without newly added facts return honest `Unknown`/unavailable values.

### 18.3 Real Smoke

Use the actual installed Schedule I Atlas and legitimate API/upstream inputs available to the operator.

Repeat the repository-owned representative acceptance queries from Section 16 and verify that:

- symbol selection is deterministic;
- source output is focused and useful;
- relationship endpoints are readable while exact IDs remain visible;
- callers/callees accurately describe both body and target-resolution limitations;
- broad queries are bounded and report counts;
- local queries remain offline;
- API channel authority remains separated.

The smoke report contains metadata/counts/commands and usability findings only; it must not commit proprietary decompiled game source.

## 19. Over-Engineering Guardrails

Milestone 1 follows these constraints:

- Reuse the existing normalized symbol/relationship model.
- Add only the small persisted facts required for honest source/availability behavior.
- Do not build portal-specific DTO hierarchies.
- Do not add generic pagination frameworks beyond what current CLI queries need.
- Do not add a new confidence scoring system.
- Do not solve sparse Cpp2IL method bodies with speculative inference.
- Do not create background services or daemons.
- Do not create a second query engine.
- Do not generalize Atlas for other games in this milestone.

## 20. Definition of Done

Milestone 1 is complete when:

- Exact symbol IDs and canonical signatures are accepted as first-class selectors.
- Ambiguous single-symbol queries return deterministic structured candidates.
- Best-rank ties are treated as ambiguous.
- `source` returns the selected symbol's focused source span plus configurable context.
- Real source end positions/columns are captured and persisted for newly indexed data.
- Full-file source access remains available without unbounded terminal dumps.
- Method-body availability is explicitly captured/persisted rather than guessed from call-edge absence.
- Search/list queries expose accurate total/returned counts with bounded retrieval.
- `refs`, `callers`, and `callees` have distinct semantics.
- Relationship output contains readable endpoints **and** exact technical IDs/signatures.
- Unresolved relationships retain raw target evidence.
- Caller completeness messaging covers both sparse bodies and unresolved call targets.
- CLI errors/statuses are actionable and machine-distinguishable.
- JSON remains a complete automation interface.
- Schedule I real indexing/query smoke remains green.
- S1API/S1MAPI Release/Preview behavior is validated with real upstream data.
- Installed API behavior is validated when binaries are present; otherwise `Not present` is correctly reported and is non-blocking.
- The sanitized acceptance baseline is committed and the final real smoke repeats it.
- Network access remains explicit by default and local queries remain offline.
- Existing Atlas authority/recovery guarantees remain intact.

## 21. V1 Roadmap Context

S1Atlas V1 requires all six milestones:

1. **Polish & Usability** — this specification.
2. **Scene Intelligence** — scenes, prefabs, GameObjects, transforms, components, serialized fields/references, and links to code symbols.
3. **Build & Symbol Diffing** — structural/source/relationship changes across Schedule I and API versions.
4. **Human Portal** — static local HTML exploration with technical details and deterministic context.
5. **MCP + Agent Skill** — read-only agent access and evidence/provenance methodology.
6. **V1 Hardening & Release** — installation/distribution, documentation, real smoke coverage, recovery/privacy/hygiene, and release readiness.

All six milestones are required before S1Atlas is called V1 complete.
