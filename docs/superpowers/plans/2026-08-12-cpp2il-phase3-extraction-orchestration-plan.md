# Cpp2IL Phase 3 Extraction Orchestration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a repository-controlled, offline extraction orchestrator that resolves an exact Schedule I build and verified Cpp2IL instance, records a durable attempt, runs Cpp2IL with typed arguments in Atlas-owned paths, verifies live inputs before and after execution, optionally archives inputs, and retains bounded diagnostic evidence without claiming that unvalidated Phase 3 output is authoritative.

**Architecture:** `S1Atlas.Core` owns repository configuration contracts, deterministic recipe identity, the extraction-attempt state machine, and storage interfaces. `S1Atlas.Extraction` owns strict profile loading, managed/custom tool resolution, input resolution and snapshots, the extraction lock, attempt manifests, isolated process execution, bounded logs, and orchestration. `S1Atlas.Storage` adds schema version 4 and persists attempts and input snapshots. `S1Atlas.Cli` composes those services and exposes `extract` in human and JSON modes. A successful Phase 3 process run ends as `ProcessCompleted` and moves its unvalidated bytes to an attempt-scoped `candidate-output` directory; only Phase 4 may validate, promote, or expose those bytes as a `ValidatedExtraction`.

**Tech Stack:** C# / .NET 8, `System.Text.Json`, `System.Diagnostics.Process`, SHA-256, Microsoft.Data.Sqlite 8.0.29, System.CommandLine 2.0.10, xUnit v3, Windows 10 or later, Windows GitHub Actions.

## Global Constraints

- Target Windows 10 or later and `win-x64` for the production Cpp2IL path.
- Preserve the exact approved managed pin:

```text
Version:             2022.1.0-pre-release.21
Asset name:          Cpp2IL-2022.1.0-pre-release.21-Windows.exe
Asset size:          15,137,811 bytes
Asset SHA-256:       663fb432433b4371fd1ee0ebc321a8fff2a9aac5ac4230c843f9e03ddee4e04c
Package kind:        singleFile
Local executable:    Cpp2IL.exe
Required format:     dll_il_recovery
```

- Preserve the exact extraction profile identity and invocation:

```text
Profile ID:          cpp2il-reconstructed-assemblies-v1
Profile version:     1
Adapter version:     1
Schema version:      1
Arguments:
  --game-path=<resolved game root>
  --exe-name=Schedule I
  --output-to=<Atlas attempt output root>
  --output-as=dll_il_recovery
Timeout:             30 minutes
Stdout retention:    64 MiB
Stderr retention:    64 MiB
Accepted exit code:  0
Required identity:   Assembly-CSharp
```

- `extract` performs no network access and never invokes `tools install`; only `tools install cpp2il` may use HTTP.
- Production execution uses `ProcessStartInfo.ArgumentList`, `UseShellExecute = false`, `CreateNoWindow = true`, redirected stdout/stderr, `NO_COLOR=true`, and an Atlas-owned working directory.
- Timeout and caller cancellation terminate the entire Cpp2IL process tree and finish draining both redirected streams.
- The live `GameAssembly.dll` and `global-metadata.dat` hashes must match the selected immutable build before execution and must be unchanged after execution.
- The Schedule I installation is read-only. Working directories, logs, snapshots, retained failures, and candidate output are created only under the configured Atlas data root.
- Raw Cpp2IL arguments are not accepted from configuration, environment variables, or the CLI.
- All generated paths are containment-checked; existing reparse points are rejected wherever Atlas reads or writes managed attempt/snapshot paths.
- All automated tests use temporary fake game bytes and a source-built fake child executable. They do not download Cpp2IL, use proprietary files, or point any executable at Schedule I.
- Never commit executables, game inputs, reconstructed assemblies, databases, WAL/SHM files, backups, attempt manifests, input manifests, complete markers, candidate output, retained output, or logs.
- Treat all warnings as errors and preserve all Phase 1 and Phase 2 human output, JSON envelopes, migrations, commands, and exit codes `0/1/2`.

---

## Approved Phase 3 Lifecycle Clarification

The approved design says both that Phase 3 ships `extract` before Phase 4 validation and that `Succeeded` means an attempt is linked to a structurally validated extraction. The user approved this narrow clarification on 2026-08-12:

```text
ProcessCompleted is a terminal Phase 3 attempt state.

It means:
  the controlled child process exited with an accepted code;
  stdout and stderr were drained;
  live inputs passed pre-run and post-run verification;
  the extraction lock was released;
  candidate bytes and diagnostic evidence were retained under the attempt;
  no structural validation or immutable extraction promotion occurred.

It never means:
  Succeeded;
  Valid, ValidWithWarnings, or preferred;
  safe for ILSpy, indexing, documentation, MCP, or any downstream consumer.
```

Phase 3 stores successful raw output at:

```text
builds/<build-id>/attempts/<attempt-id>/candidate-output/
```

No `complete.marker`, `artifact-manifest.json`, `validation.json`, `extraction.json`, `validated_extractions` row, or preferred-extraction row is created. Phase 4 will validate a candidate through a separate durable attempt before promotion. `ProcessCompleted` is terminal and immutable, so recovery does not misclassify it as abandoned work.

---

## Phase 3 Scope Boundary

Phase 3 delivers:

```text
strict repository extraction-profile loading
repository validation-policy provenance for later Phase 4 use
profile, policy, recipe, and input-manifest digests
attempt lifecycle including terminal ProcessCompleted
schema migration 4 for attempts and input snapshots
source-built fake Cpp2IL/process fixture
no-shell process execution and bounded logs
timeout/cancellation with full process-tree termination
managed-pinned and explicit custom tool resolution
current/historical build selection
explicit/stored/conventional-Steam/archived input resolution
pre-run and post-run input verification
optional atomic input snapshots
single-extraction lock and stale-attempt recovery
typed Cpp2IL arguments
failed/canceled output retention policy
extract human and JSON command
local capability smoke gate for --exe-name=Schedule I
```

Phase 3 explicitly does not deliver:

```text
artifact inventory or artifact-manifest.json
PEReader/MetadataReader assembly validation
absolute or comparative sanity validation
reproducibility comparison
ValidatedExtraction or extraction identity
complete.marker for candidate output
immutable extraction promotion
preferred extraction selection
extractions list/show/promote/cleanup commands
ILSpy, source generation, symbols, docs portal, or MCP
```

---

## File Structure

### Repository configuration

```text
config/extraction/cpp2il-reconstructed-assemblies-v1.json
config/validation/managed-assemblies-v1.json
```

The validation policy lands now only so every attempt records the exact policy intended for Phase 4. Phase 3 does not interpret output thresholds or produce a validation outcome.

### `S1Atlas.Core`

```text
src/S1Atlas.Core/Extraction/IExtractionProfileProvider.cs
src/S1Atlas.Core/Extraction/IValidationPolicyProvider.cs
src/S1Atlas.Core/Extraction/ExtractionProfile.cs
src/S1Atlas.Core/Extraction/ExtractionProfileFingerprint.cs
src/S1Atlas.Core/Extraction/ValidationPolicy.cs
src/S1Atlas.Core/Extraction/ValidationPolicyFingerprint.cs
src/S1Atlas.Core/Extraction/ExtractionRecipe.cs
src/S1Atlas.Core/Extraction/ExtractionRecipeId.cs
src/S1Atlas.Core/Extraction/ExtractionAttempt.cs
src/S1Atlas.Core/Extraction/ExtractionAttemptLifecycle.cs
src/S1Atlas.Core/Extraction/ExtractionAttemptStatus.cs
src/S1Atlas.Core/Extraction/ExtractionFailureStage.cs
src/S1Atlas.Core/Extraction/ExtractionFailureCode.cs
src/S1Atlas.Core/Extraction/ExtractionInputSource.cs
src/S1Atlas.Core/Extraction/InputManifest.cs
src/S1Atlas.Core/Extraction/InputManifestFingerprint.cs
src/S1Atlas.Core/Extraction/InputSnapshot.cs
src/S1Atlas.Core/Extraction/ExtractionOperationException.cs
src/S1Atlas.Core/Extraction/IIIl2CppExtractor.cs
src/S1Atlas.Core/Storage/IExtractionRepository.cs
```

Core contains no JSON, process, filesystem, Steam, or SQLite implementation.

### `S1Atlas.Extraction`

```text
src/S1Atlas.Extraction/Profiles/ExtractionProfileDocument.cs
src/S1Atlas.Extraction/Profiles/ExtractionProfileSerializer.cs
src/S1Atlas.Extraction/Profiles/ExtractionProfileValidator.cs
src/S1Atlas.Extraction/Profiles/RepositoryExtractionProfileProvider.cs
src/S1Atlas.Extraction/Profiles/ValidationPolicyDocument.cs
src/S1Atlas.Extraction/Profiles/ValidationPolicySerializer.cs
src/S1Atlas.Extraction/Profiles/ValidationPolicyValidator.cs
src/S1Atlas.Extraction/Profiles/RepositoryValidationPolicyProvider.cs
src/S1Atlas.Extraction/Processes/ProcessRequest.cs
src/S1Atlas.Extraction/Processes/ProcessResult.cs
src/S1Atlas.Extraction/Processes/ProcessRunner.cs
src/S1Atlas.Extraction/Processes/BoundedLogWriter.cs
src/S1Atlas.Extraction/Tools/ExtractionToolResolver.cs
src/S1Atlas.Extraction/Tools/ManagedToolInstanceFactory.cs
src/S1Atlas.Extraction/Inputs/ExtractionInputResolver.cs
src/S1Atlas.Extraction/Inputs/LiveInputVerifier.cs
src/S1Atlas.Extraction/Inputs/InputSnapshotService.cs
src/S1Atlas.Extraction/Inputs/InputSnapshotDocumentStore.cs
src/S1Atlas.Extraction/Attempts/AttemptDocumentStore.cs
src/S1Atlas.Extraction/Attempts/ExtractionLock.cs
src/S1Atlas.Extraction/Attempts/ExtractionRecoveryService.cs
src/S1Atlas.Extraction/Attempts/OwnedAttemptPaths.cs
src/S1Atlas.Extraction/Cpp2Il/Cpp2IlArgumentBuilder.cs
src/S1Atlas.Extraction/Cpp2Il/Cpp2IlProcessExtractor.cs
src/S1Atlas.Extraction/ExtractionOrchestrator.cs
```

### `S1Atlas.Storage`

```text
src/S1Atlas.Storage/Migrations/SqliteMigrations.cs
src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.cs
src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Extractions.cs
```

### `S1Atlas.Cli`

```text
src/S1Atlas.Cli/Commands/ExtractCommand.cs
src/S1Atlas.Cli/Configuration/AtlasPaths.cs
src/S1Atlas.Cli/Configuration/CliConfigurationPaths.cs
src/S1Atlas.Cli/Output/CliEnvelope.cs
src/S1Atlas.Cli/Output/ExtractionOutputModels.cs
src/S1Atlas.Cli/Commands/CommandExecution.cs
src/S1Atlas.Cli/CliApplication.cs
src/S1Atlas.Cli/S1Atlas.Cli.csproj
README.md
```

### Test fixture and tests

```text
tests/S1Atlas.FakeCpp2Il/S1Atlas.FakeCpp2Il.csproj
tests/S1Atlas.FakeCpp2Il/Program.cs
tests/S1Atlas.Core.Tests/Extraction/ExtractionConfigurationFingerprintTests.cs
tests/S1Atlas.Core.Tests/Extraction/ExtractionRecipeIdTests.cs
tests/S1Atlas.Core.Tests/Extraction/ExtractionAttemptLifecycleTests.cs
tests/S1Atlas.Core.Tests/Extraction/InputManifestFingerprintTests.cs
tests/S1Atlas.Extraction.Tests/Profiles/RepositoryExtractionProfileProviderTests.cs
tests/S1Atlas.Extraction.Tests/Profiles/RepositoryValidationPolicyProviderTests.cs
tests/S1Atlas.Extraction.Tests/Processes/ProcessRunnerTests.cs
tests/S1Atlas.Extraction.Tests/Processes/BoundedLogWriterTests.cs
tests/S1Atlas.Extraction.Tests/Tools/ExtractionToolResolverTests.cs
tests/S1Atlas.Extraction.Tests/Inputs/ExtractionInputResolverTests.cs
tests/S1Atlas.Extraction.Tests/Inputs/LiveInputVerifierTests.cs
tests/S1Atlas.Extraction.Tests/Inputs/InputSnapshotServiceTests.cs
tests/S1Atlas.Extraction.Tests/Attempts/AttemptDocumentStoreTests.cs
tests/S1Atlas.Extraction.Tests/Attempts/ExtractionLockTests.cs
tests/S1Atlas.Extraction.Tests/Attempts/ExtractionRecoveryServiceTests.cs
tests/S1Atlas.Extraction.Tests/Cpp2Il/Cpp2IlArgumentBuilderTests.cs
tests/S1Atlas.Extraction.Tests/Cpp2Il/Cpp2IlProcessExtractorTests.cs
tests/S1Atlas.Extraction.Tests/ExtractionOrchestratorTests.cs
tests/S1Atlas.Storage.Tests/Migrations/ExtractionAttemptMigrationTests.cs
tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryExtractionTests.cs
tests/S1Atlas.IntegrationTests/Extraction/ExtractionCliFixture.cs
tests/S1Atlas.IntegrationTests/Extraction/ExtractionCliTests.cs
```

---

### Task 1: Add Strict Extraction Profile and Validation Policy Configuration

**Files:**
- Create: `config/extraction/cpp2il-reconstructed-assemblies-v1.json`
- Create: `config/validation/managed-assemblies-v1.json`
- Create: `src/S1Atlas.Core/Extraction/IExtractionProfileProvider.cs`
- Create: `src/S1Atlas.Core/Extraction/IValidationPolicyProvider.cs`
- Create: `src/S1Atlas.Core/Extraction/ExtractionProfile.cs`
- Create: `src/S1Atlas.Core/Extraction/ExtractionProfileFingerprint.cs`
- Create: `src/S1Atlas.Core/Extraction/ValidationPolicy.cs`
- Create: `src/S1Atlas.Core/Extraction/ValidationPolicyFingerprint.cs`
- Create: `src/S1Atlas.Extraction/Profiles/ExtractionProfileDocument.cs`
- Create: `src/S1Atlas.Extraction/Profiles/ExtractionProfileSerializer.cs`
- Create: `src/S1Atlas.Extraction/Profiles/ExtractionProfileValidator.cs`
- Create: `src/S1Atlas.Extraction/Profiles/RepositoryExtractionProfileProvider.cs`
- Create: `src/S1Atlas.Extraction/Profiles/ValidationPolicyDocument.cs`
- Create: `src/S1Atlas.Extraction/Profiles/ValidationPolicySerializer.cs`
- Create: `src/S1Atlas.Extraction/Profiles/ValidationPolicyValidator.cs`
- Create: `src/S1Atlas.Extraction/Profiles/RepositoryValidationPolicyProvider.cs`
- Modify: `src/S1Atlas.Cli/S1Atlas.Cli.csproj`
- Test: `tests/S1Atlas.Core.Tests/Extraction/ExtractionConfigurationFingerprintTests.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Profiles/RepositoryExtractionProfileProviderTests.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Profiles/RepositoryValidationPolicyProviderTests.cs`

**Interfaces:**
- Consumes: `CanonicalHashWriter`, the strict repository-document pattern established by `RepositoryToolDefinitionProvider`, and the approved profile/policy values.
- Produces: `ResolvedExtractionProfile GetRequired(string profileId)`, `ResolvedValidationPolicy GetRequired(string policyId)`, stable canonical digests, typed snapshot-input declarations, and no raw argument collection.

- [ ] **Step 1: Commit the exact repository documents in RED with provider tests**

Use this extraction document shape and exact production values:

```json
{
  "schemaVersion": 1,
  "profileId": "cpp2il-reconstructed-assemblies-v1",
  "profileVersion": 1,
  "adapterVersion": 1,
  "extractionSchemaVersion": 1,
  "executableName": "Schedule I",
  "outputFormat": "dll_il_recovery",
  "timeoutSeconds": 1800,
  "maximumRetainedStandardOutputBytes": 67108864,
  "maximumRetainedStandardErrorBytes": 67108864,
  "acceptedExitCodes": [0],
  "requiredAssemblyIdentities": ["Assembly-CSharp"],
  "snapshotInputs": [
    { "relativePath": "GameAssembly.dll", "role": "gameAssembly" },
    { "relativePath": "Schedule I_Data/il2cpp_data/Metadata/global-metadata.dat", "role": "globalMetadata" },
    { "relativePath": "Schedule I.exe", "role": "executableSupport" }
  ],
  "unityVersionSources": [
    "Schedule I_Data/globalgamemanagers",
    "Schedule I_Data/data.unity3d"
  ]
}
```

Use this policy document; Phase 3 records its digest but does not apply it:

```json
{
  "schemaVersion": 1,
  "policyId": "managed-assemblies-v1",
  "policyVersion": 1,
  "requiredAssemblyIdentities": ["Assembly-CSharp"],
  "minimumManagedAssemblyCount": 1,
  "minimumTypeDefinitionCount": 1,
  "minimumMethodDefinitionCount": 1,
  "minimumTotalManagedBytes": 1048576,
  "comparativeWarningRelativeChange": 0.25,
  "catastrophicDecreaseRelativeChange": 0.80
}
```

The first tests must assert exact values, strict case-sensitive IDs, unknown-property rejection, missing-field rejection, duplicate identity rejection, invalid relative paths, non-integral timeout values, invalid ratios, duplicate repository files for one logical ID, and the absence of any raw `arguments` property.

```csharp
[Fact]
public void GetRequired_LoadsExactProductionProfile()
{
    var profile = provider.GetRequired("cpp2il-reconstructed-assemblies-v1");

    Assert.Equal(1, profile.Profile.SchemaVersion);
    Assert.Equal(1, profile.Profile.ProfileVersion);
    Assert.Equal(1, profile.Profile.AdapterVersion);
    Assert.Equal("Schedule I", profile.Profile.ExecutableName);
    Assert.Equal("dll_il_recovery", profile.Profile.OutputFormat);
    Assert.Equal(TimeSpan.FromMinutes(30), profile.Profile.Timeout);
    Assert.Equal(64L * 1024 * 1024, profile.Profile.MaximumRetainedStandardOutputBytes);
    Assert.Equal([0], profile.Profile.AcceptedExitCodes);
    Assert.DoesNotContain("arguments", File.ReadAllText(profilePath), StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

```powershell
dotnet test tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj --filter "FullyQualifiedName~ExtractionConfigurationFingerprintTests"
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "FullyQualifiedName~RepositoryExtractionProfileProviderTests|FullyQualifiedName~RepositoryValidationPolicyProviderTests"
```

Expected: compilation fails because the configuration contracts and providers do not exist.

- [ ] **Step 3: Add immutable Core contracts and deterministic fingerprints**

Define the profile without an arbitrary argument field:

```csharp
public sealed record ExtractionProfile(
    int SchemaVersion,
    string ProfileId,
    int ProfileVersion,
    int AdapterVersion,
    int ExtractionSchemaVersion,
    string ExecutableName,
    string OutputFormat,
    TimeSpan Timeout,
    long MaximumRetainedStandardOutputBytes,
    long MaximumRetainedStandardErrorBytes,
    IReadOnlyList<int> AcceptedExitCodes,
    IReadOnlyList<string> RequiredAssemblyIdentities,
    IReadOnlyList<SnapshotInputDefinition> SnapshotInputs,
    IReadOnlyList<string> UnityVersionSources);

public sealed record SnapshotInputDefinition(string RelativePath, string Role);
public sealed record ResolvedExtractionProfile(
    ExtractionProfile Profile,
    string ProfileDigest);

public interface IExtractionProfileProvider
{
    ResolvedExtractionProfile GetRequired(string profileId);
}
```

Define `ValidationPolicy` with the exact scalar fields from the JSON document and return it as `ResolvedValidationPolicy`. Fingerprints use identity kinds `extraction-profile` and `validation-policy`, identity version 1. Sort semantically unordered accepted exit codes and required assembly identities before hashing; preserve the meaningful order of `unityVersionSources`.

- [ ] **Step 4: Implement strict serializers, validators, and repository providers**

Use the existing strict JSON settings:

```csharp
private static readonly JsonSerializerOptions JsonOptions = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    AllowTrailingCommas = false,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
};
```

Validation must require schema/version integers equal to 1; lower-case safe IDs; positive limits; timeout exactly 1800 for the committed v1 profile; ratios strictly between 0 and 1 with catastrophic decrease greater than warning change; ordinal uniqueness; contained `/`-normalized snapshot paths; safe roles; and exact `Schedule I`/`dll_il_recovery` production values for this profile ID. Repository providers enumerate top-level `*.json` files deterministically and fail on duplicate IDs.

- [ ] **Step 5: Copy configuration into build and publish output**

Add both directory groups without changing the existing tool group:

```xml
<Content Include="..\..\config\extraction\*.json"
         Link="config\extraction\%(Filename)%(Extension)"
         CopyToOutputDirectory="PreserveNewest"
         CopyToPublishDirectory="PreserveNewest" />
<Content Include="..\..\config\validation\*.json"
         Link="config\validation\%(Filename)%(Extension)"
         CopyToOutputDirectory="PreserveNewest"
         CopyToPublishDirectory="PreserveNewest" />
```

- [ ] **Step 6: Prove every effective field changes its digest**

Use member data that mutates one effective field per case. Also prove JSON whitespace/property ordering do not affect either digest and absolute repository paths are excluded.

```csharp
[Theory]
[MemberData(nameof(ProfileMutations))]
public void Create_WhenAnyEffectiveFieldChanges_ChangesDigest(
    Func<ExtractionProfile, ExtractionProfile> mutate)
{
    Assert.NotEqual(
        ExtractionProfileFingerprint.Create(ProfileFixture.Valid),
        ExtractionProfileFingerprint.Create(mutate(ProfileFixture.Valid)));
}
```

- [ ] **Step 7: Run focused and project tests**

```powershell
dotnet test tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "FullyQualifiedName~Profiles"
```

Expected: all pass with zero warnings.

- [ ] **Step 8: Commit Task 1**

```powershell
git add -- config/extraction config/validation src/S1Atlas.Core/Extraction src/S1Atlas.Extraction/Profiles src/S1Atlas.Cli/S1Atlas.Cli.csproj tests/S1Atlas.Core.Tests/Extraction/ExtractionConfigurationFingerprintTests.cs tests/S1Atlas.Extraction.Tests/Profiles
git commit -m "feat: add repository extraction configuration"
```

---

### Task 2: Define Extraction Identity and the Attempt Lifecycle

**Files:**
- Create: `src/S1Atlas.Core/Extraction/ExtractionRecipe.cs`
- Create: `src/S1Atlas.Core/Extraction/ExtractionRecipeId.cs`
- Create: `src/S1Atlas.Core/Extraction/ExtractionAttempt.cs`
- Create: `src/S1Atlas.Core/Extraction/ExtractionAttemptLifecycle.cs`
- Create: `src/S1Atlas.Core/Extraction/ExtractionAttemptStatus.cs`
- Create: `src/S1Atlas.Core/Extraction/ExtractionFailureStage.cs`
- Create: `src/S1Atlas.Core/Extraction/ExtractionFailureCode.cs`
- Create: `src/S1Atlas.Core/Extraction/ExtractionInputSource.cs`
- Create: `src/S1Atlas.Core/Extraction/InputManifest.cs`
- Create: `src/S1Atlas.Core/Extraction/InputManifestFingerprint.cs`
- Create: `src/S1Atlas.Core/Extraction/InputSnapshot.cs`
- Create: `src/S1Atlas.Core/Extraction/ExtractionOperationException.cs`
- Create: `src/S1Atlas.Core/Extraction/IIIl2CppExtractor.cs`
- Test: `tests/S1Atlas.Core.Tests/Extraction/ExtractionRecipeIdTests.cs`
- Test: `tests/S1Atlas.Core.Tests/Extraction/ExtractionAttemptLifecycleTests.cs`
- Test: `tests/S1Atlas.Core.Tests/Extraction/InputManifestFingerprintTests.cs`

**Interfaces:**
- Consumes: immutable build ID, `ToolInstance`, profile/policy digests, and `CanonicalHashWriter`.
- Produces: deterministic `recipe_id`, canonical input-manifest digests, a legal state-transition function, stable failure stage/code values, and the `IIIl2CppExtractor` boundary consumed by Task 9.

- [ ] **Step 1: Write RED recipe and manifest identity tests**

Pin recipe identity to exactly these inputs:

```csharp
var recipe = new ExtractionRecipe(
    BuildId: new string('a', 64),
    ToolInstanceId: new string('b', 64),
    ProfileDigest: new string('c', 64),
    AdapterVersion: 1,
    ExtractionSchemaVersion: 1);

var id = ExtractionRecipeId.Create(recipe);

Assert.Matches("^[0-9a-f]{64}$", id);
```

Tests must prove the build ID, tool instance ID, profile digest, adapter version, and extraction schema version each affect the result. Validation-policy digest, absolute paths, attempt ID, process ID, and timestamps must not be accepted by the `ExtractionRecipe` constructor and therefore cannot affect recipe identity.

Define input manifest entries as normalized relative path, role, size, SHA-256, and last-write UTC. Prove the canonical manifest digest sorts entries by normalized relative path using ordinal comparison, includes role/size/hash, and excludes timestamps and absolute roots.

- [ ] **Step 2: Write RED lifecycle tests including `ProcessCompleted`**

The exact status set is:

```csharp
public enum ExtractionAttemptStatus
{
    Created,
    Preparing,
    Running,
    Validating,
    ProcessCompleted,
    Succeeded,
    Failed,
    Canceled,
    Abandoned
}
```

`Validating` and `Succeeded` are reserved for Phase 4; Phase 3 never transitions into them. Test these legal Phase 3 edges:

```text
Created   -> Preparing | Failed | Canceled | Abandoned
Preparing -> Running   | Failed | Canceled | Abandoned
Running   -> ProcessCompleted | Failed | Canceled | Abandoned
Validating -> Succeeded | Failed | Canceled | Abandoned   (reserved Phase 4 rule)
```

All five terminal states are immutable:

```text
ProcessCompleted Succeeded Failed Canceled Abandoned
```

Tests must reject `Running -> Succeeded`, `ProcessCompleted -> Validating`, every terminal-state transition, failure metadata on `ProcessCompleted`, missing candidate output on `ProcessCompleted`, result extraction IDs on any Phase 3 state, and a terminal failure without stage/code/message.

- [ ] **Step 3: Run the Core tests and verify RED**

```powershell
dotnet test tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj --filter "FullyQualifiedName~ExtractionRecipeIdTests|FullyQualifiedName~ExtractionAttemptLifecycleTests|FullyQualifiedName~InputManifestFingerprintTests"
```

Expected: compilation fails because the domain types do not exist.

- [ ] **Step 4: Implement deterministic identities**

Use identity kind `extraction-recipe`, identity version 1, and append fields in the approved order:

```csharp
using var writer = new CanonicalHashWriter("extraction-recipe", 1);
writer.AppendString(recipe.BuildId);
writer.AppendString(recipe.ToolInstanceId);
writer.AppendString(recipe.ProfileDigest);
writer.AppendInt32(recipe.AdapterVersion);
writer.AppendInt32(recipe.ExtractionSchemaVersion);
return writer.Complete();
```

Use identity kind `input-manifest`, identity version 1. Prefix with the sorted entry count, normalize `\` to `/`, reject rooted/dot/dot-dot/empty path segments, and append relative path, role, size, and lower-case SHA-256. Do not append source timestamps.

- [ ] **Step 5: Implement the attempt record and lifecycle guard**

The record carries all Phase 3 facts without using a mutable bag:

```csharp
public sealed record ExtractionAttempt(
    string AttemptId,
    string? RecipeId,
    string BuildId,
    string? ToolInstanceId,
    string ProfileId,
    int ProfileVersion,
    string ProfileDigest,
    string ValidationPolicyId,
    int ValidationPolicyVersion,
    string ValidationPolicyDigest,
    int AdapterVersion,
    int ExtractionSchemaVersion,
    ExtractionInputSource? InputSource,
    string? InputSnapshotId,
    ExtractionAttemptStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? PreInputManifestDigest,
    string? PostInputManifestDigest,
    string WorkingPath,
    string StandardOutputPath,
    string StandardErrorPath,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated,
    long StandardOutputDiscardedBytes,
    long StandardErrorDiscardedBytes,
    int? ProcessId,
    int? ProcessExitCode,
    ExtractionFailureStage? FailureStage,
    ExtractionFailureCode? FailureCode,
    string? FailureMessage,
    bool KeepFailedArtifacts,
    int DiscardedFileCount,
    long DiscardedByteCount,
    string? CandidateOutputPath,
    string? ResultExtractionId);
```

`ExtractionAttemptLifecycle.Transition(current, next)` validates legal edges and state invariants before returning `next`. IDs are lower-case `Guid.NewGuid().ToString("N")`. Failure-stage and failure-code enums contain every stable value from design sections 16.1/16.2. Add only the two Phase 3 orchestration codes the representative design list did not name: `CustomToolPathInvalid` and `ExtractionAlreadyActive`. `ValidationUnavailable` is deliberately **not** added because `ProcessCompleted` is not a failure.

- [ ] **Step 6: Add typed operation and extractor boundaries**

```csharp
public sealed class ExtractionOperationException : Exception
{
    public ExtractionOperationException(
        ExtractionFailureStage stage,
        ExtractionFailureCode code,
        string message,
        string? attemptId = null,
        Exception? innerException = null);

    public ExtractionFailureStage Stage { get; }
    public ExtractionFailureCode Code { get; }
    public string? AttemptId { get; }
}

public interface IIl2CppExtractor
{
    Task<ExtractionProcessResult> ExtractAsync(
        ExtractionProcessRequest request,
        Func<int, CancellationToken, Task> processStarted,
        CancellationToken cancellationToken);
}

public sealed record ExtractionProcessRequest(
    string ExecutablePath,
    string GameRoot,
    string WorkingDirectory,
    string OutputDirectory,
    string StandardOutputPath,
    string StandardErrorPath,
    ResolvedExtractionProfile Profile);

public sealed record ExtractionLogResult(
    string Path,
    long RetainedBytes,
    long DiscardedBytes,
    bool Truncated);

public enum ExtractionProcessTerminationReason
{
    Exited,
    StartFailed,
    TimedOut,
    StartPersistenceFailed
}

public sealed record ExtractionProcessResult(
    ExtractionProcessTerminationReason TerminationReason,
    int? ProcessId,
    int? ExitCode,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    ExtractionLogResult StandardOutput,
    ExtractionLogResult StandardError,
    string? StartFailureMessage);
```

`ExtractionProcessRequest` contains only executable path, game root, Atlas working/output/log paths, and the resolved profile. It has no arbitrary argument or environment property. `Cpp2IlProcessExtractor` supplies the one production environment override, `NO_COLOR=true`, internally. Generic `ProcessRunner` tests exercise additional explicit environment values without widening this Core boundary.

- [ ] **Step 7: Run Core tests**

```powershell
dotnet test tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj
```

Expected: all tests pass with zero warnings.

- [ ] **Step 8: Commit Task 2**

```powershell
git add -- src/S1Atlas.Core/Extraction tests/S1Atlas.Core.Tests/Extraction
git commit -m "feat: define extraction attempt lifecycle"
```

---
### Task 3: Add Schema Version 4 and Atomic Phase 3 Persistence

**Files:**
- Create: `src/S1Atlas.Core/Storage/IExtractionRepository.cs`
- Modify: `src/S1Atlas.Storage/Migrations/SqliteMigrations.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.cs`
- Create: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Extractions.cs`
- Create: `tests/S1Atlas.Storage.Tests/Migrations/ExtractionAttemptMigrationTests.cs`
- Create: `tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryExtractionTests.cs`
- Modify: `tests/S1Atlas.IntegrationTests/Foundation/FoundationMigrationTests.cs`

**Interfaces:**
- Consumes: `ExtractionAttempt`, `InputSnapshot`, existing builds/environment snapshots, and the append-only migration ledger.
- Produces: schema version 4 plus atomic create/transition/read APIs for attempts, build/observation lookup for input resolution, and atomic snapshot/file persistence.

- [ ] **Step 1: Write RED migration tests from real schema-version-3 databases**

Create a v3 database by running the committed migrations 1-3, seed a current build, a second historical build, installation observations, and a verified managed tool, then initialize the new repository. Assert:

```csharp
Assert.Equal([1, 2, 3, 4], await ReadMigrationVersionsAsync(databasePath));
Assert.True(await TableExistsAsync(databasePath, "extraction_attempts"));
Assert.True(await TableExistsAsync(databasePath, "input_snapshots"));
Assert.True(await TableExistsAsync(databasePath, "input_snapshot_files"));
Assert.Equal(seed.CurrentBuildId, (await repository.GetCurrentSnapshotAsync(token))!.Build.BuildId);
Assert.NotNull(await repository.GetManagedToolAsync("cpp2il", "test-version", "win-x64", token));
```

Also assert one `atlas-before-schema-4-*.db` backup, idempotent second initialization, unchanged checksums/text for migrations 1-3, transactional rollback under an intentionally failing version 4, and no modification of an unknown schema.

- [ ] **Step 2: Write RED repository tests for lifecycle concurrency and terminal immutability**

Cover:

```text
create Created attempt once
duplicate attempt ID rejected
Created -> Preparing updates exactly once
stale expected status loses optimistic transition
ProcessCompleted cannot transition
Failed/Canceled/Abandoned cannot transition
nonterminal listing excludes every terminal state
attempt round-trips every nullable/scalar field exactly
current build and historical build lookup
installation observations ordered captured_at_utc DESC then snapshot_id DESC
input snapshot and files commit atomically
duplicate snapshot ID with identical facts is an idempotent no-op
duplicate snapshot ID with different facts is rejected
```

Use an explicit expected status:

```csharp
await repository.TransitionAttemptAsync(
    runningAttempt,
    expectedStatus: ExtractionAttemptStatus.Preparing,
    cancellationToken);

await Assert.ThrowsAsync<InvalidOperationException>(() =>
    repository.TransitionAttemptAsync(
        conflictingAttempt,
        expectedStatus: ExtractionAttemptStatus.Created,
        cancellationToken));
```

- [ ] **Step 3: Run focused Storage tests and verify RED**

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj --filter "FullyQualifiedName~ExtractionAttemptMigrationTests|FullyQualifiedName~SqliteAtlasRepositoryExtractionTests"
```

Expected: compilation fails because migration 4 and extraction persistence do not exist.

- [ ] **Step 4: Add migration 4 without altering prior migration text**

Append `ExtractionAttemptsV4Sql` and migration metadata:

```csharp
new(4, "extraction-attempts-v4", ExtractionAttemptsV4Sql)
```

The migration creates `input_snapshots` and `input_snapshot_files` before `extraction_attempts`. The attempt table uses nullable resolution fields so tool/input failures after build selection can still be recorded, but state validation in Core prevents later states without their required facts.

```sql
CREATE TABLE input_snapshots (
    input_snapshot_id TEXT NOT NULL PRIMARY KEY,
    build_id TEXT NOT NULL,
    root_path TEXT NOT NULL,
    manifest_digest TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    replay_verified INTEGER NOT NULL CHECK (replay_verified IN (0, 1)),
    replay_verified_at_utc TEXT NULL,
    FOREIGN KEY (build_id) REFERENCES builds(build_id)
);

CREATE TABLE input_snapshot_files (
    input_snapshot_id TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    role TEXT NOT NULL,
    size INTEGER NOT NULL CHECK (size >= 0),
    sha256 TEXT NOT NULL,
    PRIMARY KEY (input_snapshot_id, relative_path),
    FOREIGN KEY (input_snapshot_id)
        REFERENCES input_snapshots(input_snapshot_id)
        ON DELETE CASCADE
);

CREATE TABLE extraction_attempts (
    attempt_id TEXT NOT NULL PRIMARY KEY,
    recipe_id TEXT NULL,
    build_id TEXT NOT NULL,
    tool_instance_id TEXT NULL,
    profile_id TEXT NOT NULL,
    profile_version INTEGER NOT NULL,
    profile_digest TEXT NOT NULL,
    validation_policy_id TEXT NOT NULL,
    validation_policy_version INTEGER NOT NULL,
    validation_policy_digest TEXT NOT NULL,
    adapter_version INTEGER NOT NULL,
    extraction_schema_version INTEGER NOT NULL,
    input_source TEXT NULL,
    input_snapshot_id TEXT NULL,
    status TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    started_at_utc TEXT NULL,
    completed_at_utc TEXT NULL,
    pre_input_manifest_digest TEXT NULL,
    post_input_manifest_digest TEXT NULL,
    working_path TEXT NOT NULL,
    stdout_path TEXT NOT NULL,
    stderr_path TEXT NOT NULL,
    stdout_truncated INTEGER NOT NULL CHECK (stdout_truncated IN (0, 1)),
    stderr_truncated INTEGER NOT NULL CHECK (stderr_truncated IN (0, 1)),
    stdout_discarded_bytes INTEGER NOT NULL CHECK (stdout_discarded_bytes >= 0),
    stderr_discarded_bytes INTEGER NOT NULL CHECK (stderr_discarded_bytes >= 0),
    process_id INTEGER NULL,
    process_exit_code INTEGER NULL,
    failure_stage TEXT NULL,
    failure_code TEXT NULL,
    failure_message TEXT NULL,
    keep_failed_artifacts INTEGER NOT NULL CHECK (keep_failed_artifacts IN (0, 1)),
    discarded_file_count INTEGER NOT NULL CHECK (discarded_file_count >= 0),
    discarded_byte_count INTEGER NOT NULL CHECK (discarded_byte_count >= 0),
    candidate_output_path TEXT NULL,
    result_extraction_id TEXT NULL,
    FOREIGN KEY (build_id) REFERENCES builds(build_id),
    FOREIGN KEY (tool_instance_id) REFERENCES tool_instances(tool_instance_id),
    FOREIGN KEY (input_snapshot_id) REFERENCES input_snapshots(input_snapshot_id)
);

CREATE INDEX ix_extraction_attempts_build_created
ON extraction_attempts(build_id, created_at_utc DESC);
CREATE INDEX ix_extraction_attempts_recipe
ON extraction_attempts(recipe_id);
CREATE INDEX ix_extraction_attempts_status
ON extraction_attempts(status);
```

Do not create validated-extraction, artifact, validation-issue, or preference tables in this migration.

- [ ] **Step 5: Add the extraction repository interface**

```csharp
public interface IExtractionRepository
{
    Task<GameBuild?> GetBuildAsync(string buildId, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationObservationRecord>> ListInstallationObservationsAsync(
        string buildId,
        CancellationToken cancellationToken);
    Task CreateAttemptAsync(ExtractionAttempt attempt, CancellationToken cancellationToken);
    Task TransitionAttemptAsync(
        ExtractionAttempt attempt,
        ExtractionAttemptStatus expectedStatus,
        CancellationToken cancellationToken);
    Task<ExtractionAttempt?> GetAttemptAsync(
        string attemptId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ExtractionAttempt>> ListNonTerminalAttemptsAsync(
        CancellationToken cancellationToken);
    Task SaveInputSnapshotAsync(
        InputSnapshot snapshot,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<InputSnapshot>> ListReplayVerifiedInputSnapshotsAsync(
        string buildId,
        CancellationToken cancellationToken);
}
```

`InstallationObservationRecord` contains snapshot ID, build ID, captured UTC, installation root, game-assembly path, and metadata path. It does not create a second authoritative build identity.

- [ ] **Step 6: Implement atomic writes and exact enum parsing**

Make `SqliteAtlasRepository` implement `IExtractionRepository` through a partial file. `TransitionAttemptAsync` begins a transaction, reads the current status, calls `ExtractionAttemptLifecycle.Transition`, and updates with both attempt ID and expected status in the `WHERE` clause. A zero row count is a concurrency failure. Serialize enums with `ToString()` and parse with `Enum.Parse(..., ignoreCase: false)`.

Snapshot persistence inserts the header and all sorted files in one transaction. If an existing ID is found, read it and require structural equality before returning an idempotent success.

- [ ] **Step 7: Run migration, Storage, and compatibility tests**

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter "FullyQualifiedName~FoundationMigrationTests"
```

Expected: all pass; existing v1/v2/v3 upgrade tests remain green and exactly one schema-4 backup is observed where applicable.

- [ ] **Step 8: Commit Task 3**

```powershell
git add -- src/S1Atlas.Core/Storage/IExtractionRepository.cs src/S1Atlas.Storage/Migrations/SqliteMigrations.cs src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.cs src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Extractions.cs tests/S1Atlas.Storage.Tests/Migrations/ExtractionAttemptMigrationTests.cs tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryExtractionTests.cs tests/S1Atlas.IntegrationTests/Foundation/FoundationMigrationTests.cs
git commit -m "feat: persist phase 3 extraction state"
```

---

### Task 4: Build the Isolated Process Runner and Bounded Log Capture

**Files:**
- Create: `tests/S1Atlas.FakeCpp2Il/S1Atlas.FakeCpp2Il.csproj`
- Create: `tests/S1Atlas.FakeCpp2Il/Program.cs`
- Modify: `S1Atlas.sln`
- Modify: `tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj`
- Create: `src/S1Atlas.Extraction/Processes/ProcessRequest.cs`
- Create: `src/S1Atlas.Extraction/Processes/ProcessResult.cs`
- Create: `src/S1Atlas.Extraction/Processes/BoundedLogWriter.cs`
- Create: `src/S1Atlas.Extraction/Processes/ProcessRunner.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Processes/FakeCpp2IlLocator.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Processes/BoundedLogWriterTests.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Processes/ProcessRunnerTests.cs`

**Interfaces:**
- Consumes: an executable path, typed argument list, explicit environment overrides, Atlas-owned paths, timeout, and per-stream byte caps.
- Produces: one `ProcessResult` with start/exit/termination facts and two `BoundedLogResult` values; invokes a durable process-start callback immediately after start and kills the process tree if that callback fails.

- [ ] **Step 1: Add the source-built fake executable and RED process tests**

The fake executable supports these modes through its own typed test arguments:

```text
process success <exit-code>
process emit <stdout-bytes> <stderr-bytes>
process wait
process spawn-child <child-pid-file>
process print-args <output-file> <remaining args...>
cpp2il --help
cpp2il --list-output-formats
cpp2il extraction arguments
```

The Cpp2IL mode writes a deterministic fake file only under the supplied `--output-to` path. It records received arguments when `S1ATLAS_FAKE_ARGUMENT_RECORD` is explicitly supplied by a test. It never reads outside the temporary fake game root.

Add the console project to the solution and reference it for build ordering with `ReferenceOutputAssembly="false"`. `FakeCpp2IlLocator` walks from `AppContext.BaseDirectory` to `S1Atlas.sln`, then resolves `tests/S1Atlas.FakeCpp2Il/bin/<Configuration>/net8.0/S1Atlas.FakeCpp2Il.exe`; it fails with a diagnostic path if the apphost is absent. No executable is checked into Git.

- [ ] **Step 2: Specify bounded logging in RED**

Use small injected caps in tests and production caps from the profile:

```csharp
var result = await writer.DrainAsync(source, logPath, maximumRetainedBytes: 1024, token);

Assert.True(result.Truncated);
Assert.Equal(totalBytes - 1024, result.DiscardedBytes);
Assert.Equal(1024, result.RetainedBytes);
Assert.EndsWith(
    $"[S1Atlas log truncated; discarded {result.DiscardedBytes} bytes]{Environment.NewLine}",
    await File.ReadAllTextAsync(logPath, token),
    StringComparison.Ordinal);
```

Tests must prove the writer continues reading after its cap, uses `FileMode.CreateNew`, counts bytes rather than UTF-16 characters, writes the marker once, preserves an empty stream as an empty file, rejects negative/zero caps, cleans a newly owned partial log after write failure, and never overwrites an existing log.

- [ ] **Step 3: Specify process behavior in RED**

Cover success, rejected/nonzero exit reporting, exact argument boundaries including spaces/metacharacters, working directory, environment override, start failure, timeout, caller cancellation, simultaneous large stdout/stderr, process-start callback ordering, callback failure, and child-tree termination.

```csharp
var request = new ProcessRequest(
    ExecutablePath: fakeExe,
    WorkingDirectory: working,
    Arguments: ["process", "print-args", recordPath, "a b", "&", "$(literal)"],
    EnvironmentOverrides: new Dictionary<string, string?> { ["NO_COLOR"] = "true" },
    StandardOutputPath: stdoutPath,
    StandardErrorPath: stderrPath,
    MaximumRetainedStandardOutputBytes: 4096,
    MaximumRetainedStandardErrorBytes: 4096,
    Timeout: TimeSpan.FromSeconds(10));

var result = await runner.RunAsync(request, ProcessStarted, token);

Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
Assert.Equal(["a b", "&", "$(literal)"], await ReadRecordedArguments(recordPath));
```

- [ ] **Step 4: Run focused tests and verify RED**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "FullyQualifiedName~BoundedLogWriterTests|FullyQualifiedName~ProcessRunnerTests"
```

Expected: compilation fails because the process boundary does not exist.

- [ ] **Step 5: Implement exact request/result contracts**

```csharp
internal sealed record ProcessRequest(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> EnvironmentOverrides,
    string StandardOutputPath,
    string StandardErrorPath,
    long MaximumRetainedStandardOutputBytes,
    long MaximumRetainedStandardErrorBytes,
    TimeSpan Timeout);

internal enum ProcessTerminationReason
{
    Exited,
    StartFailed,
    TimedOut,
    Canceled,
    StartPersistenceFailed
}

internal sealed record BoundedLogResult(
    string Path,
    long RetainedBytes,
    long DiscardedBytes,
    bool Truncated);
```

`ProcessResult` contains termination reason, process ID, nullable exit code, start/end UTC, stdout/stderr results, and a start-failure message. It does not decide whether an exit code is accepted by a profile.

- [ ] **Step 6: Implement no-shell execution and deadlock-free termination**

Create the start info exactly as follows and add each argument independently:

```csharp
var startInfo = new ProcessStartInfo
{
    FileName = request.ExecutablePath,
    WorkingDirectory = request.WorkingDirectory,
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};
foreach (var argument in request.Arguments)
{
    startInfo.ArgumentList.Add(argument);
}
```

Start both `BoundedLogWriter.DrainAsync` tasks immediately after `Process.Start()`. Once the child owns the pipe handles, pass `CancellationToken.None` to those two drains: timeout/caller cancellation controls the child process, not consumption of already-available pipe bytes. Invoke the callback with the child PID before waiting. Use separate timeout and caller tokens so `TimedOut` and `Canceled` remain distinct. On timeout, caller cancellation, or callback failure, call `Kill(entireProcessTree: true)`, tolerate only the documented already-exited/unsupported process exceptions, await process exit, and await both drain tasks before returning or rethrowing cancellation.

If process start fails before stream drains exist, create both requested log files as owned zero-byte `FileMode.CreateNew` files and return zero-byte `BoundedLogResult` values with `StartFailed`. `Cpp2IlProcessExtractor` maps runner cancellation to `OperationCanceledException`, maps `StartPersistenceFailed` without losing its callback exception, and maps the other runner termination reasons one-for-one into `ExtractionProcessTerminationReason`.

- [ ] **Step 7: Run focused stress tests repeatedly**

```powershell
1..5 | ForEach-Object {
  dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "FullyQualifiedName~ProcessRunnerTests|FullyQualifiedName~BoundedLogWriterTests" --no-restore
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
```

Expected: five green runs, no deadlocks, no surviving fake child process, and no warnings.

- [ ] **Step 8: Commit Task 4**

```powershell
git add -- S1Atlas.sln tests/S1Atlas.FakeCpp2Il tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj tests/S1Atlas.Extraction.Tests/Processes src/S1Atlas.Extraction/Processes
git commit -m "feat: run isolated extraction processes"
```

---

### Task 5: Resolve a Verified Managed or Explicit Custom Cpp2IL Instance

**Files:**
- Create: `src/S1Atlas.Extraction/Tools/ManagedToolInstanceFactory.cs`
- Create: `src/S1Atlas.Extraction/Tools/ExtractionToolResolver.cs`
- Modify: `src/S1Atlas.Extraction/Tools/ManagedToolService.cs`
- Modify: `src/S1Atlas.Core/Storage/IToolRepository.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Tools.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Tools/ExtractionToolResolverTests.cs`
- Modify: `tests/S1Atlas.Extraction.Tests/Tools/ManagedToolServiceTests.cs`
- Modify: `tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryToolTests.cs`

**Interfaces:**
- Consumes: committed Cpp2IL definition, managed installation validator, capability probes, file hasher, tool repository, optional `--cpp2il-path`, and Atlas tools root.
- Produces: a freshly verified `ResolvedExtractionTool` with a stable `ToolInstance`, executable path, definition, and probe evidence. It performs no HTTP operation.

- [ ] **Step 1: Write RED managed-resolution tests**

Assert that no override:

```text
loads exactly cpp2il for the current platform
returns ToolNotInstalled with install instructions when absent
rejects Incomplete/DefinitionMismatch/Corrupt/ProbeFailed
hashes and probes immediately before returning
persists the ManagedPinned tool instance
returns the exact managed executable path and hash
makes zero HTTP calls
```

The resolver must not trust only the database row. Mutate the installed executable after registration and assert `ToolChecksumMismatch`/`Corrupt` before any extraction process callback is reached.

- [ ] **Step 2: Write RED custom-resolution tests**

Cover regular custom executable success, missing path, directory path, reparse-point executable, failed hash, failed probe, custom instance persistence, path changes excluded from identity, byte changes included in identity, and an override pointing anywhere under the managed tools root.

```csharp
var resolved = await resolver.ResolveAsync(customPath, token);

Assert.Equal(ToolTrustLevel.CustomOverride, resolved.Instance.TrustLevel);
Assert.Null(resolved.Instance.DefinitionDigest);
Assert.Null(resolved.Instance.PackageSha256);
Assert.Equal(expectedSha256, resolved.Instance.ExecutableSha256);
Assert.Equal(Path.GetFullPath(customPath), resolved.ExecutablePath);
```

An override inside the Atlas managed-tools root must fail with `CustomToolPathInvalid`; a modified managed executable is never relabeled `CustomOverride`. Walk every existing segment from the custom path's volume root and reject a reparse-point ancestor, not only a reparse-point executable leaf.

- [ ] **Step 3: Run focused tests and verify RED**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "FullyQualifiedName~ExtractionToolResolverTests"
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj --filter "FullyQualifiedName~SqliteAtlasRepositoryToolTests"
```

Expected: compilation fails because extraction-time tool resolution and standalone tool-instance persistence do not exist.

- [ ] **Step 4: Extract the shared managed instance factory**

Move the existing private construction logic from `ManagedToolService` into one internal factory used by both services:

```csharp
internal static ToolInstance Create(
    ResolvedToolDefinition definition,
    ManagedToolInstallation installation)
```

Keep the existing identity inputs unchanged: tool ID/name, observed executable SHA-256, platform, and `ManagedPinned`. Absolute path, display/version label, definition digest, package digest, and timestamps remain provenance only.

- [ ] **Step 5: Add standalone tool-instance persistence**

Extend `IToolRepository`:

```csharp
Task SaveToolInstanceAsync(
    ToolInstance toolInstance,
    CancellationToken cancellationToken);
```

Reuse the existing `tool_instances` upsert. Preserve `first_observed_at_utc` on conflict; update last-verified/status/path/provenance only when the stable ID is the same. `SaveVerifiedManagedToolAsync` continues committing installation plus instance in one transaction.

- [ ] **Step 6: Implement offline extraction-time resolution**

The public contract is:

```csharp
internal sealed record ResolvedExtractionTool(
    ResolvedToolDefinition Definition,
    ToolInstance Instance,
    string ExecutablePath,
    IReadOnlyList<ToolProbeResult> ProbeResults);

internal sealed class ExtractionToolResolver
{
    public Task<ResolvedExtractionTool> ResolveAsync(
        string? customExecutablePath,
        CancellationToken cancellationToken);
}
```

For managed resolution, call `ManagedToolInstallationValidator.InspectAsync`, require `Verified`, rebuild and persist the managed instance, and return the exact contained executable. For custom resolution, normalize the explicit path, reject directories/reparse points/managed-root containment, hash it, run the committed definition probes from its own parent working directory, require every probe to succeed, create a `CustomOverride` instance with null definition/package digests and version label, and persist it. Map failures to stable tool-resolution codes without exposing a stack trace.

- [ ] **Step 7: Run focused and regression tests**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "FullyQualifiedName~Tools"
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj --filter "FullyQualifiedName~Tool"
```

Expected: all Phase 2 tests remain green and tool resolution performs no network request.

- [ ] **Step 8: Commit Task 5**

```powershell
git add -- src/S1Atlas.Extraction/Tools/ManagedToolInstanceFactory.cs src/S1Atlas.Extraction/Tools/ExtractionToolResolver.cs src/S1Atlas.Extraction/Tools/ManagedToolService.cs src/S1Atlas.Core/Storage/IToolRepository.cs src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Tools.cs tests/S1Atlas.Extraction.Tests/Tools tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryToolTests.cs
git commit -m "feat: resolve verified Cpp2IL instances"
```

---

### Task 6: Resolve and Verify Exact Live or Archived Game Inputs

**Files:**
- Create: `src/S1Atlas.Extraction/Inputs/ExtractionInputResolver.cs`
- Create: `src/S1Atlas.Extraction/Inputs/LiveInputVerifier.cs`
- Modify: `src/S1Atlas.Extraction/Discovery/WindowsScheduleOneLocator.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Inputs/ExtractionInputResolverTests.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Inputs/LiveInputVerifierTests.cs`
- Modify: `tests/S1Atlas.Extraction.Tests/Discovery/WindowsScheduleOneLocatorTests.cs`

**Interfaces:**
- Consumes: selected `GameBuild`, optional explicit game root, stored installation observations, conventional Steam candidates, replay-verified snapshots, profile input declarations, and `IFileHasher`.
- Produces: one `ResolvedExtractionInput` plus canonical pre/post `InputManifest` values. Explicit paths never silently fall back, and a candidate is accepted only when content hashes match the selected build.

- [ ] **Step 1: Write RED live-verification tests**

Use temporary files with deterministic bytes. Cover matching build, GameAssembly mismatch, metadata mismatch, missing file, regular-file requirement, reparse-point rejection, cancellation between files, size/last-write observation, and a file changing during hash capture.

```csharp
var manifest = await verifier.CaptureAsync(
    installation,
    selectedBuild,
    profile,
    token);

Assert.Equal(selectedBuild.GameAssemblySha256,
    manifest.Files.Single(file => file.Role == "gameAssembly").Sha256);
Assert.Equal(selectedBuild.MetadataSha256,
    manifest.Files.Single(file => file.Role == "globalMetadata").Sha256);
```

`VerifyUnchanged(pre, post, build)` must throw `InputChangedDuringExtraction` when either content hash differs from the selected build or from the pre-run manifest. Size and last-write changes are recorded for diagnostics but hashes decide acceptance.

- [ ] **Step 2: Write RED resolution-order tests**

Test this exact order:

```text
1. explicit --game-path, when supplied
2. stored installation observations for the selected build, newest first
3. conventional local Steam candidates
4. replay-verified archived input snapshots, newest first
```

An explicit path is authoritative: if it is missing or mismatched, return `LiveInputNotFound` or `BuildInputMismatch` and do not fall through. For implicit candidates, skip missing/mismatched roots and continue. Deduplicate normalized paths case-insensitively before hashing. Historical selection never rewrites the stored build.

Tests also cover current-build selection, explicit historical build selection, unknown build, no current snapshot, stored observation fallback, Steam fallback, archived fallback, invalid archived snapshot rejection, and total failure with a concise rescan message.

- [ ] **Step 3: Specify conventional Steam discovery without network access**

Extend the Windows locator behind an internal candidate-source seam. It checks:

```text
%ProgramFiles(x86)%/Steam/steamapps/common/Schedule I
%ProgramFiles%/Steam/steamapps/common/Schedule I
local library roots named in Steam/config/libraryfolders.vdf
```

The parser accepts only quoted local path values, does not use a hard-coded Schedule I app ID, ignores malformed/locked files, and never performs HTTP. Candidate order is deterministic and duplicates are removed with `StringComparer.OrdinalIgnoreCase`.

- [ ] **Step 4: Run focused tests and verify RED**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "FullyQualifiedName~ExtractionInputResolverTests|FullyQualifiedName~LiveInputVerifierTests|FullyQualifiedName~WindowsScheduleOneLocatorTests"
```

Expected: compilation/test failures because extraction input resolution and multi-library Steam discovery are absent.

- [ ] **Step 5: Implement typed input contracts and path checks**

```csharp
internal sealed record ResolvedExtractionInput(
    ExtractionInputSource Source,
    string GameRoot,
    string GameAssemblyPath,
    string GlobalMetadataPath,
    string ExecutablePath,
    string UnityVersionSourcePath,
    string? InputSnapshotId);

internal sealed class ExtractionInputResolver
{
    public Task<ResolvedExtractionInput> ResolveAsync(
        GameBuild build,
        string? explicitGamePath,
        ExtractionProfile profile,
        CancellationToken cancellationToken);
}
```

Normalize every candidate with `Path.GetFullPath`. Require the root to be a normal directory and each required input to be a regular non-reparse file. Walk existing path segments for reparse points before use. `Schedule I.exe` and the first existing profile-declared Unity-version source are required for live/snapshot execution even though only GameAssembly and metadata define the Atlas build ID.

- [ ] **Step 6: Implement race-aware manifest capture**

For each file, read attributes/length/last-write, hash with the existing asynchronous hasher, then read attributes again. If length or last-write changed during capture, hash once more and require the second observation to be stable; otherwise fail with `BuildInputMismatch` before process start or `InputChangedDuringExtraction` after process exit. Store normalized relative paths and never store a mutable absolute root in the manifest digest.

- [ ] **Step 7: Run focused and full Extraction tests**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "FullyQualifiedName~Inputs|FullyQualifiedName~WindowsScheduleOneLocatorTests"
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj
```

Expected: all pass with no proprietary paths or files involved.

- [ ] **Step 8: Commit Task 6**

```powershell
git add -- src/S1Atlas.Extraction/Inputs/ExtractionInputResolver.cs src/S1Atlas.Extraction/Inputs/LiveInputVerifier.cs src/S1Atlas.Extraction/Discovery/WindowsScheduleOneLocator.cs tests/S1Atlas.Extraction.Tests/Inputs tests/S1Atlas.Extraction.Tests/Discovery/WindowsScheduleOneLocatorTests.cs
git commit -m "feat: resolve and verify extraction inputs"
```

---

### Task 7: Create Atomic Optional Input Snapshots

**Files:**
- Create: `src/S1Atlas.Extraction/Inputs/InputSnapshotService.cs`
- Create: `src/S1Atlas.Extraction/Inputs/InputSnapshotDocumentStore.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Inputs/InputSnapshotServiceTests.cs`
- Modify: `src/S1Atlas.Core/Extraction/InputSnapshot.cs`
- Modify: `src/S1Atlas.Core/Extraction/InputManifestFingerprint.cs`

**Interfaces:**
- Consumes: verified live input, selected build, profile snapshot declarations, build inputs/staging paths, file hasher, time provider, and `IExtractionRepository`.
- Produces: an atomically promoted immutable `InputSnapshot` with `input-manifest.json`, `complete.marker`, exact copied bytes, and `ReplayVerified = false` until a later archived-only capability run proves replay.

- [ ] **Step 1: Write RED snapshot-content tests**

The initial snapshot must contain exactly:

```text
GameAssembly.dll
Schedule I_Data/il2cpp_data/Metadata/global-metadata.dat
Schedule I.exe
the first existing file in this order:
  Schedule I_Data/globalgamemanagers
  Schedule I_Data/data.unity3d
```

Assert preserved relative paths, recorded roles/sizes/lower-case hashes, source/destination equality, `ReplayVerified == false`, source build ownership, canonical manifest digest, and deterministic snapshot ID.

- [ ] **Step 2: Write RED safety, cancellation, and idempotence tests**

Cover missing support file, missing both Unity sources, source reparse point, destination reparse point, traversal in a profile fixture, case-insensitive path collision, source changes during copy, destination hash mismatch, cancellation after each copied file, database failure after filesystem promotion, existing identical snapshot, and existing conflicting snapshot.

```csharp
var first = await service.CreateAsync(input, build, profile, token);
var second = await service.CreateAsync(input, build, profile, token);

Assert.Equal(first.InputSnapshotId, second.InputSnapshotId);
Assert.Equal(first.RootPath, second.RootPath);
Assert.False(first.ReplayVerified);
Assert.Empty(Directory.EnumerateFileSystemEntries(stagingRoot));
```

- [ ] **Step 3: Run focused tests and verify RED**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "FullyQualifiedName~InputSnapshotServiceTests"
```

Expected: compilation fails because snapshot materialization does not exist.

- [ ] **Step 4: Implement deterministic copy-and-verify staging**

Use an owned directory:

```text
builds/<build-id>/inputs/.staging/<guid>/game-root/...
```

For each source file:

```text
capture stable source hash and metadata
open destination with FileMode.CreateNew
copy asynchronously
flush and close destination
hash destination
capture source again
require pre-source == destination == post-source hashes
```

Never follow reparse points. Use overflow-checked totals. On cancellation or failure, delete only the exact owned staging GUID after proving it is contained beneath the expected `.staging` root and has no reparse-point ancestor.

- [ ] **Step 5: Write normalized documents and promote atomically**

`input-manifest.json` uses camelCase, schema version 1, build ID, manifest digest, and files sorted by normalized relative path. `complete.marker` is strict JSON containing marker schema version 1, input snapshot ID, and manifest digest. Write both with `FileMode.CreateNew`; flush; write the marker last; then rename staging to:

```text
builds/<build-id>/inputs/<input-snapshot-id>
```

If an identical final directory already exists, verify its marker, manifest, and all hashes before discarding the owned staging directory. Ambiguous/conflicting existing content fails closed.

- [ ] **Step 6: Persist with filesystem recovery on database failure**

After rename, call `SaveInputSnapshotAsync`. If persistence fails, leave the fully verified final snapshot intact and return `DatabasePromotionFailed`; a retry re-verifies and registers the same ID. Never delete a proven snapshot because SQLite was unavailable.

- [ ] **Step 7: Run focused, Storage, and cancellation tests**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "FullyQualifiedName~InputSnapshotServiceTests"
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj --filter "FullyQualifiedName~InputSnapshot|FullyQualifiedName~Extraction"
```

Expected: all pass, staging is empty after owned failures, and persisted snapshots remain `ReplayVerified = false`.

- [ ] **Step 8: Commit Task 7**

```powershell
git add -- src/S1Atlas.Extraction/Inputs/InputSnapshotService.cs src/S1Atlas.Extraction/Inputs/InputSnapshotDocumentStore.cs src/S1Atlas.Core/Extraction/InputSnapshot.cs src/S1Atlas.Core/Extraction/InputManifestFingerprint.cs tests/S1Atlas.Extraction.Tests/Inputs/InputSnapshotServiceTests.cs
git commit -m "feat: archive exact extraction inputs"
```

---

### Task 8: Add Attempt Documents, the Extraction Lock, and Conservative Recovery

**Files:**
- Create: `src/S1Atlas.Extraction/Attempts/OwnedAttemptPaths.cs`
- Create: `src/S1Atlas.Extraction/Attempts/AttemptDocumentStore.cs`
- Create: `src/S1Atlas.Extraction/Attempts/ExtractionLock.cs`
- Create: `src/S1Atlas.Extraction/Attempts/ExtractionRecoveryService.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Attempts/AttemptDocumentStoreTests.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Attempts/ExtractionLockTests.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Attempts/ExtractionRecoveryServiceTests.cs`

**Interfaces:**
- Consumes: Atlas data/build roots, attempt IDs, current S1Atlas process ID, optional child PID, `IExtractionRepository`, and `TimeProvider`.
- Produces: strict atomic `attempt.json` mirrors, one exclusive lock per Atlas data root, explicit child-PID updates, and recovery that marks only provably interrupted nonterminal attempts `Abandoned`.

- [ ] **Step 1: Write RED owned-path and document tests**

`OwnedAttemptPaths.Create(dataRoot, buildId, attemptId)` returns:

```text
staging root:     builds/<build-id>/extractions/.staging/<attempt-id>
working root:     <staging>/working
output root:      <staging>/output
staging logs:     <staging>/logs
attempt root:     builds/<build-id>/attempts/<attempt-id>
attempt document: <attempt-root>/attempt.json
final logs:       <attempt-root>/logs
candidate output: <attempt-root>/candidate-output
retained output:  <attempt-root>/retained-output
```

Reject unsafe build/attempt segments, root escapes, existing reparse points, file-as-ancestor cases, and case-insensitive containment tricks.

Document tests assert strict schema version 1, all attempt provenance, exact typed argument list and environment overrides, pre/post input manifests, timeout, process/log/retention facts, atomic replacement through an owned sibling temporary file, malformed/unknown property rejection, and no raw exception stack in human-facing fields.

- [ ] **Step 2: Write RED extraction-lock tests**

Use `%S1ATLAS_HOME%/extraction.lock` with this strict document:

```json
{
  "schemaVersion": 1,
  "attemptId": "<32 lower-case hex>",
  "ownerProcessId": 1234,
  "childProcessId": null,
  "startedAtUtc": "2026-08-12T12:00:00.0000000+00:00"
}
```

Cover exclusive `FileMode.CreateNew` acquisition, second contender reporting the active attempt, atomic child-PID update, only-owner release, stale dead owner, live owner, malformed lock, reparse-point lock path, cancellation, and dispose-after-release idempotence. Tests inject `Func<int, bool> isProcessAlive`; they do not depend on arbitrary machine PIDs.

- [ ] **Step 3: Write RED recovery tests**

Simulate:

```text
Created/Preparing/Running database attempt + dead owner/process -> Abandoned
Validating + dead process -> Abandoned (future-state compatibility)
ProcessCompleted -> unchanged
Failed/Canceled/Abandoned/Succeeded -> unchanged
live lock owner -> active-attempt error and no mutation
malformed/ambiguous lock -> preserve and fail closed
nonterminal attempt whose child is alive -> preserve and fail closed
missing/lagging attempt.json -> recreate from authoritative database attempt
owned staging for abandoned attempt -> apply keep/discard policy
unassociated staging directory -> preserve as ambiguous evidence
```

Recovery must never kill a process, guess ownership, inspect complete extraction directories, register validated output, or alter a terminal attempt.

- [ ] **Step 4: Run focused tests and verify RED**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "FullyQualifiedName~AttemptDocumentStoreTests|FullyQualifiedName~ExtractionLockTests|FullyQualifiedName~ExtractionRecoveryServiceTests"
```

Expected: compilation fails because attempt filesystem/recovery services do not exist.

- [ ] **Step 5: Implement strict atomic attempt documents**

Persist the exact arguments generated by Task 9, never a shell command string. Write `<attempt.json>.<guid>.tmp` with `FileMode.CreateNew`, flush, then use `File.Move(temp, final, overwrite: true)` only after containment/reparse checks. Cleanup is restricted to that owned temporary file. Reading uses strict JSON options and validates document facts against the domain attempt.

The database remains lifecycle-authoritative. After each successful DB transition, rewrite `attempt.json`. If the mirror write fails, the orchestrator records `AttemptPersistence` when possible; recovery may reconstruct the mirror from the database plus retained manifest data.

- [ ] **Step 6: Implement exclusive lock ownership**

Acquire before creating staging or launching Cpp2IL. Keep an open handle where Windows semantics allow and retain the `CreateNew` file as human/recovery evidence. Child-PID update writes a sibling owned temp and atomically replaces the document. Release verifies the current document still names the same owner/attempt before deleting exactly `extraction.lock`; ownership mismatch is preserved and reported.

- [ ] **Step 7: Implement conservative startup recovery**

Run recovery at the start of `extract` after repository initialization and before acquiring a new lock. For each nonterminal attempt, prove both recorded S1Atlas owner and child process are absent before transitioning to `Abandoned` with stage `Recovery`, code `InterruptedProcess`, a stable message, and completed UTC. Move retained output only when the attempt requested it; otherwise count and remove only its owned staging output. Always retain bounded logs and the attempt document.

- [ ] **Step 8: Run focused tests repeatedly and the Extraction project**

```powershell
1..3 | ForEach-Object {
  dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "FullyQualifiedName~Attempts" --no-restore
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj
```

Expected: all pass; no test leaves a live fake process, lock file, or owned staging directory.

- [ ] **Step 9: Commit Task 8**

```powershell
git add -- src/S1Atlas.Extraction/Attempts tests/S1Atlas.Extraction.Tests/Attempts
git commit -m "feat: lock and recover extraction attempts"
```

---

### Task 9: Build the Typed Cpp2IL Adapter and Phase 3 Orchestrator

**Files:**
- Create: `src/S1Atlas.Extraction/Cpp2Il/Cpp2IlArgumentBuilder.cs`
- Create: `src/S1Atlas.Extraction/Cpp2Il/Cpp2IlProcessExtractor.cs`
- Create: `src/S1Atlas.Extraction/ExtractionOrchestrator.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Cpp2Il/Cpp2IlArgumentBuilderTests.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Cpp2Il/Cpp2IlProcessExtractorTests.cs`
- Create: `tests/S1Atlas.Extraction.Tests/ExtractionOrchestratorTests.cs`

**Interfaces:**
- Consumes: selected build/profile/policy, tool resolver, input resolver/verifier, optional snapshot service, extraction repository, attempt document store, extraction lock/recovery, process extractor, Atlas paths, and time provider.
- Produces: one durable terminal Phase 3 attempt. Accepted process/input results become `ProcessCompleted` with quarantined `candidate-output`; every failure/cancellation preserves required metadata/logs and cannot create authoritative output.

- [ ] **Step 1: Write RED argument-builder tests**

The builder returns exactly four arguments in this exact order and spelling:

```csharp
Assert.Equal(
    [
        $"--game-path={Path.GetFullPath(gameRoot)}",
        "--exe-name=Schedule I",
        $"--output-to={Path.GetFullPath(outputRoot)}",
        "--output-as=dll_il_recovery"
    ],
    Cpp2IlArgumentBuilder.Build(profile, gameRoot, outputRoot));
```

Cover spaces, ampersands, parentheses, Unicode, and dollar/backtick characters as literal path content. Assert the builder rejects non-v1 adapter/profile fields, non-Atlas output containment, rooted/relative ambiguity, a reparse output ancestor, alternate executable name, alternate format, and any attempt to append raw arguments.

- [ ] **Step 2: Write RED process-extractor tests**

Using the source-built fake executable, assert:

```text
exact ArgumentList delivery
Atlas-owned working/output paths
NO_COLOR=true
profile timeout and independent stream caps
accepted exit 0
nonzero exit returned for orchestration mapping
timeout and caller cancellation remain distinct
child PID callback occurs before process completion
fake output exists only under requested output root
```

`Cpp2IlProcessExtractor` delegates process mechanics and does not resolve tools, touch SQLite, select builds, retain failures, validate assemblies, or promote output.

- [ ] **Step 3: Write the RED orchestrator happy-path test**

Arrange a known build, verified custom fake tool, matching temporary game root, profile/policy, empty Atlas paths, and in-memory/spying repository seams. Assert the exact state sequence:

```text
Created -> Preparing -> Running -> ProcessCompleted
```

Then assert:

```csharp
Assert.Equal(ExtractionAttemptStatus.ProcessCompleted, result.Attempt.Status);
Assert.Null(result.Attempt.ResultExtractionId);
Assert.Null(result.Attempt.FailureCode);
Assert.NotNull(result.Attempt.CandidateOutputPath);
Assert.True(Directory.Exists(result.Attempt.CandidateOutputPath));
Assert.False(File.Exists(Path.Combine(result.Attempt.CandidateOutputPath, "complete.marker")));
Assert.False(result.IsAuthoritative);
```

Also assert the lock is gone, staging is gone, final logs/attempt document exist, all final paths are under Atlas home, pre/post manifest digests match, exact args are recorded, tool/profile/policy/recipe provenance is present, and no validated/preference repository API exists in Phase 3.

- [ ] **Step 4: Write RED failure/cancellation/retention tests**

Use a theory covering exact terminal mapping:

```text
managed tool absent            -> Failed / ToolResolution / ToolNotInstalled
custom probe fails             -> Failed / ToolResolution / ToolProbeFailed
live input absent              -> Failed / InputResolution / LiveInputNotFound
pre-run mismatch               -> Failed / PreRunInputVerification / BuildInputMismatch
snapshot manifest/copy invalid -> Failed / InputSnapshotCreation / ArchivedInputInvalid
snapshot filesystem promotion  -> Failed / InputSnapshotCreation / FilesystemPromotionFailed
snapshot database persistence  -> Failed / InputSnapshotCreation / DatabasePromotionFailed
process start failure          -> Failed / ProcessStart / ProcessStartFailed
process timeout                -> Failed / ProcessExecution / ProcessTimedOut
nonzero exit                   -> Failed / ProcessExecution / ProcessExitNonZero
post-run input mutation        -> Failed / PostRunInputVerification / InputChangedDuringExtraction
caller cancellation            -> Canceled / current stage / OperationCanceled
process-start DB persistence   -> Failed / AttemptPersistence / DatabasePromotionFailed
process-start document mirror  -> Failed / AttemptPersistence / FilesystemPromotionFailed
candidate move failure         -> Failed / FilesystemPromotion / FilesystemPromotionFailed
```

For every case, assert no `candidate-output` survives unless the process reached accepted completion and pre/post inputs matched. Attempt JSON and bounded logs remain. Partial output is deleted and counted by default; `KeepFailedArtifacts=true` moves only the owned output to `retained-output`. A requested input snapshot survives later process failure. The previous terminal attempt/candidate is never modified by a later run.

- [ ] **Step 5: Run focused tests and verify RED**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "FullyQualifiedName~Cpp2IlArgumentBuilderTests|FullyQualifiedName~Cpp2IlProcessExtractorTests|FullyQualifiedName~ExtractionOrchestratorTests"
```

Expected: compilation fails because the adapter and orchestrator do not exist.

- [ ] **Step 6: Implement typed options and result contracts**

```csharp
public sealed record ExtractionOptions(
    string? BuildId,
    string? GamePath,
    string? CustomCpp2IlPath,
    string ProfileId,
    bool Retry,
    bool SnapshotInputs,
    bool KeepFailedArtifacts);

public sealed record ExtractionOperationResult(
    ExtractionAttempt Attempt,
    ToolInstance ToolInstance,
    ExtractionInputSource InputSource,
    string? InputSnapshotId,
    bool ProcessWasRun,
    bool IsAuthoritative);
```

`ProfileId` defaults in CLI composition to `cpp2il-reconstructed-assemblies-v1`. `Retry` is recorded/accepted for forward-compatible CLI shape, but Phase 3 has no validated extraction eligible for a no-op, so every successful invocation runs a process. Phase 4 implements valid-recipe reuse and policy-only revalidation.

- [ ] **Step 7: Implement the exact orchestration order**

Use this order and map every exception at its current stage:

```text
initialize repository
run conservative recovery
resolve current or explicit immutable build
load strict profile and policy
allocate attempt ID and contained paths
acquire extraction lock
persist Created attempt before resolving/running external code
resolve and freshly verify managed/custom Cpp2IL
resolve matching live or replay-verified archived input
calculate recipe ID
transition Preparing
capture pre-run input manifest
optionally create and persist an input snapshot
create owned working/output/log directories
launch Cpp2IL
  process-start callback updates lock child PID
  process-start callback transitions Running with PID/start time
drain logs and classify process result
capture post-run manifest and require unchanged build inputs
move logs to attempt root
atomically move accepted output to candidate-output
transition ProcessCompleted and rewrite attempt.json
release lock in finally
delete only owned empty staging in finally
```

Build/profile selection failures before an attempt root exists return structured errors without an invented attempt ID. After `Created` persistence, all failures include the real attempt ID.

- [ ] **Step 8: Implement safe candidate/failed-output finalization**

Before any move/delete/count operation, validate exact containment and reject existing reparse points. Candidate promotion uses same-volume `Directory.Move` from owned staging output to the previously absent attempt `candidate-output` path. Do not write a completeness marker. If the move fails, preserve staging evidence, mark `FilesystemPromotionFailed`, and never claim `ProcessCompleted`.

Failure counting enumerates without following reparse directories and uses checked totals. Default cleanup removes only the exact staging output link/directory. Retention moves it to the previously absent `retained-output` path, which remains permanently non-authoritative.

Before finalizing any attempt that never reached the process runner, create missing stdout/stderr files as contained zero-byte `FileMode.CreateNew` diagnostics under the final attempt log directory. For attempts that did run, move the two bounded staging logs with no overwrite. Thus every durable attempt has both log paths, even when tool or input resolution failed before process start.

- [ ] **Step 9: Run focused, project, and repeated cancellation tests**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "FullyQualifiedName~Cpp2Il|FullyQualifiedName~ExtractionOrchestratorTests"
1..3 | ForEach-Object {
  dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "FullyQualifiedName~ExtractionOrchestratorTests&Name~Cancel" --no-restore
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj
```

Expected: all pass, no live child, no lock, no unowned deletion, and no authoritative output.

- [ ] **Step 10: Commit Task 9**

```powershell
git add -- src/S1Atlas.Extraction/Cpp2Il src/S1Atlas.Extraction/ExtractionOrchestrator.cs tests/S1Atlas.Extraction.Tests/Cpp2Il tests/S1Atlas.Extraction.Tests/ExtractionOrchestratorTests.cs
git commit -m "feat: orchestrate non-authoritative Cpp2IL runs"
```

---

### Task 10: Expose `extract`, Preserve CLI Contracts, and Run the Phase 3 Boundary

**Files:**
- Create: `src/S1Atlas.Cli/Commands/ExtractCommand.cs`
- Modify: `src/S1Atlas.Cli/Configuration/AtlasPaths.cs`
- Modify: `src/S1Atlas.Cli/Configuration/CliConfigurationPaths.cs`
- Modify: `src/S1Atlas.Cli/Output/CliEnvelope.cs`
- Modify: `src/S1Atlas.Cli/Output/CommandOutput.cs`
- Create: `src/S1Atlas.Cli/Output/ExtractionOutputModels.cs`
- Modify: `src/S1Atlas.Cli/Commands/CommandExecution.cs`
- Modify: `src/S1Atlas.Cli/CliApplication.cs`
- Modify: `README.md`
- Create: `tests/S1Atlas.IntegrationTests/Extraction/ExtractionCliFixture.cs`
- Create: `tests/S1Atlas.IntegrationTests/Extraction/ExtractionCliTests.cs`
- Modify: `tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj`

**Interfaces:**
- Consumes: Phase 3 orchestrator, existing CLI envelope, repository/config/data-root composition, real cancellation token, and injected fake executable/configuration in tests.
- Produces: `extract [options]`, stable human/JSON results, stage/code/attempt-aware failures, no HTTP access, updated docs, full automated verification, and one separately authorized local capability smoke.

- [ ] **Step 1: Extend Atlas and configuration path models in RED**

Add and test:

```csharp
public string BuildsDirectory => Path.Combine(RootDirectory, "builds");
public string ExtractionLockPath => Path.Combine(RootDirectory, "extraction.lock");
public string GetBuildDirectory(string buildId);
public string GetBuildAttemptsDirectory(string buildId);
public string GetBuildExtractionStagingDirectory(string buildId);
public string GetBuildInputsDirectory(string buildId);
public string GetBuildInputStagingDirectory(string buildId);

public string ExtractionProfilesDirectory => Path.Combine(RootDirectory, "extraction");
public string ValidationPoliciesDirectory => Path.Combine(RootDirectory, "validation");
```

Require build IDs to be exactly 64 lower-case hexadecimal characters before using them as path segments. `CliConfigurationPaths.Resolve()` requires all three committed configuration directories in publish or repository discovery.

- [ ] **Step 2: Write RED command-tree and JSON tests**

Define the exact command:

```text
s1atlas extract [--build <id>]
                [--game-path <path>]
                [--cpp2il-path <path>]
                [--profile <profile-id>]
                [--retry]
                [--snapshot-inputs]
                [--keep-failed-artifacts]
                [--json]
```

The success document is one schema-version-1 JSON object:

```json
{
  "schemaVersion": 1,
  "command": "extract",
  "success": true,
  "exitCode": 0,
  "data": {
    "attemptId": "...",
    "status": "ProcessCompleted",
    "buildId": "...",
    "recipeId": "...",
    "toolInstanceId": "...",
    "toolTrustLevel": "CustomOverride",
    "inputSource": "Live",
    "inputSnapshotId": null,
    "candidateOutputPath": "...",
    "standardOutputPath": "...",
    "standardErrorPath": "...",
    "processWasRun": true,
    "authoritative": false,
    "validationOutcome": null
  },
  "error": null
}
```

Human success must say:

```text
Cpp2IL process completed under S1Atlas control.
Attempt:       <attempt-id>
Build:         <build-id>
Tool trust:    <ManagedPinned|CustomOverride>
Input source:  <Live|ArchivedSnapshot>
Candidate:     <path>

This Phase 3 output is unvalidated and is not available to downstream consumers.
Phase 4 validation and immutable promotion are still required.
```

- [ ] **Step 3: Preserve the existing envelope while adding extraction error facts**

Extend `CliError` with optional fields annotated individually so null fields are omitted without omitting the envelope's required null `data`/`error` properties:

```csharp
internal sealed record CliError(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AttemptId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Stage,
    string Code,
    string Message);
```

Update existing calls with null attempt/stage so Phase 1/2 JSON remains structurally unchanged. Catch `ExtractionOperationException` before generic exceptions and emit its attempt ID, exact enum stage, code, and concise message. Cancellation exits 2; operational/integrity failures exit 1; `ProcessCompleted` exits 0. Human mode never prints a stack trace.

- [ ] **Step 4: Build the offline integration fixture**

The fixture creates under one temporary root:

```text
fake repository extraction/validation/tool definitions
fake Schedule I root with generated local bytes
fake GameAssembly.dll and global-metadata.dat
fake Schedule I.exe support bytes
fake globalgamemanagers
source-built FakeCpp2Il.exe outside the managed tools root
temporary S1ATLAS_HOME/database
HTTP handler that increments then throws if called
```

Invoke `scan --game-path <fake-root>` to seed a real immutable build, then invoke `extract --cpp2il-path <fake-exe>`. Do not use an external package, proprietary fixture, network request, or committed executable.

- [ ] **Step 5: Write the complete RED CLI/integration matrix**

Cover:

```text
extract appears in root help
default current build + custom fake tool -> ProcessCompleted
explicit known build
unknown build structured failure
missing current build structured failure
missing managed tool prints tools install cpp2il
custom path probe failure
explicit game path priority
pre-run mismatch recommends scan
post-run mutation rejects output
nonzero child exit with log paths
timeout and Ctrl+C map to exit 1/2 respectively
--snapshot-inputs retains verified-but-not-replay-verified snapshot
--keep-failed-artifacts retains only failed partial output
JSON stdout is exactly one document
JSON progress never contaminates stdout
human errors contain no stack trace
candidate path stays beneath Atlas home
candidate has no complete.marker
attempt database/document facts agree
extract performs zero HTTP requests in every test
status/env/builds/tools JSON contracts remain unchanged
```

The successful integration test records hashes of every fake game input before and after invocation and asserts exact equality.

- [ ] **Step 6: Run integration tests and verify RED**

```powershell
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter "FullyQualifiedName~ExtractionCliTests"
```

Expected: compilation fails because the command/composition/output models do not exist.

- [ ] **Step 7: Compose production services without adding a network path**

Instantiate profile/policy providers, tool resolver, input services, process runner, attempt services, recovery, and orchestrator in `CliApplication`. The existing `HttpClient` remains reachable only from `ManagedToolInstaller`, which the `extract` code path never calls. Keep the existing internal constructor test seam and add only narrowly typed factories/time/process dependencies needed by integration tests.

Register:

```csharp
root.Subcommands.Add(ExtractCommand.Create(
    orchestrator,
    repository,
    output,
    error,
    cancellationToken));
```

`Program` already wires `Console.CancelKeyPress`; preserve it and prove the same token reaches the runner.

- [ ] **Step 8: Update README with the exact Phase 3 boundary**

Document:

```text
Phase 1 metadata/migration complete
Phase 2 managed tool supply chain complete
Phase 3 extraction orchestration complete
extract options and examples
extract is offline and never installs/downloads tools
managed vs CustomOverride trust
live pre/post hash guarantees
optional input snapshots and replay-verified distinction
attempt/log/candidate/retained paths
ProcessCompleted is terminal but non-authoritative
candidate-output cannot feed downstream consumers
Phase 4 adds validation, immutable promotion, history, and preference
exact official Cpp2IL pin remains unchanged
```

- [ ] **Step 9: Run the complete automated verification boundary**

```powershell
dotnet restore S1Atlas.sln
dotnet build S1Atlas.sln --configuration Release --no-restore
dotnet test S1Atlas.sln --configuration Release --no-build --verbosity normal
```

Require exit code 0, zero warnings, zero errors, and every Core, Extraction, Storage, and Integration test green. Record actual per-project and total test counts from the final output; do not reuse an earlier count.

- [ ] **Step 10: Verify repository hygiene and generated-file exclusion**

```powershell
git diff --check
git status --short
git ls-files | Select-String -Pattern "Cpp2IL\.exe|atlas\.db|\.db-wal|\.db-shm|installation\.json|tool-manifest\.json|attempt\.json|input-manifest\.json|complete\.marker|candidate-output|retained-output|stdout\.log|stderr\.log"
```

Expected: only source/config/docs/test changes are tracked. The scan returns no generated/downloaded/proprietary file. Inspect `.gitignore` and add only missing generated path patterns; never ignore source directories such as `src/S1Atlas.Extraction/Attempts`.

- [ ] **Step 11: Commit the automated-GREEN Task 10 checkpoint**

```powershell
git add -- src/S1Atlas.Cli tests/S1Atlas.IntegrationTests/Extraction tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj README.md .gitignore
git commit -m "feat: expose phase 3 extraction orchestration"
```

Do not commit if any automated test fails, any warning is emitted, or a generated/proprietary file is tracked.

- [ ] **Step 12: Obtain explicit confirmation before the first real game execution**

The Phase 3 capability smoke is the first command in this project that points Cpp2IL at Schedule I. Stop and obtain the user's explicit confirmation immediately before running it. Do not infer permission from Phase 2's managed-tool smoke, and do not run while Schedule I or an updater is active.

- [ ] **Step 13: Run one read-only real capability smoke after confirmation**

Preflight and capture the authoritative input hashes:

```powershell
$atlasRoot = if ($env:S1ATLAS_HOME) { $env:S1ATLAS_HOME } else { Join-Path $env:LOCALAPPDATA 'S1Atlas' }
git status --short
$toolJson = ((& dotnet run --configuration Release --no-build --project src/S1Atlas.Cli -- tools status cpp2il --json) | Out-String | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0 -or $toolJson.data.tools[0].status -ne 'Verified') { throw 'The exact managed Cpp2IL pin is not verified.' }
$environmentJson = ((& dotnet run --configuration Release --no-build --project src/S1Atlas.Cli -- env --json) | Out-String | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw 'The current Atlas environment could not be read.' }
$gameRoot = [IO.Path]::GetFullPath($environmentJson.data.installationRoot)
$gameAssemblyPath = [IO.Path]::GetFullPath($environmentJson.data.gameAssemblyPath)
$metadataPath = [IO.Path]::GetFullPath($environmentJson.data.globalMetadataPath)
$runningGame = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq 'Schedule I' }
if ($runningGame) { throw 'Schedule I is running. Stop it before the capability smoke.' }
$gameReparsePoints = Get-ChildItem -LiteralPath $gameRoot -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }
if ($gameReparsePoints) { throw 'The game tree contains a reparse point; inspect it before smoke execution.' }
function Get-GameInventory([string] $root) {
  Get-ChildItem -LiteralPath $root -File -Force -Recurse |
    ForEach-Object {
      $relative = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
      '{0}`t{1}`t{2}' -f $relative, $_.Length, $_.LastWriteTimeUtc.ToString('O')
    } |
    Sort-Object -CaseSensitive
}
$preGameInventory = @(Get-GameInventory $gameRoot)
$preGameAssemblySha256 = (Get-FileHash -LiteralPath $gameAssemblyPath -Algorithm SHA256).Hash.ToLowerInvariant()
$preMetadataSha256 = (Get-FileHash -LiteralPath $metadataPath -Algorithm SHA256).Hash.ToLowerInvariant()
```

Require the exact managed pin to be `Verified`, the current build to match the intended Schedule I installation, no running Schedule I process, and human confirmation that Steam is not updating the game. Record pre-run GameAssembly/metadata hashes and the metadata inventory directly from the current snapshot paths, then run exactly once:

```powershell
$extractText = ((& dotnet run --configuration Release --no-build --project src/S1Atlas.Cli -- extract --json) | Out-String)
$extractExitCode = $LASTEXITCODE
$extractJson = $extractText | ConvertFrom-Json
if ($extractExitCode -ne 0) { throw "Extraction capability smoke failed with exit code $extractExitCode.`n$extractText" }
```

Inspect the one JSON document and require:

```text
exit code 0
status ProcessCompleted
tool trust ManagedPinned
authoritative false
validationOutcome null
input source Live
exact profile/recipe provenance in attempt.json
exact four typed arguments
--exe-name=Schedule I
process exit 0
matching pre/post input manifest digests
candidate output exists only under Atlas home
candidate output contains no complete.marker
no validated extraction or preferred row exists
```

The accepted exit plus produced candidate output is the Phase 3 capability evidence that `--exe-name=Schedule I` resolved `Schedule I_Data`. Perform the explicit post-run checks:

```powershell
if ($extractJson.data.status -ne 'ProcessCompleted' -or $extractJson.data.authoritative -ne $false) { throw 'The smoke result crossed the Phase 3 authority boundary.' }
if ($extractJson.data.toolTrustLevel -ne 'ManagedPinned') { throw 'The smoke did not use the managed pin.' }
if ($null -ne $extractJson.data.validationOutcome) { throw 'Phase 3 must not report a validation outcome.' }
$candidatePath = [IO.Path]::GetFullPath($extractJson.data.candidateOutputPath)
$atlasPrefix = [IO.Path]::GetFullPath($atlasRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $candidatePath.StartsWith($atlasPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Candidate output escaped the Atlas data root.' }
if (-not (Test-Path -LiteralPath $candidatePath -PathType Container)) { throw 'Candidate output is missing.' }
if (Test-Path -LiteralPath (Join-Path $candidatePath 'complete.marker')) { throw 'Phase 3 candidate output must not have a complete marker.' }
$attemptRoot = Split-Path -Parent $candidatePath
$attemptDocument = Get-Content -Raw -LiteralPath (Join-Path $attemptRoot 'attempt.json') | ConvertFrom-Json
$expectedArgument1 = "--game-path=$gameRoot"
$expectedArgument2 = '--exe-name=Schedule I'
$expectedArgument4 = '--output-as=dll_il_recovery'
if ($attemptDocument.arguments.Count -ne 4 -or
    $attemptDocument.arguments[0] -ne $expectedArgument1 -or
    $attemptDocument.arguments[1] -ne $expectedArgument2 -or
    -not $attemptDocument.arguments[2].StartsWith('--output-to=', [StringComparison]::Ordinal) -or
    $attemptDocument.arguments[3] -ne $expectedArgument4) { throw 'The retained attempt arguments do not match the typed profile.' }
$recordedOutputPath = [IO.Path]::GetFullPath($attemptDocument.arguments[2].Substring('--output-to='.Length))
if (-not $recordedOutputPath.StartsWith($atlasPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'The process output argument escaped the Atlas data root.' }
if ($attemptDocument.environmentOverrides.NO_COLOR -ne 'true') { throw 'NO_COLOR=true was not recorded.' }
$postGameAssemblySha256 = (Get-FileHash -LiteralPath $gameAssemblyPath -Algorithm SHA256).Hash.ToLowerInvariant()
$postMetadataSha256 = (Get-FileHash -LiteralPath $metadataPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($postGameAssemblySha256 -ne $preGameAssemblySha256 -or $postMetadataSha256 -ne $preMetadataSha256) { throw 'Authoritative game input hashes changed.' }
$postGameInventory = @(Get-GameInventory $gameRoot)
if (Compare-Object -ReferenceObject $preGameInventory -DifferenceObject $postGameInventory -SyncWindow 0) { throw 'The Schedule I directory inventory changed.' }
git status --short
git ls-files | Select-String -Pattern "Cpp2IL\.exe|atlas\.db|\.db-wal|\.db-shm|attempt\.json|input-manifest\.json|complete\.marker|candidate-output|retained-output|stdout\.log|stderr\.log"
```

The automated schema-4 tests already prove that Phase 3 creates no validated-extraction or preference table; the smoke confirms the runtime does not synthesize any authoritative filesystem marker. Do not run the candidate through ILSpy or any validator in this phase.

- [ ] **Step 14: Inspect history, push normally, and open a draft PR**

```powershell
git log --oneline --decorate -12
git diff origin/main...HEAD --check
git status --short
git push -u origin feature/cpp2il-phase3-extraction-orchestration
```

Use a normal push only. Open a draft PR against `main`. The body reports Tasks 1-10, exact head SHA, Release warning/error totals, per-project/total tests, real capability smoke facts, pre/post input hashes, managed executable hash, `ProcessCompleted` non-authoritative status, confirmation that no game file changed, and confirmation that generated/proprietary files are not tracked. Leave the PR draft and unmerged for human QA.

---

## Phase 3 Review Checklist

```text
[ ] Production Cpp2IL pin remains byte-for-byte unchanged
[ ] Extraction profile exact ID/version/adapter/arguments/limits are committed
[ ] Validation policy is provenance-only; Phase 3 does not claim validation
[ ] Raw Cpp2IL arguments cannot enter through CLI/config/environment
[ ] Recipe identity excludes validation policy, paths, times, PIDs, and attempt ID
[ ] ProcessCompleted is terminal, immutable, and explicitly non-authoritative
[ ] ProcessCompleted has candidate-output and no result extraction ID
[ ] No Phase 3 path creates complete.marker for candidate output
[ ] Migrations 1-3 text/checksums remain unchanged
[ ] Schema-4 migration is transactional, backed up, and idempotent
[ ] Attempts persist before Cpp2IL game execution
[ ] Terminal attempts cannot be mutated
[ ] Fake process fixture is built from source and no executable is committed
[ ] Production process execution uses no shell and ArgumentList only
[ ] Stdout/stderr drain concurrently beyond their 64 MiB retention caps
[ ] Timeout and caller cancellation are distinguished
[ ] Timeout/cancellation/persistence failure kill the entire child tree
[ ] Managed tool is re-hashed/probed immediately before extraction
[ ] Custom tool is hashed/probed and cannot disguise a corrupt managed tool
[ ] extract never downloads or invokes the installer
[ ] Explicit/stored/Steam/archived input precedence is deterministic
[ ] Live build hashes match before and after execution
[ ] Input snapshots copy, re-hash, atomically promote, and default replay_verified false
[ ] Snapshot database failure leaves a verifiable filesystem snapshot recoverable
[ ] One extraction lock exists per Atlas data root
[ ] Recovery never kills/guesses and never mutates ProcessCompleted
[ ] Default failed output is deleted only from owned staging
[ ] Explicit retained-output and all candidate-output remain quarantined
[ ] Human/JSON errors include stable stage/code and optional attempt ID
[ ] Existing Phase 1/2 commands and JSON contracts remain green
[ ] Automated tests perform no HTTP and use no proprietary bytes
[ ] Full Release build has zero warnings/errors
[ ] All four test projects pass
[ ] Real smoke runs only after explicit confirmation
[ ] Real managed execution proves --exe-name=Schedule I resolution
[ ] Real game input hashes are identical before/after
[ ] No executable/game/generated output is tracked
[ ] Draft PR remains unmerged for human QA
```

## Phase 3 Completion Boundary

After this plan, S1Atlas can select a known build, prove exact live or archived inputs, resolve a freshly verified managed/custom Cpp2IL instance, derive a deterministic recipe, create a durable attempt, optionally archive inputs, execute Cpp2IL without a shell, bound logs without deadlock, terminate the process tree, survive interruption conservatively, and retain a truthful non-authoritative candidate.

Phase 4 begins with:

```text
output containment validation
complete artifact inventory and hashing
PEReader/MetadataReader managed assembly inspection
absolute and comparative sanity checks
policy application and validation.json
reproducibility comparison
extraction ID and immutable manifests
two-phase filesystem/database promotion
validated extraction history and recovery
managed automatic/custom manual preference rules
extractions list/show/promote commands
```

No Phase 4 consumer may read `candidate-output` directly. It must enter through the Phase 4 validation-attempt boundary and produce a verified immutable extraction before downstream use.

## Execution Mode

After this plan is reviewed, create or switch to `feature/cpp2il-phase3-extraction-orchestration` from the merged `main` commit `f22ccac1d913c9e7409c9f1db3fa18e46fe681a2`. Execute Tasks 1-10 sequentially using TDD and coherent GREEN commits. Never push a RED/non-compiling checkpoint. Run the real capability smoke only after the automated suite is green and the user explicitly authorizes pointing Cpp2IL at Schedule I.

