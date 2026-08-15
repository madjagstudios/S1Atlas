# Scene Intelligence Real-Install Smoke — 2026-08-15

This publication record contains aggregate, redacted evidence only. Raw commands, local paths, exact authority IDs, container names, local file IDs, scene names, per-file hashes, the Atlas database, generated manifests, and proprietary bytes remain outside the repository.

## Outcome

The production `index --scene --json` workflow was honestly blocked before parsing with `NoReplayVerifiedExtractionInput`; the existing preferred extraction was not backed by a replay-verified input snapshot. No authority row was altered to bypass that gate, no final scene index was promoted, and no production scene database rows were written.

A separate local-only, read-only harness then exercised the same input verifier, AssetsTools.NET parser adapter, normalizer, recovery classifier, and existing completed Schedule I Installed code index in memory. All selected files were static inputs. The harness did not initialize a second authority, persist normalized rows, or write manifests.

## Ordered continuation from `NoReplayVerifiedExtractionInput`

The scene smoke remains blocked until the preferred extraction for this exact build is backed by replay-certified input. The following continuation is reproducible without publishing any local value. Commands that create or certify extraction input launch Cpp2IL and are therefore **not part of this no-child-process scene smoke**; they require a separately authorized offline extraction-maintenance session. If that authority is not granted, stop after step 2 and retain `NoReplayVerifiedExtractionInput` as the honest result.

1. With Schedule I and Unity closed and network access denied, run `s1atlas status --json`, `s1atlas env --json`, `s1atlas tools status cpp2il --json`, `s1atlas extractions list --build <build-id> --json`, and `s1atlas extractions show <preferred-extraction-id> --json`. Confirm locally that the selected build still resolves to the same actual `Schedule I_Data` install, the preferred extraction is integrity-verified, and the managed tool pin is verified. Keep every returned path, name, ID, digest, manifest, and database outside Git.
2. Retry `s1atlas index --scene --build <build-id> --force --json`. If it still returns `NoReplayVerifiedExtractionInput`, do not edit authority rows, substitute another build, or infer certification. Record the block and proceed only through the separately authorized extraction-maintenance steps below.
3. If no input snapshot from that same live install/build is available, the extraction operator runs `s1atlas extract --build <build-id> --snapshot-inputs --retry --json`. Retain the full output locally and require `processWasRun=true`, `validationWasRun=true`, `authoritative=true`, `inputSource=Live`, a non-null `inputSnapshotId`, and `inputSnapshotReplayVerified=false`. This step creates an archive but does not certify it.
4. The extraction operator then runs `s1atlas extract --build <build-id> --input-snapshot <input-snapshot-id> --retry --json`. Require `processWasRun=true`, `validationWasRun=true`, `authoritative=true`, `inputSource=ArchivedSnapshot`, the exact same locally retained input-snapshot ID, and `inputSnapshotReplayVerified=true`. Any other result leaves the scene smoke blocked.
5. Run `s1atlas extractions show <certifying-extraction-id> --json`; require `integrityVerified=true` and correlate its build/source-attempt facts locally with the certifying command output. If it is not preferred, a separately authorized authority change must run `s1atlas extractions promote <certifying-extraction-id> --json`, followed by `s1atlas extractions show <certifying-extraction-id> --json` confirming `preferred=true`. Promotion changes Atlas authority only; it is not a scene-smoke shortcut and must never cross builds.
6. End the extraction-maintenance session. Reconfirm that Schedule I, Unity, and extraction child processes are absent; keep network denied. Re-run `status`, `env`, and preferred-extraction checks from step 1 against the same actual install/build. Hash the selected supported containers and sidecars before indexing, retaining only local per-file evidence.
7. Run `s1atlas index --scene --build <build-id> --force --json`. The production authority gate itself is the final certification check. A repeated authority or integrity error remains a block; do not fall back to the supplementary harness or promote partial output.
8. If indexing succeeds, retain the full snapshot locally and run bounded `s1atlas scenes --snapshot <scene-snapshot-id> --json`, `s1atlas scene <local-exact-selector> --children --components --refs --json`, and `s1atlas component <local-component-id> --refs --code --json` queries. Review the parser's locally retained TypeTree-equivalent facts against decoded object boundaries, standard GameObject/component/Transform attachments, hierarchy PPtrs, MonoScript identity, custom reference field paths/types, and exact code-link evidence. Strings, class IDs, or reconstructed code members alone are not field-schema evidence.
9. Re-hash the same selected inputs and compare the complete before/after denominator. Confirm zero game/Unity launches, zero child-process launch calls during the scene smoke, zero network calls, and zero game-install writes. Keep raw parser output, query rows, database/manifests, bytes, names, paths, IDs, and hashes outside Git.

`StubOrUnavailable` can advance only when parser-certified bytes and a usable embedded or otherwise reviewed TypeTree-equivalent schema establish readable object boundaries. A decoded standard GameObject/MonoBehaviour/MonoScript/Transform attachment and hierarchy graph, without a reviewed custom-field schema, advances the applicable component to `GraphOnly`; it does not authorize custom values. `PartiallyRecovered` requires actual supported fields with incomplete required coverage, and `FullyRecovered` requires actual supported fields with all required fields complete. Mere TypeTree presence, symbol-name similarity, raw strings, or zero-valued guesses do not advance recovery.

Only aggregate counts may be added to this record: candidate/accepted/rejected/parsed containers; parser identity/version; scene, prefab, asset-root, GameObject, root, Transform, component, and MonoScript denominators; exact-link totals and aggregate link reasons; `GraphOnly` and reviewed-schema availability; reference totals by target kind/resolution status; recovery-status totals; complete hash-match denominator; process/network/write checks; and aggregate scene-name-source and prefab-evidence outcomes. No exact paths, container/scene/type names, IDs, bytes, databases, manifests, raw rows, per-file hashes, or proprietary data may be published.

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
