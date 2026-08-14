# S1Atlas Build & Symbol Diffing Design

**Status:** Proposed design for review
**Date:** 2026-08-14
**Milestone:** V1 Completion — Build & Symbol Diffing (roadmap milestone 3, sequenced next)
**Scope:** Compare two completed indexed snapshots and classify meaningful changes, read-only over the existing index
**Target platform:** Windows
**Primary language:** C# / .NET 8

## 1. Purpose

This milestone turns the immutable indexed snapshots into a precise answer to *"what changed?"* — between two Schedule I builds, or between an installed API and its upstream Release/Preview.

That is the original recurring pain S1Atlas exists to remove: after a game or API update, know exactly which types, members, signatures, relationships, and (where recoverable) bodies changed, with exact provenance, so a modder or agent can re-check assumptions instead of re-reverse-engineering.

Diffing reuses the existing index and adds a **derived** comparison layer. It performs no extraction, no re-indexing, and introduces no new trust authority.

The governing principle carries forward from Milestone 1:

> **Progressive readability, not progressive disclosure of truth.** A diff may organize and summarize change, but it must never claim more certainty than the underlying index actually recovered.

## 2. Honest Fidelity Boundary (read this first)

Diff fidelity is bounded by what the underlying index recovered. This is the single most important design fact, because it prevents the tool from lying about behavioral change.

```text
Schedule I (Cpp2IL)      declaration / structural / metadata-relationship / source-text diffs only.
                         Method bodies are stubs (0 Recovered in the M1 smoke), so behavioral
                         (body / call-edge) change CANNOT be observed. A changed body is reported
                         as "body not recoverable", never as "unchanged".

Installed S1API/S1MAPI   full fidelity: real managed IL, so method-body and call-edge diffs ARE
(real managed binary)    available (method-body fingerprint present).

Release/Preview          declaration / structural / source diffs; behavioral diffs only where
(Roslyn source)          source binding resolved a target during indexing.
```

Every diff result **states its fidelity basis** per side, so no consumer reads "no body change" as "no behavior change."

## 3. Scope

### 3.1 Included

```text
pairwise diff of two completed index runs of the same codebase
deterministic symbol matching across snapshots
change classification (added/removed/signature/structural/body/relationships/source/unchanged)
explicit "body unavailable" outcome distinct from "unchanged"
relationship-set diffing by kind, resolved and unresolved
change detection driven by normalized fingerprints (never raw artifact hashes)
cross-channel "update impact" view (Installed vs Release / Preview of the same codebase)
diff CLI: whole-snapshot and single-symbol, human + JSON
bounded, counted output with --limit and --kind filtering
```

### 3.2 Deferred

```text
rename / move detection heuristics (a rename shows as Removed + Added)
semantic or behavioral change inference
automatic mod-breakage prediction
three-way / N-way / historical-timeline diff
a persistent diff authority, diff graph, or diff database
cross-codebase diff (Schedule I vs S1API are different codebases and are not comparable)
the HTML portal presentation of diffs (that is milestone 4)
```

## 4. Inputs and Authority

- Diff consumes only **completed, integrity-valid index runs**. It never triggers extraction or indexing, and never mutates a snapshot.
- Both sides must be the **same codebase** (`ScheduleI`, `S1Api`, or `S1MApi`). The **channel may differ** — that is the update-impact case — but the tool labels both sides' channel and never presents Release/Preview as Installed.
- **Raw artifact hashes are not a diff signal.** The Phase 5 finding stands: identical logical inputs produce different reconstructed bytes (fresh MVIDs), so raw SHA-256 equality/inequality says nothing about logical change. Diffs use the **normalized fingerprint layers** (`declaration`, `structural`, `method-body`, `source`) plus symbol and relationship facts.
- Diff output is **DERIVED** data with full provenance. It never overwrites or re-ranks snapshot facts.

## 5. Symbol Matching Across Snapshots

The current `symbols` row exposes `canonical_key`, `qualified_name`, `signature`, `kind`, and `body_recovery_status`. There is **no persisted `lineage_key`** (it was proposed in the ILSpy design but never shipped). Matching therefore derives its keys at diff time — consistent with the milestone-1 discipline of deriving new query facts from existing persisted facts rather than adding schema.

### 5.1 Comparison Key (exact identity)

`canonical_key` is `{codebase}:{channel}:{kind}:{rendered-signature}`, so it embeds channel. For diffing, derive a **comparison key** that strips the leading `{codebase}:{channel}:` segment:

```text
comparison_key = {kind}:{rendered-signature}
```

- Same-codebase, same-channel diff (e.g. two Schedule I / Installed builds): comparison keys are directly comparable.
- Cross-channel diff (e.g. S1API Installed vs Release): stripping channel is **required**, or the identical symbol would never match.

Two symbols with equal comparison keys are the **same logical symbol**.

### 5.2 Lineage Key (signature-change pairing)

Derive a coarser key by eliding overload-significant detail (parameters/return) from `qualified_name`:

```text
lineage_key = {kind}:{declaring-type}::{member-name}     # parameters and return elided
```

- Same `lineage_key`, differing `comparison_key` → **SignatureChanged** candidate.
- `lineage_key` only in the old side → **Removed**. Only in the new side → **Added**.

### 5.3 No rename detection

A renamed or moved symbol appears as **Removed + Added**. Sophisticated rename detection is explicitly deferred; surfacing it honestly as remove+add is acceptable for V1. Matching is deterministic and scoped to the two chosen snapshots.

## 6. Change Classification

Each symbol carries one or more layered flags, and each flag names the fact/fingerprint that produced it (its evidence):

```text
Added                 present only in the new snapshot
Removed               present only in the old snapshot
SignatureChanged      same lineage_key, different comparison_key, or a changed
                      accessibility / static / abstract / sealed / etc. modifier
StructuralChanged     structural fingerprint differs (member set / shape)
BodyChanged           method-body fingerprint differs AND both sides are Recovered
BodyUnavailable       one or both bodies are not Recovered (distinct from Unchanged)
RelationshipsChanged  the symbol's relationship set differs (see §7)
SourceChanged         normalized source fingerprint differs
Unchanged             every applicable comparison layer is equal
```

A method may be `SignatureChanged` and `RelationshipsChanged` simultaneously; classification is a set, not a single verdict. `Unchanged` is asserted **only** when every layer that applies to that kind was available and equal — never when a layer was unavailable.

## 7. Relationship Diffing

For a matched symbol, diff its relationship sets and report edges **added** and **removed**, grouped by normalized kind (`Inherits`, `ImplementsInterface`, `FieldType`, `PropertyType`, `EventType`, `ParameterType`, `ReturnType`, `Calls`, `Constructs`, `ReadsField`, `WritesField`). Resolved vs unresolved endpoints are preserved on both sides.

On Schedule I, behavioral edges (`Calls`/field access) are sparse because bodies are stubs, so relationship diffs there are dominated by metadata edges (inheritance, interface, member types) — stated honestly rather than presented as complete behavioral change.

## 8. Cross-Channel Update Impact

When `from = Installed` and `to = Release` or `Preview` of the **same codebase** (typically S1API/S1MAPI), the diff is framed as *"what changes if I update."* It is a **projected delta**, always labeled with both channels; Preview deltas are labeled unreleased. It never relabels Release/Preview as installed truth. This directly serves the agent-skill guidance to check API changes before relying on old behavior.

## 9. Persistence

Compute **on demand**. A diff between two immutable snapshots is deterministic, so it needs no authoritative storage.

- No new migration is required in this milestone; derive from existing `symbols`, `relationships`, `symbol_fingerprints`, and `source_locations`.
- An **optional** result cache is permissible only as disposable derived data keyed by `(from_index_id, to_index_id, diff_algorithm_version)`; it must be safely rebuildable and must never become a second source of truth. Do not build a diff graph or diff database.
- If profiling later shows the derived `lineage_key` is a real hotspot, a persisted `symbols.lineage_key` is a legitimate *future* additive migration — out of scope here.

## 10. CLI Surface

```text
s1atlas diff --codebase <schedule-i|s1api|s1mapi> --from <selector> --to <selector> [--json]
s1atlas diff ... --symbol <selector>            # single-symbol diff detail
s1atlas diff ... --kind <Added|Removed|SignatureChanged|...>   # filter
s1atlas diff ... --limit <n>                    # bounded, with total/returned counts
```

- `--from` / `--to` selectors accept a build id, a snapshot/index id, or a channel name; channel defaults to Installed.
- Output preserves the existing JSON envelope and reuses milestone-1 conventions: bounded results, `totalCount`/`returnedCount`, `Found N changes. Showing M.` in human output, exact IDs/signatures/evidence alongside readable names.
- Distinct, machine-stable outcome codes, reusing milestone-1 codes where they apply and adding: `SnapshotNotFound`, `NotComparable` (different codebase), plus the existing `NoCompletedIndex`, `AmbiguousSymbol`, `SymbolNotFound`.
- Every diff result carries **both** snapshot identities (build/extraction/commit + channel), the **fidelity basis** per side, and per-change evidence.

## 11. Honesty and Provenance Rules

```text
Never report Unchanged when a comparison layer was unavailable — use BodyUnavailable / Unknown.
Raw artifact hash equality is never used as an Unchanged signal (MVID nondeterminism).
FACT (a fingerprint/symbol/edge differs) is separated from DERIVED (the classification).
No behavioral interpretation of a change is emitted.
Fidelity basis is stated per codebase/channel on every result.
Release/Preview deltas are always labeled and never presented as Installed truth.
```

## 12. Failure Behavior

```text
either snapshot missing/incomplete   -> distinct error; no partial diff shown as complete
different codebases                  -> NotComparable
same snapshot on both sides          -> valid, reports all Unchanged (a useful self-check)
diff never mutates snapshots, indexes, or authority
normal human output contains no raw stack traces
```

## 13. Testing Strategy

### 13.1 Unit / Integration (fixtures, offline, no real game)

```text
comparison-key and lineage-key derivation (including channel stripping)
added / removed / signature / structural / relationship / source classification
BodyChanged vs BodyUnavailable using a real managed fixture (bodies) vs a stub fixture
cross-channel update-impact labeling and Installed-non-substitution
determinism: identical snapshot pair -> identical diff
bounded + counted + --kind-filtered output; distinct outcome codes
same-snapshot self-diff -> all Unchanged
```

### 13.2 Real Smoke

```text
diff two real Schedule I builds if two indexed builds exist; otherwise document the
  single-build limitation honestly rather than fabricating a second build
diff installed S1API/S1MAPI against its cached Release and Preview (update-impact)
record where fidelity was structural-only vs body-available
prove no snapshot/authority mutation and no network access on local diff
```

## 14. Anti-Overengineering Guardrails

```text
no rename/move heuristics
no semantic or behavioral change inference
no mod-breakage prediction
no graph database or diff-authority framework
no persistent diff store beyond an optional disposable cache
no portal / MCP / agent-skill work (later milestones)
no three-way merge, no cross-game generalization
prefer deriving diff facts from existing persisted facts over new schema
```

## 15. Definition of Done

```text
[ ] diff compares two completed, integrity-valid index runs of one codebase, read-only
[ ] symbol matching uses derived comparison/lineage keys; channel is stripped for cross-channel diff
[ ] added/removed/signature/structural/relationships/source changes are classified with evidence
[ ] BodyChanged is asserted only when both bodies are Recovered; otherwise BodyUnavailable
[ ] Unchanged is never asserted over an unavailable comparison layer
[ ] relationship-set diffs report added/removed edges by kind, resolved and unresolved
[ ] cross-channel update-impact is labeled and never presents Release/Preview as Installed
[ ] raw artifact hashes are not used as a change signal
[ ] diff is deterministic; identical inputs produce identical output
[ ] CLI whole-snapshot and single-symbol diff work in human and JSON with counts/limits/codes
[ ] no migration required, or any added migration is additive with 1..N unchanged
[ ] real smoke documents fidelity honestly, including stub-body limits
[ ] build / tests / format / hygiene gates pass
```

## 16. Hard Invariants

```text
A diff never triggers extraction or indexing and never mutates a snapshot or authority.
Only completed, integrity-valid index runs are comparable.
Both sides of a diff are the same codebase.
Raw artifact hashes are never a change signal; normalized fingerprints are.
Unchanged is never claimed over an unavailable comparison layer.
Preview/Release deltas can never masquerade as Installed truth.
Diff output is DERIVED and never overwrites indexed facts.
```

## 17. Roadmap Context

This is roadmap milestone 3 of the six V1 completion milestones, sequenced next because it reuses the shipped index and fingerprints and delivers the core update-recheck value with minimal new subsystem surface:

```text
1. Polish & Usability            complete
2. Scene Intelligence            pending (largest net-new subsystem; independent)
3. Build & Symbol Diffing        this document
4. Human Portal                  consumes the diff + query layer
5. MCP + Agent Skill             consumes the same layer; update-impact is a core agent question
6. V1 Hardening & Release
```

Milestone 4 (Portal) and Milestone 5 (MCP + Agent Skill) both present this diff through the same shared query layer rather than recomputing changes.
