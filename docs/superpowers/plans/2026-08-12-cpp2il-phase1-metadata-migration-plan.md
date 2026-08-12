# S1Atlas Cpp2IL Phase 1 Metadata and Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correct S1Atlas’s game-version model, detect local Steam metadata offline, migrate every Foundation database safely to a versioned v2 schema, preserve the existing build and dependency history, and add stable JSON output to the Foundation query commands.

**Architecture:** Phase 1 keeps the existing modular boundaries. `S1Atlas.Core` separates immutable game-content identity from mutable installation observations; `S1Atlas.Extraction` reads the Windows executable version and local Steam manifest data without network access; `S1Atlas.Storage` recognizes the exact Foundation schema, backs it up, applies checksummed migrations, and persists v2 snapshots; `S1Atlas.Cli` presents accurate human labels and a stable JSON envelope. This plan does not install or run Cpp2IL.

**Tech Stack:** C# / .NET 8 LTS; Windows 10+; Microsoft.Data.Sqlite 8.0.29; System.CommandLine 2.0.10; System.Text.Json from .NET 8; xUnit v3 3.2.2; plain xUnit `Assert.*`.

## Global Constraints

- The Schedule I installation remains read-only input.
- Phase 1 performs no network requests.
- Phase 1 does not install, resolve, or execute Cpp2IL.
- The existing content-derived build ID algorithm and existing build IDs must not change.
- Existing `atlas.db` data must survive migration: build hashes, dependencies, environment snapshot IDs, and `atlas_state.current_snapshot_id` remain valid.
- An existing recognized Foundation database receives a SQLite backup before any schema mutation.
- An unknown nonempty database schema is rejected without creating a migration table or changing user tables.
- Migration SQL and checksums are committed in source control and verified on every initialization.
- New environment snapshots use identity version 2; migrated Foundation snapshots retain their original IDs and use identity version 1.
- “Game version” is removed from current human and JSON output. The Windows file version is labeled `Executable version` / `executableVersion`.
- Steam app/build IDs are descriptive environment observations and do not participate in the game build ID.
- JSON-mode stdout contains exactly one JSON document for `status`, `env`, and `builds`.
- JSON schema version 1 fixes the top-level envelope, but command-specific `error` objects may add fields beyond `code` and `message`; consumers must ignore unknown error properties instead of assuming one universal closed error shape.
- Existing exit codes remain: `0` success, `1` operational failure, `2` cancellation.
- Normal human or JSON command output must not contain raw stack traces.
- Tests use xUnit v3 and `TestContext.Current.CancellationToken`; do not add FluentAssertions.
- Build output must remain at zero warnings because `TreatWarningsAsErrors` is enabled.

---

## File Structure

Create or modify the following files during this phase:

```text
src/
  S1Atlas.Core/
    Builds/
      GameBuild.cs                                  modify: content-only build model
    Discovery/
      IInstallationMetadataReader.cs                create
      ScheduleOneInstallation.cs                    modify: add executable path
    Environment/
      EnvironmentSnapshot.cs                        modify: identity + observation
      InstallationObservation.cs                    create

  S1Atlas.Extraction/
    Discovery/
      EnvironmentDiscoveryService.cs                modify
      WindowsInstallationMetadataReader.cs          create
      WindowsScheduleOneLocator.cs                   modify
    Steam/
      SteamAppManifest.cs                            create
      SteamAppManifestParser.cs                      create
      SteamAppManifestLocator.cs                     create

  S1Atlas.Storage/
    Migrations/
      FoundationSchemaRecognizer.cs                  create
      MigrationChecksum.cs                           create
      SqliteDatabaseBackupService.cs                 create
      SqliteMigration.cs                             create
      SqliteMigrationRunner.cs                       create
      SqliteMigrations.cs                            create
      UnrecognizedAtlasSchemaException.cs            create
    Properties/
      AssemblyInfo.cs                                create
    Sqlite/
      EnvironmentSnapshotId.cs                       modify
      SqliteAtlasRepository.cs                       modify
      SqliteSchema.cs                                delete after migration runner is wired

  S1Atlas.Cli/
    CliApplication.cs                                modify
    Commands/
      BuildsCommand.cs                               modify
      CommandExecution.cs                            modify
      EnvironmentCommand.cs                          modify
      ScanCommand.cs                                 modify
      StatusCommand.cs                               modify
    Configuration/
      AtlasPaths.cs                                  modify: backups directory
    Output/
      CliEnvelope.cs                                 create
      CommandOutput.cs                               create
      FoundationOutputModels.cs                      create

tests/
  S1Atlas.Core.Tests/
    Environment/
      EnvironmentSnapshotTests.cs                    create

  S1Atlas.Extraction.Tests/
    Discovery/
      EnvironmentDiscoveryServiceTests.cs            modify
      WindowsInstallationMetadataReaderTests.cs      create
      WindowsScheduleOneLocatorTests.cs               modify
    Steam/
      SteamAppManifestLocatorTests.cs                 create
      SteamAppManifestParserTests.cs                  create

  S1Atlas.Storage.Tests/
    Migrations/
      FoundationSchemaRecognizerTests.cs             create
      FoundationV1DatabaseFixture.cs                 create
      SqliteMigrationRunnerTests.cs                  create
    Sqlite/
      EnvironmentSnapshotIdTests.cs                  create
      SqliteAtlasRepositoryTests.cs                  modify

  S1Atlas.IntegrationTests/
    Foundation/
      FoundationCliTests.cs                          modify
      FoundationMigrationTests.cs                    create
      FoundationSafetyTests.cs                       modify only where fixtures require executable/Steam layout
      FoundationV1DatabaseFixture.cs                 create

README.md                                             modify
```

Each new file has one primary responsibility. Migration discovery, backup, migration execution, Steam parsing, metadata composition, repository persistence, and CLI formatting remain independently testable.

---

### Task 1: Define and recognize the exact Foundation schema

**Files:**
- Create: `src/S1Atlas.Storage/Migrations/SqliteMigration.cs`
- Create: `src/S1Atlas.Storage/Migrations/MigrationChecksum.cs`
- Create: `src/S1Atlas.Storage/Migrations/FoundationSchemaRecognizer.cs`
- Create: `src/S1Atlas.Storage/Migrations/UnrecognizedAtlasSchemaException.cs`
- Create: `src/S1Atlas.Storage/Properties/AssemblyInfo.cs`
- Create: `tests/S1Atlas.Storage.Tests/Migrations/FoundationV1DatabaseFixture.cs`
- Create: `tests/S1Atlas.Storage.Tests/Migrations/FoundationSchemaRecognizerTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Data.Sqlite.SqliteConnection`.
- Produces:
  - `SqliteMigration(int Version, string Name, string Sql)` with deterministic `Checksum`.
  - `FoundationSchemaRecognizer.IsExactFoundationV1Async(SqliteConnection, CancellationToken)`.
  - `UnrecognizedAtlasSchemaException` for safe refusal paths.
  - A reusable test fixture that creates the exact shipped Foundation schema and optionally inserts realistic rows.

- [ ] **Step 1: Add failing checksum tests**

Create `FoundationSchemaRecognizerTests.cs` with a checksum test before implementing the production types:

```csharp
[Fact]
public void MigrationChecksum_WithSameDefinition_IsDeterministic()
{
    var first = MigrationChecksum.Compute(1, "foundation", "SELECT 1;");
    var second = MigrationChecksum.Compute(1, "foundation", "SELECT 1;");

    Assert.Equal(first, second);
}

[Fact]
public void MigrationChecksum_WhenSqlChanges_ChangesDigest()
{
    var first = MigrationChecksum.Compute(1, "foundation", "SELECT 1;");
    var second = MigrationChecksum.Compute(1, "foundation", "SELECT 2;");

    Assert.NotEqual(first, second);
}
```

Run:

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj --filter MigrationChecksum
```

Expected: compile failure because `MigrationChecksum` does not exist.

- [ ] **Step 2: Implement checksummed migration definitions**

Create `MigrationChecksum.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace S1Atlas.Storage.Migrations;

internal static class MigrationChecksum
{
    public static string Compute(int version, string name, string sql)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var canonical = $"{version}\n{name}\n{sql.Replace("\r\n", "\n", StringComparison.Ordinal)}";
        return Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}
```

Create `SqliteMigration.cs`:

```csharp
namespace S1Atlas.Storage.Migrations;

internal sealed record SqliteMigration(int Version, string Name, string Sql)
{
    public string Checksum => MigrationChecksum.Compute(Version, Name, Sql);
}
```

Create `UnrecognizedAtlasSchemaException.cs`:

```csharp
namespace S1Atlas.Storage.Migrations;

public sealed class UnrecognizedAtlasSchemaException : InvalidOperationException
{
    public UnrecognizedAtlasSchemaException(string message)
        : base(message)
    {
    }
}
```

Create `Properties/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("S1Atlas.Storage.Tests")]
```

Run the checksum tests again and expect PASS.

- [ ] **Step 3: Create the exact v1 database fixture**

Create `FoundationV1DatabaseFixture.cs`. Its schema SQL must match the schema currently shipped on `main`, including explicit indexes and foreign keys:

```csharp
internal static class FoundationV1DatabaseFixture
{
    public const string SchemaSql = """
        CREATE TABLE builds (
            build_id TEXT NOT NULL PRIMARY KEY,
            game_version TEXT NULL,
            steam_build_id TEXT NULL,
            game_assembly_sha256 TEXT NOT NULL,
            metadata_sha256 TEXT NOT NULL,
            scanned_at_utc TEXT NOT NULL,
            is_valid INTEGER NOT NULL CHECK (is_valid IN (0, 1))
        );

        CREATE TABLE environment_snapshots (
            snapshot_id TEXT NOT NULL PRIMARY KEY,
            build_id TEXT NOT NULL,
            atlas_version TEXT NOT NULL,
            captured_at_utc TEXT NOT NULL,
            FOREIGN KEY (build_id) REFERENCES builds(build_id)
        );

        CREATE INDEX ix_environment_snapshots_build_id
        ON environment_snapshots(build_id);

        CREATE TABLE dependencies (
            snapshot_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            kind TEXT NOT NULL,
            version TEXT NULL,
            path TEXT NULL,
            is_installed INTEGER NOT NULL CHECK (is_installed IN (0, 1)),
            PRIMARY KEY (snapshot_id, ordinal),
            FOREIGN KEY (snapshot_id)
                REFERENCES environment_snapshots(snapshot_id)
                ON DELETE CASCADE
        );

        CREATE INDEX ix_dependencies_snapshot_kind
        ON dependencies(snapshot_id, kind);

        CREATE TABLE atlas_state (
            singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
            current_snapshot_id TEXT NULL,
            FOREIGN KEY (current_snapshot_id)
                REFERENCES environment_snapshots(snapshot_id)
        );

        INSERT OR IGNORE INTO atlas_state (singleton_id, current_snapshot_id)
        VALUES (1, NULL);
        """;
}
```

Add helper methods to create a database and insert a known build, snapshot, four dependencies, and current pointer. Use the real reference build ID from the design spec in at least one migration test.

- [ ] **Step 4: Write failing exact-recognition tests**

Add tests:

```csharp
[Fact]
public async Task IsExactFoundationV1Async_WithShippedSchema_ReturnsTrue()
```

```csharp
[Fact]
public async Task IsExactFoundationV1Async_WhenColumnIsMissing_ReturnsFalse()
```

```csharp
[Fact]
public async Task IsExactFoundationV1Async_WhenExplicitIndexIsMissing_ReturnsFalse()
```

```csharp
[Fact]
public async Task IsExactFoundationV1Async_WhenUnexpectedUserTableExists_ReturnsFalse()
```

Run:

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj --filter FoundationSchemaRecognizerTests
```

Expected: compile failure because `FoundationSchemaRecognizer` does not exist.

- [ ] **Step 5: Implement exact schema recognition**

`FoundationSchemaRecognizer` must inspect, not mutate, the open database.

Expected user tables:

```text
atlas_state
builds
dependencies
environment_snapshots
```

Expected explicit indexes:

```text
ix_dependencies_snapshot_kind
ix_environment_snapshots_build_id
```

For each expected table, query:

```sql
PRAGMA table_info('<table>');
PRAGMA foreign_key_list('<table>');
PRAGMA index_list('<table>');
```

Compare:

- column name;
- declared SQLite type;
- nullability;
- primary-key ordinal;
- expected foreign-key source/target columns and delete action;
- explicit index name and indexed columns.

Ignore SQLite’s internal `sqlite_*` tables and autoindexes, but reject any unexpected user-created table or explicit index.

Use ordinal string comparison for schema object names because the committed SQL controls their exact spelling.

- [ ] **Step 6: Verify Task 1**

Run:

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj --filter "MigrationChecksum|FoundationSchemaRecognizerTests"
dotnet build S1Atlas.sln --configuration Release
```

Expected: all selected tests pass; build has zero warnings and zero errors.

- [ ] **Step 7: Commit**

```powershell
git add -- src/S1Atlas.Storage/Migrations src/S1Atlas.Storage/Properties tests/S1Atlas.Storage.Tests/Migrations
git commit -m "feat: recognize the Foundation database schema"
```

---

### Task 2: Add versioned migrations, backups, checksum verification, and safe refusal

**Files:**
- Create: `src/S1Atlas.Storage/Migrations/SqliteMigrations.cs`
- Create: `src/S1Atlas.Storage/Migrations/SqliteDatabaseBackupService.cs`
- Create: `src/S1Atlas.Storage/Migrations/SqliteMigrationRunner.cs`
- Test: `tests/S1Atlas.Storage.Tests/Migrations/SqliteMigrationRunnerTests.cs`

**Interfaces:**
- Consumes: `FoundationSchemaRecognizer`, `SqliteMigration`, a database path, a backup directory, and `TimeProvider`.
- Produces:
  - `SqliteMigrations.All` with committed v1 and v2 SQL.
  - `SqliteDatabaseBackupService.CreateBackup(...)`.
  - `SqliteMigrationRunner.MigrateAsync(CancellationToken)`.
- Does not yet change `SqliteAtlasRepository.InitializeAsync`; wiring occurs in Task 4.

- [ ] **Step 1: Write failing migration-runner tests**

Create tests for these behaviors:

```csharp
[Fact]
public async Task MigrateAsync_NewDatabase_AppliesV1AndV2WithoutBackup()
```

```csharp
[Fact]
public async Task MigrateAsync_SyntheticFoundationDatabaseWithSteamBuildId_CreatesBackupAndPreservesRows()
```

```csharp
[Fact]
public async Task MigrateAsync_UnknownNonemptySchema_RefusesWithoutMutation()
```

```csharp
[Fact]
public async Task MigrateAsync_WhenAppliedChecksumDiffers_Refuses()
```

```csharp
[Fact]
public async Task MigrateAsync_WhenMigrationFails_RollsBackSchemaChanges()
```

Run and expect compile failure because the runner does not exist.

- [ ] **Step 2: Define committed migration SQL**

Create `SqliteMigrations.cs`.

Migration 1 is named `foundation-v1` and contains the exact Foundation schema from Task 1 plus the `INSERT OR IGNORE` atlas-state row.

Migration 2 is named `environment-observations-v2` and uses this SQL:

```sql
ALTER TABLE builds
RENAME COLUMN scanned_at_utc TO first_seen_at_utc;

ALTER TABLE environment_snapshots
ADD COLUMN identity_version INTEGER NOT NULL DEFAULT 1
CHECK (identity_version > 0);

ALTER TABLE environment_snapshots
ADD COLUMN executable_version TEXT NULL;

ALTER TABLE environment_snapshots
ADD COLUMN steam_app_id TEXT NULL;

ALTER TABLE environment_snapshots
ADD COLUMN steam_build_id TEXT NULL;

ALTER TABLE environment_snapshots
ADD COLUMN installation_root TEXT NULL;

ALTER TABLE environment_snapshots
ADD COLUMN game_assembly_path TEXT NULL;

ALTER TABLE environment_snapshots
ADD COLUMN global_metadata_path TEXT NULL;

UPDATE environment_snapshots
SET executable_version = (
        SELECT builds.game_version
        FROM builds
        WHERE builds.build_id = environment_snapshots.build_id),
    steam_build_id = (
        SELECT builds.steam_build_id
        FROM builds
        WHERE builds.build_id = environment_snapshots.build_id);

ALTER TABLE builds DROP COLUMN game_version;
ALTER TABLE builds DROP COLUMN steam_build_id;
```

Expose migrations in strictly increasing version order:

```csharp
public static IReadOnlyList<SqliteMigration> All { get; } =
[
    new(1, "foundation-v1", FoundationV1Sql),
    new(2, "environment-observations-v2", EnvironmentObservationsV2Sql)
];
```

- [ ] **Step 3: Implement SQLite backup creation**

Create `SqliteDatabaseBackupService` with:

```csharp
internal sealed class SqliteDatabaseBackupService(TimeProvider? timeProvider = null)
{
    public string CreateBackup(
        SqliteConnection source,
        string backupDirectory,
        int targetSchemaVersion)
}
```

Implementation requirements:

1. Create `backupDirectory` outside the game installation.
2. Generate a collision-resistant filename:

```text
atlas-before-schema-<version>-yyyyMMddTHHmmssfffZ-<8-char-guid>.db
```

3. Open a destination `SqliteConnection` in `ReadWriteCreate` mode with pooling disabled.
4. Call `source.BackupDatabase(destination)` so committed WAL content is included.
5. Close the destination before returning the full path.
6. If backup creation fails, abort migration and leave the source schema untouched.

- [ ] **Step 4: Implement the migration ledger and runner**

Create `SqliteMigrationRunner` with a production constructor and an internal test constructor:

```csharp
internal sealed class SqliteMigrationRunner
{
    public SqliteMigrationRunner(
        string databasePath,
        string backupDirectory,
        TimeProvider? timeProvider = null);

    internal SqliteMigrationRunner(
        string databasePath,
        string backupDirectory,
        IReadOnlyList<SqliteMigration> migrations,
        TimeProvider? timeProvider = null);

    public Task MigrateAsync(CancellationToken cancellationToken);
}
```

The migration ledger is:

```sql
CREATE TABLE schema_migrations (
    version INTEGER NOT NULL PRIMARY KEY,
    name TEXT NOT NULL,
    checksum TEXT NOT NULL,
    applied_at_utc TEXT NOT NULL
);
```

Runner behavior:

```text
Open database with foreign_keys = ON and pooling disabled
        |
        +-- no user tables
        |      create ledger
        |      apply migration 1 and record checksum
        |      apply migration 2 and record checksum
        |      no backup
        |
        +-- schema_migrations exists
        |      verify every applied version/name/checksum
        |      apply pending migrations transactionally
        |      create backup before first pending structural migration
        |
        +-- no ledger, exact Foundation v1 schema
        |      create backup before mutation
        |      in one transaction:
        |        create ledger
        |        record migration 1 as recognized baseline
        |        apply migration 2
        |        record migration 2
        |
        `-- no ledger, unknown nonempty schema
               throw UnrecognizedAtlasSchemaException
               do not create ledger or backup
```

Each migration and its ledger insert occur in the same SQLite transaction. On failure, rollback with `CancellationToken.None` and throw an `InvalidOperationException` naming the failed migration version and name.

Before applying pending migrations, verify that every ledger row matches the currently committed version, name, and checksum. Missing committed migrations or mismatched checksums are integrity errors.

- [ ] **Step 5: Prove data and pointer preservation**

In `MigrateAsync_SyntheticFoundationDatabaseWithSteamBuildId_CreatesBackupAndPreservesRows`, insert:

```text
build ID:                  6fbd38f8401afa2241a1322afd4b8a8eadc99aa1f1c660ece253da7859d54bdc
snapshot ID:               foundation-snapshot-v1
executable version:        2022.3.62.7762112
synthetic Steam build ID:  12345678
four dependency rows
current pointer:           foundation-snapshot-v1
```

The synthetic Steam build ID exists only to exercise the migration’s copy path. The shipped Foundation implementation always wrote `SteamBuildId: null`, so real Foundation databases are expected to migrate a null Steam build ID until a later v2 scan reads one from a local Steam manifest.

After migration, query raw SQLite facts and assert:

- both migration rows exist with current checksums;
- build ID and both hashes are unchanged;
- `first_seen_at_utc` equals the prior scan timestamp;
- `game_version` and build-level `steam_build_id` columns no longer exist;
- snapshot ID is unchanged;
- `identity_version == 1`;
- `executable_version == 2022.3.62.7762112`;
- snapshot `steam_build_id == 12345678` for this synthetic copy-path fixture;
- path and Steam app columns are null;
- all dependency rows remain;
- current pointer is unchanged;
- the backup opens and still contains the v1 schema and original rows.

- [ ] **Step 6: Prove unknown-schema refusal and transactional rollback**

Unknown schema test:

1. Create only `CREATE TABLE unrelated(value TEXT);`.
2. Run migration and expect `UnrecognizedAtlasSchemaException`.
3. Assert `unrelated` still exists.
4. Assert `schema_migrations` does not exist.
5. Assert backup directory is absent or empty.

Rollback test:

1. Use the internal constructor with migration 1 plus a migration 2 whose SQL performs one valid `ALTER TABLE` followed by invalid SQL.
2. Expect `InvalidOperationException`.
3. Reopen the database.
4. Assert the valid `ALTER TABLE` was rolled back.
5. Assert the failed migration has no ledger row.

- [ ] **Step 7: Verify Task 2**

```powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj --filter SqliteMigrationRunnerTests
dotnet build S1Atlas.sln --configuration Release
```

Expected: migration tests pass; zero warnings/errors.

- [ ] **Step 8: Commit**

```powershell
git add -- src/S1Atlas.Storage/Migrations tests/S1Atlas.Storage.Tests/Migrations
git commit -m "feat: add versioned Atlas database migrations"
```

---

### Task 3: Read executable and Steam installation metadata offline

**Files:**
- Create: `src/S1Atlas.Core/Discovery/IInstallationMetadataReader.cs`
- Create: `src/S1Atlas.Core/Environment/InstallationObservation.cs`
- Modify: `src/S1Atlas.Core/Discovery/ScheduleOneInstallation.cs`
- Modify: `src/S1Atlas.Extraction/Discovery/WindowsScheduleOneLocator.cs`
- Create: `src/S1Atlas.Extraction/Steam/SteamAppManifest.cs`
- Create: `src/S1Atlas.Extraction/Steam/SteamAppManifestParser.cs`
- Create: `src/S1Atlas.Extraction/Steam/SteamAppManifestLocator.cs`
- Create: `src/S1Atlas.Extraction/Discovery/WindowsInstallationMetadataReader.cs`
- Modify: `tests/S1Atlas.Extraction.Tests/Discovery/WindowsScheduleOneLocatorTests.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Steam/SteamAppManifestParserTests.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Steam/SteamAppManifestLocatorTests.cs`
- Create: `tests/S1Atlas.Extraction.Tests/Discovery/WindowsInstallationMetadataReaderTests.cs`

**Interfaces:**
- Consumes: a validated `ScheduleOneInstallation` and local filesystem metadata.
- Produces:

```csharp
public interface IInstallationMetadataReader
{
    Task<InstallationObservation> ReadAsync(
        ScheduleOneInstallation installation,
        CancellationToken cancellationToken);
}
```

```csharp
public sealed record InstallationObservation(
    string? ExecutableVersion,
    string? SteamAppId,
    string? SteamBuildId,
    string? InstallationRoot,
    string? GameAssemblyPath,
    string? GlobalMetadataPath);
```

- [ ] **Step 1: Add the final metadata contracts**

Create `InstallationObservation.cs` exactly as shown above plus:

```csharp
public static InstallationObservation Unknown { get; } =
    new(null, null, null, null, null, null);
```

Create `IInstallationMetadataReader.cs` with the signature above.

Add `ExecutablePath` to `ScheduleOneInstallation`:

```csharp
public sealed record ScheduleOneInstallation(
    string RootPath,
    string ExecutablePath,
    string GameAssemblyPath,
    string GlobalMetadataPath,
    string ModsPath,
    string MelonLoaderPath);
```

Update `WindowsScheduleOneLocator` to set:

```csharp
ExecutablePath: Path.Combine(rootPath, "Schedule I.exe")
```

The executable is not a Foundation validity requirement; the locator still requires only `GameAssembly.dll` and `global-metadata.dat`. Missing executables produce unknown executable version.

Update locator tests to assert the expected executable path.

- [ ] **Step 2: Write failing ACF parser tests**

Use an actual minimal Steam manifest fixture:

```text
"AppState"
{
    "appid"      "3164500"
    "Universe"   "1"
    "installdir" "Schedule I"
    "buildid"    "19420567"
    "UserConfig"
    {
        "language" "english"
    }
}
```

Tests:

```csharp
[Fact]
public void TryParse_ValidManifest_ReturnsDirectAppStateValues()
```

```csharp
[Fact]
public void TryParse_NestedDuplicateKey_DoesNotReplaceDirectValue()
```

```csharp
[Fact]
public void TryParse_MalformedManifest_ReturnsFalse()
```

```csharp
[Fact]
public void TryParse_EscapedQuotedValue_DecodesValue()
```

- [ ] **Step 3: Implement a minimal explicit ACF parser**

Create:

```csharp
internal sealed record SteamAppManifest(
    string AppId,
    string InstallDirectory,
    string BuildId);
```

`SteamAppManifestParser.TryParse(string content, out SteamAppManifest? manifest)` must use a small tokenizer rather than a broad regex over the entire file.

Tokenizer tokens:

```text
QuotedString
OpenBrace
CloseBrace
End
Invalid
```

Parsing rules:

1. Expect quoted root key `AppState`.
2. Expect `{`.
3. At depth 1, capture direct quoted key/value pairs.
4. When a direct key is followed by `{`, skip that nested object with balanced-brace tracking.
5. Decode `\\` and `\"` inside quoted strings.
6. Require nonblank direct values for `appid`, `installdir`, and `buildid`.
7. Return `false` for unbalanced braces, unterminated strings, missing required direct fields, or unexpected token order.

Run parser tests and expect PASS.

- [ ] **Step 4: Write failing manifest-location tests**

Create a temporary layout:

```text
<root>/steamapps/
  appmanifest_111.acf          unrelated
  appmanifest_3164500.acf      installdir = Schedule I
  common/
    Schedule I/
```

Tests:

```csharp
[Fact]
public async Task LocateAsync_MatchingInstallDirectory_ReturnsAppAndBuildIds()
```

```csharp
[Fact]
public async Task LocateAsync_UnrelatedManifests_ReturnsUnknown()
```

```csharp
[Fact]
public async Task LocateAsync_MalformedMatchingFile_ReturnsUnknown()
```

```csharp
[Fact]
public async Task LocateAsync_NonSteamInstallation_ReturnsUnknown()
```

- [ ] **Step 5: Implement defensive Steam manifest location**

`SteamAppManifestLocator` should expose:

```csharp
internal Task<SteamAppManifest?> LocateAsync(
    string installationRoot,
    CancellationToken cancellationToken);
```

Algorithm:

1. Normalize `installationRoot` with `Path.GetFullPath` and trim ending separators.
2. Confirm its parent is named `common` and that parent’s parent is named `steamapps`, using `OrdinalIgnoreCase`.
3. Enumerate `appmanifest_*.acf` in the `steamapps` top directory only.
4. Sort paths with `OrdinalIgnoreCase`, then `Ordinal`, for deterministic selection.
5. Read each file with `FileShare.ReadWrite | FileShare.Delete` so Steam updates are tolerated.
6. Parse it.
7. Construct `Path.Combine(steamApps, "common", manifest.InstallDirectory)`.
8. Compare the normalized candidate path to `installationRoot` with `OrdinalIgnoreCase`.
9. Return the first deterministic match.
10. Catch expected `IOException`, `UnauthorizedAccessException`, and `SecurityException`; return unknown instead of inventing metadata.
11. Call `cancellationToken.ThrowIfCancellationRequested()` between files.

No network API or hard-coded Schedule I app ID is used.

- [ ] **Step 6: Implement the Windows installation metadata reader**

Create `WindowsInstallationMetadataReader`:

```csharp
public sealed class WindowsInstallationMetadataReader : IInstallationMetadataReader
{
    public WindowsInstallationMetadataReader();

    internal WindowsInstallationMetadataReader(
        SteamAppManifestLocator steamLocator,
        Func<string, string?> executableVersionProbe);

    public async Task<InstallationObservation> ReadAsync(
        ScheduleOneInstallation installation,
        CancellationToken cancellationToken);
}
```

Default executable version probe:

```csharp
FileVersionInfo.GetVersionInfo(path).FileVersion
```

Expected probe failures return null:

```text
Win32Exception
IOException
UnauthorizedAccessException
SecurityException
```

Return canonical full paths in every available path field. Do not create any game directory or file.

- [ ] **Step 7: Verify metadata-reader behavior**

Tests must prove:

- executable version and Steam IDs are combined;
- missing executable produces null executable version while paths and Steam metadata remain available;
- file-version probe failure produces unknown version without failing discovery;
- malformed or absent Steam data produces null IDs;
- observation paths are full paths;
- the reader does not write into the game tree.

Run:

```powershell
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj --filter "SteamAppManifest|WindowsInstallationMetadataReader|WindowsScheduleOneLocator"
dotnet build S1Atlas.sln --configuration Release
```

- [ ] **Step 8: Commit**

```powershell
git add -- src/S1Atlas.Core/Discovery src/S1Atlas.Core/Environment/InstallationObservation.cs src/S1Atlas.Extraction/Discovery src/S1Atlas.Extraction/Steam tests/S1Atlas.Extraction.Tests
git commit -m "feat: read local Schedule I installation metadata"
```

---

### Task 4: Migrate the domain and repository to v2 environment observations

**Files:**
- Modify: `src/S1Atlas.Core/Builds/GameBuild.cs`
- Modify: `src/S1Atlas.Core/Environment/EnvironmentSnapshot.cs`
- Modify: `src/S1Atlas.Extraction/Discovery/EnvironmentDiscoveryService.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/EnvironmentSnapshotId.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.cs`
- Delete: `src/S1Atlas.Storage/Sqlite/SqliteSchema.cs`
- Modify: `src/S1Atlas.Cli/CliApplication.cs`
- Modify: `src/S1Atlas.Cli/Configuration/AtlasPaths.cs`
- Create: `tests/S1Atlas.Core.Tests/Environment/EnvironmentSnapshotTests.cs`
- Modify: `tests/S1Atlas.Extraction.Tests/Discovery/EnvironmentDiscoveryServiceTests.cs`
- Create: `tests/S1Atlas.Storage.Tests/Sqlite/EnvironmentSnapshotIdTests.cs`
- Modify: `tests/S1Atlas.Storage.Tests/Sqlite/SqliteAtlasRepositoryTests.cs`

**Interfaces:**
- Consumes: migration runner, installation metadata reader, file hashes, dependencies.
- Produces the final Phase 1 domain:

```csharp
public sealed record GameBuild(
    string BuildId,
    string GameAssemblySha256,
    string MetadataSha256,
    DateTimeOffset FirstSeenAtUtc,
    bool IsValid);
```

```csharp
public sealed record EnvironmentSnapshot(
    int IdentityVersion,
    GameBuild Build,
    InstallationObservation Installation,
    IReadOnlyList<DependencyVersion> Dependencies,
    string AtlasVersion,
    DateTimeOffset CapturedAtUtc);
```

- [ ] **Step 1: Write failing final-domain tests**

Create `EnvironmentSnapshotTests.cs`:

```csharp
[Fact]
public void Constructor_SeparatesBuildIdentityFromInstallationObservation()
{
    var build = new GameBuild(
        "build-a",
        "assembly-hash",
        "metadata-hash",
        DateTimeOffset.Parse("2026-08-12T12:00:00Z"),
        true);
    var observation = new InstallationObservation(
        "2022.3.62.7762112",
        "3164500",
        "19420567",
        "C:\\Steam\\steamapps\\common\\Schedule I",
        "C:\\Steam\\steamapps\\common\\Schedule I\\GameAssembly.dll",
        "C:\\Steam\\steamapps\\common\\Schedule I\\Schedule I_Data\\il2cpp_data\\Metadata\\global-metadata.dat");

    var snapshot = new EnvironmentSnapshot(
        2,
        build,
        observation,
        [],
        "0.2.0",
        DateTimeOffset.Parse("2026-08-12T12:00:00Z"));

    Assert.Equal(2, snapshot.IdentityVersion);
    Assert.Equal("build-a", snapshot.Build.BuildId);
    Assert.Equal("19420567", snapshot.Installation.SteamBuildId);
}
```

Run and expect compile failure because the records still use the Foundation signatures.

- [ ] **Step 2: Apply the final content/observation domain model**

Replace `GameBuild.cs` and `EnvironmentSnapshot.cs` with the signatures above.

`GameBuild` contains no executable version, Steam ID, or installation path.

Update all construction sites and fixture helpers in the same task so the solution returns to a buildable state before committing.

- [ ] **Step 3: Update environment discovery**

Change `EnvironmentDiscoveryService` constructor:

```csharp
public EnvironmentDiscoveryService(
    IScheduleOneLocator locator,
    IFileHasher fileHasher,
    IDependencyDetector dependencyDetector,
    IInstallationMetadataReader installationMetadataReader,
    TimeProvider? timeProvider = null)
```

After hashing the two authoritative inputs:

```csharp
var capturedAt = _timeProvider.GetUtcNow();
var observation = await _installationMetadataReader.ReadAsync(
    installation,
    cancellationToken);
var build = new GameBuild(
    BuildFingerprint.Create(gameAssemblyHash, metadataHash),
    gameAssemblyHash,
    metadataHash,
    capturedAt,
    IsValid: true);

return new EnvironmentSnapshot(
    IdentityVersion: 2,
    Build: build,
    Installation: observation,
    Dependencies: _dependencyDetector.Detect(installation),
    AtlasVersion: atlasVersion,
    CapturedAtUtc: capturedAt);
```

Update extraction tests to inject a fake metadata reader and assert all observation fields plus identity version 2.

- [ ] **Step 4: Implement v2 snapshot identity**

`EnvironmentSnapshotId.Create` must reject non-v2 candidate snapshots because migrated v1 IDs are preserved rather than recomputed:

```csharp
if (snapshot.IdentityVersion != 2)
{
    throw new InvalidOperationException(
        "Only identity-version 2 snapshots can receive a new snapshot ID.");
}
```

Canonical inputs, in order:

```text
environment-snapshot
2
build ID
Atlas version
executable version or empty
Steam app ID or empty
Steam build ID or empty
normalized installation root or empty
normalized GameAssembly path or empty
normalized metadata path or empty
ordered dependency count
ordered dependency facts
```

Keep the existing length-prefixed SHA-256 writer. Normalize paths with full path, trimmed ending separators, and invariant uppercase for Windows identity comparison.

Add tests proving:

- identical v2 snapshots produce the same ID;
- capture timestamp does not change the ID;
- executable version changes the ID;
- Steam build ID changes the ID;
- installation path changes the ID;
- dependency change changes the ID;
- identity version 1 is rejected for new ID creation.

- [ ] **Step 5: Wire the migration runner into repository initialization**

Add to `AtlasPaths`:

```csharp
public string BackupsDirectory => Path.Combine(RootDirectory, "backups");
```

Give `SqliteAtlasRepository` two constructors:

```csharp
public SqliteAtlasRepository(string databasePath)
    : this(
        databasePath,
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(databasePath))!,
            "backups"))
{
}

public SqliteAtlasRepository(
    string databasePath,
    string backupDirectory)
```

Store a `SqliteMigrationRunner` and implement:

```csharp
public Task InitializeAsync(CancellationToken cancellationToken) =>
    _migrationRunner.MigrateAsync(cancellationToken);
```

Delete `SqliteSchema.cs` after no code references it.

`CliApplication` must construct the repository with both `_paths.DatabasePath` and `_paths.BackupsDirectory`.

- [ ] **Step 6: Update repository writes for v2**

Build insert:

```sql
INSERT OR IGNORE INTO builds (
    build_id,
    game_assembly_sha256,
    metadata_sha256,
    first_seen_at_utc,
    is_valid)
VALUES (
    $buildId,
    $gameAssemblySha256,
    $metadataSha256,
    $firstSeenAtUtc,
    $isValid);
```

Snapshot insert:

```sql
INSERT OR IGNORE INTO environment_snapshots (
    snapshot_id,
    build_id,
    atlas_version,
    captured_at_utc,
    identity_version,
    executable_version,
    steam_app_id,
    steam_build_id,
    installation_root,
    game_assembly_path,
    global_metadata_path)
VALUES (
    $snapshotId,
    $buildId,
    $atlasVersion,
    $capturedAtUtc,
    $identityVersion,
    $executableVersion,
    $steamAppId,
    $steamBuildId,
    $installationRoot,
    $gameAssemblyPath,
    $globalMetadataPath);
```

Only identity-version 2 snapshots are accepted by `SaveSnapshotAsync`. Migrated v1 snapshots remain readable but are never reinserted as new candidates.

Dependency ordering and transaction/promotion behavior remain unchanged.

- [ ] **Step 7: Update repository reads for v1 and v2 snapshots**

Current snapshot query returns:

```text
build_id
game_assembly_sha256
metadata_sha256
first_seen_at_utc
is_valid
atlas_version
captured_at_utc
snapshot_id
identity_version
executable_version
steam_app_id
steam_build_id
installation_root
game_assembly_path
global_metadata_path
```

Construct `GameBuild`, `InstallationObservation`, and `EnvironmentSnapshot` from those values.

`ListBuildsAsync` reads only content identity and orders by:

```sql
ORDER BY first_seen_at_utc DESC, build_id DESC;
```

- [ ] **Step 8: Update repository tests**

Adapt existing tests to the final domain and add assertions for:

- v2 observation round-trip;
- same build with changed Steam build ID creates/promotes a new environment snapshot but leaves one build row;
- same build with changed installation path creates a new environment snapshot;
- multiple same-kind dependencies still round-trip;
- failed dependency insert still rolls back current pointer and candidate build;
- invalid build is rejected;
- newest first-seen builds list correctly;
- `InitializeAsync` on a v1 fixture migrates and reads the old current snapshot as identity version 1;
- a new v2 scan after migration promotes a v2 snapshot while the v1 snapshot row remains.

- [ ] **Step 9: Verify Task 4**

```powershell
dotnet test tests/S1Atlas.Core.Tests/S1Atlas.Core.Tests.csproj
dotnet test tests/S1Atlas.Extraction.Tests/S1Atlas.Extraction.Tests.csproj
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj
dotnet build S1Atlas.sln --configuration Release
```

Expected: all project tests pass; zero warnings/errors.

- [ ] **Step 10: Commit**

```powershell
git add -- src/S1Atlas.Core src/S1Atlas.Extraction/Discovery src/S1Atlas.Storage src/S1Atlas.Cli/CliApplication.cs src/S1Atlas.Cli/Configuration tests/S1Atlas.Core.Tests tests/S1Atlas.Extraction.Tests/Discovery tests/S1Atlas.Storage.Tests
git commit -m "feat: persist versioned environment observations"
```

---

### Task 5: Add accurate human output and stable JSON output

**Files:**
- Create: `src/S1Atlas.Cli/Output/CliEnvelope.cs`
- Create: `src/S1Atlas.Cli/Output/CommandOutput.cs`
- Create: `src/S1Atlas.Cli/Output/FoundationOutputModels.cs`
- Modify: `src/S1Atlas.Cli/Commands/CommandExecution.cs`
- Modify: `src/S1Atlas.Cli/Commands/ScanCommand.cs`
- Modify: `src/S1Atlas.Cli/Commands/StatusCommand.cs`
- Modify: `src/S1Atlas.Cli/Commands/EnvironmentCommand.cs`
- Modify: `src/S1Atlas.Cli/Commands/BuildsCommand.cs`
- Modify: `tests/S1Atlas.IntegrationTests/Foundation/FoundationCliTests.cs`

**Interfaces:**
- Produces `--json` for `status`, `env`, and `builds`.
- Preserves human mode and exit codes.
- JSON stdout contains exactly one `CliEnvelope<T>` with camel-case property names.

- [ ] **Step 1: Create failing JSON integration tests**

Add tests that invoke the actual `CliApplication` and parse stdout with `JsonDocument`:

```csharp
[Fact]
public async Task StatusJson_AfterScan_WritesSingleStableEnvelope()
```

```csharp
[Fact]
public async Task EnvironmentJson_AfterScan_IncludesObservationsAndDependencies()
```

```csharp
[Fact]
public async Task BuildsJson_WhenEmpty_ReturnsEmptyArray()
```

```csharp
[Fact]
public void StatusJson_WhenDatabasePathFails_ReturnsJsonErrorWithoutStackTrace()
```

Assertions for every JSON test:

- stdout parses as one document;
- `schemaVersion == 1`;
- `command` matches;
- `success` and `exitCode` match;
- success has nonnull `data` and null `error`;
- failure has null `data` and structured `error`;
- stderr is empty in JSON mode for normal command results;
- no `"   at "` stack trace text exists.

Run and expect failure because `--json` is not defined.

- [ ] **Step 2: Define the JSON envelope and output DTOs**

Create `CliEnvelope.cs`:

```csharp
namespace S1Atlas.Cli.Output;

internal sealed record CliEnvelope<T>(
    int SchemaVersion,
    string Command,
    bool Success,
    int ExitCode,
    T? Data,
    CliError? Error);

internal sealed record CliError(
    string Code,
    string Message);
```

For Phase 1 Foundation commands, `CliError` contains only `code` and `message`. `schemaVersion: 1` versions the top-level envelope, not one closed command-independent error-object union; later commands may add fields such as `attemptId` or `stage`, and consumers must ignore unknown error properties.

Create `FoundationOutputModels.cs`:

```csharp
internal sealed record StatusOutput(
    bool HasCurrentBuild,
    string? BuildId,
    string? ExecutableVersion,
    string? SteamAppId,
    string? SteamBuildId,
    DateTimeOffset? CapturedAtUtc,
    int InstalledDependencyCount,
    int DependencyCount);

internal sealed record EnvironmentOutput(
    string BuildId,
    string? ExecutableVersion,
    string? SteamAppId,
    string? SteamBuildId,
    string? InstallationRoot,
    string? GameAssemblyPath,
    string? GlobalMetadataPath,
    IReadOnlyList<DependencyOutput> Dependencies);

internal sealed record DependencyOutput(
    string Kind,
    string? Version,
    string? Path,
    bool IsInstalled);

internal sealed record BuildsOutput(
    IReadOnlyList<BuildOutput> Builds);

internal sealed record BuildOutput(
    string BuildId,
    DateTimeOffset FirstSeenAtUtc,
    bool IsValid);
```

- [ ] **Step 3: Implement command output selection**

Create `CommandOutput`:

```csharp
internal sealed class CommandOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public CommandOutput(
        string commandName,
        bool json,
        TextWriter standardOutput,
        TextWriter standardError);

    public bool IsJson { get; }

    public int Success<T>(T data, Action<TextWriter> writeHuman);

    public int Failure(int exitCode, string code, string message);
}
```

Behavior:

- Human success invokes `writeHuman(standardOutput)`.
- JSON success serializes exactly one `CliEnvelope<T>(1, commandName, true, 0, data, null)` to stdout.
- Human failure writes the concise message to stderr.
- JSON failure serializes one envelope to stdout and writes nothing to stderr.
- Always terminate output with one newline.

Create a helper that returns a fresh `Option<bool>` for each command:

```csharp
public static Option<bool> CreateJsonOption() => new("--json")
{
    Description = "Write one machine-readable JSON result."
};
```

- [ ] **Step 4: Make command exception handling output-mode aware**

Change `CommandExecution.Run`:

```csharp
public static int Run(
    Func<int> action,
    CommandOutput output,
    CancellationToken cancellationToken)
```

Catch cancellation:

```text
code: OperationCanceled
message: S1Atlas operation was canceled.
exit: 2
```

Catch other exceptions:

```text
code: OperationalFailure
message: S1Atlas failed: <exception.Message>
exit: 1
```

Do not serialize exception type or stack trace into the public envelope.

- [ ] **Step 5: Update human labels**

`scan` remains human-only in Phase 1 and writes:

```text
Indexed Schedule I build <id>
Executable version: <value or unknown>
Steam app ID: <value or unknown>
Steam build ID: <value or unknown>
<dependency lines>
```

`status` human output:

```text
Current build: <id>
Executable version: <value or unknown>
Steam app ID: <value or unknown>
Steam build ID: <value or unknown>
Captured: <UTC O-format>
Dependencies installed: <installed>/<total>
```

`env` human output:

```text
Build: <id>
Executable version: <value or unknown>
Steam app ID: <value or unknown>
Steam build ID: <value or unknown>
Installation root: <value or unknown>
GameAssembly: <value or unknown>
Global metadata: <value or unknown>
<dependency lines>
```

`builds` human output no longer pretends a build owns executable-version metadata:

```text
<build-id> | first seen <UTC O-format> | valid
```

No current output may contain the label `Game version:`.

- [ ] **Step 6: Add JSON behavior to status**

Add a local `--json` option.

No-build result is successful:

```json
{
  "schemaVersion": 1,
  "command": "status",
  "success": true,
  "exitCode": 0,
  "data": {
    "hasCurrentBuild": false,
    "buildId": null,
    "executableVersion": null,
    "steamAppId": null,
    "steamBuildId": null,
    "capturedAtUtc": null,
    "installedDependencyCount": 0,
    "dependencyCount": 0
  },
  "error": null
}
```

- [ ] **Step 7: Add JSON behavior to env and builds**

`env --json` with no current build returns exit 1:

```text
code: NoIndexedBuilds
message: No indexed builds. Run 's1atlas scan' first.
```

`builds --json` with no builds succeeds with an empty array.

Dependency kinds serialize using existing enum names (`S1Api`, `S1Mapi`, `MelonLoader`, `Sideload`).

- [ ] **Step 8: Verify human and JSON compatibility**

Add/update integration assertions:

- human `scan`, `status`, and `env` use accurate labels;
- no output contains `Game version:`;
- v1-migrated current snapshots show copied executable version and unknown paths;
- v2 scans show Steam IDs and paths;
- JSON property names are camel case;
- timestamps are ISO 8601;
- `status --json` operational error is structured and contains no stack trace;
- JSON consumers can ignore additional command-specific error properties without invalidating the schema-version 1 top-level envelope;
- human errors retain the existing concise stderr behavior.

Run:

```powershell
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter FoundationCliTests
dotnet build S1Atlas.sln --configuration Release
```

- [ ] **Step 9: Commit**

```powershell
git add -- src/S1Atlas.Cli tests/S1Atlas.IntegrationTests/Foundation/FoundationCliTests.cs
git commit -m "feat: add accurate Foundation metadata output"
```

---

### Task 6: Prove real Foundation migration compatibility and read-only safety

**Files:**
- Create: `tests/S1Atlas.IntegrationTests/Foundation/FoundationV1DatabaseFixture.cs`
- Create: `tests/S1Atlas.IntegrationTests/Foundation/FoundationMigrationTests.cs`
- Modify: `tests/S1Atlas.IntegrationTests/Foundation/FoundationCliTests.cs`
- Modify: `tests/S1Atlas.IntegrationTests/Foundation/FoundationSafetyTests.cs`
- Modify: `README.md`

**Interfaces:**
- Consumes the complete Phase 1 production path through `CliApplication` and `SqliteAtlasRepository`.
- Produces evidence that an actual Foundation-shaped database migrates safely, new scans create v2 observations, JSON remains stable, and the game tree remains untouched.

- [ ] **Step 1: Add an integration-level v1 fixture**

Create `FoundationV1DatabaseFixture` in the integration project rather than referencing test code from another test assembly.

It must create the exact v1 schema and insert:

```text
build ID:            6fbd38f8401afa2241a1322afd4b8a8eadc99aa1f1c660ece253da7859d54bdc
snapshot ID:         real-scan-foundation-v1
executable version:  2022.3.62.7762112
Steam build ID:      null (matches shipped Foundation behavior)
Atlas version:       0.1.0
captured time:       2026-08-12T19:06:40.3468325Z
S1API:               3.1.12.0 installed
S1MAPI:              missing
MelonLoader:         0.7.3.0 installed
Sideload:            1.30.0.0 installed
current pointer:     real-scan-foundation-v1
```

- [ ] **Step 2: Write the end-to-end migration test**

```csharp
[Fact]
public async Task Status_OnFoundationDatabase_MigratesAndPreservesCurrentState()
```

Flow:

1. Create the v1 database at `AtlasPaths.DatabasePath`.
2. Invoke `status` through `CliApplication`.
3. Assert exit 0 and accurate executable-version label.
4. Assert the build ID is unchanged.
5. Query the repository and assert identity version 1, copied executable version, null Steam build ID, dependencies, and current state.
6. Query raw SQLite and assert snapshot/current IDs unchanged.
7. Assert exactly one pre-schema-2 backup exists.
8. Open the backup and assert it still has `builds.game_version`.
9. Invoke `status` again.
10. Assert no second backup was created and migration ledger remains two rows.

- [ ] **Step 3: Prove a post-migration scan creates v2 observations**

Create a fake Steam layout:

```text
<temp>/Steam/steamapps/
  appmanifest_3164500.acf
  common/Schedule I/
```

The game directory includes:

```text
Schedule I.exe
GameAssembly.dll
Schedule I_Data/il2cpp_data/Metadata/global-metadata.dat
Mods/UserLibs/Plugins/MelonLoader fixture directories
```

The app manifest has matching `installdir`, app ID, and build ID.

Because a dummy executable does not carry a useful file version, inject or arrange the test at the discovery-service level for exact executable-version assertions; the CLI integration assertion may accept null/unknown while requiring Steam IDs and all three stored paths.

After `scan --game-path`:

- current snapshot identity version is 2;
- installation root and both input paths are full paths;
- Steam IDs match the fixture;
- build count remains one when hashes match the migrated build;
- old v1 snapshot row remains;
- a new v2 snapshot is intentionally created and becomes current even when the environment otherwise appears unchanged, because the identity algorithm and recorded inputs changed.

- [ ] **Step 4: Prove unknown-schema CLI failure is clean and non-mutating**

```csharp
[Fact]
public void Status_OnUnknownDatabaseSchema_ReturnsCleanFailureWithoutMigration()
```

Assert:

- exit code 1;
- human stderr starts with `S1Atlas failed:`;
- no raw stack trace;
- `schema_migrations` does not exist;
- unrelated table still exists;
- no backup was created because no recognized migration was attempted.

Add a JSON variant and assert one structured error document.

- [ ] **Step 5: Extend read-only safety coverage**

Update the fake installation helper to include `Schedule I.exe` and, where relevant, a Steam manifest outside the game directory.

Capture the complete game file/directory tree before and after `scan` as the existing safety test does. Assert byte-for-byte equality.

Also capture the local Steam app manifest bytes before and after scan and assert they are unchanged.

- [ ] **Step 6: Update README accurately**

Document:

- the Foundation now reports `Executable version`, local Steam app ID, and Steam build ID;
- the content-derived build ID remains authoritative;
- first run after updating may create one backup under `%LOCALAPPDATA%\S1Atlas\backups`;
- the first scan after migrating a v1 database intentionally creates and promotes a new identity-version 2 environment snapshot even when the observed environment is unchanged, while retaining the v1 snapshot as history;
- unknown schemas are refused rather than guessed;
- `status --json`, `env --json`, and `builds --json` are available;
- JSON stdout is one stable top-level envelope, while later command-specific error objects may add fields that consumers should ignore when unknown;
- Phase 1 still does not install or run Cpp2IL;
- the next implementation phase is the managed Cpp2IL tool supply chain.

Do not rewrite the historical Foundation as-built plan.

- [ ] **Step 7: Run complete verification**

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
```

- [ ] **Step 8: Verify repository scope and generated-data safety**

```powershell
git status --short
git diff --check
git ls-files | Select-String -Pattern "atlas\.db|\.db-wal|\.db-shm|appmanifest_.*\.acf"
```

Expected:

- only planned source, tests, and README changes are tracked;
- no whitespace errors;
- no generated database, backup, Steam manifest fixture outside source-controlled test text, or proprietary game file is tracked.

- [ ] **Step 9: Commit**

```powershell
git add -- tests/S1Atlas.IntegrationTests/Foundation README.md
git commit -m "test: verify Foundation metadata migration"
```

---

## Phase 1 Review Checklist

Before opening the implementation PR, verify every statement below with current command output or test evidence:

```text
[ ] Current Foundation v1 schema is recognized exactly
[ ] Unknown nonempty schemas are not mutated
[ ] Existing v1 database is backed up before mutation
[ ] Migration ledger checksums are verified on every initialization
[ ] Migration is idempotent
[ ] Migration rollback leaves the prior database usable
[ ] Reference build ID remains unchanged
[ ] Reference current snapshot ID remains unchanged during migration
[ ] All dependency rows remain attached
[ ] Migrated snapshot is identity version 1
[ ] First post-migration scan intentionally promotes a new v2 snapshot while retaining v1 history
[ ] New scans create identity version 2 snapshots
[ ] Build rows contain only content identity and first-seen metadata
[ ] Executable and Steam data live on environment snapshots
[ ] Real migrated Foundation data keeps Steam build ID unknown until a v2 scan reads it locally
[ ] Synthetic Steam build ID fixtures are labeled as test-only copy-path coverage
[ ] Steam metadata is read offline from a matching local manifest
[ ] Missing or malformed Steam metadata remains unknown
[ ] No command displays “Game version”
[ ] status/env/builds JSON stdout is one valid document
[ ] JSON consumers tolerate unknown command-specific error properties within the schema-version 1 envelope
[ ] Human output remains readable and exit codes remain 0/1/2
[ ] Scan does not modify game or Steam files
[ ] Full Release build has zero warnings/errors
[ ] Full automated suite passes
[ ] No Cpp2IL code, config, downloads, or execution entered Phase 1
```

## Phase 1 Completion Boundary

When this plan is complete, S1Atlas can safely open the existing local Foundation database, create a recoverable backup, migrate it to a checksummed v2 schema, preserve the real indexed build, record accurate installation observations on new scans, and expose that information through human or JSON Foundation commands.

The following remain outside this plan and begin only in Phase 2 or later:

```text
repository tool-definition files
Cpp2IL download and managed cache
Cpp2IL capability probes
Cpp2IL process execution
extraction attempts
artifact manifests
managed assembly validation
preferred extraction state
cleanup of extraction attempts
ILSpy and symbol indexing
```