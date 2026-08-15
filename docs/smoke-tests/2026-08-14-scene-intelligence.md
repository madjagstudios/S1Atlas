# Scene Intelligence Real-Install Smoke — 2026-08-15

This publication record contains aggregate, redacted evidence only. Raw commands, local paths, exact authority IDs, container names, local file IDs, scene names, per-file hashes, the Atlas database, generated manifests, and proprietary bytes remain outside the repository.

## Outcome

The production `index --scene --json` workflow was honestly blocked before parsing with `NoReplayVerifiedExtractionInput`; the existing preferred extraction was not backed by a replay-verified input snapshot. No authority row was altered to bypass that gate, no final scene index was promoted, and no production scene database rows were written.

A separate local-only, read-only harness then exercised the same input verifier, AssetsTools.NET parser adapter, normalizer, recovery classifier, and existing completed Schedule I Installed code index in memory. All selected files were static inputs. The harness did not initialize a second authority, persist normalized rows, or write manifests.

## Aggregate results

| Measurement | Result |
|---|---:|
| Supported container candidates | 9 |
| Discovered / accepted / rejected / parsed | 9 / 9 / 0 / 9 |
| Parser | `assetstools-net` `3.0.5` |
| Containers with usable TypeTree-derived facts | 0 |
| External-reference table entries | 40 |
| Scene documents / proven prefab documents | 7 / 0 |
| Asset-file roots | 0 |
| GameObjects / roots / Transforms / components | 0 / 0 / 0 / 0 |
| MonoScript object-table records / recovered identities | 4,659 / 0 |
| MonoBehaviour object-table records / normalized graph components | 104,495 / 0 |
| Exact Schedule I Installed symbol links | 0 |
| Link reasons | none; no normalized MonoBehaviour graph components were available to resolve |
| Custom MonoBehaviour `GraphOnly` components | 0 |
| Reviewed-schema field decodes / custom field-value rows | 0 / 0 |
| Serialized references / resolved GameObject / component / code-symbol targets | 0 / 0 / 0 / 0 |
| Serialized-reference resolution statuses | none; no references were decoded |
| `SceneRecovery` counts | `StubOrUnavailable=7` |
| Build-settings scene-path objects / paths | 0 / 0 |
| Scene-name source | raw container fallback only; no scene name is published here |
| Prefab class-ID evidence records | 0 |
| Prefab result | no proven prefab documents; this is not proof that the install has no prefabs |

The parser recovered class-ID/object-table denominators, including MonoScript and MonoBehaviour records, but no selected container exposed a usable embedded TypeTree-equivalent schema. Consequently the honest normalized result is document-level `StubOrUnavailable`, not invented GameObjects, fields, PPtrs, names, or links. Zero graph counts measure recovered coverage only; they do not describe the runtime game's actual graph.

## Integrity and runtime invariants

| Check | Result |
|---|---|
| Selected primary/sidecar hashes before and after | PASS — all 20 pairs matched |
| Game or Unity processes before / after | 0 / 0 |
| Child-process launch calls | 0 |
| Network calls | 0 |
| Game-install writes | 0 |
| Database writes by read-only harness | 0 |
| Copied asset/database bytes in repository | 0 |
| Published container names, local IDs, scene names, paths, or per-file hashes | 0 |

The smoke is evidence of the measured availability boundary, not evidence of complete scene or prefab coverage. Synthetic integration fixtures separately cover verified manifests, decoded graph/reference cases, exact symbol linking, migration 8, transaction rollback, final-marker promotion, bounded SQLite queries, CLI JSON, `GraphOnly`, and parser-certified prefab class-ID evidence.
