# Build Diffing Design Specification

**Status:** Approved design
**Date:** 2026-08-15
**Parent spec:** [S1Atlas V1 Design Specification](2026-08-12-s1atlas-design.md), section 10

## 1. Purpose

Given two indexed builds, compute and report factual per-symbol changes using
V1 classifications. A method with an unchanged declaration is still reported
when its body references or call relationships changed. The diff reports facts
only and never concludes that a specific mod is broken.

Everything runs offline through the full integrity-verifying API. The diff
engine reads existing normalized symbols, fingerprints, and relationships per
build — it never re-decompiles.

## 2. Command Surface

```
diff <id-a> <id-b> [--codebase <id>] [--channel <id>] [--kind <symbol-kind>] [--limit <n>] [--json]
```

- **`<id-a>`** and **`<id-b>`** — required positional arguments. Build A is
  the baseline ("before"), Build B is the target ("after"). The meaning of
  each identifier depends on the codebase and channel — see the resolution
  contract below.
- **`--codebase`** — `schedule-i` (default), `s1api`, or `s1mapi`. Same
  parsing as existing query commands.
- **`--channel`** — `installed` (default). Schedule I only supports
  `installed`. Release and preview channels are not supported for V1 diffing
  (see section 3.1).
- **`--kind`** — optional filter: `type`, `method`, `constructor`, `field`,
  `property`, `event`. When set, only symbols of that kind appear in the
  changed-symbols list and all classification counts reflect the filtered
  view. The `totalSymbolsA` and `totalSymbolsB` totals are always unfiltered.
  The kind filter is passed into `BuildDiffService` so that per-kind
  unchanged counts are computed correctly (see section 5).
- **`--limit`** — maximum changed symbols listed in output. Default 50,
  consistent with other query commands. Must be greater than zero; zero or
  negative values are rejected with exit 1 and code `InvalidLimit`.
  `CountsByClassification` is always complete regardless of limit.
- **`--json`** — standard schemaVersion-1 envelope output.

### 2.1 Source Identifier Resolution Contract

The positional arguments are **source identifiers** whose meaning depends on
the codebase and channel:

| Codebase | Channel | Identifier | Format | Resolution |
|---|---|---|---|---|
| ScheduleI | Installed | Build ID | 64-char hex | Build → preferred validated extraction → extraction ID → find completed index whose `code_snapshot.source_identity` equals the extraction ID |
| S1API / S1MAPI | Installed | Build ID | 64-char hex | Build → find the latest completed index whose `code_snapshot.environment_snapshot_id` matches any `environment_snapshot` belonging to that build, for the requested API codebase and `Installed` channel |

Each step may fail independently. The error code reflects the first failure
in the chain.

### 2.2 New Repository Methods

Two new query methods on `IIndexRepository`:

- **`GetLatestCompletedIndexBySourceIdentityAsync(codebase, channel,
  sourceIdentity, ct)`** — joins `index_runs` to `code_snapshots`, filters
  by codebase, channel, `source_identity`, and `status = 'Completed'`,
  returns the most recently completed index. Used for Schedule I resolution.

- **`GetLatestCompletedIndexForBuildAsync(codebase, channel, buildId, ct)`**
  — joins `index_runs` to `code_snapshots` to `environment_snapshots`,
  filters by codebase, channel, and `status = 'Completed'`, matching any
  `environment_snapshot` whose `build_id` equals the given build ID. Returns
  the most recently completed index across all matching environment snapshots.
  Used for API installed resolution.

### 2.3 Error Cases

| Condition | Exit | Code |
|---|---|---|
| Unknown build ID (not in `builds` table) | 1 | `BuildNotFound` |
| No preferred validated extraction for the build (Schedule I) | 1 | `NoPreferredExtraction` |
| No environment snapshot for the build (API installed) | 1 | `NoEnvironmentSnapshot` |
| No completed index for the resolved codebase/channel/identity | 1 | `NoCompletedIndex` |
| Both source identifiers resolve to the same index ID | 1 | `SameIndex` |
| `--channel release` or `--channel preview` | 1 | `UnsupportedChannel` |
| `--limit` is zero or negative | 1 | `InvalidLimit` |

## 3. Evidence Model

### 3.1 Evidence Availability by Index Type

Installed-channel indexes (Schedule I, S1API, S1MAPI) are built from
decompiled managed assemblies and have full evidence:

| Evidence | Installed indexes |
|---|---|
| `declaration` fingerprint (SHA-256 of Signature) | All symbols |
| `structural` fingerprint (SHA-256 of Kind + QualifiedName + Signature) | All symbols |
| `method-body` fingerprint (SHA-256 of sorted IL reference targets) | Methods/constructors with recovered body AND at least one IL reference |
| `source` fingerprint (SHA-256 of decompiled source line) | Symbols with source locations |
| Relationships (all 11 kinds) | All symbols |

Cached-source indexes (S1API/S1MAPI release and preview channels) are built
from raw upstream `.cs` files via Roslyn. They produce only `SourceLine`
fingerprints (raw text, not hashed) and no relationships. They lack
`declaration`, `structural`, `method-body`, and `source` fingerprints.

**V1 comparison precondition:** both indexes must be installed-channel. The
diff command rejects `--channel release` and `--channel preview` with error
code `UnsupportedChannel`. Cached-source index diffing can be added in a
future milestone once a reduced classification model is defined.

### 3.2 Missing Body Evidence

A method or constructor present in an installed index may lack a `method-body`
fingerprint for two distinct reasons:

1. **Known empty reference set** — `BodyRecoveryStatus` is `Recovered` but
   the method body contains zero IL references. The fingerprint service skips
   creating a `method-body` fingerprint when `evidence.Count == 0`
   (`SymbolFingerprintService.cs:20`). The body is known; it simply has no
   outgoing references.

2. **Body unavailable** — `BodyRecoveryStatus` is `StubOrUnavailable`,
   `NoBodyByDesign`, or `Unknown`. No body was recovered, so no evidence
   exists. The absence of a fingerprint represents missing data, not a known
   empty set.

These two cases must be handled differently at diff time. The diff engine
uses `BodyRecoveryStatus` from the `IndexSymbolRecord` to distinguish them:

| Build A | Build B | Result |
|---|---|---|
| Has fingerprint | Has fingerprint | Compare hashes |
| Has fingerprint | Missing, Recovered | MethodBodyChanged (reference set became empty) |
| Has fingerprint | Missing, body unavailable | Skip (evidence unavailable in B) |
| Missing, Recovered | Has fingerprint | MethodBodyChanged (reference set became non-empty) |
| Missing, Recovered | Missing, Recovered | Skip (both empty, no change detectable) |
| Missing, Recovered | Missing, body unavailable | Skip (evidence unavailable in B) |
| Missing, body unavailable | Any | Skip (evidence unavailable in A) |

"Skip" means this rule does not match; the symbol falls through to
RelationshipsChanged or Unchanged. This ensures that unavailable evidence
never produces a factual body-change claim.

### 3.3 Method-Body Fingerprint Limitations

The `method-body` fingerprint hashes sorted IL reference targets — the set of
`"ReferenceKind:Target"` strings extracted from the recovered method body
(e.g., `"MemberReference:System.Console.WriteLine"`). It detects changes to:

- Which methods/constructors are called
- Which fields are read or written
- Which types are constructed

It does **not** detect changes to:

- Constant values, literals, or string arguments
- Control flow (branching, loops, exception handling)
- Local variable declarations or arithmetic
- Method body changes that preserve the same reference set

A method whose control flow changes but whose call/field reference set remains
identical is classified as Unchanged. This is a known V1 limitation. The
`source` fingerprint (decompiled source line hash) could provide a secondary
signal but is not used for classification because it is sensitive to ILSpy
version drift and decompiler formatting changes.

## 4. Change Classification

Each symbol receives exactly one classification, determined by the first
matching rule in priority order:

| Priority | Classification | Condition |
|---|---|---|
| 1 | **Added** | `CanonicalKey` exists in Build B but not Build A |
| 2 | **Removed** | `CanonicalKey` exists in Build A but not Build B |
| 3 | **MethodBodyChanged** | Present in both; `method-body` fingerprint differs or is asymmetrically present with known evidence (see section 3.2). Only applicable to Method/Constructor kinds. |
| 4 | **RelationshipsChanged** | Present in both; method-body evidence matches or is not applicable; the normalized relationship hash differs |
| 5 | **Unchanged** | Present in both; all applicable evidence matches |

### 4.1 Why DeclarationChanged Is Not in V1

The V1 design spec (section 10) lists "Signature changed" as a
classification. In the current indexing model, this classification is
**unreachable**:

- **Methods/constructors:** The `Signature` field is the output of
  `CanonicalSignatureRenderer.RenderMethod()`, which produces
  `Type::Name(ParamTypes):ReturnType`. The `CanonicalKey` embeds this same
  rendered string (`Codebase:Channel:Kind:{RenderMethod(...)}`). Therefore
  the `declaration` fingerprint (hash of Signature) can only differ when the
  canonical key also differs — which produces Added + Removed, never
  DeclarationChanged.

- **Types:** The `Signature` field is `type.FullName`, and the canonical key
  is `Codebase:Channel:Type:{RenderType(FullName)}`. For non-generic,
  non-nested types these are identical; for all types they co-vary.

- **Fields/properties/events:** The `Signature` field is a canonical
  rendering that feeds directly into the canonical key.

In all cases, a change to the Signature produces a different CanonicalKey,
so the symbol is classified as Added + Removed. No combination of inputs
produces a stable CanonicalKey with a changed declaration fingerprint.

Accessibility modifiers (public/private), static/virtual/sealed/abstract,
base class, and interface implementations are **not** currently captured in
either the Signature or the CanonicalKey. They cannot produce
DeclarationChanged because they are not indexed.

**Future enhancement:** A later milestone can enrich the Signature field to
include modifiers and declaration details beyond the structural identity.
This would make DeclarationChanged reachable while keeping the CanonicalKey
as a stable identity key. Old indexes would lack this enriched data and
would need reindexing.

### 4.2 Relationship Change Detection

At diff time, for each symbol present in both builds, the engine collects the
symbol's outgoing relationships as `(Kind, TargetCanonicalKey ?? TargetText)`
tuples. When a relationship's `TargetSymbolId` is set, it is resolved to
`TargetCanonicalKey` via the index's SymbolId → CanonicalKey lookup. When
only `TargetText` is available (unresolved target), the raw text is used.

The tuples are sorted deterministically and SHA-256 hashed. If the hash
differs between builds, the symbol is classified as RelationshipsChanged
(assuming higher-priority rules did not already match).

All 11 relationship kinds are included in the comparison: Inherits,
ImplementsInterface, FieldType, PropertyType, EventType, ParameterType,
ReturnType, Calls, Constructs, ReadsField, WritesField.

**Resolution-status edge case:** if a target is unresolved in Build A
(`TargetText` only) but resolved in Build B (`TargetSymbolId` →
`TargetCanonicalKey`), the hash inputs may differ even if the logical target
is the same entity. This is correct V1 behavior — the relationship evidence
genuinely changed.

### 4.3 Edge Cases

- A symbol with `IsBestEffort = true` is diffed normally.
- A symbol with no outgoing relationships in either build hashes to the same
  empty-set value in both and falls through to Unchanged.

## 5. Architecture

### New components

**Repository additions** (`IIndexRepository`):

- `GetCompletedFingerprintsAsync(string indexId, CancellationToken)` —
  returns all `IndexFingerprintRecord` rows for a completed index.
- `GetLatestCompletedIndexBySourceIdentityAsync(CodebaseKind, CodeChannel,
  string sourceIdentity, CancellationToken)` — finds the latest completed
  index matching the given source identity.
- `GetLatestCompletedIndexForBuildAsync(CodebaseKind, CodeChannel,
  string buildId, CancellationToken)` — finds the latest completed API
  installed index for a specific game build, joining through
  `environment_snapshots`. Matches any environment snapshot belonging to
  the build, not a specific one.

**Diff engine** (`S1Atlas.Indexing/Diff/BuildDiffService.cs`):

- Takes two index IDs and an optional kind filter, bulk-loads symbols,
  fingerprints, and relationships for each, performs the join-and-classify
  logic, returns a `BuildDiffResult`.
- The kind filter is applied during classification so that per-kind counts
  (including Unchanged) are computed correctly. The service classifies all
  symbols but only counts and returns those matching the filter.
- The service does not resolve build IDs to index IDs. That responsibility
  belongs to the CLI command, which has access to both the index and
  extraction repositories.

**Domain models** (`S1Atlas.Core/Indexing/DiffModels.cs`):

```csharp
public enum DiffClassification
{
    Added,
    Removed,
    MethodBodyChanged,
    RelationshipsChanged,
    Unchanged
}

public sealed record SymbolDiff(
    string CanonicalKey,
    string QualifiedName,
    string Kind,
    DiffClassification Classification,
    string? SignatureBefore,
    string? SignatureAfter);

public sealed record BuildDiffResult(
    string IndexIdA,
    string IndexIdB,
    string Codebase,
    string Channel,
    int TotalSymbolsA,
    int TotalSymbolsB,
    IReadOnlyDictionary<DiffClassification, int> CountsByClassification,
    IReadOnlyList<SymbolDiff> Changes);
```

**CLI command** (`S1Atlas.Cli/Commands/DiffCommand.cs`):

- Resolves source identifiers to index IDs using the resolution contract
  in section 2.1.
- Calls `BuildDiffService` with the kind filter.
- Applies `--limit` to the returned Changes list.
- Formats human-readable and JSON output.

### Data flow

```
CLI: parse args, validate codebase/channel/limit
     ↓
CLI: resolve id-a → index ID A (per resolution contract)
CLI: resolve id-b → index ID B
CLI: verify index A ≠ index B
     ↓
BuildDiffService.DiffAsync(indexIdA, indexIdB, kindFilter?):
  1. Load all symbols for index A (keyed by CanonicalKey)
  2. Load all symbols for index B (keyed by CanonicalKey)
  3. Load all fingerprints for index A (keyed by SymbolId → Kind → Hash)
  4. Load all fingerprints for index B
  5. Load all relationships for index A (grouped by SourceSymbolId)
  6. Load all relationships for index B
  7. Build SymbolId → CanonicalKey lookup for each index
  8. Join by CanonicalKey, classify each symbol per section 4
  9. Apply kind filter: count all classifications, collect non-Unchanged
  10. Sort changes: by classification priority, then QualifiedName ascending
  11. Return BuildDiffResult (Changes excludes Unchanged; counts include all)
     ↓
CLI: apply --limit to Changes (counts remain complete)
CLI: format output
```

### No new migration

The diff is computed from existing indexed data. The `symbol_fingerprints`
table already stores all fingerprint kinds. The only repository changes are
adding read methods for fingerprints and the two new index-resolution queries
on `IIndexRepository`, implemented in `SqliteAtlasRepository.Indexing.cs`.

## 6. Output Format

### 6.1 Human-readable

```
Build diff: a1b2c3d4...e5f6g7h8 (before) → f7g8h9i0...j1k2l3m4 (after)
Codebase: ScheduleI  Channel: Installed

  Added:                 42
  Removed:               18
  Method body changed:   31
  Relationships changed:  5
  Unchanged:          4,898
  ─────────────────────────
  Total (before):     4,994
  Total (after):      5,018

Changed symbols (50 of 96):

  [Added]      Method    NewNamespace.NewClass.DoThing
  [Removed]    Method    OldNamespace.Deprecated.Run
  [BodyChange] Method    Employee.Fire
  [RelChange]  Type      EmployeeManager
```

Build IDs are truncated (first 8 + `...` + last 8) for readability.

### 6.2 JSON

```json
{
  "schemaVersion": 1,
  "command": "diff",
  "success": true,
  "exitCode": 0,
  "data": {
    "identifierA": "<source-id-as-provided>",
    "identifierB": "<source-id-as-provided>",
    "indexIdA": "<resolved-index-id>",
    "indexIdB": "<resolved-index-id>",
    "codebase": "ScheduleI",
    "channel": "Installed",
    "totalSymbolsA": 4994,
    "totalSymbolsB": 5018,
    "counts": {
      "added": 42,
      "removed": 18,
      "methodBodyChanged": 31,
      "relationshipsChanged": 5,
      "unchanged": 4898
    },
    "totalChanged": 96,
    "returnedCount": 50,
    "changes": [
      {
        "canonicalKey": "ScheduleI:Installed:Method:...",
        "qualifiedName": "NewNamespace.NewClass.DoThing",
        "kind": "Method",
        "classification": "Added",
        "signatureBefore": null,
        "signatureAfter": "System.Void NewNamespace.NewClass::DoThing()"
      }
    ]
  },
  "error": null
}
```

### 6.3 Output Semantics

- **`changes`** contains only non-Unchanged symbols. Unchanged symbols
  contribute to `counts.unchanged` but never appear in the list.
- **`signatureBefore`** is null for Added; **`signatureAfter`** is null for
  Removed. Both null for MethodBodyChanged, RelationshipsChanged, and
  Unchanged (signatures are identical in the current indexing model).
- **`--kind`** filters both `changes` and all entries in `counts`, including
  `unchanged`. The filter is applied inside `BuildDiffService` so that
  Unchanged counts are accurate per-kind. The `totalSymbolsA` and
  `totalSymbolsB` fields are always unfiltered. `totalChanged` reflects the
  filtered count.
- **`--limit`** truncates `changes` only. `counts`, `totalChanged`,
  `totalSymbolsA`, and `totalSymbolsB` are unaffected by limit.
- **Sort order** in `changes`: classification priority (Added first, then
  Removed, MethodBodyChanged, RelationshipsChanged), then `QualifiedName`
  ascending within each classification. This order is deterministic and
  stable.

## 7. Testing Strategy

### Unit tests (`S1Atlas.Indexing.Tests/Diff/BuildDiffServiceTests`)

Core classification tests (one per classification):

- Added, Removed, MethodBodyChanged, RelationshipsChanged, Unchanged.

Priority and edge-case tests:

- Method without method-body fingerprint in either build, both with
  `Recovered` status → skips MethodBodyChanged, falls through.
- Method with method-body fingerprint in one build, other build has
  `Recovered` status and no fingerprint → MethodBodyChanged (asymmetric
  known evidence).
- Method with method-body fingerprint in one build, other build has
  `StubOrUnavailable` status and no fingerprint → skips MethodBodyChanged
  (unavailable evidence does not produce a factual change claim).
- Parameter-type change → Added + Removed pair (different canonical keys).
- Unresolved relationship target (TargetText only) compared against
  resolved target → RelationshipsChanged.
- Kind filter: when filtering to `Method`, unchanged types are excluded
  from `counts.unchanged`.
- Deterministic sort: results are ordered by classification priority then
  QualifiedName.

All tests use generated `IndexSymbolRecord`, `IndexFingerprintRecord`, and
`IndexRelationshipRecord` fixtures. No decompilation, no proprietary bytes,
no network.

### Integration tests (`S1Atlas.IntegrationTests/Diff/DiffCliUsabilityTests`)

- Happy path: seed two Schedule I indexes into real SQLite, run
  `diff <a> <b> --json`, verify envelope, counts, and changes.
- Human output: same seed, verify readable format.
- Build resolution: seed builds, extractions, and indexes; verify that
  build ID → extraction ID → index ID resolution works end-to-end.
- API installed resolution: seed environment snapshots and API indexes;
  verify build ID → environment snapshot → API index resolution.
- Error cases: unknown build, no preferred extraction, no completed index,
  same index ID, unsupported channel.
- `--kind` filtering: verify counts (including unchanged) and changes both
  reflect the filter.
- `--limit` capping: verify changes are truncated but counts are complete.

## 8. Scope Boundary

This milestone delivers:

- The `diff` CLI command with human and JSON output.
- `BuildDiffService` in `S1Atlas.Indexing`.
- Fingerprint read queries and index-resolution queries on `IIndexRepository`.
- Domain models in `S1Atlas.Core`.
- Unit and integration tests.
- README updates (command table, examples, milestone status).

This milestone does not deliver:

- **DeclarationChanged classification** — the current indexing model produces
  identical Signature and CanonicalKey for all symbol kinds, making this
  classification unreachable. A future milestone can enrich the Signature
  field with modifier/accessibility data to enable it (see section 4.1).
- Release/preview channel diffing (cached-source indexes lack the evidence
  model for structural classification; a reduced-capability diff using
  `SourceLine` fingerprints can be added in a future milestone).
- Source comparison between builds for changed methods (belongs to the
  human portal milestone).
- `--symbol` filtering to a single symbol (can be added later without
  breaking changes).
- Diff result persistence or caching.
- Automatic mod breakage prediction.
- Any network access or new migration.
