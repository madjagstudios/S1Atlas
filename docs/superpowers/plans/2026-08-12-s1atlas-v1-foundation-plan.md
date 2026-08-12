# S1Atlas V1 Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first independently useful S1Atlas slice: a readable modular .NET solution that discovers the local Schedule I environment, fingerprints it, models immutable build metadata, persists validated snapshots in SQLite, and exposes status/environment/build information through the CLI without modifying the game.

**Architecture:** This plan implements only the Foundation milestone from the approved V1 design. `S1Atlas.Core` owns domain types and interfaces; `S1Atlas.Extraction` performs read-only Windows environment discovery and hashing; `S1Atlas.Storage` persists builds/environment snapshots in SQLite; `S1Atlas.Cli` composes the services. Extraction/decompilation with Cpp2IL/ILSpy, symbol indexing, docs, diffing, MCP, and API deep-indexing are separate follow-on plans.

**Tech Stack:** C# / .NET 8 LTS; Windows 10+; Microsoft.Data.Sqlite; System.CommandLine; xUnit; FluentAssertions.

## Global Constraints

- V1 scanning runs on Windows only.
- The Schedule I installation is read-only input; S1Atlas must never modify game files during discovery or scanning.
- Generated Atlas data lives outside the game installation.
- A failed candidate scan must never replace the last valid build.
- One SQLite database stores metadata for all builds, keyed by build ID.
- Raw/decompiled artifacts will be stored separately by build in later milestones.
- Code must favor readability, small focused classes, and explicit naming so it remains approachable to a developing C# programmer.
- No Cpp2IL, ILSpy, HTML portal, MCP, semantic search, mod-breakage prediction, or API deep-indexing in this plan.

---

## File Structure

Create the following files during this plan:

```text
S1Atlas.sln
Directory.Build.props
src/
  S1Atlas.Core/
    S1Atlas.Core.csproj
    Builds/GameBuild.cs
    Builds/BuildFingerprint.cs
    Environment/DependencyKind.cs
    Environment/DependencyVersion.cs
    Environment/EnvironmentSnapshot.cs
    Discovery/IScheduleOneLocator.cs
    Discovery/ScheduleOneInstallation.cs
    Hashing/IFileHasher.cs
    Storage/IAtlasRepository.cs
  S1Atlas.Extraction/
    S1Atlas.Extraction.csproj
    Discovery/WindowsScheduleOneLocator.cs
    Hashing/Sha256FileHasher.cs
    Discovery/EnvironmentDiscoveryService.cs
  S1Atlas.Storage/
    S1Atlas.Storage.csproj
    Sqlite/SqliteAtlasRepository.cs
    Sqlite/SqliteSchema.cs
  S1Atlas.Cli/
    S1Atlas.Cli.csproj
    Program.cs
    Commands/StatusCommand.cs
    Commands/EnvironmentCommand.cs
    Commands/BuildsCommand.cs
    Configuration/AtlasPaths.cs

tests/
  S1Atlas.Core.Tests/
    S1Atlas.Core.Tests.csproj
    Builds/BuildFingerprintTests.cs
  S1Atlas.Extraction.Tests/
    S1Atlas.Extraction.Tests.csproj
    Discovery/WindowsScheduleOneLocatorTests.cs
    Hashing/Sha256FileHasherTests.cs
    Discovery/EnvironmentDiscoveryServiceTests.cs
  S1Atlas.Storage.Tests/
    S1Atlas.Storage.Tests.csproj
    Sqlite/SqliteAtlasRepositoryTests.cs
  S1Atlas.IntegrationTests/
    S1Atlas.IntegrationTests.csproj
    Foundation/EnvironmentSnapshotRoundTripTests.cs
```

Responsibilities stay narrow: domain records contain data, locator discovers paths, hasher hashes files, discovery service assembles a snapshot, repository persists/query snapshots, and CLI commands only format/query results.

---

### Task 1: Bootstrap the readable .NET solution

**Files:**
- Create: `S1Atlas.sln`
- Create: `Directory.Build.props`
- Create: `src/S1Atlas.Core/S1Atlas.Core.csproj`
- Create: `src/S1Atlas.Extraction/S1Atlas.Extraction.csproj`
- Create: `src/S1Atlas.Storage/S1Atlas.Storage.csproj`
- Create: `src/S1Atlas.Cli/S1Atlas.Cli.csproj`
- Create: `tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj`
- Create: `tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj`
- Create: `tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj`
- Create: `tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj`

**Interfaces:**
- Consumes: none.
- Produces: buildable .NET 8 solution with project references `Extraction -> Core`, `Storage -> Core`, `Cli -> Core + Extraction + Storage`; test projects reference their target projects.

- [ ] **Step 1: Verify SDK availability**

Run:

```powershell
dotnet --info
dotnet --list-sdks
```

Expected: a .NET 8 SDK (`8.0.x`) is installed. If it is absent, stop and install .NET 8 SDK before scaffolding; do not silently target another major version.

- [ ] **Step 2: Create the solution and projects**

Run from repository root:

```powershell
dotnet new sln -n S1Atlas
dotnet new classlib -n S1Atlas.Core -o src/S1Atlas.Core -f net8.0
dotnet new classlib -n S1Atlas.Extraction -o src/S1Atlas.Extraction -f net8.0
dotnet new classlib -n S1Atlas.Storage -o src/S1Atlas.Storage -f net8.0
dotnet new console -n S1Atlas.Cli -o src/S1Atlas.Cli -f net8.0
dotnet new xunit -n S1Atlas.Core.Tests -o tests/S1Atlas.Core.Tests -f net8.0
dotnet new xunit -n S1Atlas.Extraction.Tests -o tests/S1Atlas.Extraction.Tests -f net8.0
dotnet new xunit -n S1Atlas.Storage.Tests -o tests/S1Atlas.Storage.Tests -f net8.0
dotnet new xunit -n S1Atlas.IntegrationTests -o tests/S1Atlas.IntegrationTests -f net8.0
```

Delete generated `Class1.cs` files.

- [ ] **Step 3: Add projects and references**

Run:

```powershell
dotnet sln S1Atlas.sln add src/S1Atlas.Core/S1Atlas.Core.csproj src/S1Atlas.Extraction/S1Atlas.Extraction.csproj src/S1Atlas.Storage/S1Atlas.Storage.csproj src/S1Atlas.Cli/S1Atlas.Cli.csproj tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj
dotnet add src/S1Atlas.Extraction/S1Atlas.Extraction.csproj reference src/S1Atlas.Core/S1Atlas.Core.csproj
dotnet add src/S1Atlas.Storage/S1Atlas.Storage.csproj reference src/S1Atlas.Core/S1Atlas.Core.csproj
dotnet add src/S1Atlas.Cli/S1Atlas.Cli.csproj reference src/S1Atlas.Core/S1Atlas.Core.csproj src/S1Atlas.Extraction/S1Atlas.Extraction.csproj src/S1Atlas.Storage/S1Atlas.Storage.csproj
dotnet add tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj reference src/S1Atlas.Core/S1Atlas.Core.csproj
dotnet add tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj reference src/S1Atlas.Core/S1Atlas.Core.csproj src/S1Atlas.Extraction/S1Atlas.Extraction.csproj
dotnet add tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj reference src/S1Atlas.Core/S1Atlas.Core.csproj src/S1Atlas.Storage/S1Atlas.Storage.csproj
dotnet add tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj reference src/S1Atlas.Core/S1Atlas.Core.csproj src/S1Atlas.Extraction/S1Atlas.Extraction.csproj src/S1Atlas.Storage/S1Atlas.Storage.csproj
```

- [ ] **Step 4: Add explicit common build settings**

Create `Directory.Build.props`:

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

- [ ] **Step 5: Add packages**

Run:

```powershell
dotnet add src/S1Atlas.Storage/S1Atlas.Storage.csproj package Microsoft.Data.Sqlite
dotnet add src/S1Atlas.Cli/S1Atlas.Cli.csproj package System.CommandLine
dotnet add tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj package FluentAssertions
dotnet add tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj package FluentAssertions
dotnet add tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj package FluentAssertions
dotnet add tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj package FluentAssertions
```

Record the resolved package versions in the project files; do not use floating versions.

- [ ] **Step 6: Verify the empty solution**

Run:

```powershell
dotnet restore S1Atlas.sln
dotnet build S1Atlas.sln --no-restore
dotnet test S1Atlas.sln --no-build
```

Expected: build succeeds with zero warnings/errors; generated xUnit placeholder tests pass.

- [ ] **Step 7: Commit**

```powershell
git add -- S1Atlas.sln Directory.Build.props src tests
git commit -m "build: bootstrap S1Atlas solution"
```

---

### Task 2: Define build identity and environment domain models

**Files:**
- Create: `src/S1Atlas.Core/Builds/BuildFingerprint.cs`
- Create: `src/S1Atlas.Core/Builds/GameBuild.cs`
- Create: `src/S1Atlas.Core/Environment/DependencyKind.cs`
- Create: `src/S1Atlas.Core/Environment/DependencyVersion.cs`
- Create: `src/S1Atlas.Core/Environment/EnvironmentSnapshot.cs`
- Create: `src/S1Atlas.Core/Discovery/ScheduleOneInstallation.cs`
- Test: `tests/S1Atlas.Core.Tests/Builds/BuildFingerprintTests.cs`

**Interfaces:**
- Consumes: .NET base class library only.
- Produces: `BuildFingerprint.Create(string gameAssemblySha256, string metadataSha256)`, `GameBuild`, `DependencyVersion`, `EnvironmentSnapshot`, and `ScheduleOneInstallation` records used by later tasks.

- [ ] **Step 1: Write failing fingerprint tests**

Create `BuildFingerprintTests.cs` with tests proving fingerprints are deterministic and input-order-sensitive:

```csharp
using FluentAssertions;
using S1Atlas.Core.Builds;

namespace S1Atlas.Core.Tests.Builds;

public sealed class BuildFingerprintTests
{
    [Fact]
    public void Create_WithSameHashes_ReturnsSameId()
    {
        var first = BuildFingerprint.Create("aaa", "bbb");
        var second = BuildFingerprint.Create("aaa", "bbb");

        first.Should().Be(second);
    }

    [Fact]
    public void Create_WhenMetadataHashChanges_ReturnsDifferentId()
    {
        var first = BuildFingerprint.Create("aaa", "bbb");
        var second = BuildFingerprint.Create("aaa", "ccc");

        first.Should().NotBe(second);
    }
}
```

- [ ] **Step 2: Run the tests and verify failure**

```powershell
dotnet test tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj --filter BuildFingerprintTests
```

Expected: compile failure because `BuildFingerprint` does not exist.

- [ ] **Step 3: Implement the domain types**

Create `BuildFingerprint.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace S1Atlas.Core.Builds;

public static class BuildFingerprint
{
    public static string Create(string gameAssemblySha256, string metadataSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameAssemblySha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataSha256);

        var bytes = Encoding.UTF8.GetBytes($"{gameAssemblySha256}:{metadataSha256}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
```

Create `GameBuild.cs`:

```csharp
namespace S1Atlas.Core.Builds;

public sealed record GameBuild(
    string BuildId,
    string? GameVersion,
    string? SteamBuildId,
    string GameAssemblySha256,
    string MetadataSha256,
    DateTimeOffset ScannedAtUtc,
    bool IsValid);
```

Create `DependencyKind.cs`:

```csharp
namespace S1Atlas.Core.Environment;

public enum DependencyKind
{
    S1Api,
    S1Mapi,
    MelonLoader,
    Sideload
}
```

Create `DependencyVersion.cs`:

```csharp
namespace S1Atlas.Core.Environment;

public sealed record DependencyVersion(
    DependencyKind Kind,
    string? Version,
    string? Path,
    bool IsInstalled);
```

Create `EnvironmentSnapshot.cs`:

```csharp
using S1Atlas.Core.Builds;

namespace S1Atlas.Core.Environment;

public sealed record EnvironmentSnapshot(
    GameBuild Build,
    IReadOnlyList<DependencyVersion> Dependencies,
    string AtlasVersion,
    DateTimeOffset CapturedAtUtc);
```

Create `ScheduleOneInstallation.cs`:

```csharp
namespace S1Atlas.Core.Discovery;

public sealed record ScheduleOneInstallation(
    string RootPath,
    string GameAssemblyPath,
    string GlobalMetadataPath,
    string ModsPath,
    string MelonLoaderPath);
```

- [ ] **Step 4: Run core tests**

```powershell
dotnet test tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add -- src/S1Atlas.Core tests/S1Atlas.Core.Tests
git commit -m "feat: define Atlas build domain model"
```

---

### Task 3: Implement read-only Schedule I installation discovery

**Files:**
- Create: `src/S1Atlas.Core/Discovery/IScheduleOneLocator.cs`
- Create: `src/S1Atlas.Extraction/Discovery/WindowsScheduleOneLocator.cs`
- Test: `tests/S1Atlas.Extraction.Tests/Discovery/WindowsScheduleOneLocatorTests.cs`

**Interfaces:**
- Consumes: `ScheduleOneInstallation`.
- Produces: `IScheduleOneLocator.LocateAsync(string? overridePath, CancellationToken cancellationToken)` returning a validated installation or `null`.

- [ ] **Step 1: Write failing locator tests using a temporary fake install**

The tests create a temp directory with only the required path layout and never require the real game:

```csharp
[Fact]
public async Task LocateAsync_WithValidOverride_ReturnsInstallation()
{
    using var fixture = FakeScheduleOneInstall.Create();
    var locator = new WindowsScheduleOneLocator();

    var result = await locator.LocateAsync(fixture.RootPath, CancellationToken.None);

    result.Should().NotBeNull();
    result!.GameAssemblyPath.Should().Be(fixture.GameAssemblyPath);
    result.GlobalMetadataPath.Should().Be(fixture.MetadataPath);
}

[Fact]
public async Task LocateAsync_WhenRequiredMetadataIsMissing_ReturnsNull()
{
    using var fixture = FakeScheduleOneInstall.Create(includeMetadata: false);
    var locator = new WindowsScheduleOneLocator();

    var result = await locator.LocateAsync(fixture.RootPath, CancellationToken.None);

    result.Should().BeNull();
}
```

Implement `FakeScheduleOneInstall` as a private test helper in the same test file. Its layout must include `GameAssembly.dll`, `Schedule I_Data/il2cpp_data/Metadata/global-metadata.dat`, `Mods`, and `MelonLoader` when requested.

- [ ] **Step 2: Verify failure**

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter WindowsScheduleOneLocatorTests
```

Expected: compile failure because locator types do not exist.

- [ ] **Step 3: Define the locator interface**

```csharp
namespace S1Atlas.Core.Discovery;

public interface IScheduleOneLocator
{
    Task<Schedule