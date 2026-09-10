# AT-37 — Focused IL2CPP Native-Body Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give S1Atlas a bounded, read-only native-body recovery path that maps a stubbed IL2CPP managed method to its native `GameAssembly.dll` address and returns focused, provenance-stamped direct-call and field-access evidence — or a precise reason recovery is unavailable.

**Architecture:** Implement the already-defined `INativeBodyRecoveryProvider` seam with an in-process provider built on the pinned `Samboy063.LibCpp2IL` library (managed-symbol ↔ native-pointer mapping) and `Iced` (bounded x86-64 decode). The existing `NativeRecoveryWorkflow` validates, normalizes, sanitizes, and integrity-stamps the result; a new CLI command drives live recovery and persists it, and the existing `investigate_seam` surfaces persist-then-read evidence on both CLI and MCP. No subprocess, no raw disassembly persisted, no game binary copied.

**Tech Stack:** C# / .NET 8, `Samboy063.LibCpp2IL` 2022.1.0-pre-release.21 (MIT), `Iced` 1.21.0 (MIT), SQLite (existing storage), existing extraction/discovery/authority services.

## Global Constraints

- **Provider identity model:** pinned-library identity, not an executable. `ToolName`, `ToolVersion`, `ToolSha256` describe the pinned LibCpp2IL + Iced libraries. `ToolSha256` **must be exactly 64 lowercase hex characters** (workflow `RequireSha256`), computed as SHA-256 over a canonical library descriptor.
- **Pinned dependencies (exact):** `Samboy063.LibCpp2IL` = `2022.1.0-pre-release.21`; `Iced` = `1.21.0`. Both MIT. Restored via committed `packages.lock.json`, CI restores with `--locked-mode`. These are build-time dependencies, **not** runtime tool downloads.
- **Evidence sanitization (verbatim, from `NativeRecoveryWorkflow.SanitizeSummary`):** every `MappingEvidence`, `FieldAccesses`, and each edge's `SourceMethodPointer` / `TargetMethodPointer` / `TargetText` / `Kind` / `Evidence`, and `FailureMessage` must, after `Trim()`: be non-blank; be ≤ 512 chars; contain no `\0`; contain no `"://"`; contain no `\`; contain no `/`; contain no `".bin"` (case-insensitive); contain no `"disassembly"` (case-insensitive). Violations become `Status = Failed`, message "Native recovery provider returned invalid evidence." **Managed names must therefore render nested types with `.` or `+`, never `/`.**
- **Direct-edge rule (verbatim):** only `Kind == "DirectCall"` (ordinal) with a non-null `TargetMethodPointer` survives as a direct edge. Every other kind (`IndirectDispatch`, `RuntimeDispatch`, `CrossThreadDispatch`, targetless, unknown) is rewritten to `Kind = "UNKNOWN"`, `TargetMethodPointer = null`, `IsComplete = false`.
- **Traversal budget:** integer `1..500` inclusive (`MaxTraversalEdges`); truncation marks the record incomplete.
- **Read-only:** never launch or mutate the game. Recovery reads bytes from `GameAssembly.dll` + `global-metadata.dat` only. Raw disassembly is consumed in memory and never persisted.
- **Determinism:** identical inputs reproduce identical `OutputSha256` and `RecoveryId`. `LibCpp2IlMain` is global static state — access is single-flighted and the loaded binary is cached keyed by `(gameAssemblySha256, metadataSha256)`.
- **Repo policy:** public repo — **no `Co-Authored-By: Claude` / "Generated with" trailers** on commits or PRs. Keep worknotes local (`docs/worknotes/` is gitignored). CI runs a `dotnet format` verify gate — run it before pushing.
- **Unity version:** the codebase does not probe the engine version from the binary; scene indexing hard-codes the `2022.3.62` family. The provider passes a known supported `UnityVersion` and returns a negative result (not wrong data) if the build's metadata is incompatible.

---

## File Structure

New project **`src/S1Atlas.NativeRecovery/`** isolates the reverse-engineering library dependencies (LibCpp2IL, Iced) from the rest of the solution. It references `S1Atlas.Core` (models) and `S1Atlas.Indexing` (the `INativeBodyRecoveryProvider` interface).

- Create `src/S1Atlas.NativeRecovery/S1Atlas.NativeRecovery.csproj` — the only project referencing LibCpp2IL + Iced.
- Create `src/S1Atlas.NativeRecovery/LibCpp2IlNativeBodyRecoveryProvider.cs` — implements `INativeBodyRecoveryProvider`.
- Create `src/S1Atlas.NativeRecovery/Il2CppImageCache.cs` — single-flight load + hash-keyed cache of `LibCpp2IlMain` global state.
- Create `src/S1Atlas.NativeRecovery/ManagedSymbolResolver.cs` — S1Atlas symbol id → `Il2CppMethodDefinition`, with ambiguity detection.
- Create `src/S1Atlas.NativeRecovery/BoundedNativeDecoder.cs` — Iced decode loop: emits `DirectCall`/`UNKNOWN` edges + field-offset accesses within budget.
- Create `src/S1Atlas.NativeRecovery/NativeNameNormalizer.cs` — slash-free managed name + hex-VA formatting helpers.
- Create `src/S1Atlas.NativeRecovery/LibraryToolIdentity.cs` — computes the 64-hex `ToolSha256` from the pinned-library descriptor.
- Create `config/tools/native-recovery-libraries.json` — reviewed pinned-library tool definition (identity + license), read at composition time.
- Modify `src/S1Atlas.Cli/CliApplication.cs` — compose the provider + workflow, resolve tool identity.
- Create `src/S1Atlas.Cli/Commands/RecoverNativeBodyCommand.cs` — CLI write-path command that runs recovery and persists.
- Modify `src/S1Atlas.Mcp/Mapping/EnvelopeMapper.cs` — add `FromNativeRecovery<T>` status→envelope mapping (only if a dedicated MCP tool is added; otherwise the existing `investigate_seam` path is reused).
- Modify `docs/design/2026-08-29-native-recovery-provenance.md` — amend to bless pinned-library provider identity.
- Test projects: `tests/S1Atlas.NativeRecovery.Tests/` (unit), and integration coverage under `tests/S1Atlas.IntegrationTests/`.

---

## Phase 0 — Dependencies, provenance, and tool identity

### Task 0.1: Amend the provenance design doc for pinned-library identity

**Files:**
- Modify: `docs/design/2026-08-29-native-recovery-provenance.md`

**Steps:**
- [ ] **Step 1:** Add a new subsection "Provider identity: pinned library" under "Licensing and distribution boundary" stating: a provider MAY be an in-process pinned library rather than an executable; its `ToolName`/`ToolVersion`/`ToolSha256` describe the pinned NuGet libraries; `ToolSha256` is a SHA-256 over a canonical descriptor of `{packageId, version, contentHash}` entries taken from the committed `packages.lock.json`; the libraries are hash-verified at restore via lockfile locked-mode; no runtime download occurs. Keep the existing exe-inventory table; add the library identity as an accepted alternative.
- [ ] **Step 2:** Commit.

```bash
git add docs/design/2026-08-29-native-recovery-provenance.md
git commit -m "docs: bless pinned-library provider identity for native recovery"
```

### Task 0.2: Add the isolated project and pinned dependencies with a lockfile

**Files:**
- Create: `src/S1Atlas.NativeRecovery/S1Atlas.NativeRecovery.csproj`
- Modify: the solution file (`*.sln`) to include the new project
- Create: `src/S1Atlas.NativeRecovery/packages.lock.json` (generated)

**Interfaces:**
- Produces: project `S1Atlas.NativeRecovery` referencing `S1Atlas.Core`, `S1Atlas.Indexing`, and packages `Samboy063.LibCpp2IL` 2022.1.0-pre-release.21, `Iced` 1.21.0.

**Steps:**
- [ ] **Step 1:** Create the csproj:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Samboy063.LibCpp2IL" Version="2022.1.0-pre-release.21" />
    <PackageReference Include="Iced" Version="1.21.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\S1Atlas.Core\S1Atlas.Core.csproj" />
    <ProjectReference Include="..\S1Atlas.Indexing\S1Atlas.Indexing.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2:** Add the project to the solution: `dotnet sln add src/S1Atlas.NativeRecovery/S1Atlas.NativeRecovery.csproj`
- [ ] **Step 3:** Restore to generate the lockfile: `dotnet restore src/S1Atlas.NativeRecovery/S1Atlas.NativeRecovery.csproj`
- [ ] **Step 4:** Verify the LibCpp2IL + Iced public API resolves — build the empty project: `dotnet build src/S1Atlas.NativeRecovery -c Release`. Expected: success.
- [ ] **Step 5:** Confirm CI restores in locked mode. Check the CI workflow restore step passes `--locked-mode` (add it if the repo-wide restore doesn't already). Expected: restore fails if the lockfile drifts.
- [ ] **Step 6:** Commit.

```bash
git add src/S1Atlas.NativeRecovery/ *.sln
git commit -m "chore: add isolated S1Atlas.NativeRecovery project with pinned LibCpp2IL + Iced"
```

### Task 0.3: Reviewed pinned-library tool definition + `LibraryToolIdentity`

**Files:**
- Create: `config/tools/native-recovery-libraries.json`
- Create: `src/S1Atlas.NativeRecovery/LibraryToolIdentity.cs`
- Test: `tests/S1Atlas.NativeRecovery.Tests/LibraryToolIdentityTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record LibraryPin(string PackageId, string Version, string ContentSha256);
  public static class LibraryToolIdentity
  {
      public const string ToolName = "s1atlas-native-recovery-libs";
      public static string ComputeToolSha256(IReadOnlyList<LibraryPin> pins); // 64 lowercase hex
      public static string ToolVersion(IReadOnlyList<LibraryPin> pins);       // e.g. "libcpp2il=2022.1.0-pre-release.21;iced=1.21.0"
  }
  ```
  The digest is SHA-256 over a length-prefixed canonical stream: domain tag `"s1atlas-native-recovery-libs-v1"`, then for each pin sorted by `PackageId` ordinal: `PackageId`, `Version`, `ContentSha256`. `ContentSha256` values come from `packages.lock.json`.

- [ ] **Step 1: Write the failing test.**

```csharp
[Fact]
public void ComputeToolSha256_is_64_lowercase_hex_and_order_independent()
{
    var a = new[] { new LibraryPin("Iced", "1.21.0", "sha512-abc"),
                    new LibraryPin("Samboy063.LibCpp2IL", "2022.1.0-pre-release.21", "sha512-def") };
    var b = new[] { a[1], a[0] };
    var ha = LibraryToolIdentity.ComputeToolSha256(a);
    Assert.Equal(ha, LibraryToolIdentity.ComputeToolSha256(b)); // sorted canonically
    Assert.Matches("^[0-9a-f]{64}$", ha);
}
```

- [ ] **Step 2: Run it, verify FAIL** (`LibraryToolIdentity` not defined).
  Run: `dotnet test tests/S1Atlas.NativeRecovery.Tests --filter LibraryToolIdentityTests`
- [ ] **Step 3: Implement `LibraryToolIdentity`** using `System.Security.Cryptography.IncrementalHash` (SHA-256), length-prefix each UTF-8 string as little-endian int32 (mirror `NativeRecoveryIntegrity`'s framing), sort pins by `PackageId` (ordinal), return `Convert.ToHexString(hash).ToLowerInvariant()`.
- [ ] **Step 4: Run test, verify PASS.**
- [ ] **Step 5: Create `config/tools/native-recovery-libraries.json`** recording `toolName`, both library `pins` (packageId, version, contentSha256 copied from `packages.lock.json`), and `license` (both MIT + source URLs). Add a test asserting the file's pins match the `packages.lock.json` entries so drift is caught.
- [ ] **Step 6: Commit.**

```bash
git add src/S1Atlas.NativeRecovery/LibraryToolIdentity.cs config/tools/native-recovery-libraries.json tests/S1Atlas.NativeRecovery.Tests/LibraryToolIdentityTests.cs
git commit -m "feat: pinned-library tool identity for native recovery provenance"
```

---

## Phase 1 — Spikes (produce findings before dependent implementation)

These are real tasks with concrete deliverables (a findings note in `docs/worknotes/AT-37.md` + a committed test fixture), not placeholders. They de-risk the two API-iteration-heavy areas against the real installed build before their consuming tasks are written.

### Task 1.1 (SPIKE): LibCpp2IL load + symbol→method + address→symbol against the real build

**Deliverable:** a committed, skippable integration probe test that, given a local install, proves the end-to-end mapping for `Customer.EvaluateCounteroffer` and `Customer.GetValueProposition`.

**Files:**
- Test: `tests/S1Atlas.NativeRecovery.Tests/Spikes/LibCpp2IlMappingSpike.cs` (guarded by an env var / `[Trait("Category","LocalGameRequired")]` so CI without the game skips it)

- [ ] **Step 1:** Write a `[Fact]` that: reads bytes of `GameAssembly.dll` + `global-metadata.dat` from the discovered install; calls `LibCpp2IlMain.Initialize(binaryBytes, metadataBytes, UnityVersion.Parse("2022.3.62f2"))`; finds the `Il2CppMethodDefinition` for `ScheduleOne.Economy.Customer::EvaluateCounteroffer` by declaring-type full name + method name + parameter count/types; asserts `MethodPointer != 0`, `MethodOffsetInFile > 0`, `Rva != 0`.
- [ ] **Step 2:** Extend it to read the first N bytes at `MethodOffsetInFile`, decode with Iced, collect `call` immediate targets, and assert at least one resolves via `LibCpp2IlMain.GetMethodDefinitionByGlobalAddress(target)` to a non-null method (records the resolved managed name).
- [ ] **Step 3:** Record findings in `docs/worknotes/AT-37.md`: exact API names/namespaces used, the `UnityVersion` construction that worked, whether `Initialize` needs `LibCpp2IlMain.Reset()` between loads, and confirmed behavior of `GetMethodDefinitionByGlobalAddress` on thunks vs. real methods. **These findings become the literal code in Tasks 2.x.**
- [ ] **Step 4: Commit** (the spike test + worknotes are the deliverable; worknotes stay local/uncommitted per policy — commit only the test).

```bash
git add tests/S1Atlas.NativeRecovery.Tests/Spikes/LibCpp2IlMappingSpike.cs
git commit -m "test: local spike proving LibCpp2IL symbol/address mapping on installed build"
```

### Task 1.2 (SPIKE): Field-offset → field-name resolution fidelity + decode termination

**Deliverable:** findings + a decision recorded in the plan and worknotes on (a) whether to resolve `[base+offset]` reads to field names via `Il2CppTypeDefinition` field layout or emit raw offsets only, and (b) how the decode loop bounds a function (ret detection vs. budget vs. max-byte cap).

**Files:**
- Test: `tests/S1Atlas.NativeRecovery.Tests/Spikes/FieldLayoutSpike.cs` (LocalGameRequired)

- [ ] **Step 1:** For `Customer`, enumerate the type's fields via LibCpp2IL and their offsets; for the decoded `EvaluateCounteroffer`, map observed `[rbx+0x168]`-style reads (rbx = `this`) to a field where the offset matches; record hit rate.
- [ ] **Step 2:** Decide the field-access evidence format (slash-free), e.g. `"this.<fieldName> @ 0x168"` when resolved, else `"field @ 0x168"`. Record the decision in the plan's Decisions Log below.
- [ ] **Step 3:** Decide decode termination policy: stop at `ret`, at budget edges, or at a max instruction/byte cap (whichever first); truncation → `IsComplete = false`. Record it.
- [ ] **Step 4: Commit** the spike test.

```bash
git add tests/S1Atlas.NativeRecovery.Tests/Spikes/FieldLayoutSpike.cs
git commit -m "test: local spike for field-offset resolution and decode termination"
```

---

## Phase 2 — Provider implementation (TDD, no game required for unit tests)

Unit tests in this phase use small synthetic byte buffers + a fake/loopback image, not the real game, so they run in CI. Real-build behavior is covered by the LocalGameRequired integration tests.

### Task 2.1: `NativeNameNormalizer` — slash-free names and hex VAs

**Files:**
- Create: `src/S1Atlas.NativeRecovery/NativeNameNormalizer.cs`
- Test: `tests/S1Atlas.NativeRecovery.Tests/NativeNameNormalizerTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class NativeNameNormalizer
  {
      public static string Pointer(ulong virtualAddress);            // "0x1806a7c20" (lowercase, no separators)
      public static string ManagedName(string declaringTypeFullName, string methodName); // nested '/' -> '.', strips '/'
      public static string FieldAccess(string? fieldName, ulong offset); // "this.name @ 0x168" | "field @ 0x168"
      public static bool IsSummarySafe(string value);                 // mirrors SanitizeSummary rules
  }
  ```

- [ ] **Step 1: Write failing tests** asserting: `Pointer(0x1806A7C20) == "0x1806a7c20"`; `ManagedName("ScheduleOne.Economy.Customer/Nested","M") == "ScheduleOne.Economy.Customer.Nested.M"`; every output satisfies `IsSummarySafe` (no `/`, `\`, `://`, `.bin`, `disassembly`, ≤512).
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement** — replace `/` and `+` with `.`, collapse, format VA with `x` and no grouping. `IsSummarySafe` re-implements the exact `SanitizeSummary` predicate as a guard so the provider never emits a rejectable string.
- [ ] **Step 4: Run, verify PASS.**
- [ ] **Step 5: Commit.**

### Task 2.2: `Il2CppImageCache` — single-flight, hash-keyed global load

**Files:**
- Create: `src/S1Atlas.NativeRecovery/Il2CppImageCache.cs`
- Test: `tests/S1Atlas.NativeRecovery.Tests/Il2CppImageCacheTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class Il2CppImageCache : IDisposable
  {
      // Ensures LibCpp2IlMain global state reflects (gameAssemblySha256, metadataSha256).
      // Serializes all access via SemaphoreSlim; re-Initializes only when the key changes.
      public Task<T> WithImageAsync<T>(
          string gameAssemblySha256, byte[] gameAssemblyBytes,
          string metadataSha256, byte[] metadataBytes,
          UnityVersion unityVersion,
          Func<CancellationToken, Task<T>> body,
          CancellationToken cancellationToken);
  }
  ```
  Contract: only one `body` runs at a time; on entry, if the cached key differs, call `LibCpp2IlMain.Reset()` (per Task 1.1 findings) then `Initialize(...)` and store the key; `body` runs while the lock is held; exceptions from `Initialize` surface to the caller.

- [ ] **Step 1: Write failing tests** (using the fake image loopback from the spike, or a tiny valid metadata+PE fixture if feasible): two concurrent `WithImageAsync` calls never overlap (assert via a re-entrancy counter inside `body`); a second call with the same key does not re-`Initialize` (assert via an injected init counter).
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement** with `SemaphoreSlim(1,1)` + a stored key string + an injectable initializer delegate (so the counter can be observed in tests without the real library).
- [ ] **Step 4: Run, verify PASS.**
- [ ] **Step 5: Commit.**

### Task 2.3: `ManagedSymbolResolver` — S1Atlas symbol id → `Il2CppMethodDefinition`

**Files:**
- Create: `src/S1Atlas.NativeRecovery/ManagedSymbolResolver.cs`
- Test: `tests/S1Atlas.NativeRecovery.Tests/ManagedSymbolResolverTests.cs`

**Interfaces:**
- Consumes: an index lookup that turns an S1Atlas `symbolId` into `{ declaringTypeFullName, methodName, parameterTypeFullNames[] }` (via `IIndexRepository` / `IndexQueryService` — resolve the exact call in Task 3.1; the resolver takes the already-resolved managed identity as input so it is unit-testable without the DB).
- Produces:
  ```csharp
  public enum SymbolResolution { Resolved, NotFound, Ambiguous }
  public sealed record ResolvedNativeSymbol(string SymbolId, ulong MethodPointer, long MethodOffsetInFile, ulong Rva, string ManagedName);
  public sealed record SymbolResolutionResult(SymbolResolution Kind, ResolvedNativeSymbol? Symbol, string? Detail);
  public static class ManagedSymbolResolver
  {
      public static SymbolResolutionResult Resolve(
          string symbolId, string declaringTypeFullName, string methodName,
          IReadOnlyList<string> parameterTypeFullNames,
          IIl2CppMethodLookup lookup); // abstraction over LibCpp2IlMain, fakeable in tests
  }
  ```
  `IIl2CppMethodLookup` is a thin interface (implemented in Task 2.5 over LibCpp2IL) returning candidate methods by declaring type + name; the resolver applies parameter-type matching, returns `Ambiguous` when >1 survives, `NotFound` when 0.

- [ ] **Step 1: Write failing tests** with a fake `IIl2CppMethodLookup`: exact single match → `Resolved`; two overloads, params disambiguate → `Resolved`; two indistinguishable candidates → `Ambiguous`; none → `NotFound`.
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement** the matching (normalize parameter type names before compare; slash-free via `NativeNameNormalizer`).
- [ ] **Step 4: Run, verify PASS.**
- [ ] **Step 5: Commit.**

### Task 2.4: `BoundedNativeDecoder` — Iced decode → edges + field accesses

**Files:**
- Create: `src/S1Atlas.NativeRecovery/BoundedNativeDecoder.cs`
- Test: `tests/S1Atlas.NativeRecovery.Tests/BoundedNativeDecoderTests.cs`

**Interfaces:**
- Consumes: a byte slice + start VA + budget + an `IAddressResolver` (`ulong -> string? managedName`, fakeable), and a `IFieldResolver` (`(typeContext, offset) -> string? fieldName`).
- Produces:
  ```csharp
  public sealed record DecodedEvidence(
      IReadOnlyList<NativeEvidenceEdge> Edges,
      IReadOnlyList<string> FieldAccesses,
      bool IsComplete);
  public static class BoundedNativeDecoder
  {
      public static DecodedEvidence Decode(
          ReadOnlySpan<byte> code, ulong startVirtualAddress, string sourcePointer,
          int maxEdges, IAddressResolver addresses, IFieldResolver fields);
  }
  ```
  Rules (per Task 1.2 findings): iterate with Iced `Decoder` (64-bit); on `Call` with a near-branch immediate target → resolve via `addresses`; if resolved → `NativeEvidenceEdge{ Kind="DirectCall", SourceMethodPointer=sourcePointer, TargetMethodPointer=Pointer(target), TargetText=managedName, Evidence="direct call" , IsComplete=true }`; else (indirect/register/memory call, or unresolved) → `Kind="RuntimeDispatch"` (workflow will normalize to UNKNOWN); on memory reads `[base+disp]` with a plausible field base → append a `FieldAccess`. Stop at the first `ret`, when `Edges.Count == maxEdges` (then `IsComplete=false`), or a max-byte cap (then `IsComplete=false`). Never emit raw disassembly text into any field.

- [ ] **Step 1: Write failing tests** using hand-assembled byte buffers (fixed opcodes): a `call rel32` to a resolvable address yields one `DirectCall` edge with the expected `TargetText`; a `call [rax+0x10]` yields a non-direct edge (kind not `DirectCall`); hitting `maxEdges` sets `IsComplete=false`; a `ret` terminates and `IsComplete=true`; a `mov rax,[rbx+0x168]` with a resolvable field yields the expected `FieldAccess` string.
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement** with `Iced.Intel.Decoder`. (Exact Iced call shapes confirmed by Task 1.1 spike; Iced 1.21.0 is already a proven dependency of Cpp2IL.Core.)
- [ ] **Step 4: Run, verify PASS.**
- [ ] **Step 5: Commit.**

### Task 2.5: `LibCpp2IlNativeBodyRecoveryProvider` — assemble the provider

**Files:**
- Create: `src/S1Atlas.NativeRecovery/LibCpp2IlNativeBodyRecoveryProvider.cs`
- Test: `tests/S1Atlas.NativeRecovery.Tests/LibCpp2IlNativeBodyRecoveryProviderTests.cs`

**Interfaces:**
- Consumes: `Il2CppImageCache`, `ManagedSymbolResolver`, `BoundedNativeDecoder`, `LibraryToolIdentity`, and the source of `GameAssembly.dll` + `global-metadata.dat` bytes (an injected `INativeImageSource` resolving from `ScheduleOneInstallation`), plus the resolved `UnityVersion`.
- Produces: `public sealed class LibCpp2IlNativeBodyRecoveryProvider : INativeBodyRecoveryProvider` with
  ```csharp
  public Task<NativeRecoveryRecord> RecoverAsync(NativeRecoveryRequest request, CancellationToken cancellationToken);
  ```
  Behavior: builds the concrete LibCpp2IL implementations of `IIl2CppMethodLookup` / `IAddressResolver` / `IFieldResolver` inside `Il2CppImageCache.WithImageAsync`; resolves each `request.SymbolIds` entry; if any resolves `Ambiguous` → returns `Status=AmbiguousMapping` (with slash-free mapping evidence, no edges); if all `NotFound` → `Status=NoBody`; otherwise decodes each resolved method within `request.MaxTraversalEdges` (budget shared/divided across selected symbols — decision recorded in Decisions Log), aggregates edges + field accesses + mapping evidence, sets record `Status=Recovered`, `IsComplete` = (no truncation AND no UNKNOWN edges). Stamps `ToolName/ToolVersion/ToolSha256` from `LibraryToolIdentity`. **Returns evidence for exactly `request` (unchanged) so the workflow's `RequestsMatch` passes.** Never throws for expected negatives; unexpected exceptions propagate to the workflow which records `Failed`.

- [ ] **Step 1: Write failing tests** with fakes wired for: all-resolved → `Recovered` with expected edges and matching `Request`; one ambiguous → `AmbiguousMapping`; none found → `NoBody`; a resolver that throws → exception propagates (workflow test covers `Failed`). Assert every emitted string passes `NativeNameNormalizer.IsSummarySafe`.
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement** the provider + the LibCpp2IL-backed `IIl2CppMethodLookup`/`IAddressResolver`/`IFieldResolver`.
- [ ] **Step 4: Run, verify PASS.**
- [ ] **Step 5: Commit.**

### Task 2.6: Workflow round-trip test through the real workflow

**Files:**
- Test: `tests/S1Atlas.NativeRecovery.Tests/ProviderWorkflowRoundTripTests.cs`

- [ ] **Step 1: Write a test** that constructs `new NativeRecoveryWorkflow(provider)` with the real provider over a fake image, a valid `NativeRecoveryExecutionContext` (tool identity = `LibraryToolIdentity` values), and asserts: `Recovered` record has deterministic `OutputSha256`/`RecoveryId` (run twice, equal); a non-`DirectCall` edge from the provider is normalized to `Kind="UNKNOWN"`, `IsComplete=false`; an evidence string containing `/` (deliberately injected via a fake) yields `Failed` "returned invalid evidence."
- [ ] **Step 2: Run, verify PASS** (implementation already exists; this proves integration).
- [ ] **Step 3: Commit.**

---

## Phase 3 — Composition, CLI write-path, MCP read surface

### Task 3.1: Compose provider + workflow + execution context in the CLI

**Files:**
- Modify: `src/S1Atlas.Cli/CliApplication.cs` (composition near `RepositoryToolDefinitionProvider` construction, ~lines 126-127, and service wiring)
- Create: `src/S1Atlas.NativeRecovery/NativeRecoveryExecutionContextFactory.cs`
- Test: `tests/S1Atlas.NativeRecovery.Tests/NativeRecoveryExecutionContextFactoryTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class NativeRecoveryExecutionContextFactory
  {
      // Resolves current identity via InstalledBuildAuthorityResolver + current GameAssembly.dll hash,
      // and tool identity via LibraryToolIdentity. Returns null-with-reason when the build/index is unresolved.
      public static Task<NativeRecoveryExecutionContext> CreateAsync(
          InstalledBuildAuthority authority, string currentGameAssemblySha256,
          IReadOnlyList<LibraryPin> pins, CancellationToken ct);
  }
  ```
  `CurrentBuildId = authority.ResolvedBuildId`, `CurrentIndexId = authority.IndexId`, `CurrentGameAssemblySha256 = <hash of installed GameAssembly.dll>`, tool fields from `LibraryToolIdentity`.

- [ ] **Step 1: Write failing test** asserting the factory maps an `Resolved` `InstalledBuildAuthority` + hash + pins into a context whose fields equal the inputs and whose `ToolSha256` is 64-hex.
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement** the factory; wire `LibCpp2IlNativeBodyRecoveryProvider` + `new NativeRecoveryWorkflow(provider)` into CLI composition, resolving `UnityVersion` from a supported-version constant (`"2022.3.62f2"`, matching the scene gate) exposed as config.
- [ ] **Step 4: Run, verify PASS.**
- [ ] **Step 5: Commit.**

### Task 3.2: `recover-native-body` CLI command (runs recovery, persists, prints)

**Files:**
- Create: `src/S1Atlas.Cli/Commands/RecoverNativeBodyCommand.cs`
- Modify: CLI command registration (wherever `InvestigateSeamCommand` is registered)
- Create: `src/S1Atlas.Cli/Output/NativeRecoveryOutputModels.cs`
- Test: `tests/S1Atlas.IntegrationTests/NativeRecovery/RecoverNativeBodyCliTests.cs`

**Interfaces:**
- Options: `--symbol-id` (repeatable, ≥1), `--traversal-budget` (int, default 100, bounds 1..500), optional `--build-id`. Behavior: resolve authority; if not `Resolved`, print the mapped negative (`InputChanged`/unavailable) and exit non-zero without calling the provider; else build request + execution context, call `workflow.RecoverAsync`, `repository.SaveNativeRecoveryAsync(record)`, and print the record via the JSON output model (mirrors `SourceCliOutput` conventions, includes `Status`, `IsComplete`, `Edges`, `FieldAccesses`, `MappingEvidence`, `RecoveryId`, `OutputSha256`, tool identity).

- [ ] **Step 1: Write failing integration test** (LocalGameRequired) that runs the command for `Customer.EvaluateCounteroffer`'s symbol id with budget 100 and asserts exit 0, `Status=Recovered` or a precise negative, a non-empty `RecoveryId`, and that a second identical run yields the identical `RecoveryId` (determinism).
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement** the command + output model + registration. Reuse the `InvalidNativeTraversalBudget` bounds error style from `InvestigateSeamCommand`.
- [ ] **Step 4: Run, verify PASS** (locally; CI skips LocalGameRequired). Add a non-game unit test for option parsing + bounds.
- [ ] **Step 5: Commit.**

### Task 3.3: Confirm MCP surface parity (envelope mapping if a dedicated tool is added)

**Files:**
- Modify (only if adding a dedicated MCP tool): `src/S1Atlas.Mcp/Mapping/EnvelopeMapper.cs`, `src/S1Atlas.Mcp/Tools/SeamTools.cs`
- Test: `tests/S1Atlas.Mcp.Tests/NativeRecoveryEnvelopeTests.cs`

**Decision gate:** `investigate_seam` already surfaces persisted `NativeEvidenceEdge` evidence on **both** CLI and MCP via `NativeEvidenceSummary`. AC #4 ("CLI and MCP expose the same bounded evidence model") is satisfied by the shared `NativeRecoveryRecord`/`NativeEvidenceEdge` model the moment the CLI write-path populates rows. So a *new* MCP tool is optional.

- [ ] **Step 1:** Verify via a test that after the CLI persists a record, `investigate_seam` (MCP) returns the same edges/status for the same symbol ids + budget. If it does, close this task as "parity via existing tool" and record that in the plan.
- [ ] **Step 2 (only if a dedicated `recover_native_body` read tool is wanted):** Add `EnvelopeMapper.FromNativeRecovery<T>` mapping `Recovered→Resolved`, `NoBody/Unsupported→Unavailable`, `AmbiguousMapping→Ambiguous`, `InputChanged→Invalid`, `Failed→Unavailable` with a `ToolError(code,message)`, modeled on `FromSeamInvestigation` (EnvelopeMapper.cs:545-576). Add the read-only MCP tool that looks up persisted rows via `GetNativeRecoveriesAsync`. Write the mapping test.
- [ ] **Step 3: Commit.**

---

## Phase 4 — Documentation and close-out

### Task 4.1: Update surfaced docs

**Files:**
- Modify: any CLI/MCP reference doc that lists commands/tools (add `recover-native-body`; note native evidence on `investigate_seam`).
- Modify: `docs/design/2026-08-29-native-recovery-provenance.md` cross-reference to this plan and the shipped provider.

- [ ] **Step 1:** Update docs to describe the bounded evidence model, the four-way status distinction (managed-unavailable via existing `BodyRecoveryStatus.StubOrUnavailable`, native-not-attempted, native-failed, native-recovered), and that recovered pseudocode is static evidence requiring runtime validation.
- [ ] **Step 2:** Run the `dotnet format` verify gate: `dotnet format --verify-no-changes` (CI format gate). Fix any violations.
- [ ] **Step 3: Commit.**

### Task 4.2: Full verification pass

- [ ] **Step 1:** `dotnet build -c Release` — expected success.
- [ ] **Step 2:** `dotnet test` (CI subset; LocalGameRequired skipped) — expected all pass.
- [ ] **Step 3:** Locally (with the game installed) run the LocalGameRequired suite — expected pass, `Customer.EvaluateCounteroffer` + `Customer.GetValueProposition` recover or return precise negatives.
- [ ] **Step 4:** `dotnet format --verify-no-changes` — expected clean.
- [ ] **Step 5:** Confirm no game binary / raw disassembly / path leaked into any persisted row (query `native_recovery_*` in a test and assert `IsSummarySafe` on every stored string).

---

## Acceptance-criteria coverage (self-review)

1. *Stubbed method returns focused native-recovery or precise reason* → Tasks 2.5 (Recovered/NoBody/AmbiguousMapping), workflow (InputChanged/Failed/Unsupported), 3.2 (CLI drives it).
2. *Identifies binary/metadata provenance; no silent build substitution* → execution context + `InputMatches`/`RequestsMatch` (workflow), Task 3.1; `SaveNativeRecoveryAsync`'s `RequireCompletedNativeInputAsync` join.
3. *Preserves uncertainty on optimized/inlined/unresolved* → Task 2.4 UNKNOWN normalization + `IsComplete=false` on truncation/indirect; Task 2.6 proves it.
4. *CLI and MCP expose the same bounded evidence model* → shared `NativeRecoveryRecord`/`NativeEvidenceEdge`; Task 3.3 verifies parity via `investigate_seam`.
5. *Read-only; never launches/mutates the game* → provider only reads bytes; Task 4.2 Step 5 asserts no leak; no process launch anywhere.

## Decisions Log

- **Provenance identity:** Option 1 — pinned-library identity; `ToolSha256` = SHA-256 over the library descriptor (Task 0.3). Doc amended (Task 0.1).
- **(open, set in Task 1.2)** Field-offset → field-name resolution format and whether names are resolved or offsets-only.
- **(open, set in Task 1.2)** Decode termination policy (ret / budget / byte cap).
- **(open, set in Task 2.5)** Traversal-budget division across multiple selected symbols (per-symbol vs. shared pool).
- **(open, set in Task 1.1)** Whether `LibCpp2IlMain.Reset()` is required between image loads.
