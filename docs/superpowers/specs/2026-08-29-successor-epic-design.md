# S1Atlas Successor Epic Design

**Status:** Proposed for implementation planning

**Source requirements:** Jira AT-31 and its five child tickets: AT-33, AT-32, AT-35, AT-34, and AT-36. AT-23 is the completed predecessor and remains outside this scope.

## Goal

Make S1Atlas guide an agent from a behavioral question to an evidence-bounded implementation decision: identify likely workflow owners, test authority/identity/lifecycle/exclusivity claims, expose coverage limits, check public S1API/S1MAPI surfaces first, and produce one bounded next step when static evidence is insufficient.

## Problem statement

S1Atlas v1.1.0 closed the raw-fidelity and token-efficiency gaps tracked by AT-23: indexed bodies, callable interop surfaces, reference-mod prior art, relationship queries, and static/runtime distinctions are available. The Organized Crime Local Pressure work showed a different failure class. Agents could still treat a named event as a lifecycle contract, adjacent callbacks as one transaction, a readable runtime identifier as canonical identity, or visible mod behavior as proof that the native workflow was preserved.

The successor must compose existing evidence before recommending a hook, patch, replacement, or adapter. It must preserve the distinction between indexed facts, deterministic derivations, runtime-only unknowns, and evidence that is missing because coverage is incomplete.

## Scope and decomposition

This is one product outcome delivered through three independently testable implementation plans:

1. **Evidence policy and composition:** AT-33 followed by AT-32. This establishes the behavior-ownership gate and the shared `investigate_seam` result consumed by both CLI and MCP.
2. **Public API and native evidence:** AT-34's standalone MCP parity surface can proceed after the existing API-index infrastructure is verified and the AT-33 policy contract is accepted; its `investigate_seam` integration lands after AT-32. AT-35 begins after the AT-32 evidence contract is stable. Both enrich the seam report without changing its trust rules.
3. **Runtime-proof protocol:** AT-36 begins after AT-33 and can build its planner contract in parallel with AT-32, but final integration consumes the completed seam packet.

AT-31 is the umbrella acceptance frame, not a separate coding task. AT-35 is additionally split into a feasibility/provenance slice and a recovery implementation slice because the local recovery toolchain, licensing boundary, and reproducibility constraints are the principal risk.

This intentionally resequences AT-34 ahead of AT-35 for the low-risk, read-only API parity portion. It does not make AT-34's seam integration independent of AT-32, and it does not reduce AT-35's position as the highest-risk evidence work.

## Design principles

- Querying is read-only and offline. No MCP operation launches the game, mutates Atlas state, downloads inputs, or applies a patch.
- A completed, matching index is the only authoritative indexed evidence.
- Every result preserves build or API codebase/channel, index identity, source/body status, and applicable hashes.
- Evidence is classified as `FACT`, `DERIVED`, or `UNKNOWN`; an interpretation or recommendation is explicitly separate.
- Missing or partial callers are not equivalent to zero callers.
- Event names, callback order, callable wrappers, visible effects, and proximity do not establish behavior ownership by themselves.
- Public S1API/S1MAPI and native contracts are checked before recommending direct patches or replacements.
- Native recovery is additive evidence with its own provenance and completeness state; it never silently replaces an interop stub or managed edge.
- Proprietary binaries, raw disassembly, extracted method bodies, reference-mod code/assets, and local paths remain outside the repository.

## Architecture

### Shared seam investigation service

Add a shared service in the existing indexing/query layer. The service receives a pinned query scope, an exact-or-resolvable symbol selector, and bounded traversal limits. It resolves the target before collecting evidence and returns one structured packet containing:

- pinned build/index or API codebase/channel provenance;
- exact resolution status and ambiguity candidates;
- body and callable-surface status;
- bounded callers, callees, references, field reads/writes, call sites, and source neighborhood;
- role candidates such as request, RPC ingress, host logic, state writer, event emission, presentation, persistence, cleanup, or unknown;
- authority, identity, lifecycle, exclusivity, and native-substrate evidence dimensions;
- coverage state for each relationship family;
- bounded reverse-traversal owner candidates, each with its evidence path;
- explicit unproven dimensions and deterministic next actions;
- a valid completed result for “no supportable seam found.”

The CLI and MCP adapters map this same service result. They do not independently re-query SQLite or implement separate owner heuristics.

Owner candidates are selected and truncated deterministically without a confidence score. Traverse relationships in ascending `RelationshipId` order, retain the shortest evidence path for each candidate, then sort candidates by shortest path length ascending, distinct supporting relationship-family count descending, canonical key ordinal ascending, and symbol ID ordinal ascending. Apply the configured candidate limit only after this ordering. The packet includes the selected path and the evidence-family count as DERIVED facts, not as a probability or confidence value.

The committed operation name is `investigate_seam` for both CLI and MCP. Its gate-aligned result fields are: behavioral question; pinned provenance; candidate symbol and role; body/callability coverage; authority and entity attribution; alternate/generic callers and exclusivity; lifecycle position and before/after state evidence; public API check; UNKNOWN dimensions; and bounded next actions. AT-32 may add detail sections, but these gate fields remain present one-for-one in every resolved packet.

The initial AT-32 implementation uses current managed/indexed evidence only. AT-35 evidence is an optional, provenance-tagged enrichment path added later.

### API index access

Extend the existing API index selection/query path so MCP can enumerate and query completed S1API/S1MAPI indexes for installed, release, and preview channels. API selection must not require Schedule I build authority. Results must expose source commit or installed binary identity, index identity, completion status, and stale/unavailable conditions. The standalone API MCP surface can ship before AT-32; only the seam-service record of whether the API-before-patch check was performed and what it found waits for AT-32 integration.

### Native evidence

Add a bounded local recovery workflow for explicitly selected methods. Each recovered method is keyed to the Schedule I build, `GameAssembly.dll` hash, S1Atlas index identity, recovery tool identity, method pointer/native address mapping, wrapper-to-native relationship, recovered direct edges/field accesses, recovery status, completeness, output hashes, and timestamps. Input changes invalidate the evidence. Indirect dispatch, cross-thread behavior, and licensing/distribution decisions remain explicitly unknown or out of scope unless directly supported.

### Runtime-proof planner

Implement a pure planner/reporting component. It consumes known static facts, known unavailable observables, and the policy gate. It emits competing hypotheses, only the telemetry available for the selected build, positive/negative controls, bounded duration/sample rate, lifecycle checks when relevant, cleanup/artifact requirements, and a PASS / INCONCLUSIVE / STOP decision table. Every plan is scoped to an explicit execution boundary: single-player, listen-host, dedicated server, or client. It must not transfer authority or observability assumptions between those roles. It never launches a game or invents telemetry.

## Output contract

The public structured result must support these states without collapsing them:

- `Resolved`: evidence packet produced, including the possibility that no supportable seam was found.
- `Ambiguous`: exact resolution requires caller qualification; include bounded candidates.
- `NotFound`: no symbol or API index matched the selector.
- `Unavailable`: authority, integrity, source, recovery, or required index state is unavailable.
- `Invalid`: arguments or scope violate the bounded contract.

Within a resolved packet, owner candidates and evidence dimensions carry explicit status rather than a confidence score. Relationship families expose total, returned, and completeness/coverage state. A zero result is conclusive only when the relevant index and relationship family are complete for the selected scope.

## Ticket-level behavior

### AT-33 — behavior-ownership policy

Update the canonical S1Atlas agent skill with a mandatory gate whose required fields map one-for-one to the AT-32 packet: behavioral question; pinned provenance; candidate symbol and role; body/callability coverage; authority/entity attribution; alternate/generic callers and exclusivity; lifecycle position and before/after state; API-before-patch result; remaining UNKNOWNs; and the smallest bounded next action. Add negative-seam and runtime-proof templates. Add examples for OC-29, OC-32, OC-30, and OC-2. Add a regression fixture that rejects symbol-presence, friendly-name, callback-order, wrapper-callability, and visible-effect reasoning when ownership evidence is absent.

### AT-32 — deterministic seam packet

Add the shared service plus CLI and MCP surfaces under the committed operation name `investigate_seam`. Use exact resolution before traversal, the explicit owner-candidate ordering defined above, deterministic bounded limits, prominent coverage warnings, full owner paths, optional detail sections, and token-lean summary output. Add synthetic fixtures for the OC-32 arrest shape and OC-2 native-navigation replacement shape. Ensure incomplete call graphs cannot produce a definitive negative.

### AT-34 — S1API/S1MAPI MCP parity

Expose completed API indexes through read-only MCP. Support index/channel enumeration, metadata, exact lookup, source/body status, relationships, inheritance/interfaces, stale/unavailable states, and CLI-equivalent semantics. Cover exact, ambiguous, missing, stale, and parity cases. MCP calls must not build, download, or mutate indexes. Ship this standalone parity surface first; add its API-before-patch result to `investigate_seam` only after AT-32's shared packet exists.

### AT-35 — targeted native recovery

Implement selective recovery only for explicitly bounded methods or traversal budgets. Preserve wrapper, RPC ingress, host logic, client/presentation, and native body distinctions. Add success, no-body, ambiguous mapping, changed-build, reproducibility, and local non-committed OC-32 smoke coverage. Recovery failures remain visible and do not fall back silently.

### AT-36 — runtime-proof and compatibility planning

Generate bounded diagnostic protocols from static unknowns. Include authority and canonical identity, occurrence-time versus receipt-time state, lifecycle/load/save/sleep/restart checks, native workflow owner and downstream consumers, substrate preservation, interception/replacement, teardown, precedent/licensing boundary, and the smallest native-preserving alternative. Every generated plan must preserve the single-player, listen-host, dedicated-server, and client evidence boundary. Cover OC-29, OC-30, and OC-2 fixture shapes.

## Repository touchpoints

- `skills/s1atlas/SKILL.md`: canonical behavior gate, templates, and escalation language.
- `tests/S1Atlas.Docs.Tests/AgentUsageContractTests.cs`: skill contract and negative recommendation regression coverage.
- `src/S1Atlas.Indexing/Query/`: all new public seam, API-selection, native-evidence, and runtime-plan query records/enums, alongside the existing query contracts; shared seam composition and deterministic owner traversal.
- `src/S1Atlas.Core/Indexing/`: only persistence-neutral primitives that are required by those query contracts; do not split the public seam packet across Core and Indexing query namespaces.
- `src/S1Atlas.Indexing/Workflow/`: bounded native-recovery workflow and existing API-index integration.
- `src/S1Atlas.Application/Composition/ReadOnlyAtlasComposition.cs`: shared read-only service wiring where required.
- `src/S1Atlas.Cli/Commands/` and `src/S1Atlas.Cli/Output/`: CLI command and JSON/human output adapters, including the committed `investigate_seam` name.
- `src/S1Atlas.Mcp/Tools/`, `src/S1Atlas.Mcp/McpServerComposition.cs`, and `src/S1Atlas.Mcp/Mapping/`: MCP tools, API-index composition, seam composition, and envelope mapping.
- `src/S1Atlas.Storage/`: only if native evidence requires durable, hash-keyed records; any schema change must include migration and read-only repository coverage.
- `tests/S1Atlas.Indexing.Tests/`, `tests/S1Atlas.IntegrationTests/`, and `tests/S1Atlas.Mcp.Tests/`: pure service, CLI parity, MCP envelope, determinism, and trust-boundary tests.
- `docs/USAGE.md`, `docs/REFERENCE.md`, and the relevant design docs: user-facing command/tool/provenance documentation. AT-32 and AT-34 tests must assert that the new CLI/MCP surfaces, statuses, provenance, and read-only boundaries are documented.

## Verification gates

1. **Policy gate:** AT-33 tests reject unsupported ownership recommendations and require explicit unknowns/escalation.
2. **Static composition gate:** AT-32 CLI and MCP return equivalent structured packets for pinned fixtures, including “no supportable seam found.”
3. **API-first gate:** AT-34 can prove an API index was checked and distinguishes completed, stale, missing, and ambiguous states without mutation.
4. **Native evidence gate:** AT-35 reproduces evidence records for pinned inputs, invalidates them after input changes, and never commits proprietary artifacts.
5. **Runtime protocol gate:** AT-36 produces one bounded diagnostic plan with available observables, an explicit single-player/listen-host/dedicated-server/client boundary, and first-class INCONCLUSIVE/STOP outcomes.
6. **Repository gate:** Release build, tests, format/hygiene checks, read-only MCP trust tests, and documentation checks pass. Generated/private data remains outside the repository.

## Non-goals

- Reopening or modifying AT-23 functionality.
- Automatic mod downloading, internet similarity ranking, or reference-mod redistribution.
- Whole-game native decompilation or uncontrolled native graph extraction.
- Runtime mutation, automatic patch generation, game launch, or automated proof execution.
- Opaque confidence scoring.
- Duplicated CLI/MCP query logic.
