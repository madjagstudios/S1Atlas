# AT-122 reference member resolution: real-atlas measurement

Date: 2026-10-10. Status: **measured.** Baseline and corrected reference indexes
were built on an isolated copy of the real atlas from the same selected inputs,
and every count below comes from saved query results.

## Summary

- Resolved reference to game edges are unchanged between the baseline and the
  corrected resolver: 38 direct Field edges (16 reads, 22 writes), 0 direct
  Property edges, 0 direct Event edges, and 2,483 game-target edges in total.
- The baseline already contains the qualified Field/Property/Event lookup key,
  so the original type-less key bug does not show on this collection. The
  compiled real-pipeline test is the regression proof for that bug.
- The one change is the removal of first-wins selection: 324 edges that the
  baseline resolved to an arbitrary reference-mod symbol are now unresolved.
  All 324 fall on 63 target texts, and every one of those targets has exactly
  two distinct candidate symbols, so each edge is genuinely ambiguous. No edge
  moved from unresolved to resolved, and no edge changed its resolved target.
- Cache identity behaves as required: the corrected run did not reuse the
  baseline index, and an unchanged rerun reused the corrected index.

## Revisions and inputs

- Baseline: `cf815ba7` (Release 2.0.0, reference resolver version 2), built
  from a `git archive` export.
- Corrected: this branch with reference resolver version 3, measured before a
  later change that restored 2.0.0's Signature keys for game methods. The
  baseline already used those method keys, so that change does not affect the
  game-target counts below.
- Collection: selected by the owner on 2026-10-10 (recorded on Jira AT-122),
  because the atlas had no reference collection. Two installed mods from the
  game's `Mods` folder, copied to scratch space with a scratch manifest
  (collection id `at122-installed-mods`, mod ids `organizedcrime` and `s1api`,
  include `*.dll`). UnityExplorer was excluded.

| Selected input | SHA-256 (before and after both runs) |
| --- | --- |
| `OrganizedCrime.dll` | `816ae5d8dfab0bb0bf457249a51829b45eeca660de231c8ec66d6ae6f218b1f0` |
| `S1API.Il2Cpp.MelonLoader.dll` | `85b218ff0ac3d18ec03e2a959f13ce13c596d38d46ab78f7908ec9fa7533c6e3` |

| Context identity | Value |
| --- | --- |
| Build | `a9add121f99f9f00a6f23054e2f4ea4403c980e2e6ae1cf4e8df05d5620aa116` |
| Completed game index | `23bf6d58ced7091b57a9ec06c81ca50c1078dce798a0e3aa6a3c58f844381c94` |
| Baseline reference index | `ea01ae25e2eefbb8be3a9ffdc09cb6948e1e19b6f1b501a63ed78a5dd12f9c81` |
| Corrected reference index | `020cd0a39108d34edb44615b1144b0becd2f162d63f6c6b189fbcbfa78959865` |

Both reference indexes record the same build, game index and game snapshot in
`reference_index_context`.

| Run | Command | Result |
| --- | --- | --- |
| Baseline | `reference index <manifest> --force --json` | `reused: false`, 44,186 symbols, 117,280 relationships, no warnings |
| Corrected | `reference index <manifest> --json` | `reused: false`, new index ID, 44,186 symbols, 117,280 relationships, no warnings |
| Corrected rerun | `reference index <manifest> --json` | `reused: true`, same corrected index ID |

## Results

| Direct game target | Before (resolver 2) | After (resolver 3) | Delta |
| --- | --- | --- | --- |
| Field | 38 | 38 | 0 |
| Property | 0 | 0 | 0 |
| Event | 0 | 0 | 0 |

| Game-target breakdown (kind, relationship) | Before | After |
| --- | --- | --- |
| Field, ReadsField | 16 | 16 |
| Field, WritesField | 22 | 22 |
| Method, Calls | 544 | 544 |
| Method, CallsVirtual | 1,652 | 1,652 |
| Method, Patches | 132 | 132 |
| Method, ReferencesMethod | 6 | 6 |
| Constructor, Constructs | 110 | 110 |
| Constructor, Calls | 1 | 1 |

| Separate measurement | Before | After | Delta |
| --- | --- | --- | --- |
| Getter-shaped Method edges | 1,390 | 1,390 | 0 |
| Setter-shaped Method edges | 37 | 37 | 0 |
| Add-shaped Method edges | 2 | 2 | 0 |
| Remove-shaped Method edges | 1 | 1 | 0 |
| Unresolved reference-owned edges | 42,177 | 42,501 | +324 |
| Resolved targets in the game snapshot | 2,483 | 2,483 | 0 |
| Resolved targets outside the game snapshot | 72,620 | 72,296 | -324 |
| Non-null targets missing a symbol row | 0 | 0 | 0 |
| Total reference-owned edges | 117,280 | 117,280 | 0 |

Direct Property and Event counts are zero because C# property and event
operations compile to accessor method calls, which are counted separately above.

## Changed-edge assessment

Edges were paired across the two indexes by source qualified name, relationship
kind and target text, preserving multiplicity, because reference symbol IDs
depend on index identity.

- Resolved to unresolved: 324 edges on 63 distinct target texts.
- Unresolved to resolved: 0.
- For each of the 63 target texts, the corrected index has exactly two distinct
  candidate symbols: game symbols whose qualified name equals the target text
  plus reference symbols whose unprefixed signature equals it. Every newly
  unresolved edge is therefore a genuine ambiguity that the baseline resolved
  by first-wins. None of them targeted the game snapshot, which is why the
  game-target counts are unchanged.

## Counting SQL

Each query was executed read-only on the copy, once per reference index, with
`:reference_index_id` bound to that index.

```sql
WITH kinds(kind) AS (VALUES ('Field'), ('Property'), ('Event')),
resolved AS (
    SELECT r.relationship_id, target.kind
    FROM reference_index_context AS context
    JOIN index_runs AS run
      ON run.index_id = context.reference_index_id AND run.status = 'Completed'
    JOIN relationships AS r ON r.snapshot_id = context.reference_snapshot_id
    JOIN symbols AS source
      ON source.symbol_id = r.source_symbol_id
     AND source.snapshot_id = context.reference_snapshot_id
    JOIN reference_symbol_owners AS owner
      ON owner.symbol_id = source.symbol_id
     AND owner.index_id = context.reference_index_id
    JOIN symbols AS target
      ON target.symbol_id = r.target_symbol_id
     AND target.snapshot_id = context.game_snapshot_id
    WHERE context.reference_index_id = :reference_index_id
)
SELECT kinds.kind, COUNT(resolved.relationship_id) AS resolved_edges
FROM kinds LEFT JOIN resolved ON resolved.kind = kinds.kind
GROUP BY kinds.kind ORDER BY kinds.kind;
```

The breakdown uses the same scoped resolved set, projecting
`r.relationship_kind` and `target.qualified_name`, grouped by kind and
relationship kind. The accessor counts replace the final `SELECT` with:

```sql
SELECT
    COUNT(CASE WHEN kind = 'Method'
        AND instr(qualified_name, '::get_') > 0 THEN 1 END) AS getter_edges,
    COUNT(CASE WHEN kind = 'Method'
        AND instr(qualified_name, '::set_') > 0 THEN 1 END) AS setter_edges,
    COUNT(CASE WHEN kind = 'Method'
        AND instr(qualified_name, '::add_') > 0 THEN 1 END) AS add_edges,
    COUNT(CASE WHEN kind = 'Method'
        AND instr(qualified_name, '::remove_') > 0 THEN 1 END) AS remove_edges
FROM resolved;
```

These are name-shaped Method counts, not proof that every matching method is a
declared accessor. The accounting query splits every scoped edge into four
mutually exclusive buckets, which sum to the total for both indexes:

```sql
SELECT
    COUNT(*) AS total_edges,
    COUNT(CASE WHEN r.target_symbol_id IS NULL THEN 1 END) AS unresolved_edges,
    COUNT(CASE WHEN target.snapshot_id = context.game_snapshot_id
        THEN 1 END) AS resolved_game_edges,
    COUNT(CASE WHEN target.snapshot_id <> context.game_snapshot_id
        THEN 1 END) AS resolved_outside_game_edges,
    COUNT(CASE WHEN r.target_symbol_id IS NOT NULL AND target.symbol_id IS NULL
        THEN 1 END) AS missing_target_symbol_edges
FROM reference_index_context AS context
JOIN index_runs AS run
  ON run.index_id = context.reference_index_id AND run.status = 'Completed'
JOIN relationships AS r ON r.snapshot_id = context.reference_snapshot_id
JOIN symbols AS source
  ON source.symbol_id = r.source_symbol_id
 AND source.snapshot_id = context.reference_snapshot_id
JOIN reference_symbol_owners AS owner
  ON owner.symbol_id = source.symbol_id
 AND owner.index_id = context.reference_index_id
LEFT JOIN symbols AS target ON target.symbol_id = r.target_symbol_id
WHERE context.reference_index_id = :reference_index_id;
```

## Isolation

- The copy was made with SQLite's online backup API from a read-only
  connection (`quick_check` returned `ok`), plus the `builds` folder. Backups
  were not copied, and no `-wal` or `-shm` file was copied. Extraction roots
  are absolute paths into the source atlas and were only read.
- `S1ATLAS_HOME` pointed at the copy only for each indexing command and was
  unset before and after.
- The source atlas was unchanged: `index_runs` held 30 rows with the same
  latest start time before and after, and its file list (excluding `-wal` and
  `-shm`) was identical.
- The selected mod inputs had identical hashes before and after both runs. The
  copy, the scratch mods and the manifest were deleted afterwards.

Raw query outputs stay local. No database, game or mod binary, decompiled
source, manifest or local path is checked in.

## Regression verification

```text
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --artifacts-path TestResults/at122-task3-review/fresh-build --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~ReferenceModMemberPipelineTests --logger "trx;LogFileName=at122-step8-compiled-members.trx" --results-directory TestResults/at122-task3-review
Exit 0: passed 1, failed 0, skipped 0, total 1.

dotnet format S1Atlas.sln --verify-no-changes --no-restore
Exit 0: no formatting changes required.
```

The compiled-member test verifies field reads/writes and getter/setter Method
calls against persisted game IDs for two classes sharing type-less member
signatures.
