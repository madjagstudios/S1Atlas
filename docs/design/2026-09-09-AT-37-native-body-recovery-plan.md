# AT-37 — Focused IL2CPP Native-Body Recovery Implementation Plan

**Goal:** Give S1Atlas a bounded, read-only native-body recovery path that maps a stubbed IL2CPP managed method to its native `GameAssembly.dll` address and returns focused, provenance-stamped direct-call and field-access evidence — or a precise reason recovery is unavailable.

**Architecture:** Implement the already-defined `INativeBodyRecoveryProvider` seam with an in-process provider built on the pinned `Samboy063.LibCpp2IL` library (managed-symbol ↔ native-pointer mapping) and `Iced` (bounded x86-64 decode). The existing `NativeRecoveryWorkflow` validates, normalizes, sanitizes, and integrity-stamps the result; a new CLI command drives live recovery and persists it, and existing seam investigation (CLI `investigate-seam`, MCP `investigate_seam`) surfaces persist-then-read evidence on both surfaces. No subprocess, no raw disassembly persisted, no game binary copied.

**Tech Stack:** C# / .NET 8, `Samboy063.LibCpp2IL` 2022.1.0-pre-release.21 (MIT), `Iced` 1.21.0 (MIT), SQLite (existing storage), existing extraction/discovery/authority services.

## Global Constraints

- **Provider identity model:** pinned-library identity, not an executable. `ToolName`, `ToolVersion`, `ToolSha256` describe the pinned LibCpp2IL + Iced libraries. `ToolSha256` **must be exactly 64 lowercase hex characters** (workflow `RequireSha256`), computed as SHA-256 over a canonical library descriptor.
- **Pinned dependencies (exact):** `Samboy063.LibCpp2IL` = `2022.1.0-pre-release.21`; `Iced` = `1.21.0`. Both MIT. Restored via committed `packages.lock.json`, CI restores with `--locked-mode`. These are build-time dependencies, **not** runtime tool downloads.
- **Evidence sanitization (verbatim, from `NativeRecoveryWorkflow.SanitizeSummary`):** every `MappingEvidence`, `FieldAccesses`, and each edge's `SourceMethodPointer` / `TargetMethodPointer` / `TargetText` / `Kind` / `Evidence`, and `FailureMessage` must, after `Trim()`: be non-blank; be ≤ 512 chars; contain no `\0`; contain no `"://"`; contain no `\`; contain no `/`; contain no `".bin"` (case-insensitive); contain no `"disassembly"` (case-insensitive). Violations become `Status = Failed`, message "Native recovery provider returned invalid evidence." **Managed names must therefore render nested types with `.` or `+`, never `/`.**
- **Direct-edge rule (verbatim):** only `Kind == "DirectCall"` (ordinal) with a non-null `TargetMethodPointer` survives as a direct edge. Every other kind (`IndirectDispatch`, `RuntimeDispatch`, `CrossThreadDispatch`, targetless, unknown) is rewritten to `Kind = "UNKNOWN"`, `TargetMethodPointer = null`, `IsComplete = false`.
- **Traversal budget:** integer `1..500` inclusive (`MaxTraversalEdges`); truncation marks the record incomplete.
- **Read-only:** never launch or mutate the game. Recovery reads bytes from `GameAssembly.dll` + `global-metadata.dat` only. Raw disassembly is consumed in memory and never persisted.
- **Determinism:** identical inputs reproduce identical `OutputSha256` and `RecoveryId`. `LibCpp2IlMain` is global static state — access is single-flighted and the loaded binary is cached keyed by `(gameAssemblySha256, metadataSha256)`.
- **Unity version:** the codebase does not probe the engine version from the binary; scene indexing hard-codes the `2022.3.62` family. The provider passes a known supported `UnityVersion` and returns a negative result (not wrong data) if the build's metadata is incompatible.

---

## File Structure

New project **`src/S1Atlas.NativeRecovery/`** isolates the reverse-engineering library dependencies (LibCpp2IL, Iced) from the rest of the solution. It references `S1Atlas.Core` (models) and `S1Atlas.Indexing` (the `INativeBodyRecoveryProvider` interface).

- `src/S1Atlas.NativeRecovery/S1Atlas.NativeRecovery.csproj` — the only project referencing LibCpp2IL + Iced.
- `src/S1Atlas.NativeRecovery/LibCpp2IlNativeBodyRecoveryProvider.cs` — implements `INativeBodyRecoveryProvider`.
- `src/S1Atlas.NativeRecovery/Il2CppImageCache.cs` — single-flight load + hash-keyed cache of `LibCpp2IlMain` global state.
- `src/S1Atlas.NativeRecovery/ManagedSymbolResolver.cs` — S1Atlas symbol id → `Il2CppMethodDefinition`, with ambiguity detection.
- `src/S1Atlas.NativeRecovery/BoundedNativeDecoder.cs` — Iced decode loop: emits `DirectCall`/`UNKNOWN` edges + field-offset accesses within budget.
- `src/S1Atlas.NativeRecovery/NativeNameNormalizer.cs` — slash-free managed name + hex-VA formatting helpers.
- `src/S1Atlas.NativeRecovery/LibraryToolIdentity.cs` — computes the 64-hex `ToolSha256` from the pinned-library descriptor.
- `config/native-recovery/libraries.json` — reviewed pinned-library tool definition (identity + license), read at composition time.
- `src/S1Atlas.Cli/CliApplication.cs` — composition root; see the as-built notes on `NativeRecoveryComposition`.
- `src/S1Atlas.Cli/Commands/RecoverNativeBodyCommand.cs` — CLI write-path command that runs recovery and persists.
- `docs/design/2026-08-29-native-recovery-provenance.md` — amended to bless pinned-library provider identity.
- Test projects: `tests/S1Atlas.NativeRecovery.Tests/` (unit), and integration coverage under `tests/S1Atlas.IntegrationTests/`.

---

## Decisions Log

- **Provenance identity:** Option 1 — pinned-library identity; `ToolSha256` = SHA-256 over the library descriptor. Provenance doc amended.
- **SETTLED (AT-39):** Field-offset → field-name resolution: **resolve names** (~92% hit rate on the validation method). Format `"this.<name> @ 0x<hex>"` when resolved, else `"field @ 0x<hex>"`.
- **SETTLED (AT-39):** Decode termination: stop at the first of **`ret` / instruction-or-edge budget / max-byte cap**; only reaching `ret` leaves `IsComplete=true`.
- **SETTLED (AT-39):** Traversal-budget division across symbols: **shared pool** (single decrementing budget), not a per-symbol split — edge density varies too much for an even split.
- **SETTLED (AT-39):** `LibCpp2IlMain.Reset()` is **not strictly required** between loads (verified: identical `MethodPointer` across repeated loads), but `Il2CppImageCache` calls it **defensively before each (re-)initialization** since the different-image case was untested and the cost is negligible.
- **SETTLED (AT-39) — API correction:** call-target resolution uses **`GetManagedMethodImplementationsAtAddress(ulong)`** (returns a `List`; 0/1/>1 ⇒ None/Single/Ambiguous), **not** `GetMethodDefinitionByGlobalAddress` (returns null universally). See the `IAddressResolver` consumed by `BoundedNativeDecoder`.

## As-built notes

Real-build verification against the installed Schedule I game surfaced four bugs the unit tests did not catch, each fixed in its own commit:

- **Persistence linkage via `validated_extractions`:** `SaveNativeRecoveryAsync`'s precondition originally could not join a native recovery record back to its Schedule I extraction; it now links through `validated_extractions` so the persistence precondition resolves for the `ScheduleI` codebase (`114b1dc`).
- **Call-site edge distinguisher:** multiple direct-call edges from the same source method were being rejected as duplicate IDs; edges are now distinguished by call site so distinct call instructions to different (or the same) targets each get a stable, non-colliding `EdgeId` (`67986b8`).
- **Idempotent save:** re-running `recover-native-body` with identical inputs originally attempted a duplicate insert; persistence is now idempotent — an identical re-run reproduces the same `RecoveryId` without erroring or duplicating rows (`6307e2b`).
- **Codebase-aware read linkage:** the seam-investigation native-evidence read path resolved the wrong index build when linking back to a ScheduleI index; it now resolves the build via extraction linkage scoped to the correct codebase (`b6121af`).

Two structural decisions also differed from the original interface sketch:

- `ISymbolIdentityResolver` (the seam that turns an S1Atlas symbol ID into a managed identity for native lookup) is **async and batched** — `Task<IReadOnlyDictionary<string, ManagedSymbolDescriptor?>> ResolveAsync(string indexId, IReadOnlyList<string> symbolIds, CancellationToken)` — rather than a single-symbol synchronous shape, so a multi-symbol `recover-native-body` call resolves all requested symbols in one index round trip.
- Composition was consolidated into a single `NativeRecoveryComposition` (`src/S1Atlas.NativeRecovery/NativeRecoveryComposition.cs`) rather than spreading provider/workflow/execution-context wiring inline in `CliApplication.cs`; the CLI command depends on `NativeRecoveryComposition` and `NativeRecoveryExecutionContextFactory` directly.
- The pinned-library tool definition lives at `config/native-recovery/libraries.json`, moved out of the typed `config/tools/` directory (which models executable tool pins) since a pinned-library identity has a different shape (`8578ce5`).

No dedicated MCP write tool for native recovery was added: seam investigation already surfaces the same `NativeRecoveryRecord`/`NativeEvidenceEdge` model read-only on both CLI (`investigate-seam`) and MCP (`investigate_seam`) once the CLI write path persists a row, confirmed by an end-to-end parity test (`9779cce`).
