# Cpp2IL Phase 2 Managed Tool Supply Chain Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an explicit, checksum-pinned, recoverable managed Cpp2IL installation pipeline with offline status inspection, safe package handling, capability probes, SQLite provenance, and human/JSON CLI commands.

**Architecture:** The repository owns one typed Cpp2IL definition under `config/tools`. `S1Atlas.Core` owns immutable tool contracts and deterministic identities; `S1Atlas.Extraction` parses the committed definition, downloads and verifies packages, materializes them only in Atlas-owned staging, runs controlled probes, and atomically promotes verified installations; `S1Atlas.Storage` persists verified installation and tool-instance provenance; `S1Atlas.Cli` exposes `tools status` and `tools install`. No extraction profile, game input, Cpp2IL extraction, reconstructed assembly, or ILSpy behavior enters this phase.

**Tech Stack:** C# / .NET 8, `System.Text.Json`, `System.Net.Http`, `System.IO.Compression`, `System.Diagnostics.Process`, SHA-256, Microsoft.Data.Sqlite, System.CommandLine, xUnit v3, Windows GitHub Actions.

## Global Constraints

- Target Windows 10 or later and `win-x64`; the production Cpp2IL pin is not silently substituted on unsupported platforms.
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
- Tool definition changes are reviewed source changes. No mutable “latest” lookup exists.
- The initial asset is a standalone executable. The implementation also safely supports a future ZIP archive definition, but no production archive pin is added.
- All working, staging, quarantine, and final installation paths are under the configured Atlas data root. The Schedule I installation is never touched.
- Use `ProcessStartInfo.ArgumentList`; never invoke probes through `cmd.exe`, PowerShell, or a shell string in production code.
- All automated tests use injected local bytes and fake HTTP handlers. CI never downloads Cpp2IL or any release asset.
- Do not commit executable fixtures, downloaded packages, databases, backups, or generated local installation records.
- Preserve Phase 1 human output, JSON envelopes, exit codes `0/1/2`, migration compatibility, and zero-warning Release builds.

---

## Phase 2 Scope Boundary

Phase 2 delivers:

```text
config/tools/cpp2il.win-x64.json
repository-controlled tool-definition parsing and validation
deterministic tool-definition and tool-instance identities
schema migration 3 for managed tool provenance
safe HTTPS streaming download
exact size and SHA-256 verification
single-file materialization
safe ZIP extraction for future definitions
controlled --help and --list-output-formats probes
managed installation inspection and status states
staged atomic installation
repair and quarantine
s1atlas tools status [cpp2il] [--json]
s1atlas tools install cpp2il [--repair] [--json]
README and local smoke instructions
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
cleanup of extraction attempts
ILSpy or symbol indexing
```

---

## File Structure

### Repository configuration

```text
config/tools/cpp2il.win-x64.json
```

The only production tool definition. It contains the approved pin, safety limits, license metadata, and controlled probe definitions.

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

`SqliteAtlasRepository` becomes a partial class implementing both `IAtlasRepository` and `IToolRepository` so one migration runner and database remain authoritative.

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
- Create: `src/S1Atlas.Core/Tools/ToolArchiveFormat.cs`
- Create: `src/S1Atlas.Core/Tools/ToolDefinition.cs`
- Create: `src/S1Atlas.Core/Tools/ToolDefinitionFingerprint.cs`
- Create: `src/S1Atlas.Core/Tools/ToolInstallResult.cs`
- Create: `src/S1Atlas.Core/Tools/ToolInstallationStatus.cs`
- Create: `src/S1Atlas.Core/Tools/ToolInstance.cs`
- Create: `src/S1Atlas.Core/Tools/ToolInstanceId.cs`
- Create: `src/S1Atlas.Core/Tools/ToolOperationException.cs`
- Create: `src/S1Atlas.Core/Tools/ToolPackageKind.cs`
- Create: `src/S1Atlas.Core/Tools/ToolPlatform.cs`
- Create: `src/S1Atlas.Core/Tools/ToolStatus.cs`
- Create: `src/S1Atlas.Core/Tools/ToolTrustLevel.cs`
- Create: `src/S1Atlas.Core/Tools/IToolDefinitionProvider.cs`
- Create: `src/S1Atlas.Core/Tools/IToolInstaller.cs`
- Create: `src/S1Atlas.Core/Storage/IToolRepository.cs`
- Test: `tests/S1Atlas.Core.Tests/Identity/CanonicalHashWriterTests.cs`
- Test: `tests/S1Atlas.Core.Tests/Tools/ToolDefinitionFingerprintTests.cs`
- Test: `tests/S1Atlas.Core.Tests/Tools/ToolInstanceIdTests.cs`

**Interfaces:**
- Produces `ResolvedToolDefinition`, `ManagedToolInstallation`, `ToolInstance`, `ManagedToolStatus`, `ToolInstallResult`, and deterministic digest helpers used by every later task.
- Consumes no Phase 2 implementation types.

- [ ] **Step 1: Add internals visibility and failing canonical-identity tests**

Create `src/S1Atlas.Core/Properties/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("S1Atlas.Core.Tests")]
```

Create tests proving the version-1 canonical writer:

```csharp
[Fact]
public void Complete_WithSameTypedValues_ReturnsSameLowercaseSha256()
```

```csharp
[Fact]
public void Complete_NullAndEmptyString_ReturnDifferentDigests()
```

```csharp
[Fact]
public void Complete_ChangingIdentityKindOrVersion_ChangesDigest()
```

Run:

```powershell
dotnet test tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj --filter CanonicalHashWriterTests
```

Expected: compile failure because `CanonicalHashWriter` does not exist.

- [ ] **Step 2: Implement `CanonicalHashWriter` version 1**

Use an `IncrementalHash` with SHA-256. The constructor writes identity kind and identity schema version first.

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

Encoding rules:

```text
non-null scalar:
  one byte 0x01
  four-byte little-endian UTF-8 byte length
  UTF-8 bytes

null scalar:
  one byte 0x00

integers:
  invariant decimal text through the non-null scalar encoding

boolean:
  "1" or "0" through the non-null scalar encoding

result:
  lower-case full SHA-256 hexadecimal
```

`Complete()` may be called once. Subsequent appends or completion throw `InvalidOperationException`.

Run the focused tests and expect all to pass.

- [ ] **Step 3: Define exact tool enums and immutable records**

Create enums:

```csharp
public enum ToolPackageKind
{
    SingleFile,
    Archive
}

public enum ToolArchiveFormat
{
    Zip
}

public enum ToolTrustLevel
{
    ManagedPinned,
    CustomOverride
}

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

Create `ToolDefinition.cs` with these records:

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

Create status/provenance records:

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

`ToolName` in tool-instance identity means the stable lower-case tool ID (`cpp2il`), not the display label.

- [ ] **Step 4: Write failing definition and instance identity tests**

Add tests:

```csharp
[Fact]
public void Create_WithEquivalentDefinition_ReturnsSameDigest()
```

```csharp
[Fact]
public void Create_WhenProbeRequirementChanges_ReturnsDifferentDigest()
```

```csharp
[Fact]
public void Create_WhenLicenseOrSafetyLimitChanges_ReturnsDifferentDigest()
```

```csharp
[Fact]
public void ToolInstanceId_SameBytesAtDifferentPaths_ReturnsSameId()
```

```csharp
[Fact]
public void ToolInstanceId_WhenTrustLevelChanges_ReturnsDifferentId()
```

```csharp
[Fact]
public void ToolInstanceId_NullAndEmptyVersionLabels_DoNotAffectIdentity()
```

The last test verifies version label is descriptive and excluded entirely from tool-instance identity.

Run and expect compile failure because the fingerprint helpers do not exist.

- [ ] **Step 5: Implement definition and tool-instance identities**

`ToolDefinitionFingerprint.Create(ToolDefinition)` appends every effective field in this exact order:

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
source URL absolute URI
release URL absolute URI
asset name
expected size
package SHA-256
executable relative path
maximum download bytes
maximum expanded bytes
maximum file count
license SPDX identifier
license source URL absolute URI
probe count
for each probe in declared order:
  probe ID
  argument count and each argument
  accepted exit-code count and each exit code
  timeout milliseconds
  required-output count and each substring
```

`ToolInstanceId.Create(...)` appends:

```text
identity kind "tool-instance"
identity version 1
stable tool ID
observed executable SHA-256
platform
trust-level enum name
```

Absolute paths, timestamps, display name, and version label are not included.

- [ ] **Step 6: Add platform and operation contracts**

`ToolPlatform.GetCurrent()` returns `win-x64` only when:

```csharp
OperatingSystem.IsWindows() &&
RuntimeInformation.ProcessArchitecture == Architecture.X64
```

Otherwise it throws:

```csharp
new ToolOperationException(
    "ToolPlatformUnsupported",
    "The managed Cpp2IL tool is supported only on Windows x64.");
```

Create:

```csharp
public sealed class ToolOperationException : InvalidOperationException
{
    public ToolOperationException(string code, string message, Exception? innerException = null);
    public string Code { get; }
}
```

Interfaces:

```csharp
public interface IToolDefinitionProvider
{
    IReadOnlyList<ResolvedToolDefinition> GetAll();
    ResolvedToolDefinition GetRequired(string toolId, string platform);
}

public interface IToolInstaller
{
    Task<ToolInstallResult> InstallAsync(
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
```

Expected: all Core tests pass; zero warnings and zero errors.

```powershell
git add -- src/S1Atlas.Core tests/S1Atlas.Core.Tests
git commit -m "feat: define managed tool identities and contracts"
```

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
- Test: `tests/S1Atlas.Extraction.Tests/Tools/ToolTestFixture.cs`

**Interfaces:**
- Consumes Core tool records and `ToolDefinitionFingerprint`.
- Produces a strict `IToolDefinitionProvider` and normalized manifest serializer used by installation and local verification.

- [ ] **Step 1: Create the exact production definition**

Add `config/tools/cpp2il.win-x64.json` exactly:

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

Do not add a mutable API URL, release lookup, or “latest” field.

- [ ] **Step 2: Write failing production-pin and strict-validation tests**

Tests must load the repository file and assert every independently verified value:

```csharp
[Fact]
public void GetRequired_Cpp2IlWindowsX64_ReturnsApprovedPin()
```

Assert exact version, platform, source URL, release URL, asset name, expected size, SHA-256, executable relative path, MIT license, probe arguments, and `dll_il_recovery` requirement.

Add invalid-definition cases:

```csharp
[Theory]
[InlineData("http://example.test/tool.exe")]
[InlineData("https://user:password@example.test/tool.exe")]
public void Load_WhenSourceUrlIsNotApprovedHttpsShape_Rejects(string sourceUrl)
```

```csharp
[Fact]
public void Load_WhenExpectedSizeExceedsDownloadLimit_Rejects()
```

```csharp
[Fact]
public void Load_WhenExecutablePathEscapesRoot_Rejects()
```

```csharp
[Fact]
public void Load_WhenProbeIdsRepeat_Rejects()
```

```csharp
[Fact]
public void Load_WhenArchiveFormatConflictsWithPackageKind_Rejects()
```

```csharp
[Fact]
public void GetAll_WhenToolPlatformPairRepeats_Rejects()
```

Run and expect failure because the provider does not exist.

- [ ] **Step 3: Implement JSON document DTOs and strict parsing**

`ToolDefinitionDocument` mirrors the committed JSON with nullable document properties. Do not deserialize directly into trusted Core records.

`ToolDefinitionValidator.Validate(document, sourceName)` must enforce:

```text
schemaVersion == 1
toolId matches ^[a-z0-9][a-z0-9.-]*$
displayName/version are nonblank
platform matches ^[a-z0-9][a-z0-9-]*$
source, release, and license URLs are absolute HTTPS URLs with no user info
assetName is a single file name and matches the final source-URL path segment
expectedSize > 0
expectedSize <= maximumDownloadBytes
SHA-256 is exactly 64 hexadecimal characters and normalized lower-case
executableRelativePath is relative, contains no empty, '.', or '..' segment, and has no drive/UNC root
singleFile requires archiveFormat == null and maximumFileCount == 1
archive requires archiveFormat == zip
maximum download/expanded bytes and file count are positive
probe list is nonempty
probe IDs are unique using Ordinal comparison
acceptedExitCodes is nonempty and contains no duplicates
1 <= timeoutSeconds <= 300
arguments and required-output strings contain no null values
```

Any validation failure throws `ToolOperationException` with code `ToolDefinitionInvalid` and a message naming the source definition and invalid field.

- [ ] **Step 4: Implement deterministic repository loading**

`RepositoryToolDefinitionProvider` constructor accepts the tool-definition directory.

```csharp
public sealed class RepositoryToolDefinitionProvider : IToolDefinitionProvider
{
    public RepositoryToolDefinitionProvider(string toolDefinitionDirectory);
    public IReadOnlyList<ResolvedToolDefinition> GetAll();
    public ResolvedToolDefinition GetRequired(string toolId, string platform);
}
```

Behavior:

1. Enumerate only top-level `*.json` files.
2. Order paths case-insensitively, then ordinally.
3. Parse with `JsonSerializerOptions` that reject comments and trailing commas.
4. Validate each document.
5. Compute `ToolDefinitionFingerprint` from the trusted Core record.
6. Reject duplicate `(toolId, platform)` pairs case-insensitively.
7. `GetRequired` uses case-insensitive lookup but returns the committed canonical values.
8. Unknown tool returns `ToolOperationException("UnknownTool", ...)`.
9. Missing definition directory returns `ToolOperationException("ToolDefinitionInvalid", ...)`.

`ToolDefinitionSerializer` writes a normalized, indented, camel-case copy for `tool-manifest.json` and reads local copies through the same validator.

- [ ] **Step 5: Add reusable test definitions without executable artifacts**

`ToolTestFixture` must:

- locate the repository root by walking upward from `AppContext.BaseDirectory` until `S1Atlas.sln` exists;
- build small `ToolDefinitionDocument` instances for `https://example.test/tool.exe`;
- calculate expected size and SHA-256 from caller-provided bytes;
- write fixture definitions to temporary directories;
- never copy bytes into the repository.

For executable probe tests later, obtain `%ComSpec%` at runtime and read its bytes into the test’s temporary directory only.

- [ ] **Step 6: Package configuration with the CLI output**

Modify `src/S1Atlas.Cli/S1Atlas.Cli.csproj`:

```xml
<ItemGroup>
  <Content Include="..\..\config\tools\*.json"
           Link="config\tools\%(Filename)%(Extension)"
           CopyToOutputDirectory="PreserveNewest"
           CopyToPublishDirectory="PreserveNewest" />
</ItemGroup>
```

Add a test that `dotnet build` leaves `config/tools/cpp2il.win-x64.json` beside the CLI output. The test may inspect the known Release output after the build target; do not invoke publish or network access.

- [ ] **Step 7: Verify and commit Task 2**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "ToolDefinition|RepositoryToolDefinitionProvider"
dotnet build S1Atlas.sln --configuration Release
```

Expected: exact pin test passes; invalid documents are rejected; build has zero warnings/errors.

```powershell
git add -- config/tools src/S1Atlas.Extraction/Tools src/S1Atlas.Cli/S1Atlas.Cli.csproj tests/S1Atlas.Extraction.Tests/Tools
git commit -m "feat: pin and validate the managed Cpp2IL definition"
```

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
- Produces durable, atomic verified-tool provenance for the CLI and Phase 3 extraction resolver.

- [ ] **Step 1: Write failing migration-3 tests**

Add:

```csharp
[Fact]
public async Task MigrateAsync_V2Database_AddsToolTablesAndCreatesOneSchema3Backup()
```

```csharp
[Fact]
public async Task MigrateAsync_NewDatabase_AppliesThreeMigrationsWithoutBackup()
```

```csharp
[Fact]
public async Task MigrateAsync_FoundationV1Database_AppliesThroughV3AndPreservesFoundationState()
```

Assertions:

```text
schema_migrations has versions 1, 2, and 3 with current checksums
managed_tool_installations exists with the approved composite primary key
tool_instances exists with its primary key
ix_tool_instances_tool_trust exists
v2 -> v3 creates exactly one atlas-before-schema-3-*.db backup
new empty database creates no backup
Foundation build/snapshot/dependencies/current pointer remain unchanged
```

Run and expect failure because migration 3 does not exist.

- [ ] **Step 2: Add exact migration 3 SQL**

Append to `SqliteMigrations` without modifying migration 1 or migration 2 text:

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

Existing migration checksums must remain unchanged.

- [ ] **Step 3: Make the SQLite repository a partial dual-interface implementation**

Change the declaration only:

```csharp
public sealed partial class SqliteAtlasRepository : IAtlasRepository, IToolRepository
```

Keep existing Foundation methods in `SqliteAtlasRepository.cs`.

Create `SqliteAtlasRepository.Tools.cs` with:

```csharp
public async Task SaveVerifiedManagedToolAsync(
    ManagedToolInstallation installation,
    ToolInstance toolInstance,
    CancellationToken cancellationToken)
```

Hard requirements before opening a transaction:

```text
installation.Status == Verified
toolInstance.Status == Verified
toolInstance.TrustLevel == ManagedPinned
tool IDs/platform/executable SHA-256/definition digest agree
ToolInstanceId.Create(...) equals toolInstance.ToolInstanceId
```

In one SQLite transaction:

1. Upsert `managed_tool_installations` by `(tool_id, version, platform)`.
2. Upsert `tool_instances` by `tool_instance_id`.
3. Preserve the earliest `first_observed_at_utc` on tool-instance conflict.
4. Update observed path, status, version label, and last verification time.
5. Serialize `ProbeResults` as compact camel-case JSON with string enums into `probe_summary`.
6. Commit both rows together; rollback with `CancellationToken.None` on failure.

Reads:

```csharp
public Task<ManagedToolInstallation?> GetManagedToolAsync(...)
public Task<ToolInstance?> GetToolInstanceAsync(...)
```

Use invariant `O` timestamps and strict enum parsing. A corrupt persisted enum or probe-summary document is an integrity error, not silently ignored.

- [ ] **Step 4: Write repository round-trip and atomicity tests**

Add:

```csharp
[Fact]
public async Task SaveVerifiedManagedToolAsync_RoundTripsInstallationAndToolInstance()
```

```csharp
[Fact]
public async Task SaveVerifiedManagedToolAsync_ReverificationPreservesFirstObservedAndUpdatesLastVerified()
```

```csharp
[Fact]
public async Task SaveVerifiedManagedToolAsync_WhenInstallationIsNotVerified_RejectsWithoutRows()
```

```csharp
[Fact]
public async Task SaveVerifiedManagedToolAsync_WhenToolInstanceIdentityDisagrees_RollsBackBothRows()
```

Use a managed installation whose path is under a temporary Atlas root. No executable needs to exist for storage-only tests.

- [ ] **Step 5: Reconcile Phase 1 migration integration tests**

Update existing expectations:

```text
migration ledger count: 3
Foundation-v1 direct upgrade backup pattern: atlas-before-schema-3-*.db
backup still contains original Foundation-v1 schema
new v2 observations and current pointer behavior remain unchanged
```

Add an explicit v2-to-v3 fixture path so the real Phase 2 upgrade shape is tested independently from the Foundation-v1 adoption path.

Do not rename Phase 1 tests in a way that implies shipped Foundation databases contained non-null Steam build IDs.

- [ ] **Step 6: Verify and commit Task 3**

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter FoundationMigrationTests
dotnet build S1Atlas.sln --configuration Release
```

Expected: all selected tests pass; zero warnings/errors.

```powershell
git add -- src/S1Atlas.Storage src/S1Atlas.Core/Storage tests/S1Atlas.Storage.Tests tests/S1Atlas.IntegrationTests/Foundation/FoundationMigrationTests.cs
git commit -m "feat: persist managed tool provenance"
```

---

### Task 4: Add bounded HTTPS download, exact verification, and safe package materialization

**Files:**
- Create: `src/S1Atlas.Extraction/Tools/ToolDownloadClient.cs`
- Create: `src/S1Atlas.Extraction/Tools/ToolPackageVerifier.cs`
- Create: `src/S1Atlas.Extraction/Tools/ToolPathPolicy.cs`
- Create: `src/S1Atlas.Extraction/Tools/SafeToolPackageInstaller.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Tools/ToolDownloadClientTests.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Tools/ToolPackageVerifierTests.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Tools/SafeToolPackageInstallerTests.cs`

**Interfaces:**
- Consumes trusted `ResolvedToolDefinition` records.
- Produces a verified staged package and a contained staged installation tree. It does not execute the tool or promote it to the managed cache.

- [ ] **Step 1: Write failing download-boundary tests**

Add tests using a custom `HttpMessageHandler`:

```csharp
[Fact]
public async Task DownloadAsync_StreamsExactResponseToStaging()
```

```csharp
[Fact]
public async Task DownloadAsync_WhenContentLengthExceedsLimit_RejectsBeforeReadingBody()
```

```csharp
[Fact]
public async Task DownloadAsync_WhenChunkedBodyExceedsLimit_StopsAndDeletesPartialFile()
```

```csharp
[Fact]
public async Task DownloadAsync_WhenStatusIsNotSuccess_ReportsToolDownloadFailed()
```

```csharp
[Fact]
public async Task DownloadAsync_WhenFinalRequestUriIsNotHttps_Rejects()
```

Expected failures: `ToolDownloadClient` does not exist.

- [ ] **Step 2: Implement the download client**

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

Implementation requirements:

1. Require an absolute HTTPS source URI with empty `UserInfo`.
2. Use `HttpCompletionOption.ResponseHeadersRead`.
3. Require a successful status code.
4. Check `Content-Length` before reading when present.
5. Create only the destination’s Atlas-owned staging parent.
6. Stream with a fixed-size buffer.
7. Count every byte and abort once the maximum would be exceeded.
8. Flush and close before returning.
9. On HTTP, IO, cancellation, limit, or disposal failure, delete the partial destination best-effort.
10. After redirects, require `response.RequestMessage.RequestUri` to remain HTTPS with no user info.
11. Map non-cancellation failures to `ToolOperationException("ToolDownloadFailed", ...)` unless a more specific size code applies.

- [ ] **Step 3: Write failing exact package-verification tests**

```csharp
[Fact]
public async Task VerifyAsync_WhenSizeAndShaMatch_ReturnsObservedFacts()
```

```csharp
[Fact]
public async Task VerifyAsync_WhenSizeDiffers_ThrowsToolSizeMismatch()
```

```csharp
[Fact]
public async Task VerifyAsync_WhenShaDiffers_ThrowsToolChecksumMismatch()
```

Implement:

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

Compute size and full SHA-256 from the staged file. Compare size first, then hash using ordinal lower-case comparison.

- [ ] **Step 4: Add path-containment policy tests and implementation**

`ToolPathPolicy` must provide:

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

Tests must reject:

```text
absolute relative paths
UNC paths
drive-qualified paths
'.' and '..' segments
mixed slash/backslash traversal
candidate paths outside root
existing reparse points between root and candidate
invalid tool/version path segments
```

Install root is:

```text
<toolsRoot>/<toolId>/<version>
```

Staging and quarantine names include tool ID, version, timestamp or GUID, and remain single safe path segments.

- [ ] **Step 5: Write failing single-file and ZIP safety tests**

Single-file tests:

```csharp
[Fact]
public async Task MaterializeAsync_SingleFile_CopiesOnlyToDeclaredExecutablePath()
```

```csharp
[Fact]
public async Task MaterializeAsync_SingleFile_WhenDeclaredPathEscapes_Rejects()
```

ZIP tests must cover:

```csharp
MaterializeAsync_Zip_ExtractsContainedRegularFiles
MaterializeAsync_Zip_WhenEntryContainsDotDot_Rejects
MaterializeAsync_Zip_WhenEntryIsAbsolute_Rejects
MaterializeAsync_Zip_WhenEntriesCollideCaseInsensitively_Rejects
MaterializeAsync_Zip_WhenEntryIsUnixSymlink_Rejects
MaterializeAsync_Zip_WhenExpandedBytesExceedLimit_Rejects
MaterializeAsync_Zip_WhenFileCountExceedsLimit_Rejects
MaterializeAsync_Zip_WhenDeclaredExecutableIsMissing_Rejects
```

Construct ZIPs in temporary directories. Set Unix symlink external attributes in the malicious fixture; do not create real links on disk.

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

For `SingleFile`:

- create only the executable parent under staged root;
- copy package bytes asynchronously;
- verify copied byte count equals package size;
- reject a pre-existing destination or reparse point.

For `Archive/Zip`:

- open read-only with `ZipArchive`;
- preflight all entries before writing any file;
- normalize `/` and `\` as separators for security checks;
- reject rooted, empty, `.`, and `..` segments;
- reject duplicate case-insensitive destinations;
- reject DOS reparse attributes and Unix file type `0xA000` symlinks;
- accept only regular files and directories;
- check total entries and expanded bytes with overflow-safe arithmetic;
- extract each file without overwrite;
- verify the declared executable exists as a regular contained file.

On failure, delete the staged install root best-effort. Never delete outside the supplied staged root.

- [ ] **Step 7: Verify and commit Task 4**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "ToolDownloadClient|ToolPackageVerifier|ToolPathPolicy|SafeToolPackageInstaller"
dotnet build S1Atlas.sln --configuration Release
```

```powershell
git add -- src/S1Atlas.Extraction/Tools tests/S1Atlas.Extraction.Tests/Tools
git commit -m "feat: safely acquire and materialize tool packages"
```

---

### Task 5: Add controlled capability probes and installation inspection

**Files:**
- Create: `src/S1Atlas.Extraction/Tools/ToolProbeRunner.cs`
- Create: `src/S1Atlas.Extraction/Tools/ToolInstallationDocument.cs`
- Create: `src/S1Atlas.Extraction/Tools/ToolInstallationDocumentStore.cs`
- Create: `src/S1Atlas.Extraction/Tools/ManagedToolInstallationValidator.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Tools/ToolProbeRunnerTests.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Tools/ManagedToolInstallationValidatorTests.cs`

**Interfaces:**
- Consumes a contained executable and committed probe definitions.
- Produces bounded probe results and one of the exact installation states: `NotInstalled`, `Verified`, `Corrupt`, `Incomplete`, `DefinitionMismatch`, or `ProbeFailed`.

- [ ] **Step 1: Write failing process-probe tests**

Use `%ComSpec%` as a runtime-only test executable. Copy it to a temporary path so no system file is modified.

Add:

```csharp
[Fact]
public async Task RunAsync_WhenExitAndRequiredOutputMatch_ReturnsSucceeded()
```

Use arguments:

```text
/d
/c
echo dll_il_recovery
```

Add:

```csharp
RunAsync_WhenExitCodeIsNotAccepted_ReturnsFailure
RunAsync_WhenRequiredOutputIsMissing_ReturnsFailure
RunAsync_WhenTimeoutExpires_KillsProcessAndReturnsTimedOut
RunAsync_WhenCancellationRequested_KillsProcessAndThrowsCancellation
RunAsync_WhenOutputExceedsLimit_ContinuesDrainingAndMarksTruncated
```

For timeout, use a command that remains alive longer than a 100 ms test timeout. Do not sleep the test thread.

- [ ] **Step 2: Implement bounded no-shell probe execution**

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
UseShellExecute = false
CreateNoWindow = true
RedirectStandardOutput = true
RedirectStandardError = true
WorkingDirectory = verified staged/final install root
NO_COLOR = true
arguments added one-by-one through ArgumentList
```

Read stdout and stderr concurrently at the byte-stream level. Retain at most 1 MiB from each while continuing to drain discarded bytes. Decode retained bytes as UTF-8 with replacement fallback.

Timeout and cancellation:

- link caller cancellation with a probe timeout token;
- on timeout or cancellation, call `Kill(entireProcessTree: true)` when still running;
- await process exit and both drain tasks;
- return `TimedOut = true` for timeout;
- rethrow caller cancellation as `OperationCanceledException`;
- never expose a public stack trace.

A probe succeeds only when the exit code is accepted and every required substring occurs in combined retained stdout/stderr using ordinal comparison.

- [ ] **Step 3: Define normalized local installation documents**

`ToolInstallationDocument` mirrors `ManagedToolInstallation` using JSON-safe strings and probe result documents.

`ToolInstallationDocumentStore`:

```csharp
internal sealed class ToolInstallationDocumentStore
{
    public Task WriteAsync(
        string installRoot,
        ResolvedToolDefinition definition,
        ManagedToolInstallation installation,
        CancellationToken cancellationToken);

    public Task<(ResolvedToolDefinition Definition, ManagedToolInstallation Installation)?>
        TryReadAsync(
            string installRoot,
            CancellationToken cancellationToken);
}
```

Write:

```text
<installRoot>/tool-manifest.json
<installRoot>/installation.json
```

Rules:

- UTF-8 without BOM;
- camel-case, indented JSON;
- string enums;
- write temporary sibling files and rename within staged root;
- normalized tool manifest is generated through `ToolDefinitionSerializer`;
- local manifest is parsed and validated through the same strict path as repository definitions;
- malformed/missing local documents return null to the inspector, not a partially trusted record;
- no public output contains probe stdout/stderr.

- [ ] **Step 4: Write failing installation-state tests**

Create a temporary managed root and add:

```csharp
[Fact]
public async Task InspectAsync_WhenRootDoesNotExist_ReturnsNotInstalled()
```

```csharp
[Fact]
public async Task InspectAsync_WhenDocumentsOrExecutableAreMissing_ReturnsIncomplete()
```

```csharp
[Fact]
public async Task InspectAsync_WhenLocalDefinitionDigestDiffers_ReturnsDefinitionMismatch()
```

```csharp
[Fact]
public async Task InspectAsync_WhenExecutableHashDiffers_ReturnsCorruptWithoutRunningProbes()
```

```csharp
[Fact]
public async Task InspectAsync_WhenProbeFails_ReturnsProbeFailed()
```

```csharp
[Fact]
public async Task InspectAsync_WhenEverythingMatches_ReturnsVerifiedWithFreshVerificationTime()
```

Use an injected probe runner abstraction or delegate in validator tests to count invocations. Hash mismatch must short-circuit before process execution.

- [ ] **Step 5: Implement managed installation inspection**

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

Constructor dependencies:

```text
tools root
ToolInstallationDocumentStore
ToolProbeRunner
IFileHasher
TimeProvider
```

Inspection order:

```text
1. expected install root absent -> NotInstalled
2. root is not a normal contained directory or crosses a reparse point -> Incomplete
3. local manifest/installation document missing or malformed -> Incomplete
4. local effective definition digest or installation definition digest differs -> DefinitionMismatch
5. declared executable missing/not regular/reparse point -> Incomplete
6. observed executable SHA-256 differs -> Corrupt
7. controlled probes run in committed order
8. any probe failure -> ProbeFailed
9. all checks pass -> Verified
```

For `Verified`, return a `ManagedToolInstallation` carrying the original `InstalledAtUtc`, current observed paths/hashes, current probe results, and `LastVerifiedAtUtc = TimeProvider.GetUtcNow()`.

Do not invalidate an otherwise verified installation merely because an old absolute `RootPath` stored in `installation.json` differs after the entire Atlas data root was moved. Current contained root and bytes are authoritative local observations; paths are excluded from tool identity.

- [ ] **Step 6: Verify and commit Task 5**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "ToolProbeRunner|ManagedToolInstallationValidator"
dotnet build S1Atlas.sln --configuration Release
```

```powershell
git add -- src/S1Atlas.Extraction/Tools tests/S1Atlas.Extraction.Tests/Tools
git commit -m "feat: probe and inspect managed tool installations"
```

---

### Task 6: Add staged installation, repair, quarantine, and service orchestration

**Files:**
- Create: `src/S1Atlas.Extraction/Tools/ManagedToolInstaller.cs`
- Create: `src/S1Atlas.Extraction/Tools/ManagedToolService.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Tools/ManagedToolInstallerTests.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Tools/ManagedToolServiceTests.cs`

**Interfaces:**
- Consumes Tasks 1–5 and `IToolRepository`.
- Produces the complete Phase 2 application service used by CLI commands.

- [ ] **Step 1: Write failing installer orchestration tests**

Use copied `%ComSpec%` bytes, a fixture definition with fake HTTPS URL, and controlled probes that echo `dll_il_recovery`.

Add:

```csharp
[Fact]
public async Task InstallAsync_WhenNotInstalled_DownloadsVerifiesProbesAndPromotes()
```

Assert:

```text
one HTTP request
all network/package work occurs under tools/.staging
final root is tools/<toolId>/<version>
final root contains Cpp2IL.exe, tool-manifest.json, installation.json
package/executable hashes match
staging path is removed
status is Verified
```

Add:

```csharp
InstallAsync_WhenAlreadyVerified_IsNoOpWithoutHttp
InstallAsync_WhenExistingInstallationIsInvalidWithoutRepair_RequiresRepairWithoutHttp
InstallAsync_WithRepair_StagesBeforeMovingExistingRootAndQuarantinesOldRoot
InstallAsync_WhenRepairDownloadFails_LeavesExistingRootAtOriginalPath
InstallAsync_WhenPromotionFails_RestoresQuarantinedRootBestEffort
InstallAsync_WhenCanceled_RemovesOnlyOwnedStagingPath
```

- [ ] **Step 2: Implement managed installer flow**

Constructor dependencies:

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

`InstallAsync` sequence:

```text
inspect expected final root
  Verified -> return successful no-op and never call HTTP
  invalid + !repair -> throw ToolRepairRequired and never call HTTP
  NotInstalled or invalid + repair -> continue

create unique owned staging root
  download package
  verify exact size and SHA-256
  materialize staged install tree
  hash observed executable
  run all committed probes
  fail with ToolProbeFailed if any probe fails
  create ManagedToolInstallation(status Verified)
  write normalized local documents
  inspect staged root again; require Verified

promotion
  if no existing root: Directory.Move(staged install, final root)
  if repair: move existing root/file to unique quarantine path,
             then move staged install to final root
  on second move failure: best-effort restore quarantined path

inspect final root again; require Verified
return ToolInstallResult
finally delete package and owned staging root best-effort
```

Specific error codes:

```text
ToolRepairRequired
ToolSizeMismatch
ToolChecksumMismatch
ToolDownloadFailed
ToolProbeFailed
ToolInstallationFailed
```

A failed repair must not erase or overwrite the prior path. Promotion uses same-volume moves under the Atlas tools root.

- [ ] **Step 3: Write failing service and repository-registration tests**

Use fakes for provider, installer, validator, and repository:

```csharp
[Fact]
public async Task GetStatusesAsync_WithoutToolId_ReturnsDefinitionsInDeterministicOrder()
```

```csharp
[Fact]
public async Task GetStatusAsync_WhenVerified_UpsertsManagedInstallationAndToolInstance()
```

```csharp
[Fact]
public async Task InstallAsync_WhenFilesystemSucceeds_RegistersVerifiedProvenance()
```

```csharp
[Fact]
public async Task InstallAsync_WhenRepositorySaveFails_LeavesVerifiedFilesystemForLaterStatusRecovery()
```

```csharp
[Fact]
public async Task InstallAsync_UnknownTool_FailsBeforeHttpOrFilesystemWork()
```

- [ ] **Step 4: Implement the application service**

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

For every verified status/install, create the managed tool instance:

```csharp
var id = ToolInstanceId.Create(
    definition.Definition.ToolId,
    installation.ExecutableSha256,
    definition.Definition.Platform,
    ToolTrustLevel.ManagedPinned);
```

Use executable path:

```text
installation.RootPath + definition.Package.ExecutableRelativePath
```

`FirstObservedAtUtc` is the install time for a newly installed instance. On later status verification, the repository preserves the original first-observed timestamp and updates last verification.

If filesystem promotion succeeds but the DB save fails, return an operational failure. Do not delete the verified filesystem installation. A later `tools status` re-verifies and registers it.

- [ ] **Step 5: Verify and commit Task 6**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "ManagedToolInstaller|ManagedToolService"
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj --filter SqliteAtlasRepositoryToolTests
dotnet build S1Atlas.sln --configuration Release
```

```powershell
git add -- src/S1Atlas.Extraction/Tools tests/S1Atlas.Extraction.Tests/Tools
git commit -m "feat: install and register managed tools atomically"
```

---

### Task 7: Expose tool status/install CLI, document behavior, and prove the full boundary

**Files:**
- Modify: `src/S1Atlas.Cli/Configuration/AtlasPaths.cs`
- Create: `src/S1Atlas.Cli/Configuration/CliConfigurationPaths.cs`
- Create: `src/S1Atlas.Cli/Commands/ToolsCommand.cs`
- Create: `src/S1Atlas.Cli/Commands/ToolsStatusCommand.cs`
- Create: `src/S1Atlas.Cli/Commands/ToolsInstallCommand.cs`
- Create: `src/S1Atlas.Cli/Output/ToolOutputModels.cs`
- Create: `src/S1Atlas.Cli/Properties/AssemblyInfo.cs`
- Modify: `src/S1Atlas.Cli/CliApplication.cs`
- Modify: `src/S1Atlas.Cli/Commands/CommandExecution.cs`
- Modify: `src/S1Atlas.Cli/Program.cs`
- Create: `tests/S1Atlas.IntegrationTests/Tools/ManagedToolCliFixture.cs`
- Create: `tests/S1Atlas.IntegrationTests/Tools/ManagedToolCliTests.cs`
- Modify: `README.md`

**Interfaces:**
- Consumes `ManagedToolService` and the existing `CommandOutput` envelope.
- Produces the complete user-facing Phase 2 commands without changing Foundation command contracts.

- [ ] **Step 1: Add final Atlas and configuration paths**

Extend `AtlasPaths`:

```csharp
public string ToolsDirectory => Path.Combine(RootDirectory, "tools");
public string ToolStagingDirectory => Path.Combine(ToolsDirectory, ".staging");
public string ToolQuarantineDirectory => Path.Combine(ToolsDirectory, "quarantine");
```

Create `CliConfigurationPaths`:

```csharp
internal sealed record CliConfigurationPaths(string RootDirectory)
{
    public string ToolDefinitionsDirectory => Path.Combine(RootDirectory, "tools");
    public static CliConfigurationPaths Resolve();
}
```

Resolution order:

1. `AppContext.BaseDirectory/config` when it contains `tools`.
2. Walk upward from `AppContext.BaseDirectory` until a directory containing `S1Atlas.sln` and `config/tools` is found for development/test runs.
3. Otherwise return the app-base candidate so the provider emits one clear `ToolDefinitionInvalid` error.

No environment variable or CLI option may replace the repository-controlled definitions in Phase 2.

- [ ] **Step 2: Write failing CLI integration tests**

`ManagedToolCliFixture` must:

- create a temporary Atlas root and temporary configuration root;
- copy `%ComSpec%` bytes to memory only;
- write a fixture tool definition whose expected size/SHA match those bytes;
- provide a new fake `HttpClient` per invocation;
- count requests;
- create no files under the Schedule I fixture.

Add human and JSON tests:

```csharp
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

JSON assertions:

```text
schemaVersion == 1
command is "tools status" or "tools install"
success/exitCode match
normal status states, including NotInstalled/Corrupt, are successful query results
install failures have null data and { code, message }
stderr is empty in JSON mode
no stack trace appears
```

Run and expect parse failure because `tools` is not registered.

- [ ] **Step 3: Define tool output DTOs**

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

Do not expose captured probe stdout/stderr.

- [ ] **Step 4: Implement the command tree**

`ToolsCommand.Create(...)` creates the parent:

```text
tools
  status [tool-id] [--json]
  install <tool-id> [--repair] [--json]
```

`tools status`:

- optional tool ID;
- no ID lists all definitions for current platform;
- query states return exit 0 even when NotInstalled, Corrupt, Incomplete, DefinitionMismatch, or ProbeFailed;
- unknown tool/definition/platform/integrity exceptions return exit 1.

Human status format:

```text
Cpp2IL

Pinned version:       2022.1.0-pre-release.21
Platform:             win-x64
Definition digest:    <digest>
Installation status:  <state>
Executable checksum:  <hash or unknown>
Installed at:         <path or not installed>
Last verified:        <timestamp or never>
```

When not installed, append:

```text
Install with:
  s1atlas tools install cpp2il
```

`tools install`:

- required tool ID;
- `--repair` boolean;
- invokes the only network-enabled service path;
- normal verified installation is a successful no-op;
- invalid existing installation without repair returns `ToolRepairRequired` before HTTP;
- successful repair prints quarantine path.

Human success messages:

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

- [ ] **Step 5: Make structured tool exceptions output-mode aware**

Update `CommandExecution.Run` before the generic exception catch:

```csharp
catch (ToolOperationException exception)
{
    return output.Failure(1, exception.Code, exception.Message);
}
```

Cancellation remains code `OperationCanceled`, exit 2. Generic exceptions remain `OperationalFailure`, exit 1.

- [ ] **Step 6: Compose production and test dependencies**

Keep the public constructor:

```csharp
public CliApplication(string dataDirectory, string atlasVersion)
```

Add an internal constructor visible to integration tests:

```csharp
internal CliApplication(
    string dataDirectory,
    string atlasVersion,
    string configurationDirectory,
    Func<HttpClient> toolHttpClientFactory,
    TimeProvider? timeProvider = null)
```

Create `src/S1Atlas.Cli/Properties/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("S1Atlas.IntegrationTests")]
```

Production HTTP client:

```text
base address: none
timeout: infinite; cancellation token controls operation
User-Agent: S1Atlas/<atlasVersion>
automatic redirects permitted, with final HTTPS validation in ToolDownloadClient
```

`InvokeCore` constructs one `SqliteAtlasRepository`, initializes the existing Foundation commands exactly as before, and composes the tool provider/service from `AtlasPaths`, `CliConfigurationPaths`, and a fresh HTTP client factory.

`tools status` must not call the HTTP client. Integration tests use a handler that throws on any unexpected request.

- [ ] **Step 7: Wire Ctrl+C cancellation for downloads and probes**

Update `Program.cs`:

```csharp
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
```

Pass `cancellation.Token` to `Invoke`. Existing command cancellation output and exit code 2 remain unchanged.

- [ ] **Step 8: Update README**

Document:

- Phase 1 metadata/migrations are implemented.
- The managed Cpp2IL tool supply chain is implemented.
- Exact `tools status` and `tools install` commands.
- Only `tools install` performs network access.
- The official pin, size, and SHA-256.
- Default tool cache under `%LOCALAPPDATA%\S1Atlas\tools` and `S1ATLAS_HOME` behavior.
- Schema migration 3 may create one `atlas-before-schema-3-*.db` backup.
- Repair is explicit and invalid roots are quarantined.
- `tools status` is offline and may report invalid states without changing the game.
- Phase 2 still does not run Cpp2IL against Schedule I.
- The next implementation phase is extraction orchestration: profiles, typed arguments, attempts, live/archived inputs, process execution, cancellation, and logs.

Update the command table with:

```text
tools status [cpp2il] [--json]
tools install cpp2il [--repair] [--json]
```

- [ ] **Step 9: Run the complete automated verification**

```powershell
dotnet restore S1Atlas.sln
dotnet build S1Atlas.sln --configuration Release --no-restore
dotnet test S1Atlas.sln --configuration Release --no-build --verbosity normal
```

Expected:

```text
Build succeeded
0 warnings
0 errors
All Core tests pass
All Extraction tests pass
All Storage tests pass
All Integration tests pass
No test downloads an external asset
```

- [ ] **Step 10: Verify repository scope and generated-data safety**

```powershell
git status --short
git diff --check
git ls-files | Select-String -Pattern "Cpp2IL\.exe|atlas\.db|\.db-wal|\.db-shm|installation\.json|tool-manifest\.json"
```

Expected:

- the only matched tool manifest is the committed `config/tools/cpp2il.win-x64.json`, not a generated local `tool-manifest.json`;
- no executable, downloaded package, DB, backup, or generated installation record is tracked;
- no whitespace errors;
- existing CI files are unchanged unless an independently justified Windows test correction is required.

- [ ] **Step 11: Run the real managed-pin smoke gate on the Windows development PC**

Before taking the implementation PR out of draft, pull/check out the feature branch and run:

```cmd
dotnet run --configuration Release --project src\S1Atlas.Cli -- tools status cpp2il
dotnet run --configuration Release --project src\S1Atlas.Cli -- tools install cpp2il
dotnet run --configuration Release --project src\S1Atlas.Cli -- tools status cpp2il
dotnet run --configuration Release --project src\S1Atlas.Cli -- tools install cpp2il
dotnet run --configuration Release --project src\S1Atlas.Cli -- tools status cpp2il --json
```

Expected:

```text
first status: NotInstalled, unless a verified managed pin already exists
install: official asset downloaded once, exact size/hash verified, probes pass
second status: Verified
second install: successful no-op with no new download
JSON: one valid schema-version-1 document
managed executable path: %LOCALAPPDATA%\S1Atlas\tools\cpp2il\2022.1.0-pre-release.21\Cpp2IL.exe
```

Record, but do not commit:

```text
observed package SHA-256
observed executable SHA-256
definition digest
probe success
install root
schema-3 backup path, when created
```

Also verify:

```cmd
git status
```

Expected: working tree clean. No game directory comparison is required because Phase 2 commands never receive or resolve a Schedule I path.

- [ ] **Step 12: Commit Task 7**

```powershell
git add -- src/S1Atlas.Cli tests/S1Atlas.IntegrationTests/Tools README.md
git commit -m "feat: expose the managed Cpp2IL tool supply chain"
```

---

## Phase 2 Review Checklist

Before the implementation PR leaves draft, verify every statement with current tests, CI, repository inspection, or the real local smoke gate:

```text
[ ] The production Cpp2IL pin exactly matches the approved version, asset, size, and SHA-256
[ ] Tool definitions are typed, strict, and repository controlled
[ ] No “latest” lookup or remote mutable manifest exists
[ ] Definition digests change when any effective field changes
[ ] Tool-instance identity depends on stable tool ID, executable bytes, platform, and trust only
[ ] Paths and timestamps do not change tool-instance identity
[ ] Migration 3 preserves migrations 1 and 2 checksums
[ ] Existing v2 databases receive one pre-schema-3 backup
[ ] Verified installation and tool-instance rows commit atomically
[ ] Only HTTPS URLs without credentials are accepted
[ ] Downloads stream with a hard byte limit and remove partial files on failure
[ ] Exact expected package size is verified
[ ] Exact expected package SHA-256 is verified before execution
[ ] Single-file materialization stays inside Atlas staging
[ ] Future ZIP definitions reject traversal, absolute paths, collisions, links, and safety-limit violations
[ ] Probes use no shell and controlled ArgumentList values
[ ] Probe output is bounded while streams continue draining
[ ] Probe timeout/cancellation terminates the process tree
[ ] Managed status states are exactly NotInstalled/Verified/Corrupt/Incomplete/DefinitionMismatch/ProbeFailed
[ ] Hash mismatch prevents probe execution
[ ] Verified normal install is a no-op without HTTP
[ ] Invalid install requires explicit --repair without HTTP
[ ] Repair stages and verifies before moving the existing root
[ ] Failed repair leaves the existing root available
[ ] Successful repair quarantines the old root
[ ] Filesystem success can be re-registered after a DB write failure
[ ] tools status is offline
[ ] tools install is the only Phase 2 network path
[ ] Human and JSON outputs preserve schema version 1 and exit codes 0/1/2
[ ] Normal status queries return exit 0 for invalid/not-installed states
[ ] No stack trace appears in public output
[ ] Ctrl+C cancellation reaches download and probe operations
[ ] Full Windows Release build has zero warnings/errors
[ ] Full automated suite passes without external downloads
[ ] Real official-pin installation and no-op smoke gate pass
[ ] No Cpp2IL executable, generated manifest, DB, backup, or game data enters Git
[ ] No Cpp2IL game extraction behavior entered Phase 2
```

## Phase 2 Completion Boundary

When this plan is complete, S1Atlas can explain the approved Cpp2IL pin, explicitly install it from the official asset, prove its package bytes, materialize it safely under Atlas ownership, verify required capabilities, repair/quarantine invalid installations, persist managed provenance, and report the result to humans or agents.

The following begin only in Phase 3 or later:

```text
repository extraction profiles
validation policies
Cpp2IL typed game arguments
current/historical build input resolution
live input pre/post hashing
archived input snapshots
extraction locks and attempts
Cpp2IL game process execution
bounded extraction logs
failed-output retention
artifact inventories
managed assembly validation
preferred extractions
ILSpy and symbol indexing
```

## Execution Mode

The user selected **inline execution** for implementation in ChatGPT. After this plan passes QA and is merged, execute Tasks 1–7 sequentially in the existing conversation using `superpowers:executing-plans`, TDD, focused commits, CI checkpoints, and a draft implementation PR.