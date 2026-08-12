# S1Atlas V1 Foundation Implementation Plan

> **Status: IMPLEMENTED AND VERIFIED.** This is the complete as-built record for the Foundation milestone. Do not execute it against a current S1Atlas checkout as though the work were still pending. Use it to understand, review, maintain, or reconstruct the Foundation from the pre-implementation baseline.
>
> **For reconstruction work:** use `superpowers:subagent-driven-development` or `superpowers:executing-plans` and proceed task-by-task. Completed checkboxes record what shipped; they are not outstanding work.

**Goal:** Build the first independently useful S1Atlas slice: a readable modular .NET solution that discovers the local Schedule I environment, fingerprints the game build, records installed modding dependencies, persists immutable build and environment snapshots in SQLite, and exposes that information through a safe CLI without modifying the game installation.

**Architecture:** `S1Atlas.Core` owns domain records and interfaces. `S1Atlas.Extraction` performs read-only Windows discovery, hashing, and defensive dependency inspection. `S1Atlas.Storage` owns transactional SQLite persistence. `S1Atlas.Cli` composes those services and presents `scan`, `status`, `env`, and `builds`. Cpp2IL/ILSpy extraction, symbol indexing, build diffing, the generated portal, deep API indexing, MCP, and the agent skill remain separate follow-on milestones.

**Tech Stack:** C# on .NET 8 LTS; Windows 10+; Microsoft.Data.Sqlite 8.0.29; System.CommandLine 2.0.10; xUnit v3 3.2.2; xunit.runner.visualstudio 3.1.5; Microsoft.NET.Test.Sdk 18.8.1; GitHub Actions on `windows-latest`.

**Assertion style:** plain xUnit `Assert.*`. FluentAssertions is not used or referenced.

## Global Constraints

- Foundation scanning runs on Windows only.
- The Schedule I installation is read-only input.
- Discovery and scanning must never create, edit, move, or delete files inside the game installation.
- Generated Atlas state lives outside the game installation.
- The default data root is `%LOCALAPPDATA%\S1Atlas`.
- `S1ATLAS_HOME` overrides the default data root.
- One SQLite database stores all Foundation build and environment metadata.
- Game builds are immutable and keyed by a deterministic build fingerprint.
- Environment snapshots are separate from game builds so dependency-only changes do not invent a new game build.
- Persistence and current-snapshot promotion occur in one transaction.
- A failed scan or failed dependency insert cannot replace the last valid current snapshot.
- Filesystem discovery must tolerate inaccessible folders, disappearing files, malformed DLLs, and reparse-point loops.
- Multiple candidate DLLs must be resolved deterministically.
- CLI operational failures must return a nonzero exit code and concise stderr text, not a raw stack trace.
- Code favors small, readable components and explicit names so it remains approachable to a developing C# programmer.
- No Cpp2IL, ILSpy, HTML portal, MCP, semantic search, patch generation, mod-breakage prediction, or deep API indexing is implemented by this plan.

---

## As-Built File Structure

```text
.github/
  workflows/
    ci.yml
Directory.Build.props
S1Atlas.sln
README.md

src/
  S1Atlas.Core/
    S1Atlas.Core.csproj
    Builds/
      BuildFingerprint.cs
      GameBuild.cs
    Discovery/
      IDependencyDetector.cs
      IScheduleOneLocator.cs
      ScheduleOneInstallation.cs
    Environment/
      DependencyKind.cs
      DependencyVersion.cs
      EnvironmentSnapshot.cs
    Hashing/
      IFileHasher.cs
    Storage/
      IAtlasRepository.cs

  S1Atlas.Extraction/
    S1Atlas.Extraction.csproj
    Discovery/
      DependencyVersionReader.cs
      EnvironmentDiscoveryService.cs
      IDependencyFileEnumerator.cs
      IDependencyVersionReader.cs
      InstalledDependencyDetector.cs
      SafeDependencyFileEnumerator.cs
      WindowsScheduleOneLocator.cs
    Hashing/
      Sha256FileHasher.cs
    Properties/
      AssemblyInfo.cs

  S1Atlas.Storage/
    S1Atlas.Storage.csproj
    Sqlite/
      EnvironmentSnapshotId.cs
      SqliteAtlasRepository.cs
      SqliteSchema.cs

  S1Atlas.Cli/
    S1Atlas.Cli.csproj
    Program.cs
    CliApplication.cs
    Configuration/
      AtlasPaths.cs
    Commands/
      BuildsCommand.cs
      CommandExecution.cs
      DependencyDisplay.cs
      EnvironmentCommand.cs
      ScanCommand.cs
      StatusCommand.cs

tests/
  S1Atlas.Core.Tests/
    S1Atlas.Core.Tests.csproj
    BootstrapTests.cs
    Builds/
      BuildFingerprintTests.cs

  S1Atlas.Extraction.Tests/
    S1Atlas.Extraction.Tests.csproj
    BootstrapTests.cs
    Discovery/
      DependencyVersionReaderTests.cs
      EnvironmentDiscoveryServiceTests.cs
      InstalledDependencyDetectorTests.cs
      SafeDependencyFileEnumeratorTests.cs
      WindowsScheduleOneLocatorTests.cs
    Hashing/
      Sha256FileHasherTests.cs

  S1Atlas.Storage.Tests/
    S1Atlas.Storage.Tests.csproj
    BootstrapTests.cs
    Sqlite/
      SqliteAtlasRepositoryTests.cs

  S1Atlas.IntegrationTests/
    S1Atlas.IntegrationTests.csproj
    BootstrapTests.cs
    Foundation/
      FoundationCliTests.cs
      FoundationSafetyTests.cs
```

Each file has one primary responsibility: Core describes Atlas concepts; Extraction reads the local environment; Storage persists validated facts; CLI formats and invokes operations; tests verify each boundary and the end-to-end safety story.

---

### Task 1: Bootstrap the modular .NET 8 solution and Windows CI

**Files:**
- Create: `S1Atlas.sln`
- Create: `Directory.Build.props`
- Create: `.github/workflows/ci.yml`
- Create: all four `src/*/*.csproj` files
- Create: all four `tests/*/*.csproj` files
- Create: bootstrap tests in each test project

**Interfaces:**
- Produces project references `Extraction -> Core`, `Storage -> Core`, and `Cli -> Core + Extraction + Storage`.
- Produces test-project references to their corresponding production projects; IntegrationTests references Core, Extraction, Storage, and CLI.

- [x] **Step 1: Require .NET 8**

Verify the SDK before building:

```powershell
dotnet --info
dotnet --list-sdks
```

The solution targets `net8.0`; it does not silently retarget another major version.

- [x] **Step 2: Create the solution and projects**

Create four production projects and four test projects:

```powershell
dotnet new sln -n S1Atlas
dotnet new classlib -n S1Atlas.Core -o src/S1Atlas.Core -f net8.0
dotnet new classlib -n S1Atlas.Extraction -o src/S1Atlas.Extraction -f net8.0
dotnet new classlib -n S1Atlas.Storage -o src/S1Atlas.Storage -f net8.0
dotnet new console -n S1Atlas.Cli -o src/S1Atlas.Cli -f net8.0
```

The test projects are configured directly for xUnit v3 rather than relying on the older xUnit v2 template defaults.

- [x] **Step 3: Configure common compiler behavior**

`Directory.Build.props` enables nullable reference types, implicit usings, latest language features, and warnings-as-errors:

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

- [x] **Step 4: Pin package versions**

Production packages:

```text
Microsoft.Data.Sqlite 8.0.29
System.CommandLine     2.0.10
```

Test packages:

```text
Microsoft.NET.Test.Sdk       18.8.1
xunit.v3                     3.2.2
xunit.runner.visualstudio    3.1.5
```

No floating versions and no FluentAssertions dependency are present.

- [x] **Step 5: Add Windows CI**

`.github/workflows/ci.yml` checks out the repository, installs .NET 8, restores, builds Release with warnings treated as errors, and runs the full solution test suite on `windows-latest`.

- [x] **Step 6: Verify the scaffold**

```powershell
dotnet restore S1Atlas.sln
dotnet build S1Atlas.sln --configuration Release --no-restore
dotnet test S1Atlas.sln --configuration Release --no-build
```

Expected: zero warnings, zero errors, and all bootstrap tests pass.

---

### Task 2: Define build, dependency, installation, and repository contracts

**Files:**
- Create: `src/S1Atlas.Core/Builds/BuildFingerprint.cs`
- Create: `src/S1Atlas.Core/Builds/GameBuild.cs`
- Create: `src/S1Atlas.Core/Discovery/IDependencyDetector.cs`
- Create: `src/S1Atlas.Core/Discovery/IScheduleOneLocator.cs`
- Create: `src/S1Atlas.Core/Discovery/ScheduleOneInstallation.cs`
- Create: `src/S1Atlas.Core/Environment/DependencyKind.cs`
- Create: `src/S1Atlas.Core/Environment/DependencyVersion.cs`
- Create: `src/S1Atlas.Core/Environment/EnvironmentSnapshot.cs`
- Create: `src/S1Atlas.Core/Hashing/IFileHasher.cs`
- Create: `src/S1Atlas.Core/Storage/IAtlasRepository.cs`
- Test: `tests/S1Atlas.Core.Tests/Builds/BuildFingerprintTests.cs`

**Interfaces produced:**

```csharp
public interface IScheduleOneLocator
{
    Task<ScheduleOneInstallation?> LocateAsync(
        string? overridePath,
        CancellationToken cancellationToken);
}

public interface IDependencyDetector
{
    IReadOnlyList<DependencyVersion> Detect(
        ScheduleOneInstallation installation);
}

public interface IFileHasher
{
    Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken);
}

public interface IAtlasRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task SaveSnapshotAsync(
        EnvironmentSnapshot snapshot,
        CancellationToken cancellationToken);
    Task<EnvironmentSnapshot?> GetCurrentSnapshotAsync(
        CancellationToken cancellationToken);
    Task<IReadOnlyList<GameBuild>> ListBuildsAsync(
        CancellationToken cancellationToken);
}
```

- [x] **Step 1: Define immutable records**

`GameBuild` records build ID, optional game and Steam versions, both input hashes, scan time, and validation state. `EnvironmentSnapshot` combines a `GameBuild`, dependency entries, Atlas version, and capture time. `ScheduleOneInstallation` holds validated canonical paths without mutating them.

- [x] **Step 2: Define tracked dependency kinds**

```text
S1Api
S1Mapi
MelonLoader
Sideload
```

Each `DependencyVersion` records kind, optional version, optional path, and installed state.

- [x] **Step 3: Implement deterministic game-build fingerprints**

`BuildFingerprint.Create(gameAssemblySha256, metadataSha256)` validates both values, joins them with a colon, hashes the UTF-8 bytes with SHA-256, and returns lower-case hexadecimal.

- [x] **Step 4: Verify fingerprint behavior**

Tests use plain xUnit assertions and prove:

- identical inputs produce the same ID;
- changing the `GameAssembly.dll` hash changes the ID;
- changing the metadata hash changes the ID.

---

### Task 3: Locate Schedule I and hash the IL2CPP inputs without modifying them

**Files:**
- Create: `src/S1Atlas.Extraction/Discovery/WindowsScheduleOneLocator.cs`
- Create: `src/S1Atlas.Extraction/Hashing/Sha256FileHasher.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Discovery/WindowsScheduleOneLocatorTests.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Hashing/Sha256FileHasherTests.cs`

**Interfaces consumed:** `IScheduleOneLocator`, `ScheduleOneInstallation`, and `IFileHasher`.

- [x] **Step 1: Implement override-path discovery**

When `--game-path` is supplied, only that path is considered. A candidate is valid only when both files exist:

```text
<GameRoot>\GameAssembly.dll
<GameRoot>\Schedule I_Data\il2cpp_data\Metadata\global-metadata.dat
```

The returned installation also records `<GameRoot>\Mods` and `<GameRoot>\MelonLoader` paths without creating them.

- [x] **Step 2: Implement default Windows candidates**

Without an override, the locator checks the conventional Steam locations under `ProgramFilesX86` and `ProgramFiles`.

- [x] **Step 3: Respect cancellation**

`LocateAsync` checks the supplied token before and during candidate evaluation.

- [x] **Step 4: Implement streaming SHA-256 hashing**

`Sha256FileHasher.ComputeSha256Async` opens the input file read-only, computes SHA-256 asynchronously, and returns lower-case hexadecimal. It does not copy or modify the source file.

- [x] **Step 5: Verify locator and hasher behavior**

Tests use temporary fake game layouts and prove valid overrides resolve, missing required files return `null`, the known empty-file SHA-256 is returned, and missing files throw rather than producing invented hashes.

---

### Task 4: Detect installed dependencies defensively and deterministically

**Files:**
- Create: `src/S1Atlas.Extraction/Discovery/IDependencyFileEnumerator.cs`
- Create: `src/S1Atlas.Extraction/Discovery/IDependencyVersionReader.cs`
- Create: `src/S1Atlas.Extraction/Discovery/SafeDependencyFileEnumerator.cs`
- Create: `src/S1Atlas.Extraction/Discovery/DependencyVersionReader.cs`
- Create: `src/S1Atlas.Extraction/Discovery/InstalledDependencyDetector.cs`
- Create: `src/S1Atlas.Extraction/Properties/AssemblyInfo.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Discovery/SafeDependencyFileEnumeratorTests.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Discovery/DependencyVersionReaderTests.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Discovery/InstalledDependencyDetectorTests.cs`

**Search-root priority:**

```text
S1API:       UserLibs -> Mods -> Plugins
S1MAPI:      UserLibs -> Mods -> Plugins
MelonLoader: MelonLoader
Sideload:    Mods -> Plugins -> UserLibs
```

- [x] **Step 1: Replace unsafe recursive enumeration**

`SafeDependencyFileEnumerator` performs explicit depth-first traversal with top-directory-only filesystem calls. It skips subdirectories that are reparse points and continues when an individual directory is unreadable or disappears.

Expected recoverable failures include `IOException`, `UnauthorizedAccessException`, and `SecurityException`.

- [x] **Step 2: Produce deterministic candidate ordering**

Discovered full paths are deduplicated case-insensitively and sorted first with `StringComparer.OrdinalIgnoreCase`, then with `StringComparer.Ordinal`. The detector applies the same tie-break before selecting the first filename match.

- [x] **Step 3: Read DLL versions defensively**

`DependencyVersionReader` first probes `FileVersionInfo`, then managed assembly metadata. Version lookup returns `null` rather than failing the scan for expected races or access problems, including malformed images, missing files, file-load failures, Win32 failures, IO failures, unauthorized access, and security exceptions.

- [x] **Step 4: Return explicit missing entries**

The detector always returns one entry for each tracked dependency kind. Missing dependencies have `IsInstalled == false` with null path and version.

- [x] **Step 5: Verify real-world filesystem behavior**

Tests prove:

- all four known dependencies can be found across loader folders;
- missing dependencies remain explicit;
- multiple matches select the same deterministic path;
- an unreadable search root does not prevent a later root from being searched;
- inaccessible child directories are skipped;
- reparse-point children are not traversed;
- disappearing or protected DLLs remain installed with an unknown version instead of crashing discovery.

---

### Task 5: Assemble a validated environment snapshot

**Files:**
- Create: `src/S1Atlas.Extraction/Discovery/EnvironmentDiscoveryService.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Discovery/EnvironmentDiscoveryServiceTests.cs`

**Interface produced:**

```csharp
public Task<EnvironmentSnapshot?> DiscoverAsync(
    string? overridePath,
    string atlasVersion,
    CancellationToken cancellationToken)
```

- [x] **Step 1: Compose locator, hasher, and dependency detector**

The service accepts `IScheduleOneLocator`, `IFileHasher`, `IDependencyDetector`, and an optional `TimeProvider` for deterministic testing.

- [x] **Step 2: Create the game build**

For a valid installation, hash `GameAssembly.dll` and `global-metadata.dat`, calculate the build fingerprint, capture UTC time, and create a valid `GameBuild`.

- [x] **Step 3: Capture available version metadata**

The Foundation attempts to read the file version from `Schedule I.exe`. Failure or absence yields `GameVersion == null`. Steam build-ID discovery is intentionally not implemented yet, so `SteamBuildId` remains null.

- [x] **Step 4: Attach dependency facts**

The service invokes the detector and returns one `EnvironmentSnapshot` containing the build, all dependency entries, Atlas version, and capture timestamp.

- [x] **Step 5: Verify discovery**

Tests prove an unresolved installation returns `null`, while a valid fake installation produces expected hashes, a deterministic build ID, dependency entries, Atlas version, and controlled timestamps.

---

### Task 6: Persist immutable builds and atomic environment snapshots in SQLite

**Files:**
- Create: `src/S1Atlas.Storage/Sqlite/SqliteSchema.cs`
- Create: `src/S1Atlas.Storage/Sqlite/EnvironmentSnapshotId.cs`
- Create: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.cs`
- Test: `tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryTests.cs`

**Schema:**

```text
builds
  PK: build_id

environment_snapshots
  PK: snapshot_id
  FK: build_id -> builds.build_id

dependencies
  PK: (snapshot_id, ordinal)
  FK: snapshot_id -> environment_snapshots.snapshot_id

atlas_state
  singleton row containing current_snapshot_id
```

- [x] **Step 1: Separate build identity from environment identity**

`builds` stores immutable game facts. `environment_snapshots` stores Atlas version and capture time for a particular build. This permits S1API/S1MAPI/MelonLoader/Sideload changes to create a new environment snapshot without duplicating the game build.

- [x] **Step 2: Calculate stable snapshot IDs**

`EnvironmentSnapshotId.Create` hashes the game-build ID, Atlas version, and every dependency after deterministic ordering. Dependency paths are normalized to canonical upper-case full paths for Windows identity.

- [x] **Step 3: Support multiple entries of one dependency kind**

Dependencies are persisted in stable order using an integer ordinal. The primary key is `(snapshot_id, ordinal)`, while `(snapshot_id, kind)` is indexed for lookup. The schema does not reject legitimate same-kind entries.

- [x] **Step 4: Initialize safely**

Repository initialization creates the database directory outside the game, opens SQLite with foreign keys enabled, creates tables and indexes idempotently, and inserts the singleton Atlas-state row when absent.

- [x] **Step 5: Save and promote atomically**

`SaveSnapshotAsync` rejects invalid builds. In one transaction it:

1. inserts the immutable build if new;
2. inserts the environment snapshot if new;
3. inserts deterministically ordered dependencies when the snapshot is new;
4. updates the singleton current snapshot;
5. commits.

A SQLite failure rolls back the transaction and throws an `InvalidOperationException` with context.

- [x] **Step 6: Query current state and build history**

`GetCurrentSnapshotAsync` reconstructs the current build, environment metadata, and ordered dependencies. `ListBuildsAsync` returns immutable builds newest-first.

- [x] **Step 7: Verify persistence invariants**

Storage tests prove:

- a valid snapshot round-trips and becomes current;
- a dependency-only change promotes a new environment while retaining one build row;
- a dependency-insert failure rolls back and preserves the prior current snapshot;
- an invalid build cannot be promoted;
- builds list newest-first;
- multiple dependencies of the same kind all round-trip.

---

### Task 7: Provide the Foundation CLI with clean operational errors

**Files:**
- Create: `src/S1Atlas.Cli/Configuration/AtlasPaths.cs`
- Create: `src/S1Atlas.Cli/Commands/CommandExecution.cs`
- Create: `src/S1Atlas.Cli/Commands/DependencyDisplay.cs`
- Create: `src/S1Atlas.Cli/Commands/ScanCommand.cs`
- Create: `src/S1Atlas.Cli/Commands/StatusCommand.cs`
- Create: `src/S1Atlas.Cli/Commands/EnvironmentCommand.cs`
- Create: `src/S1Atlas.Cli/Commands/BuildsCommand.cs`
- Create: `src/S1Atlas.Cli/CliApplication.cs`
- Create: `src/S1Atlas.Cli/Program.cs`
- Test: `tests/S1Atlas.IntegrationTests/Foundation/FoundationCliTests.cs`

- [x] **Step 1: Centralize Atlas paths**

`AtlasPaths.FromEnvironment()` uses `S1ATLAS_HOME` when set; otherwise it uses `%LOCALAPPDATA%\S1Atlas`. `AtlasPaths.DatabasePath` is the single source for `<RootDirectory>\atlas.db` in runtime and integration-test composition.

- [x] **Step 2: Compose production services**

`CliApplication` constructs `SqliteAtlasRepository`, `WindowsScheduleOneLocator`, `Sha256FileHasher`, `InstalledDependencyDetector`, and `EnvironmentDiscoveryService`.

- [x] **Step 3: Implement commands**

```text
s1atlas scan [--game-path <path>]
s1atlas status
s1atlas env
s1atlas builds
```

`scan` discovers and persists the current environment. `status` reports the current build and dependency count. `env` reports all tracked dependency states. `builds` lists immutable builds newest-first.

- [x] **Step 4: Keep expected user errors concise**

A missing or invalid game override returns exit code `1` with a direct message. An empty Atlas is reported without a stack trace. `env` returns a failure when no current snapshot exists.

- [x] **Step 5: Guard command actions and the application boundary**

`CommandExecution.Run` protects each System.CommandLine action because command invocation may intercept action exceptions before an outer catch observes them. `CliApplication.Invoke` also protects the outer composition boundary.

Exit codes:

```text
0  success
1  operational failure
2  cancellation
```

Operational errors write `S1Atlas failed: <message>` to stderr. Cancellation writes `S1Atlas operation was canceled.` No raw stack trace is emitted by normal CLI execution.

- [x] **Step 6: Verify command behavior**

Integration tests prove:

- `status` reports an empty Atlas cleanly;
- a valid scan persists and reports the current build;
- an invalid override returns failure without creating current state;
- `env` reports every tracked dependency;
- `builds` lists the indexed build ID;
- `status` reports the current build after scanning;
- an unusable Atlas data path produces a concise failure and nonzero exit code.

---

### Task 8: Verify read-only safety, recovery behavior, documentation, and CI

**Files:**
- Create: `tests/S1Atlas.IntegrationTests/Foundation/FoundationSafetyTests.cs`
- Modify: `README.md`
- Modify: `.gitignore`
- Verify: `.github/workflows/ci.yml`

- [x] **Step 1: Prove the game installation remains unchanged**

The safety test captures every fake-game directory and file byte sequence before scanning, runs `scan`, captures the tree again, and asserts the directory set, file set, and all bytes are identical.

- [x] **Step 2: Prove failed discovery preserves current state**

After a successful baseline scan, a later scan against a missing installation must fail while leaving the original current build and environment snapshot intact.

- [x] **Step 3: Prove repeated scans preserve immutable build identity**

Scanning the same game inputs twice may create or select environment snapshots, but the immutable build list still contains exactly one build record.

- [x] **Step 4: Exclude generated data**

`.gitignore` excludes build output, IDE state, Atlas data, SQLite files, extraction artifacts, staging data, logs, and OS metadata. Generated/decompiled game material is never intended for source control.

- [x] **Step 5: Document shipped behavior honestly**

The README describes the Foundation milestone as implemented, lists only commands that exist, documents `%LOCALAPPDATA%\S1Atlas` and `S1ATLAS_HOME`, states the read-only boundary, links the approved design and this as-built plan, and identifies extraction/decompilation as the next milestone.

- [x] **Step 6: Run final verification**

```powershell
dotnet restore S1Atlas.sln
dotnet build S1Atlas.sln --configuration Release --no-restore
dotnet test S1Atlas.sln --configuration Release --no-build --verbosity normal
```

Final verified result on Windows CI:

```text
Build:        0 warnings, 0 errors
Core tests:          4 passed
Extraction tests:   16 passed
Storage tests:       7 passed
Integration tests:  11 passed
Total:              38 passed, 0 failed
```

---

## Implementation Decisions Reconciled from the Original Draft

The original planning draft was superseded during implementation in several places. This completed record intentionally reflects the shipped result:

1. **Testing framework:** xUnit v3 is used, including `TestContext.Current.CancellationToken`; the older xUnit v2 template assumptions were dropped.
2. **Assertions:** plain `Assert.*` is used; FluentAssertions is not a dependency.
3. **Integration-test layout:** the shipped files are `FoundationCliTests.cs` and `FoundationSafetyTests.cs`, not `EnvironmentSnapshotRoundTripTests.cs`.
4. **CLI file map:** `ScanCommand.cs`, `CommandExecution.cs`, `DependencyDisplay.cs`, and `CliApplication.cs` are part of the shipped Foundation.
5. **Persistence model:** immutable game builds and environment snapshots are separate records.
6. **Dependency multiplicity:** same-kind dependencies are supported through stable ordinals.
7. **Filesystem hardening:** discovery skips inaccessible folders and reparse points, tolerates file races and protected DLLs, and selects duplicate candidates deterministically.
8. **CLI failure handling:** command actions and the application composition boundary convert operational exceptions into concise errors and exit codes.

These are not pending deviations; they are the final Foundation design choices embodied in the code and tests.

## Foundation Definition of Done

- [x] A Windows-local Schedule I installation can be discovered by override or conventional Steam path.
- [x] Required IL2CPP inputs are validated and hashed read-only.
- [x] The game build receives a deterministic immutable ID.
- [x] S1API, S1MAPI, MelonLoader, and Sideload installed state is recorded defensively.
- [x] Dependency-only changes do not duplicate game builds.
- [x] Environment snapshots and current-state promotion are transactional.
- [x] The prior valid snapshot survives failed discovery and failed persistence.
- [x] The CLI exposes `scan`, `status`, `env`, and `builds`.
- [x] CLI operational failures are concise and return nonzero exit codes.
- [x] The game installation remains byte-for-byte unchanged during scanning.
- [x] Windows CI builds with zero warnings/errors and passes all 38 tests.

## Follow-On Plans Required

This Foundation plan is complete. Separate implementation plans are required for:

1. Cpp2IL/LibCpp2IL orchestration and immutable per-build extraction artifacts.
2. ILSpy decompilation and normalized symbol/source indexing.
3. inheritance, type-reference, caller, and callee relationships.
4. build and symbol diffing.
5. static HTML human portal with FACT/DERIVED/INTERPRETATION provenance.
6. deep S1API and S1MAPI indexing.
7. read-only MCP tools over the shared query layer.
8. the Schedule I agent skill and final V1 hardening.
