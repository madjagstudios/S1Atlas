# Cpp2IL Phase 2 Managed Tool Supply Chain Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an explicit, checksum-pinned, recoverable managed Cpp2IL installation pipeline with offline status inspection, safe package handling, capability probes, SQLite provenance, and human/JSON CLI commands.

**Architecture:** The repository owns one typed Cpp2IL definition under `config/tools`. `S1Atlas.Core` owns immutable tool contracts and deterministic identities; `S1Atlas.Extraction` parses the committed definition, downloads and verifies packages, materializes them only in Atlas-owned staging, runs controlled probes, and atomically promotes verified installations; `S1Atlas.Storage` persists verified installation and tool-instance provenance; `S1Atlas.Cli` exposes `tools status` and `tools install`. No extraction profile, game input, Cpp2IL game execution, reconstructed assembly, or ILSpy behavior enters this phase.

**Tech Stack:** C# / .NET 8, `System.Text.Json`, `System.Net.Http`, `System.IO.Compression`, `System.Diagnostics.Process`, SHA-256, Microsoft.Data.Sqlite, System.CommandLine, xUnit v3, Windows GitHub Actions.

## Global Constraints

- Target Windows 10 or later and `win-x64`; the production Cpp2IL pin is never silently substituted on unsupported platforms.
- Preserve the approved Cpp2IL pin exactly:

```text
Version:             2022.1.0-pre-release.21
Asset name:          Cpp2IL-2022.1.0-pre-release.21-Windows.exe
Asset size:          15,137,811 bytes
Asset SHA-256:       663fb432433b4371fd1ee0ebc321a8fff2a9aac5ac4230c843f9e03ddee4e04c
Package kind:        singleFile
Local executable:    Cpp2IL.exe
Release published:   2026-02-22
License:             MIT
Required format:     dll_il_recovery
```

- Only `s1atlas tools install cpp2il` may perform network access. `scan`, `status`, `env`, `builds`, and `tools status` remain offline.
- Installation is explicit. No command automatically downloads Cpp2IL.
- A package is never executed until its exact byte size and SHA-256 match the committed definition.
- The normal managed trust level is `ManagedPinned`; `CustomOverride` is defined for later phases but is not exposed through a Phase 2 CLI path.
- Tool definition changes are reviewed source changes. No mutable “latest” lookup or remotely refreshed manifest exists.
- The initial asset is a standalone executable. The code also safely supports a future ZIP definition, but no production archive pin is added.
- All working, staging, quarantine, and final installation paths are under the configured Atlas data root. The Schedule I installation is never touched.
- Use `ProcessStartInfo.ArgumentList`; production probe execution never uses a shell string.
- All automated tests use injected local bytes and fake HTTP handlers. CI never downloads Cpp2IL or any release asset.
- Do not commit executable fixtures, downloaded packages, databases, backups, or generated local installation records.
- Preserve Phase 1 human output, JSON envelopes, exit codes `0/1/2`, migration compatibility, and zero-warning Release builds.

---

## Phase 2 Scope Boundary

Phase 2 delivers:

```text
config/tools/cpp2il.win-x64.json
strict repository-controlled definition loading
deterministic definition and tool-instance identities
schema migration 3 for tool provenance
safe HTTPS streaming download
exact package size and SHA-256 verification
single-file materialization
safe ZIP materialization for future definitions
controlled --help and --list-output-formats probes
managed installation inspection and status states
staged atomic installation
repair and quarantine
tools status/install human and JSON commands
README and a real local managed-pin smoke gate
```

Phase 2 explicitly does not deliver:

```text
config/extraction profiles
config/validation policies
--cpp2il-path custom extraction overrides
Cpp2IL game execution
extraction attempts
input snapshots
artifact manifests
managed assembly validation
preferred extractions
extraction cleanup
ILSpy or symbol indexing
```

---

## File Structure

### Repository configuration

```text
config/tools/cpp2il.win-x64.json
```

### `S1Atlas.Core`

```text
src/S1Atlas.Core/Identity/CanonicalHashWriter.cs
src/S1Atlas.Core/Properties/AssemblyInfo.cs
src/S1Atlas.Core/Tools/ToolArchiveFormat.cs
src/S1Atlas.Core/Tools/ToolDefinition.cs
src/S1Atlas.Core/Tools/ToolDefinitionFingerprint.cs
src/S1Atlas.Core/Tools/ToolInstallResult.cs
src/S1Atlas.Core/Tools/ToolInstallationStatus.cs
src/S1Atlas.Core/Tools/ToolInstance.cs
src/S1Atlas.Core/Tools/ToolInstanceId.cs
src/S1Atlas.Core/Tools/ToolOperationException.cs
src/S1Atlas.Core/Tools/ToolPackageKind.cs
src/S1Atlas.Core/Tools/ToolPlatform.cs
src/S1Atlas.Core/Tools/ToolStatus.cs
src/S1Atlas.Core/Tools/ToolTrustLevel.cs
src/S1Atlas.Core/Tools/IToolDefinitionProvider.cs
src/S1Atlas.Core/Tools/IToolInstaller.cs
src/S1Atlas.Core/Storage/IToolRepository.cs
```

Core contains no JSON, HTTP, ZIP, process, filesystem, or SQLite implementation.

### `S1Atlas.Extraction`

```text
src/S1Atlas.Extraction/Tools/RepositoryToolDefinitionProvider.cs
src/S1Atlas.Extraction/Tools/ToolDefinitionDocument.cs
src/S1Atlas.Extraction/Tools/ToolDefinitionSerializer.cs
src/S1Atlas.Extraction/Tools/ToolDefinitionValidator.cs
src/S1Atlas.Extraction/Tools/ToolDownloadClient.cs
src/S1Atlas.Extraction/Tools/ToolPackageVerifier.cs
src/S1Atlas.Extraction/Tools/ToolPathPolicy.cs
src/S1Atlas.Extraction/Tools/SafeToolPackageInstaller.cs
src/S1Atlas.Extraction/Tools/ToolProbeRunner.cs
src/S1Atlas.Extraction/Tools/ToolInstallationDocument.cs
src/S1Atlas.Extraction/Tools/ToolInstallationDocumentStore.cs
src/S1Atlas.Extraction/Tools/ManagedToolInstallationValidator.cs
src/S1Atlas.Extraction/Tools/ManagedToolInstaller.cs
src/S1Atlas.Extraction/Tools/ManagedToolService.cs
```

### `S1Atlas.Storage`

```text
src/S1Atlas.Storage/Migrations/SqliteMigrations.cs
src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.cs
src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Tools.cs
```

`SqliteAtlasRepository` becomes a partial class implementing `IAtlasRepository` and `IToolRepository` so one migration runner and database remain authoritative.

### `S1Atlas.Cli`

```text
src/S1Atlas.Cli/Commands/ToolsCommand.cs
src/S1Atlas.Cli/Commands/ToolsStatusCommand.cs
src/S1Atlas.Cli/Commands/ToolsInstallCommand.cs
src/S1Atlas.Cli/Configuration/AtlasPaths.cs
src/S1Atlas.Cli/Configuration/CliConfigurationPaths.cs
src/S1Atlas.Cli/Output/ToolOutputModels.cs
src/S1Atlas.Cli/Properties/AssemblyInfo.cs
src/S1Atlas.Cli/CliApplication.cs
src/S1Atlas.Cli/Commands/CommandExecution.cs
src/S1Atlas.Cli/Program.cs
src/S1Atlas.Cli/S1Atlas.Cli.csproj
```

### Tests

```text
tests/S1Atlas.Core.Tests/Identity/CanonicalHashWriterTests.cs
tests/S1Atlas.Core.Tests/Tools/ToolDefinitionFingerprintTests.cs
tests/S1Atlas.Core.Tests/Tools/ToolInstanceIdTests.cs

tests/S1Atlas.Extraction.Tests/Tools/RepositoryToolDefinitionProviderTests.cs
tests/S1Atlas.Extraction.Tests/Tools/ToolDefinitionValidatorTests.cs
tests/S1Atlas.Extraction.Tests/Tools/ToolDownloadClientTests.cs
tests/S1Atlas.Extraction.Tests/Tools/ToolPackageVerifierTests.cs
tests/S1Atlas.Extraction.Tests/Tools/ToolPathPolicyTests.cs
tests/S1Atlas.Extraction.Tests/Tools/SafeToolPackageInstallerTests.cs
tests/S1Atlas.Extraction.Tests/Tools/ToolProbeRunnerTests.cs
tests/S1Atlas.Extraction.Tests/Tools/ManagedToolInstallationValidatorTests.cs
tests/S1Atlas.Extraction.Tests/Tools/ManagedToolInstallerTests.cs
tests/S1Atlas.Extraction.Tests/Tools/ManagedToolServiceTests.cs
tests/S1Atlas.Extraction.Tests/Tools/ToolTestFixture.cs

tests/S1Atlas.Storage.Tests/Migrations/SqliteMigrationRunnerTests.cs
tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryToolTests.cs

tests/S1Atlas.IntegrationTests/Tools/ManagedToolCliTests.cs
tests/S1Atlas.IntegrationTests/Tools/ManagedToolCliFixture.cs
```

---

### Task 1: Add deterministic tool contracts and identities

**Files:**
- Create: `src/S1Atlas.Core/Identity/CanonicalHashWriter.cs`
- Create: `src/S1Atlas.Core/Properties/AssemblyInfo.cs`
- Create: `src/S1Atlas.Core/Tools/*.cs` listed above
- Create: `src/S1Atlas.Core/Storage/IToolRepository.cs`
- Test: `tests/S1Atlas.Core.Tests/Identity/CanonicalHashWriterTests.cs`
- Test: `tests/S1Atlas.Core.Tests/Tools/ToolDefinitionFingerprintTests.cs`
- Test: `tests/S1Atlas.Core.Tests/Tools/ToolInstanceIdTests.cs`

**Interfaces:**
- Produces `ResolvedToolDefinition`, `ManagedToolInstallation`, `ManagedToolInstallOutcome`, `ToolInstance`, `ManagedToolStatus`, `ToolInstallResult`, and deterministic digest helpers used by later tasks.
- Consumes no Phase 2 implementation types.

- [ ] **Step 1: Add internals visibility and failing canonical-identity tests**

Create:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("S1Atlas.Core.Tests")]
```

Tests:

```csharp
[Fact]
public void Complete_WithSameTypedValues_ReturnsSameLowercaseSha256()

[Fact]
public void Complete_NullAndEmptyString_ReturnDifferentDigests()

[Fact]
public void Complete_ChangingIdentityKindOrVersion_ChangesDigest()
```

Run:

```powershell
dotnet test tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj --filter CanonicalHashWriterTests
```

Expected: compile failure because `CanonicalHashWriter` does not exist.

- [ ] **Step 2: Implement `CanonicalHashWriter` version 1**

```csharp
internal sealed class CanonicalHashWriter : IDisposable
{
    public CanonicalHashWriter(string identityKind, int identityVersion);
    public void AppendString(string value);
    public void AppendNullableString(string? value);
    public void AppendInt32(int value);
    public void AppendInt64(long value);
    public void AppendBoolean(bool value);
    public string Complete();
}
```

Use `IncrementalHash` with SHA-256. The constructor writes identity kind and identity version first.

Encoding:

```text
non-null scalar: 0x01 + little-endian Int32 UTF-8 byte length + UTF-8 bytes
null scalar:     0x00
integers:        invariant decimal text encoded as non-null scalar
boolean:         "1" or "0" encoded as non-null scalar
result:          lower-case full SHA-256 hexadecimal
```

`Complete()` is single-use. Appending or completing afterward throws `InvalidOperationException`.

- [ ] **Step 3: Define exact enums and immutable records**

```csharp
public enum ToolPackageKind { SingleFile, Archive }
public enum ToolArchiveFormat { Zip }
public enum ToolTrustLevel { ManagedPinned, CustomOverride }
public enum ToolInstallationStatus
{
    NotInstalled,
    Verified,
    Corrupt,
    Incomplete,
    DefinitionMismatch,
    ProbeFailed
}
```

Create:

```csharp
public sealed record ToolSafetyLimits(
    long MaximumDownloadBytes,
    long MaximumExpandedBytes,
    int MaximumFileCount);

public sealed record ToolPackageDefinition(
    ToolPackageKind Kind,
    ToolArchiveFormat? ArchiveFormat,
    Uri SourceUri,
    Uri ReleaseUri,
    string AssetName,
    long ExpectedSize,
    string Sha256,
    string ExecutableRelativePath,
    ToolSafetyLimits Limits);

public sealed record ToolLicenseDefinition(
    string SpdxIdentifier,
    Uri SourceUri);

public sealed record ToolProbeDefinition(
    string ProbeId,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<int> AcceptedExitCodes,
    TimeSpan Timeout,
    IReadOnlyList<string> RequiredOutputSubstrings);

public sealed record ToolDefinition(
    int SchemaVersion,
    string ToolId,
    string DisplayName,
    string Version,
    string Platform,
    ToolPackageDefinition Package,
    ToolLicenseDefinition License,
    IReadOnlyList<ToolProbeDefinition> Probes);

public sealed record ResolvedToolDefinition(
    ToolDefinition Definition,
    string DefinitionDigest);
```

Status/provenance:

```csharp
public sealed record ToolProbeResult(
    string ProbeId,
    bool Succeeded,
    int? ExitCode,
    bool TimedOut,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated,
    string? FailureCode,
    string? FailureMessage);

public sealed record ManagedToolInstallation(
    int SchemaVersion,
    string ToolId,
    string DisplayName,
    string Version,
    string Platform,
    string DefinitionDigest,
    string PackageSha256,
    string ExecutableSha256,
    string RootPath,
    ToolInstallationStatus Status,
    DateTimeOffset InstalledAtUtc,
    DateTimeOffset LastVerifiedAtUtc,
    IReadOnlyList<ToolProbeResult> ProbeResults,
    string? ReplacedInstallationPath);

public sealed record ManagedToolInstallOutcome(
    ManagedToolInstallation Installation,
    bool WasAlreadyVerified,
    bool Repaired,
    string? QuarantinePath);

public sealed record ToolInstance(
    string ToolInstanceId,
    string ToolName,
    string? VersionLabel,
    string Platform,
    ToolTrustLevel TrustLevel,
    string? DefinitionDigest,
    string? PackageSha256,
    string ExecutableSha256,
    string ObservedPath,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset LastVerifiedAtUtc,
    ToolInstallationStatus Status);

public sealed record ManagedToolStatus(
    ResolvedToolDefinition Definition,
    ToolInstallationStatus Status,
    ManagedToolInstallation? Installation,
    string? DiagnosticCode,
    string? DiagnosticMessage);

public sealed record ToolInstallResult(
    ManagedToolInstallation Installation,
    ToolInstance ToolInstance,
    bool WasAlreadyVerified,
    bool Repaired,
    string? QuarantinePath);
```

`ToolName` means the stable lower-case tool ID (`cpp2il`), not the display label.

- [ ] **Step 4: Write failing definition and instance identity tests**

```csharp
[Fact]
public void Create_WithEquivalentDefinition_ReturnsSameDigest()

[Fact]
public void Create_WhenProbeRequirementChanges_ReturnsDifferentDigest()

[Fact]
public void Create_WhenLicenseOrSafetyLimitChanges_ReturnsDifferentDigest()

[Fact]
public void ToolInstanceId_SameBytesAtDifferentPaths_ReturnsSameId()

[Fact]
public void ToolInstanceId_WhenTrustLevelChanges_ReturnsDifferentId()

[Fact]
public void ToolInstanceId_WhenExecutableHashChanges_ReturnsDifferentId()
```

Expected: compile failure because fingerprint helpers do not exist.

- [ ] **Step 5: Implement definition and tool-instance identities**

`ToolDefinitionFingerprint.Create(ToolDefinition)` appends every effective field in this order:

```text
identity kind "tool-definition"
identity version 1
definition schema version
tool ID
display name
version
platform
package kind
archive format or null
source URL AbsoluteUri
release URL AbsoluteUri
asset name
expected size
package SHA-256
executable relative path
maximum download bytes
maximum expanded bytes
maximum file count
license SPDX identifier
license URL AbsoluteUri
probe count
for each probe in declared order:
  probe ID
  argument count and arguments
  accepted-exit-code count and exit codes
  timeout milliseconds
  required-output count and substrings
```

Define the exact tool-instance helper:

```csharp
public static class ToolInstanceId
{
    public static string Create(
        string toolName,
        string executableSha256,
        string platform,
        ToolTrustLevel trustLevel);
}
```

It appends only:

```text
identity kind "tool-instance"
identity version 1
stable tool ID
observed executable SHA-256
platform
trust enum name
```

Display name, version label, path, and timestamps are deliberately not inputs.

- [ ] **Step 6: Add platform, exception, and repository contracts**

`ToolPlatform.GetCurrent()` returns `win-x64` only when Windows and x64. Otherwise:

```csharp
throw new ToolOperationException(
    "ToolPlatformUnsupported",
    "The managed Cpp2IL tool is supported only on Windows x64.");
```

```csharp
public sealed class ToolOperationException : InvalidOperationException
{
    public ToolOperationException(
        string code,
        string message,
        Exception? innerException = null);

    public string Code { get; }
}
```

```csharp
public interface IToolDefinitionProvider
{
    IReadOnlyList<ResolvedToolDefinition> GetAll();
    ResolvedToolDefinition GetRequired(string toolId, string platform);
}

public interface IToolInstaller
{
    Task<ManagedToolInstallOutcome> InstallAsync(
        ResolvedToolDefinition definition,
        bool repair,
        CancellationToken cancellationToken);
}

public interface IToolRepository
{
    Task SaveVerifiedManagedToolAsync(
        ManagedToolInstallation installation,
        ToolInstance toolInstance,
        CancellationToken cancellationToken);

    Task<ManagedToolInstallation?> GetManagedToolAsync(
        string toolId,
        string version,
        string platform,
        CancellationToken cancellationToken);

    Task<ToolInstance?> GetToolInstanceAsync(
        string toolInstanceId,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 7: Verify and commit Task 1**

```powershell
dotnet test tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj
dotnet build S1Atlas.sln --configuration Release
git add -- src/S1Atlas.Core tests/S1Atlas.Core.Tests
git commit -m "feat: define managed tool identities and contracts"
```

Expected: all Core tests pass; zero warnings/errors.

---

### Task 2: Commit and strictly validate the approved Cpp2IL definition

**Files:**
- Create: `config/tools/cpp2il.win-x64.json`
- Create: `src/S1Atlas.Extraction/Tools/ToolDefinitionDocument.cs`
- Create: `src/S1Atlas.Extraction/Tools/ToolDefinitionSerializer.cs`
- Create: `src/S1Atlas.Extraction/Tools/ToolDefinitionValidator.cs`
- Create: `src/S1Atlas.Extraction/Tools/RepositoryToolDefinitionProvider.cs`
- Modify: `src/S1Atlas.Cli/S1Atlas.Cli.csproj`
- Test: `tests/S1Atlas.Extraction.Tests/Tools/RepositoryToolDefinitionProviderTests.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Tools/ToolDefinitionValidatorTests.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Tools/ToolTestFixture.cs`

**Interfaces:**
- Consumes Core tool records and `ToolDefinitionFingerprint`.
- Produces a strict `IToolDefinitionProvider` and normalized manifest serializer used by installation inspection.

- [ ] **Step 1: Add the exact production definition**

```json
{
  "schemaVersion": 1,
  "toolId": "cpp2il",
  "displayName": "Cpp2IL",
  "version": "2022.1.0-pre-release.21",
  "platform": "win-x64",
  "package": {
    "kind": "singleFile",
    "archiveFormat": null,
    "sourceUrl": "https://github.com/SamboyCoding/Cpp2IL/releases/download/2022.1.0-pre-release.21/Cpp2IL-2022.1.0-pre-release.21-Windows.exe",
    "releaseUrl": "https://github.com/SamboyCoding/Cpp2IL/releases/tag/2022.1.0-pre-release.21",
    "assetName": "Cpp2IL-2022.1.0-pre-release.21-Windows.exe",
    "expectedSize": 15137811,
    "sha256": "663fb432433b4371fd1ee0ebc321a8fff2a9aac5ac4230c843f9e03ddee4e04c",
    "executableRelativePath": "Cpp2IL.exe",
    "limits": {
      "maximumDownloadBytes": 15137811,
      "maximumExpandedBytes": 15137811,
      "maximumFileCount": 1
    }
  },
  "license": {
    "spdxIdentifier": "MIT",
    "sourceUrl": "https://github.com/SamboyCoding/Cpp2IL/blob/2022.1.0-pre-release.21/LICENSE"
  },
  "probes": [
    {
      "probeId": "help",
      "arguments": ["--help"],
      "acceptedExitCodes": [0],
      "timeoutSeconds": 30,
      "requiredOutputSubstrings": []
    },
    {
      "probeId": "output-formats",
      "arguments": ["--list-output-formats"],
      "acceptedExitCodes": [0],
      "timeoutSeconds": 30,
      "requiredOutputSubstrings": ["dll_il_recovery"]
    }
  ]
}
```

- [ ] **Step 2: Write failing pin and strict-validation tests**

```csharp
[Fact]
public void GetRequired_Cpp2IlWindowsX64_ReturnsApprovedPin()
```

Assert every verified value: version, platform, URLs, asset, exact size/hash, executable path, MIT license, both probes, and `dll_il_recovery`.

Invalid cases:

```csharp
[Theory]
[InlineData("http://example.test/tool.exe")]
[InlineData("https://user:password@example.test/tool.exe")]
public void Load_WhenSourceUrlIsNotApprovedHttpsShape_Rejects(string sourceUrl)

[Fact]
public void Load_WhenVersionIsNotOneSafePathSegment_Rejects()

[Fact]
public void Load_WhenExpectedSizeExceedsDownloadLimit_Rejects()

[Fact]
public void Load_WhenExecutablePathEscapesRoot_Rejects()

[Fact]
public void Load_WhenProbeIdsRepeat_Rejects()

[Fact]
public void Load_WhenArchiveFormatConflictsWithPackageKind_Rejects()

[Fact]
public void GetAll_WhenToolPlatformPairRepeats_Rejects()
```

Expected: compile failure because provider/validator do not exist.

- [ ] **Step 3: Implement document DTOs and strict validation**

Deserialize into nullable document DTOs, never directly into trusted Core records.

Enforce:

```text
schemaVersion == 1
toolId matches ^[a-z0-9][a-z0-9.-]*$
version is one safe filename/path segment: not '.'/'..', no separators/colon, no invalid filename chars
displayName is nonblank
platform matches ^[a-z0-9][a-z0-9-]*$
package kind is exactly "singleFile" or "archive"
archive format is null for singleFile and exactly "zip" for archive
source/release/license URLs are absolute HTTPS with empty UserInfo
assetName is one file name and matches the final source-URL segment
expectedSize > 0 and <= maximumDownloadBytes
SHA-256 is exactly 64 hex characters, normalized lower-case
executableRelativePath is relative with no empty, '.', or '..' segment and no drive/UNC root
singleFile requires maximumFileCount == 1
all limits are positive
probe list is nonempty
probe IDs are unique ordinally
accepted exit codes are nonempty and unique
1 <= timeoutSeconds <= 300
argument/output collections contain no null values
```

Failures throw `ToolOperationException("ToolDefinitionInvalid", ...)` naming the source and field.

- [ ] **Step 4: Implement deterministic repository loading and normalized serialization**

```csharp
public sealed class RepositoryToolDefinitionProvider : IToolDefinitionProvider
{
    public RepositoryToolDefinitionProvider(string toolDefinitionDirectory);
    public IReadOnlyList<ResolvedToolDefinition> GetAll();
    public ResolvedToolDefinition GetRequired(string toolId, string platform);
}
```

Behavior:

1. Enumerate top-level `*.json` only.
2. Order paths case-insensitively then ordinally.
3. Reject comments and trailing commas.
4. Validate and compute `ToolDefinitionFingerprint`.
5. Reject duplicate `(toolId, platform)` pairs case-insensitively.
6. Use case-insensitive lookup but return canonical values.
7. Unknown tool => `UnknownTool`.
8. Missing definition directory => `ToolDefinitionInvalid`.

`ToolDefinitionSerializer` writes normalized indented camel-case JSON and reads local `tool-manifest.json` through the same validator.

- [ ] **Step 5: Add reusable test-definition helpers**

`ToolTestFixture`:

- locates repo root by walking upward from `AppContext.BaseDirectory` until `S1Atlas.sln` exists;
- builds fixture documents for `https://example.test/tool.exe`;
- computes expected size/SHA from caller bytes;
- writes temporary config directories;
- obtains `%ComSpec%` bytes only at test runtime;
- never writes executable bytes into the repository.

- [ ] **Step 6: Copy production config into build/publish output**

Modify CLI project:

```xml
<ItemGroup>
  <Content Include="..\..\config\tools\*.json"
           Link="config\tools\%(Filename)%(Extension)"
           CopyToOutputDirectory="PreserveNewest"
           CopyToPublishDirectory="PreserveNewest" />
</ItemGroup>
```

Verification after Release build:

```powershell
Test-Path src\S1Atlas.Cli\bin\Release\net8.0\config\tools\cpp2il.win-x64.json
```

Expected: `True`.

- [ ] **Step 7: Verify and commit Task 2**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "ToolDefinition|RepositoryToolDefinitionProvider"
dotnet build S1Atlas.sln --configuration Release
Test-Path src\S1Atlas.Cli\bin\Release\net8.0\config\tools\cpp2il.win-x64.json
git add -- config/tools src/S1Atlas.Extraction/Tools src/S1Atlas.Cli/S1Atlas.Cli.csproj tests/S1Atlas.Extraction.Tests/Tools
git commit -m "feat: pin and validate the managed Cpp2IL definition"
```

Expected: exact pin test passes, invalid documents fail safely, copied config exists, zero warnings/errors.

---

### Task 3: Add schema migration 3 and managed-tool provenance storage

**Files:**
- Modify: `src/S1Atlas.Storage/Migrations/SqliteMigrations.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.cs`
- Create: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Tools.cs`
- Modify: `tests/S1Atlas.Storage.Tests/Migrations/SqliteMigrationRunnerTests.cs`
- Create: `tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryToolTests.cs`
- Modify: `tests/S1Atlas.IntegrationTests/Foundation/FoundationMigrationTests.cs`

**Interfaces:**
- Consumes `IToolRepository`, `ManagedToolInstallation`, and `ToolInstance`.
- Produces atomic verified-tool provenance for CLI and Phase 3.

- [ ] **Step 1: Write failing migration-3 tests**

```csharp
[Fact]
public async Task MigrateAsync_V2Database_AddsToolTablesAndCreatesOneSchema3Backup()

[Fact]
public async Task MigrateAsync_NewDatabase_AppliesThreeMigrationsWithoutBackup()

[Fact]
public async Task MigrateAsync_FoundationV1Database_AppliesThroughV3AndPreservesFoundationState()
```

Create the v2 fixture by running an internal migration runner with:

```csharp
SqliteMigrations.All.Take(2).ToArray()
```

Then reopen through the production runner containing all three migrations.

Assert ledger versions `1/2/3`, both tool tables, index `ix_tool_instances_tool_trust`, exactly one schema-3 backup for v2 upgrade, no backup for an empty DB, and unchanged Foundation rows/pointer.

- [ ] **Step 2: Add exact migration 3 SQL**

Do not modify migration 1 or 2 text.

```sql
CREATE TABLE managed_tool_installations (
    tool_id TEXT NOT NULL,
    version TEXT NOT NULL,
    platform TEXT NOT NULL,
    definition_digest TEXT NOT NULL,
    package_sha256 TEXT NOT NULL,
    executable_sha256 TEXT NOT NULL,
    root_path TEXT NOT NULL,
    status TEXT NOT NULL,
    installed_at_utc TEXT NOT NULL,
    last_verified_at_utc TEXT NOT NULL,
    probe_summary TEXT NOT NULL,
    PRIMARY KEY (tool_id, version, platform)
);

CREATE TABLE tool_instances (
    tool_instance_id TEXT NOT NULL PRIMARY KEY,
    tool_name TEXT NOT NULL,
    version_label TEXT NULL,
    platform TEXT NOT NULL,
    trust_level TEXT NOT NULL,
    definition_digest TEXT NULL,
    package_sha256 TEXT NULL,
    executable_sha256 TEXT NOT NULL,
    observed_path TEXT NOT NULL,
    first_observed_at_utc TEXT NOT NULL,
    last_verified_at_utc TEXT NOT NULL,
    status TEXT NOT NULL
);

CREATE INDEX ix_tool_instances_tool_trust
ON tool_instances(tool_name, trust_level);
```

Register:

```csharp
new(3, "managed-tools-v3", ManagedToolsV3Sql)
```

- [ ] **Step 3: Implement the partial repository tool interface**

Change declaration:

```csharp
public sealed partial class SqliteAtlasRepository : IAtlasRepository, IToolRepository
```

Create tool methods in a separate partial file.

Before transaction, require:

```text
installation.Status == Verified
toolInstance.Status == Verified
toolInstance.TrustLevel == ManagedPinned
tool IDs/platform/executable SHA/definition digest agree
ToolInstanceId.Create(...) equals stored toolInstance ID
```

In one transaction:

1. Upsert `managed_tool_installations` by `(tool_id, version, platform)`.
2. Upsert `tool_instances` by `tool_instance_id`.
3. Preserve earliest `first_observed_at_utc` on instance conflict.
4. Update path/status/version/last verification.
5. Store compact camel-case probe-result JSON with string enums in `probe_summary`.
6. Commit both rows or rollback both.

Reads:

```csharp
public Task<ManagedToolInstallation?> GetManagedToolAsync(...)
public Task<ToolInstance?> GetToolInstanceAsync(...)
```

Use invariant `O` timestamps. Corrupt stored enums or probe JSON are integrity failures.

- [ ] **Step 4: Add round-trip and atomicity tests**

```csharp
[Fact]
public async Task SaveVerifiedManagedToolAsync_RoundTripsInstallationAndToolInstance()

[Fact]
public async Task SaveVerifiedManagedToolAsync_ReverificationPreservesFirstObservedAndUpdatesLastVerified()

[Fact]
public async Task SaveVerifiedManagedToolAsync_WhenInstallationIsNotVerified_RejectsWithoutRows()

[Fact]
public async Task SaveVerifiedManagedToolAsync_WhenToolInstanceIdentityDisagrees_RollsBackBothRows()
```

- [ ] **Step 5: Reconcile Phase 1 migration tests**

Update only expected latest-schema facts:

```text
ledger count 3
Foundation-v1 direct-upgrade backup pattern atlas-before-schema-3-*.db
backup still contains original v1 schema
v1/v2 snapshot identity and current-pointer behavior unchanged
real Foundation Steam build ID remains null until a v2 scan
```

- [ ] **Step 6: Verify and commit Task 3**

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter FoundationMigrationTests
dotnet build S1Atlas.sln --configuration Release
git add -- src/S1Atlas.Storage tests/S1Atlas.Storage.Tests tests/S1Atlas.IntegrationTests/Foundation/FoundationMigrationTests.cs
git commit -m "feat: persist managed tool provenance"
```

Expected: all selected tests pass; zero warnings/errors.

---

### Task 4: Add bounded HTTPS download, exact verification, and safe package materialization

**Files:**
- Create: `src/S1Atlas.Extraction/Tools/ToolDownloadClient.cs`
- Create: `src/S1Atlas.Extraction/Tools/ToolPackageVerifier.cs`
- Create: `src/S1Atlas.Extraction/Tools/ToolPathPolicy.cs`
- Create: `src/S1Atlas.Extraction/Tools/SafeToolPackageInstaller.cs`
- Test: corresponding Task 4 test files

**Interfaces:**
- Consumes trusted `ResolvedToolDefinition`.
- Produces a verified staged package and contained staged install tree; it does not execute or promote the tool.

- [ ] **Step 1: Write failing download-boundary tests**

```csharp
[Fact]
public async Task DownloadAsync_StreamsExactResponseToStaging()

[Fact]
public async Task DownloadAsync_WhenContentLengthExceedsLimit_RejectsBeforeReadingBody()

[Fact]
public async Task DownloadAsync_WhenChunkedBodyExceedsLimit_StopsAndDeletesPartialFile()

[Fact]
public async Task DownloadAsync_WhenStatusIsNotSuccess_ReportsToolDownloadFailed()

[Fact]
public async Task DownloadAsync_WhenFinalRequestUriIsNotHttps_RejectsBeforeReadingBody()
```

- [ ] **Step 2: Implement bounded download**

```csharp
internal sealed class ToolDownloadClient(HttpClient httpClient)
{
    public Task DownloadAsync(
        Uri sourceUri,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken);
}
```

Requirements:

1. Initial and final URI must be absolute HTTPS with empty `UserInfo`.
2. Use `ResponseHeadersRead`.
3. Check final URI and status before body read.
4. Reject oversized `Content-Length` before body read.
5. Stream to an Atlas-owned staging path with fixed buffer.
6. Count bytes and abort before exceeding maximum.
7. Flush/close before success.
8. Delete partial file best-effort on any failure/cancellation.
9. Use `ToolDownloadFailed` except more specific size/cancellation errors.

- [ ] **Step 3: Add exact package verification**

Tests:

```csharp
VerifyAsync_WhenSizeAndShaMatch_ReturnsObservedFacts
VerifyAsync_WhenSizeDiffers_ThrowsToolSizeMismatch
VerifyAsync_WhenShaDiffers_ThrowsToolChecksumMismatch
```

```csharp
internal sealed record VerifiedToolPackage(
    string Path,
    long Size,
    string Sha256);

internal sealed class ToolPackageVerifier
{
    public Task<VerifiedToolPackage> VerifyAsync(
        string packagePath,
        ToolPackageDefinition package,
        CancellationToken cancellationToken);
}
```

Compare size first, then full lower-case SHA-256 ordinally.

- [ ] **Step 4: Add path-policy tests and implementation**

```csharp
public static string GetManagedInstallRoot(string toolsRoot, ToolDefinition definition);
public static string ResolveContainedRelativePath(string root, string relativePath);
public static void EnsureNoReparsePointInExistingPath(string root, string candidate);
public static string CreateStagingPath(string stagingRoot, ToolDefinition definition);
public static string CreateQuarantinePath(
    string quarantineRoot,
    ToolDefinition definition,
    DateTimeOffset timestamp);
```

Reject absolute/UNC/drive paths, `.`/`..`, mixed-separator traversal, outside-root candidates, reparse points, and unsafe tool/version segments.

Install root:

```text
<toolsRoot>/<toolId>/<version>
```

- [ ] **Step 5: Write single-file and ZIP safety tests**

```text
MaterializeAsync_SingleFile_CopiesOnlyToDeclaredExecutablePath
MaterializeAsync_SingleFile_WhenDeclaredPathEscapes_Rejects
MaterializeAsync_Zip_ExtractsContainedRegularFiles
MaterializeAsync_Zip_WhenEntryContainsDotDot_Rejects
MaterializeAsync_Zip_WhenEntryIsAbsolute_Rejects
MaterializeAsync_Zip_WhenEntriesCollideCaseInsensitively_Rejects
MaterializeAsync_Zip_WhenEntryIsUnixSymlink_Rejects
MaterializeAsync_Zip_WhenExpandedBytesExceedLimit_Rejects
MaterializeAsync_Zip_WhenRegularFileCountExceedsLimit_Rejects
MaterializeAsync_Zip_WhenDeclaredExecutableIsMissing_Rejects
```

ZIP `MaximumFileCount` counts regular file entries, not directory entries. Set Unix symlink external attributes in-memory; do not create real links.

- [ ] **Step 6: Implement safe materialization**

```csharp
internal sealed record MaterializedToolPackage(
    string InstallRoot,
    string ExecutablePath,
    int FileCount,
    long ExpandedBytes);

internal sealed class SafeToolPackageInstaller
{
    public Task<MaterializedToolPackage> MaterializeAsync(
        ResolvedToolDefinition definition,
        VerifiedToolPackage package,
        string stagedInstallRoot,
        CancellationToken cancellationToken);
}
```

Single file: create only contained executable parent, async copy, exact byte count, no overwrite/reparse.

ZIP: preflight before writes; normalize both slash types; reject rooted/empty/dot/dot-dot segments; reject case-insensitive collisions, DOS reparse attributes, Unix symlink/special types; accept regular files/directories only; use overflow-safe count/size totals; extract without overwrite; require declared executable.

On failure, delete only the supplied staged install root best-effort.

- [ ] **Step 7: Verify and commit Task 4**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "ToolDownloadClient|ToolPackageVerifier|ToolPathPolicy|SafeToolPackageInstaller"
dotnet build S1Atlas.sln --configuration Release
git add -- src/S1Atlas.Extraction/Tools tests/S1Atlas.Extraction.Tests/Tools
git commit -m "feat: safely acquire and materialize tool packages"
```

---

### Task 5: Add controlled capability probes and installation inspection

**Files:**
- Create: `ToolProbeRunner.cs`, `ToolInstallationDocument.cs`, `ToolInstallationDocumentStore.cs`, `ManagedToolInstallationValidator.cs`
- Test: corresponding Task 5 files

**Interfaces:**
- Consumes a contained executable and committed probes.
- Produces bounded probe results and exact managed states.

- [ ] **Step 1: Write failing process-probe tests**

Copy `%ComSpec%` to a temporary path for tests only. Use `/d`, `/c`, `echo dll_il_recovery`.

```text
RunAsync_WhenExitAndRequiredOutputMatch_ReturnsSucceeded
RunAsync_WhenExitCodeIsNotAccepted_ReturnsFailure
RunAsync_WhenRequiredOutputIsMissing_ReturnsFailure
RunAsync_WhenTimeoutExpires_KillsProcessAndReturnsTimedOut
RunAsync_WhenCancellationRequested_KillsProcessAndThrowsCancellation
RunAsync_WhenOutputExceedsLimit_ContinuesDrainingAndMarksTruncated
```

- [ ] **Step 2: Implement bounded no-shell probes**

```csharp
internal sealed class ToolProbeRunner
{
    internal const int MaximumRetainedBytesPerStream = 1024 * 1024;

    public Task<ToolProbeResult> RunAsync(
        string executablePath,
        string workingDirectory,
        ToolProbeDefinition probe,
        CancellationToken cancellationToken);
}
```

Process settings:

```text
UseShellExecute false
CreateNoWindow true
redirect stdout/stderr
working directory = contained install root
NO_COLOR=true
arguments added individually via ArgumentList
```

Drain both streams concurrently. Retain at most 1 MiB each while consuming discarded bytes. Decode UTF-8 with replacement fallback.

On timeout/cancellation kill the entire process tree, await exit/drains, return timed-out result for timeout, and rethrow caller cancellation. Required substrings use ordinal comparison over combined retained output.

- [ ] **Step 3: Define normalized local documents**

```csharp
internal sealed class ToolInstallationDocumentStore
{
    public Task WriteAsync(
        string installRoot,
        ResolvedToolDefinition definition,
        ManagedToolInstallation installation,
        CancellationToken cancellationToken);

    public Task<(ResolvedToolDefinition Definition,
                 ManagedToolInstallation Installation)?> TryReadAsync(
        string installRoot,
        CancellationToken cancellationToken);
}
```

Write `tool-manifest.json` and `installation.json` as UTF-8 no-BOM, indented camel-case JSON with string enums. Use sibling temp files and rename inside staged root. Parse local manifest through the same strict validator. Malformed/missing documents return null. Do not store probe stdout/stderr.

- [ ] **Step 4: Write failing installation-state tests**

```text
InspectAsync_WhenRootDoesNotExist_ReturnsNotInstalled
InspectAsync_WhenDocumentsOrExecutableAreMissing_ReturnsIncomplete
InspectAsync_WhenLocalDefinitionDigestDiffers_ReturnsDefinitionMismatch
InspectAsync_WhenExecutableHashDiffers_ReturnsCorruptWithoutRunningProbes
InspectAsync_WhenProbeFails_ReturnsProbeFailed
InspectAsync_WhenEverythingMatches_ReturnsVerifiedWithFreshVerificationTime
```

Use an injected probe executor/delegate to count invocations; hash mismatch must short-circuit probes.

- [ ] **Step 5: Implement inspection**

```csharp
internal sealed class ManagedToolInstallationValidator
{
    public Task<ManagedToolStatus> InspectAsync(
        ResolvedToolDefinition definition,
        CancellationToken cancellationToken);

    internal Task<ManagedToolStatus> InspectAtRootAsync(
        ResolvedToolDefinition definition,
        string installRoot,
        CancellationToken cancellationToken);
}
```

Dependencies: tools root, document store, probe runner, `IFileHasher`, `TimeProvider`.

Order:

```text
root absent -> NotInstalled
not contained/normal or reparse crossing -> Incomplete
documents missing/malformed -> Incomplete
definition digests differ -> DefinitionMismatch
executable missing/not regular/reparse -> Incomplete
executable hash differs -> Corrupt
run probes in committed order
any probe failure -> ProbeFailed
all pass -> Verified
```

Verified status preserves original install time, uses current root/hash/probes, and sets fresh last verification time. A moved whole Atlas root does not invalidate bytes merely because old absolute `RootPath` differs; paths are not identity inputs.

- [ ] **Step 6: Verify and commit Task 5**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "ToolProbeRunner|ManagedToolInstallationValidator"
dotnet build S1Atlas.sln --configuration Release
git add -- src/S1Atlas.Extraction/Tools tests/S1Atlas.Extraction.Tests/Tools
git commit -m "feat: probe and inspect managed tool installations"
```

---

### Task 6: Add staged installation, repair, quarantine, and service orchestration

**Files:**
- Create: `ManagedToolInstaller.cs`, `ManagedToolService.cs`
- Test: `ManagedToolInstallerTests.cs`, `ManagedToolServiceTests.cs`

**Interfaces:**
- Consumes Tasks 1–5 and `IToolRepository`.
- Produces the complete Phase 2 application service.

- [ ] **Step 1: Write failing installer orchestration tests**

Use copied `%ComSpec%` bytes, fake HTTPS, and probes that echo `dll_il_recovery`.

```text
InstallAsync_WhenNotInstalled_DownloadsVerifiesProbesAndPromotes
InstallAsync_WhenAlreadyVerified_IsNoOpWithoutHttp
InstallAsync_WhenExistingInstallationIsInvalidWithoutRepair_RequiresRepairWithoutHttp
InstallAsync_WithRepair_StagesBeforeMovingExistingRootAndQuarantinesOldRoot
InstallAsync_WhenRepairDownloadFails_LeavesExistingRootAtOriginalPath
InstallAsync_WhenPromotionFails_RestoresQuarantinedRootBestEffort
InstallAsync_WhenCanceled_RemovesOnlyOwnedStagingPath
```

- [ ] **Step 2: Implement installer flow**

```csharp
internal sealed class ManagedToolInstaller : IToolInstaller
{
    public ManagedToolInstaller(
        string toolsRoot,
        string stagingRoot,
        string quarantineRoot,
        ManagedToolInstallationValidator validator,
        ToolDownloadClient downloadClient,
        ToolPackageVerifier packageVerifier,
        SafeToolPackageInstaller packageInstaller,
        ToolInstallationDocumentStore documentStore,
        ToolProbeRunner probeRunner,
        IFileHasher fileHasher,
        TimeProvider? timeProvider = null);
}
```

Sequence:

```text
inspect final root
  Verified -> return no-op, no HTTP
  invalid + !repair -> ToolRepairRequired, no HTTP
  NotInstalled or invalid + repair -> continue

create unique owned staging root
download package to a contained asset path
verify exact size/SHA
materialize staged install
hash executable
run committed probes
require every probe success
precompute quarantine path when replacing
create Verified ManagedToolInstallation
write local documents
inspect staged root; require Verified

promotion
  no existing root -> Directory.Move(staged install, final)
  repair -> move old file/directory to quarantine, then move staged install to final
  second move failure -> best-effort restore quarantine

inspect final root; require Verified
return ManagedToolInstallOutcome
finally remove package and owned staging path best-effort
```

Errors:

```text
ToolRepairRequired
ToolSizeMismatch
ToolChecksumMismatch
ToolDownloadFailed
ToolProbeFailed
ToolInstallationFailed
```

Failed repair never erases/overwrites prior root. Moves stay on the Atlas tools volume.

- [ ] **Step 3: Write failing service tests**

```text
GetStatusesAsync_WithoutToolId_ReturnsCurrentPlatformDefinitionsInDeterministicOrder
GetStatusAsync_WhenVerified_UpsertsInstallationAndToolInstance
InstallAsync_WhenFilesystemSucceeds_RegistersVerifiedProvenance
InstallAsync_WhenRepositorySaveFails_LeavesVerifiedFilesystemForLaterStatusRecovery
InstallAsync_UnknownTool_FailsBeforeHttpOrFilesystemWork
```

- [ ] **Step 4: Implement service orchestration**

```csharp
public sealed class ManagedToolService
{
    public ManagedToolService(
        IToolDefinitionProvider definitionProvider,
        ManagedToolInstallationValidator validator,
        IToolInstaller installer,
        IToolRepository repository,
        string platform,
        TimeProvider? timeProvider = null);

    public Task<IReadOnlyList<ManagedToolStatus>> GetStatusesAsync(
        string? toolId,
        CancellationToken cancellationToken);

    public Task<ToolInstallResult> InstallAsync(
        string toolId,
        bool repair,
        CancellationToken cancellationToken);
}
```

No tool ID: filter `GetAll()` to current platform and order by tool ID ordinally.

For each verified status or install outcome, construct:

```csharp
var toolInstanceId = ToolInstanceId.Create(
    definition.Definition.ToolId,
    installation.ExecutableSha256,
    definition.Definition.Platform,
    ToolTrustLevel.ManagedPinned);
```

Observed executable path is the contained combination of install root and declared executable path. New instance first-observed time begins at installation time; repository preserves earlier time on re-verification.

Persist verified installation and instance atomically, then return `ToolInstallResult`. If DB save fails after filesystem promotion, return operational failure but do not delete the verified filesystem root; later `tools status` re-registers it.

- [ ] **Step 5: Verify and commit Task 6**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "ManagedToolInstaller|ManagedToolService"
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj --filter SqliteAtlasRepositoryToolTests
dotnet build S1Atlas.sln --configuration Release
git add -- src/S1Atlas.Extraction/Tools tests/S1Atlas.Extraction.Tests/Tools
git commit -m "feat: install and register managed tools atomically"
```

---

### Task 7: Expose CLI commands, document behavior, and prove the full boundary

**Files:**
- Modify: `AtlasPaths.cs`, `CliApplication.cs`, `CommandExecution.cs`, `Program.cs`, `README.md`
- Create: `CliConfigurationPaths.cs`, tool command/output files, CLI AssemblyInfo, integration fixture/tests

**Interfaces:**
- Consumes `ManagedToolService` and existing `CommandOutput`.
- Produces complete Phase 2 user commands without changing Foundation command contracts.

- [ ] **Step 1: Add Atlas and config paths**

```csharp
public string ToolsDirectory => Path.Combine(RootDirectory, "tools");
public string ToolStagingDirectory => Path.Combine(ToolsDirectory, ".staging");
public string ToolQuarantineDirectory => Path.Combine(ToolsDirectory, "quarantine");
```

```csharp
internal sealed record CliConfigurationPaths(string RootDirectory)
{
    public string ToolDefinitionsDirectory => Path.Combine(RootDirectory, "tools");
    public static CliConfigurationPaths Resolve();
}
```

Resolution:

1. `AppContext.BaseDirectory/config` when `tools` exists.
2. Walk upward from app base until `S1Atlas.sln` plus `config/tools` exists for development/tests.
3. Return app-base candidate otherwise so provider emits one clear error.

No environment/CLI override for committed definitions in Phase 2.

- [ ] **Step 2: Write failing CLI integration tests**

Fixture:

- temporary Atlas and config roots;
- `%ComSpec%` bytes held/copy only in temp;
- fixture manifest with matching size/SHA;
- new fake `HttpClient` per CLI invocation;
- request counter;
- no Schedule I path/files.

Tests:

```text
ToolsStatus_WhenNotInstalled_ReportsNotInstalledWithoutHttp
ToolsStatusJson_WhenNotInstalled_ReturnsOneStableEnvelope
ToolsInstall_DownloadsOnceAndReportsVerified
ToolsInstallJson_ReturnsVerifiedManagedPinFacts
ToolsInstall_WhenAlreadyVerified_IsNoOpWithoutSecondRequest
ToolsInstall_WhenCorrupt_RequiresRepairWithoutRequest
ToolsInstallRepair_QuarantinesCorruptRootAndReturnsVerified
ToolsStatus_WhenCorrupt_ReturnsSuccessWithCorruptState
ToolsInstall_WhenChecksumDiffers_ReturnsStructuredFailureAndNoFinalRoot
ToolsStatus_UnknownTool_ReturnsUnknownToolWithoutHttp
```

JSON:

```text
schemaVersion 1
command "tools status" or "tools install"
normal status states are success/exit 0
install failures have null data and {code,message}
stderr empty in JSON mode
no stack trace
```

- [ ] **Step 3: Define output DTOs**

```csharp
internal sealed record ToolsStatusOutput(
    IReadOnlyList<ToolStatusOutput> Tools);

internal sealed record ToolStatusOutput(
    string ToolId,
    string DisplayName,
    string PinnedVersion,
    string Platform,
    string DefinitionDigest,
    string Status,
    string? TrustLevel,
    string? PackageSha256,
    string? ExecutableSha256,
    string? InstallRoot,
    DateTimeOffset? InstalledAtUtc,
    DateTimeOffset? LastVerifiedAtUtc,
    string? DiagnosticCode,
    string? DiagnosticMessage,
    IReadOnlyList<ToolProbeOutput> Probes);

internal sealed record ToolProbeOutput(
    string ProbeId,
    bool Succeeded,
    int? ExitCode,
    bool TimedOut,
    string? FailureCode,
    string? FailureMessage);

internal sealed record ToolInstallOutput(
    ToolStatusOutput Tool,
    bool WasAlreadyVerified,
    bool Repaired,
    string? QuarantinePath);
```

Never expose captured probe streams.

- [ ] **Step 4: Implement command tree**

```text
tools
  status [tool-id] [--json]
  install <tool-id> [--repair] [--json]
```

Each tool command initializes `SqliteAtlasRepository` before service use so migration 3 is applied before provenance writes.

`tools status` returns exit 0 for known NotInstalled/Corrupt/Incomplete/DefinitionMismatch/ProbeFailed states; unknown definition/platform/integrity errors return 1.

Human status:

```text
Cpp2IL

Pinned version:       2022.1.0-pre-release.21
Platform:             win-x64
Definition digest:    <digest>
Installation status:  <state>
Executable checksum:  <hash or unknown>
Install root:         <path or not installed>
Last verified:        <timestamp or never>
```

Not installed adds:

```text
Install with:
  s1atlas tools install cpp2il
```

Install messages:

```text
Cpp2IL 2022.1.0-pre-release.21 installed and verified.
```

```text
Cpp2IL 2022.1.0-pre-release.21 is already installed and verified.
No work required.
```

```text
Cpp2IL 2022.1.0-pre-release.21 repaired and verified.
Previous installation moved to:
  <quarantine path>
```

- [ ] **Step 5: Make tool exceptions output-aware**

Before generic catch:

```csharp
catch (ToolOperationException exception)
{
    return output.Failure(1, exception.Code, exception.Message);
}
```

Cancellation remains exit 2/code `OperationCanceled`; generic remains exit 1/code `OperationalFailure`.

- [ ] **Step 6: Compose production/test dependencies**

Keep public constructor. Add internal constructor:

```csharp
internal CliApplication(
    string dataDirectory,
    string atlasVersion,
    string configurationDirectory,
    Func<HttpClient> toolHttpClientFactory,
    TimeProvider? timeProvider = null)
```

CLI internals visibility:

```csharp
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("S1Atlas.IntegrationTests")]
```

Production HTTP client has infinite timeout, `User-Agent: S1Atlas/<atlasVersion>`, and is disposed after each invocation. Redirects are allowed; final URI is revalidated by download client.

Compose one SQLite repository and the tool provider/validator/installer/service from Atlas/config paths. `tools status` must never issue an HTTP request; throwing fake handler proves it.

- [ ] **Step 7: Wire Ctrl+C**

```csharp
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
```

Pass token to `Invoke`.

- [ ] **Step 8: Update README**

Document:

```text
Phase 1 implemented
managed Cpp2IL supply chain implemented
tools status/install commands
only tools install uses network
exact official pin/size/SHA
default cache and S1ATLAS_HOME
one possible atlas-before-schema-3 backup
repair/quarantine behavior
o game execution yet
Phase 3 next: profiles, inputs, attempts, process execution, cancellation, logs
```

- [ ] **Step 9: Run full automated verification**

```powershell
dotnet restore S1Atlas.sln
dotnet build S1Atlas.sln --configuration Release --no-restore
dotnet test S1Atlas.sln --configuration Release --no-build --verbosity normal
```

Expected: zero warnings/errors, all projects pass, no external download.

- [ ] **Step 10: Verify repository scope**

```powershell
git status --short
git diff --check
git ls-files | Select-String -Pattern "Cpp2IL\.exe|atlas\.db|\.db-wal|\.db-shm|installation\.json|tool-manifest\.json"
```

Expected: no executable, generated manifest, DB, backup, or local record tracked. The committed file is `config/tools/cpp2il.win-x64.json`, not generated `tool-manifest.json`.

- [ ] **Step 11: Run real Windows managed-pin smoke gate**

```cmd
dotnet run --configuration Release --project src\S1Atlas.Cli -- tools status cpp2il
dotnet run --configuration Release --project src\S1Atlas.Cli -- tools install cpp2il
dotnet run --configuration Release --project src\S1Atlas.Cli -- tools status cpp2il
dotnet run --configuration Release --project src\S1Atlas.Cli -- tools install cpp2il
dotnet run --configuration Release --project src\S1Atlas.Cli -- tools status cpp2il --json
```

Expected:

```text
first status NotInstalled unless verified pin already exists
first install downloads once and verifies exact official bytes/probes
second status Verified
second install successful no-op/no download
JSON one schema-version-1 document
path %LOCALAPPDATA%\S1Atlas\tools\cpp2il\2022.1.0-pre-release.21\Cpp2IL.exe
```

Record without committing: observed package/executable hashes, definition digest, probes, install root, schema-3 backup path. `git status` remains clean.

- [ ] **Step 12: Commit Task 7**

```powershell
git add -- src/S1Atlas.Cli tests/S1Atlas.IntegrationTests/Tools README.md
git commit -m "feat: expose the managed Cpp2IL tool supply chain"
```

---

## Phase 2 Review Checklist

```text
[ ] Production pin exactly matches approved version/asset/size/SHA
[ ] Definitions are typed, strict, repository controlled
[ ] No latest lookup or remote mutable manifest
[ ] Every effective definition field affects definition digest
[ ] Tool-instance identity uses stable ID, executable bytes, platform, trust only
[ ] Paths/timestamps/display/version metadata do not affect tool-instance identity
[ ] Migrations 1/2 text and checksums remain unchanged
[ ] Existing v2 DB gets one schema-3 backup
[ ] Installation and instance rows commit atomically
[ ] Only credential-free HTTPS accepted
[ ] Download has hard limit and partial cleanup
[ ] Exact size and SHA verified before execution
[ ] Single-file install stays inside staging
[ ] ZIP traversal/absolute/collision/link/limit attacks rejected
[ ] Probe execution has no shell string and bounded draining
[ ] Timeout/cancellation kills process tree
[ ] Status states exactly match approved six states
[ ] Hash mismatch prevents probes
[ ] Verified install is no-op without HTTP
[ ] Invalid install requires explicit repair without HTTP
[ ] Repair stages/verifies before moving old root
[ ] Failed repair preserves old root
[ ] Successful repair quarantines old root
[ ] Filesystem success can be re-registered after DB failure
[ ] tools status is offline
[ ] tools install is only Phase 2 network path
[ ] Human/JSON contracts and exit codes 0/1/2 preserved
[ ] Known invalid/not-installed status query exits 0
[ ] No public stack trace
[ ] Ctrl+C reaches download/probes
[ ] Full Windows suite passes, zero warnings/errors, no external test download
[ ] Real official-pin install/no-op smoke passes
[ ] No executable/generated manifest/DB/backup/game data enters Git
[ ] No Cpp2IL game execution entered Phase 2
```

## Phase 2 Completion Boundary

After this plan, S1Atlas can explain the approved Cpp2IL pin, explicitly install the official asset, prove package bytes, materialize it safely under Atlas ownership, verify required capabilities, repair/quarantine invalid installations, persist managed provenance, and report the result to humans or agents.

Phase 3 or later begins:

```text
extraction profiles and validation policies
typed Cpp2IL game arguments
build input resolution and pre/post hashing
archived input snapshots
extraction locks/attempts
Cpp2IL game process execution and bounded logs
failed-output retention
artifact inventories and assembly validation
preferred extractions
ILSpy and symbols
```

## Execution Mode

The user selected **inline execution** in ChatGPT. After this plan passes QA and merges, execute Tasks 1–7 sequentially with `superpowers:executing-plans`, TDD, focused commits, CI checkpoints, and a draft implementation PR.