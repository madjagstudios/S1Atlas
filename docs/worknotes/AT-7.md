# AT-7 — Batch first-index persistence and control memory pressure

Branch: `feat/at-7-batch-index-persistence`. Baseline: index = 29.5 s, **5.44 GB allocated**
(see `docs/performance/2026-08-26-cold-path-baseline.md`).

## Root cause (confirmed in code)

`SqliteAtlasRepository.CompleteIndexRunAsync` (src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs)
loops `writeSet.{SourceFiles,Symbols,SourceLocations,Fingerprints,Relationships}` and calls a
per-row helper. Each helper does `connection.CreateCommand()` + fresh `CommandText` + `AddWithValue`
params + `ExecuteNonQueryAsync`. For one install (~52k symbols + ~50k relationships + locations +
fingerprints) that's **150k+ command objects created & SQL re-parsed** → the 5.44 GB and much of the
serial time.

## Approach — increment 1: prepared, reusable commands (ticket's prescribed first step)

Replace the 5 per-row helpers with 5 batch helpers. Each: create ONE `SqliteCommand`, add typed
parameters ONCE, `Prepare()`, then loop setting `.Value` per row and `ExecuteNonQuery`. Reuse the
compiled statement across all rows.

- **Transaction boundary unchanged** — same single transaction, same commit/rollback, same
  `index_runs` completion update.
- **Deterministic order unchanged** — same collections, same foreach order.
- **Output byte-identical** — same INSERTs, same values/order.
- Kills per-row command allocation + SQL re-parse → large allocation + time drop.

Deferred (evaluate after measuring increment 1): bounded chunk sizing, WAL/pragma tuning,
lower-copy representations, streaming. Sub-phase timing to attribute commit vs parse is AT-20.

## Safety / tests

- Existing `tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryIndexingTests.cs` covers this path
  (8 facts incl. rollback/duplicate). Must stay green — primary regression net.
- Insert* helpers are private to Indexing.cs (no external callers) — safe to replace.
- Add a test asserting a mid-batch failure rolls back the whole write set (no partial authoritative
  index) if not already covered.

## Verify

- `dotnet test tests/S1Atlas.Storage.Tests` green; then full `dotnet test`.
- Re-run `index --performance` and compare allocated bytes + wall vs the 5.44 GB / 29.5 s baseline.
- `dotnet format` + hygiene gate + code-map regen (was stale: generator v0.2.0 + `-github.md`).

## Result — increment 1 (prepared commands)

Forced re-index, same box, single run each:

| Metric | Before | After | Δ |
|---|---:|---:|---:|
| Wall | 29,464 ms | 22,485 ms | **−24% (−7.0 s)** |
| CPU | 24,484 ms | 22,078 ms | −10% |
| Allocated | 5.44 GB | 5.22 GB | −4% (−223 MB) |
| Output | 52,297 sym / 50,034 rel | identical | ✓ |
| Tests | — | 130/130 Storage green | ✓ |

**Key finding:** the wall-time win is real and worthwhile, but the 5.44 GB allocation is
**parse/build-dominated, not persistence-dominated** — eliminating 150k command objects moved
allocation only 4%. So the "control memory pressure" half of AT-7 is NOT addressed by prepared
commands; it needs the streaming/lower-copy path (build symbols → persist incrementally instead of
materializing the whole write set + parsing all source in memory), which is a larger, separate change.
Precise attribution (parse vs build vs persist) is blocked on AT-20 sub-phase timers.

**Recommendation:** ship increment 1 (the 24% win) as its own PR; scope the memory-pressure work
(streaming) as a follow-up under AT-3, informed by AT-20.

## Decisions / gotchas

- Typed params: strings/enum→SqliteType.Text, bool/int/long→SqliteType.Integer, nullables→DBNull.Value.
- `IsBestEffort` bool → 0/1; `BodyRecoveryStatus?` → `.ToString()` or NULL (matches prior behavior).
