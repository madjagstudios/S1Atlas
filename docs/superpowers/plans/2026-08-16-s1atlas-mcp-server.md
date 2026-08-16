# S1Atlas Read-Only MCP Server Implementation Plan

> **Status:** Shipped — PR #23, merged as 8e4417c on 2026-08-16. Full Release suite green (1,204 tests).

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `S1Atlas.Mcp`, a local read-only Model Context Protocol server (stdio) that exposes the same integrity-verified Schedule I Atlas knowledge as the CLI, through a single shared authority path consumed by both surfaces.

**Architecture:** A new `S1Atlas.Application` library owns the read-only service composition and the Schedule I Installed build-authority resolver (current-or-explicit build → preferred verified extraction → matching completed index by source identity → integrity check). `S1Atlas.Mcp` is a thin `net8.0` stdio host over that library, mapping each existing query/diff/scene service call into a structured envelope with build context, provenance, and explicit status. The CLI's Schedule I query commands are re-routed through the same authority resolver so agent and human answers cannot diverge; the CLI keeps its separate S1API/S1MAPI resolution, which MCP V1 does not expose.

**Tech Stack:** .NET 8, C#, `ModelContextProtocol` NuGet (hosted stdio server) + `Microsoft.Extensions.Hosting`, `Microsoft.Data.Sqlite`, xUnit.

## Global Constraints

- Target framework `net8.0` for every new project (matches `Directory.Build.props`).
- Read-only only: no write/patch/mutate tools, no migrations, no DB/dir creation from the MCP path.
- No network and no external process: the MCP host constructs no HTTP client, upstream sync client, process extractor, game locator, or installer.
- MCP V1 exposes only the Schedule I `Installed` channel surface. No S1API/S1MAPI tools.
- All Schedule I data flows through the shared authority + existing query/diff/scene services; no raw DB re-query inside the MCP adapter.
- stdout is reserved for MCP protocol traffic; all diagnostics/logging go to stderr.
- Source is hash-verified on read and never written by MCP (no output-path option).
- Limits default to 50, reject `<= 0`, and clamp to a bounded server maximum of 500.
- Empty result arrays are never reported as a successful answer for an unresolved query; status must be explicit (`resolved` / `not_found` / `ambiguous` / `unavailable` / `invalid`).
- Provenance: direct extracted/indexed facts are `FACT`; deterministic selection/ranking/counts/relationship-direction/diff-classification are `DERIVED`; no `INTERPRETATION` is produced in V1 and none may be labeled as fact.
- Tests use only generated or repository-owned fixtures; no proprietary game bytes, no network, no game/extraction process execution.
- Commit messages end with the repository's `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>` trailer.

## Reference: verbatim existing types this plan consumes

```csharp
// S1Atlas.Core.Indexing
public enum CodebaseKind { ScheduleI, S1Api, S1MApi }
public enum CodeChannel { Installed, Release, Preview }

public sealed record IndexQueryOptions(
    CodebaseKind Codebase, CodeChannel? Channel = CodeChannel.Installed,
    bool AllChannels = false, int Limit = 50);

public sealed record SymbolQueryResult(
    string IndexId, string Codebase, string Channel, string SymbolId,
    string Kind, string QualifiedName, string Signature, bool IsBestEffort);

public enum SymbolResolutionStatus { Resolved, NotFound, Ambiguous, NoCompletedIndex }

public sealed record SymbolSearchResult(
    int TotalCount, int ReturnedCount, IReadOnlyList<SymbolQueryResult> Results,
    SymbolResolutionStatus? ResolutionStatus = null);

public sealed record RelationshipQuerySetResult(
    SymbolResolutionResult Resolution, IReadOnlyList<RelationshipQueryResult> Relationships,
    BodyRecoveryStatus? BodyRecoveryStatus, bool CallerCompletenessBoundedByTargetResolution,
    string CompletenessNotice);

public sealed record SourceSnippetResolutionResult(
    SymbolResolutionResult Resolution, SourceSnippetQueryResult? Snippet);

public enum DiffClassification { Added, Removed, MethodBodyChanged, RelationshipsChanged, Unchanged }
public sealed record SymbolDiff(
    string CanonicalKey, string QualifiedName, string Kind, DiffClassification Classification,
    string? SignatureBefore, string? SignatureAfter);

// S1Atlas.Core.Storage
public sealed record CodeSnapshotRecord(
    string SnapshotId, CodebaseKind Codebase, CodeChannel Channel,
    string SourceIdentity, string CreatedAtUtc, string? EnvironmentSnapshotId = null);
public sealed record IndexRunRecord(
    string IndexId, string SnapshotId, IndexRunStatus Status, string StartedAtUtc,
    string? CompletedAtUtc = null, string? FailureMessage = null);
public enum IndexRunStatus { Running, Completed, Failed }

// S1Atlas.Core.Extraction
public sealed record ValidatedExtraction(
    string ExtractionId, string RecipeId, string BuildId, /* … */ ExtractionStatistics Statistics);
public sealed record PreferredExtraction(
    string BuildId, string ExtractionId, DateTimeOffset SelectedAtUtc,
    ExtractionPreferenceReason SelectionReason);
public enum ValidatedExtractionIntegrityStatus { Valid, Missing, Incomplete, Mismatch }
public sealed record ValidatedExtractionIntegrity(
    ValidatedExtractionIntegrityStatus Status, string? Code, string? Message);

// S1Atlas.Indexing.Authority
public sealed record PreferredVerifiedExtraction(
    string BuildId, PreferredExtraction Preference, ValidatedExtraction Extraction);
public sealed class PreferredVerifiedExtractionResolver
{
    public PreferredVerifiedExtractionResolver(
        string dataRoot, IValidatedExtractionRepository repository,
        IValidatedExtractionIntegrityVerifier integrityVerifier);
    public Task<PreferredVerifiedExtraction?> ResolveAsync(string buildId, CancellationToken ct);
}

// Repository read methods (all on SqliteAtlasRepository via these interfaces)
// IAtlasRepository
Task<EnvironmentSnapshot?> GetCurrentSnapshotAsync(CancellationToken ct);
Task<IReadOnlyList<GameBuild>> ListBuildsAsync(CancellationToken ct);
// IIndexRepository
Task<IndexRunRecord?> GetCompletedIndexAsync(string indexId, CancellationToken ct);
Task<IndexRunRecord?> GetLatestCompletedIndexBySourceIdentityAsync(CodebaseKind codebase, CodeChannel channel, string sourceIdentity, CancellationToken ct);
Task<IndexRunRecord?> GetLatestCompletedIndexForBuildAsync(CodebaseKind codebase, CodeChannel channel, string buildId, CancellationToken ct);
Task<CodeSnapshotRecord?> GetCodeSnapshotAsync(string snapshotId, CancellationToken ct);
// IValidatedExtractionRepository
Task<PreferredExtraction?> GetPreferredExtractionAsync(string buildId, CancellationToken ct);
Task<ValidatedExtraction?> GetValidatedExtractionAsync(string extractionId, CancellationToken ct);
Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsAsync(string? buildId, CancellationToken ct);
// ISceneRepository
Task<SceneSnapshotRecord?> GetLatestCompletedSceneSnapshotAsync(string buildId, CancellationToken ct);
Task<SceneSnapshotRecord?> GetCompletedSceneSnapshotAsync(string sceneSnapshotId, CancellationToken ct);

// S1Atlas.Indexing.Scene.SceneQueryService (existing)
public SceneQueryService(ISceneRepository repository, IAtlasRepository atlasRepository);
public Task<SceneListResult> ScenesAsync(SceneListRequest request, CancellationToken ct);
public Task<SceneDocumentQueryResult> SceneAsync(SceneQueryRequest request, CancellationToken ct);
public Task<GameObjectQueryResult> GameObjectAsync(GameObjectQueryRequest request, CancellationToken ct);
public Task<SceneDocumentQueryResult> PrefabAsync(PrefabQueryRequest request, CancellationToken ct);
public Task<ComponentQueryResult> ComponentAsync(ComponentQueryRequest request, CancellationToken ct);
public sealed record SceneListRequest(string? BuildId = null, string? SceneSnapshotId = null, SceneDocumentKind? Kind = null, string? Query = null, int Limit = 50);
public sealed record SceneQueryRequest(string? SceneSnapshotId, string Selector, SceneDocumentKind? Kind = null, bool IncludeChildren = false, bool IncludeComponents = false, bool IncludeReferences = false, int Limit = 50);
public sealed record GameObjectQueryRequest(string? SceneSnapshotId, string Selector, bool IncludeChildren = false, bool IncludeComponents = false, bool IncludeReferences = false, int Limit = 50);
public sealed record PrefabQueryRequest(string? SceneSnapshotId, string Selector, bool IncludeObjects = false, bool IncludeComponents = false, bool IncludeReferences = false, int Limit = 50);
public sealed record ComponentQueryRequest(string? SceneSnapshotId, string Selector, bool IncludeReferences = false, bool IncludeCode = false, int Limit = 50);
```

## File Structure

New projects:

- `src/S1Atlas.Application/` — shared read-only composition + Schedule I build-authority resolver + MCP-neutral result envelope types. Referenced by both `S1Atlas.Cli` and `S1Atlas.Mcp`.
  - `Authority/InstalledBuildAuthority.cs` — authority result record + status enum.
  - `Authority/InstalledBuildAuthorityResolver.cs` — the shared resolver.
  - `Composition/ReadOnlyQueryServices.cs` — bundle of read-only services (query/diff/scene/authority) built from a read-only repository.
  - `Envelope/*.cs` — `ToolEnvelope`, `BuildContext`, `ProvenanceEntry`, `ToolStatus`, `ProvenanceClassification`, `ToolError`.
- `src/S1Atlas.Mcp/` — `net8.0` executable stdio host.
  - `Program.cs` — `mcp serve` command + hosted stdio server + stderr logging.
  - `McpServerComposition.cs` — DI registration of read-only services (no write/network/process services).
  - `Tools/CodeSymbolTools.cs`, `Tools/CompareTools.cs`, `Tools/BuildEnvironmentTools.cs`, `Tools/SceneTools.cs` — `[McpServerToolType]` classes.
  - `Mapping/EnvelopeMapper.cs` — maps service results → `ToolEnvelope`.
- `src/S1Atlas.Storage/Sqlite/ReadOnlySqliteConnectionFactory.cs` — read-only open mode.
- `src/S1Atlas.Storage/Sqlite/ReadOnlySqliteAtlasRepository.cs` — read-only repository adapter (throws on mutations).

Modified:

- `src/S1Atlas.Indexing/Query/IndexQueryService.cs` — add index-scoped public query methods.
- `src/S1Atlas.Indexing/Diff/BuildDiffService.cs` — add `DiffSymbolAsync`.
- `src/S1Atlas.Cli/Commands/IndexQueryCommandFactory.cs` (+ `SearchCommand`/`TypeCommand`/`MethodCommand`/`RefsCommand`/`CallersCommand`/`CalleesCommand`/`SourceCommand`) — route Schedule I Installed through the shared authority path; add `--build`.
- `S1Atlas.sln` — add `S1Atlas.Application`, `S1Atlas.Mcp`, `S1Atlas.Mcp.Tests`.
- `README.md` — MCP command, tools, trust boundary, milestone status.

New test project:

- `tests/S1Atlas.Mcp.Tests/` — unit + host integration coverage.

New tests in existing projects:

- `tests/S1Atlas.Indexing.Tests/` — index-scoped query methods, `DiffSymbolAsync`, authority resolver.
- `tests/S1Atlas.Storage.Tests/` — read-only open mode.
- `tests/S1Atlas.IntegrationTests/` — parity + no-mutation cases.

---

## Task 1: Read-only SQLite open mode and repository adapter

**Files:**
- Create: `src/S1Atlas.Storage/Sqlite/ReadOnlySqliteConnectionFactory.cs`
- Create: `src/S1Atlas.Storage/Sqlite/ReadOnlySqliteAtlasRepository.cs`
- Test: `tests/S1Atlas.Storage.Tests/ReadOnlySqliteAtlasRepositoryTests.cs`

**Interfaces:**
- Consumes: existing `SqliteAtlasRepository` (for seeding fixtures in tests) and the read interfaces `IAtlasRepository`, `IIndexRepository`, `ISceneRepository`, `IValidatedExtractionRepository`.
- Produces:
  - `ReadOnlySqliteConnectionFactory(string databasePath)` with `SqliteConnection Open()` that opens `Mode=ReadOnly`, throws `FileNotFoundException` when the file is absent, and never creates the file/dirs or runs migrations.
  - `ReadOnlySqliteAtlasRepository` implementing the four read interfaces above, delegating reads and throwing `InvalidOperationException("S1Atlas MCP is read-only.")` from every mutation member (`InitializeAsync`, `SaveSnapshotAsync`, and every `Save*/Commit*/Set*/Clear*/Delete*/Link*` member).

- [ ] **Step 1: Write the failing test for missing-database rejection**

```csharp
using Microsoft.Data.Sqlite;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests;

public sealed class ReadOnlySqliteAtlasRepositoryTests
{
    [Fact]
    public void Open_MissingDatabase_ThrowsAndCreatesNothing()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var dbPath = Path.Combine(dir, "atlas.db");

        var factory = new ReadOnlySqliteConnectionFactory(dbPath);

        Assert.Throws<FileNotFoundException>(() => factory.Open());
        Assert.False(File.Exists(dbPath), "read-only open must not create the database");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/S1Atlas.Storage.Tests --filter ReadOnlySqliteAtlasRepositoryTests`
Expected: FAIL — `ReadOnlySqliteConnectionFactory` does not exist.

- [ ] **Step 3: Implement `ReadOnlySqliteConnectionFactory`**

```csharp
using Microsoft.Data.Sqlite;

namespace S1Atlas.Storage.Sqlite;

public sealed class ReadOnlySqliteConnectionFactory
{
    private readonly string _databasePath;

    public ReadOnlySqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public SqliteConnection Open()
    {
        if (!File.Exists(_databasePath))
            throw new FileNotFoundException("The Atlas database was not found.", _databasePath);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/S1Atlas.Storage.Tests --filter ReadOnlySqliteAtlasRepositoryTests`
Expected: PASS.

- [ ] **Step 5: Add a mutation-rejection test and a read-passthrough test**

```csharp
    [Fact]
    public async Task Reads_SeededBuilds_AndRejectsMutations()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var dbPath = Path.Combine(dir, "atlas.db");
        var backups = Directory.CreateDirectory(Path.Combine(dir, "backups")).FullName;

        // Seed with the writable repository.
        var writable = new SqliteAtlasRepository(dbPath, backups);
        await writable.InitializeAsync(CancellationToken.None);
        await StorageTestData.SeedCurrentScheduleIBuildAsync(writable); // helper below

        var readOnly = new ReadOnlySqliteAtlasRepository(new ReadOnlySqliteConnectionFactory(dbPath));

        var builds = await readOnly.ListBuildsAsync(CancellationToken.None);
        Assert.NotEmpty(builds);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => readOnly.InitializeAsync(CancellationToken.None));
    }
```

Add a minimal `StorageTestData.SeedCurrentScheduleIBuildAsync` helper in `tests/S1Atlas.Storage.Tests/StorageTestData.cs` that saves one `EnvironmentSnapshot` through the writable repository. If an equivalent seed helper already exists in the Storage tests, reuse it instead of adding a duplicate.

- [ ] **Step 6: Implement `ReadOnlySqliteAtlasRepository`**

Implement the four read interfaces by executing the same SQL the writable partial classes use, but against a connection from `ReadOnlySqliteConnectionFactory`. Every mutation member throws:

```csharp
namespace S1Atlas.Storage.Sqlite;

public sealed class ReadOnlySqliteAtlasRepository :
    IAtlasRepository, IIndexRepository, ISceneRepository, IValidatedExtractionRepository
{
    private const string ReadOnlyMessage = "S1Atlas MCP is read-only.";
    private readonly ReadOnlySqliteConnectionFactory _factory;

    public ReadOnlySqliteAtlasRepository(ReadOnlySqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    // --- reads: open a fresh read-only connection per call and run the same
    //     SELECT statements used by SqliteAtlasRepository's partial read paths.
    //     Prefer extracting the existing read SQL into shared internal helpers
    //     callable with an SqliteConnection so both repositories share one query. ---

    public Task InitializeAsync(CancellationToken ct) => throw new InvalidOperationException(ReadOnlyMessage);
    public Task SaveSnapshotAsync(EnvironmentSnapshot snapshot, CancellationToken ct) => throw new InvalidOperationException(ReadOnlyMessage);
    // …every Save*/Commit*/Set*/Clear*/Delete*/Link* member on the four interfaces throws InvalidOperationException(ReadOnlyMessage).
}
```

**Implementation note (real, not a placeholder):** to avoid duplicating SQL, refactor `SqliteAtlasRepository`'s read methods so each SELECT body lives in an `internal static` helper taking an open `SqliteConnection`; the writable repository calls it with its read-write connection and `ReadOnlySqliteAtlasRepository` calls it with the read-only connection. Enumerate the mutation members to throw by opening each interface file under `src/S1Atlas.Core/Storage/` and rejecting every non-`Get*/List*/Count*/Search*` member. Verify completeness by compiling: the interfaces force every member to be implemented.

- [ ] **Step 7: Run the Storage tests**

Run: `dotnet test tests/S1Atlas.Storage.Tests`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/S1Atlas.Storage/Sqlite/ReadOnlySqlite*.cs tests/S1Atlas.Storage.Tests
git commit -m "feat: add read-only SQLite open mode and repository adapter"
```

---

## Task 2: `S1Atlas.Application` library + Schedule I build-authority resolver

**Files:**
- Create: `src/S1Atlas.Application/S1Atlas.Application.csproj`
- Create: `src/S1Atlas.Application/Authority/InstalledBuildAuthority.cs`
- Create: `src/S1Atlas.Application/Authority/InstalledBuildAuthorityResolver.cs`
- Modify: `S1Atlas.sln` (add project)
- Test: `tests/S1Atlas.Indexing.Tests/InstalledBuildAuthorityResolverTests.cs`

**Interfaces:**
- Consumes: `PreferredVerifiedExtractionResolver`, `IAtlasRepository`, `IIndexRepository`, `IValidatedExtractionRepository`.
- Produces:
  - `enum InstalledBuildAuthorityStatus { Resolved, NoCurrentBuild, BuildNotFound, NoPreferredVerifiedExtraction, ExtractionIntegrityFailure, NoCompletedIndex, IndexBuildMismatch }`
  - `sealed record InstalledBuildAuthority(InstalledBuildAuthorityStatus Status, string? RequestedBuildId, string? ResolvedBuildId, string? ExtractionId, string? IndexId, IndexRunRecord? IndexRun, string? Message)`
  - `InstalledBuildAuthorityResolver(PreferredVerifiedExtractionResolver preferredResolver, IAtlasRepository atlasRepository, IIndexRepository indexRepository, IValidatedExtractionRepository validatedRepository)` with `Task<InstalledBuildAuthority> ResolveAsync(string? requestedBuildId, CancellationToken ct)`.

`S1Atlas.Application.csproj` references `S1Atlas.Core`, `S1Atlas.Indexing`, and `S1Atlas.Extraction`.

- [ ] **Step 1: Write the failing test for the no-current-build branch**

```csharp
using S1Atlas.Application.Authority;
using Xunit;

namespace S1Atlas.Indexing.Tests;

public sealed class InstalledBuildAuthorityResolverTests
{
    [Fact]
    public async Task Resolve_NoCurrentSnapshot_ReturnsNoCurrentBuild()
    {
        var harness = AuthorityHarness.Empty(); // builder below seeds an empty in-memory Atlas
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync(requestedBuildId: null, CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.NoCurrentBuild, result.Status);
        Assert.Null(result.IndexId);
    }
}
```

`AuthorityHarness` is a test helper (add it in `tests/S1Atlas.Indexing.Tests/AuthorityHarness.cs`) that builds a temp data root + writable `SqliteAtlasRepository`, exposes seed methods (`SeedCurrentBuild`, `SeedPreferredVerifiedExtraction`, `SeedCompletedInstalledIndex`, `SeedCorruptedPreference`), and a `CreateResolver()` returning an `InstalledBuildAuthorityResolver` wired to a real `PreferredVerifiedExtractionResolver` and `ValidatedExtractionIntegrityVerifier`. Reuse the existing indexing-test fixtures/helpers for extraction + index seeding where they already exist.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/S1Atlas.Indexing.Tests --filter InstalledBuildAuthorityResolverTests`
Expected: FAIL — `InstalledBuildAuthorityResolver` does not exist.

- [ ] **Step 3: Implement the authority records**

```csharp
using S1Atlas.Core.Storage;

namespace S1Atlas.Application.Authority;

public enum InstalledBuildAuthorityStatus
{
    Resolved,
    NoCurrentBuild,
    BuildNotFound,
    NoPreferredVerifiedExtraction,
    ExtractionIntegrityFailure,
    NoCompletedIndex,
    IndexBuildMismatch
}

public sealed record InstalledBuildAuthority(
    InstalledBuildAuthorityStatus Status,
    string? RequestedBuildId,
    string? ResolvedBuildId,
    string? ExtractionId,
    string? IndexId,
    IndexRunRecord? IndexRun,
    string? Message);
```

- [ ] **Step 4: Implement the resolver**

```csharp
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Authority;

namespace S1Atlas.Application.Authority;

public sealed class InstalledBuildAuthorityResolver
{
    private readonly PreferredVerifiedExtractionResolver _preferredResolver;
    private readonly IAtlasRepository _atlas;
    private readonly IIndexRepository _index;
    private readonly IValidatedExtractionRepository _validated;

    public InstalledBuildAuthorityResolver(
        PreferredVerifiedExtractionResolver preferredResolver,
        IAtlasRepository atlasRepository,
        IIndexRepository indexRepository,
        IValidatedExtractionRepository validatedRepository)
    {
        _preferredResolver = preferredResolver;
        _atlas = atlasRepository;
        _index = indexRepository;
        _validated = validatedRepository;
    }

    public async Task<InstalledBuildAuthority> ResolveAsync(
        string? requestedBuildId, CancellationToken ct)
    {
        string resolvedBuildId;
        if (string.IsNullOrWhiteSpace(requestedBuildId))
        {
            var current = await _atlas.GetCurrentSnapshotAsync(ct);
            if (current is null)
                return Fail(InstalledBuildAuthorityStatus.NoCurrentBuild, requestedBuildId, null,
                    "No current environment snapshot is available.");
            resolvedBuildId = current.Build.BuildId;
        }
        else
        {
            var builds = await _atlas.ListBuildsAsync(ct);
            if (!builds.Any(b => string.Equals(b.BuildId, requestedBuildId, StringComparison.Ordinal)))
                return Fail(InstalledBuildAuthorityStatus.BuildNotFound, requestedBuildId, null,
                    "The requested build is not indexed.");
            resolvedBuildId = requestedBuildId;
        }

        var preferred = await _preferredResolver.ResolveAsync(resolvedBuildId, ct);
        if (preferred is null)
        {
            // Distinguish "no preference" from "preference present but integrity failed".
            var preferenceRow = await _validated.GetPreferredExtractionAsync(resolvedBuildId, ct);
            return preferenceRow is null
                ? Fail(InstalledBuildAuthorityStatus.NoPreferredVerifiedExtraction, requestedBuildId, resolvedBuildId,
                    "No preferred verified extraction exists for the build.")
                : Fail(InstalledBuildAuthorityStatus.ExtractionIntegrityFailure, requestedBuildId, resolvedBuildId,
                    "The preferred extraction failed integrity verification.");
        }

        if (!string.Equals(preferred.Extraction.BuildId, resolvedBuildId, StringComparison.Ordinal))
            return Fail(InstalledBuildAuthorityStatus.IndexBuildMismatch, requestedBuildId, resolvedBuildId,
                "The preferred extraction does not belong to the resolved build.");

        var extractionId = preferred.Extraction.ExtractionId;
        var run = await _index.GetLatestCompletedIndexBySourceIdentityAsync(
            CodebaseKind.ScheduleI, CodeChannel.Installed, extractionId, ct);
        if (run is null)
            return Fail(InstalledBuildAuthorityStatus.NoCompletedIndex, requestedBuildId, resolvedBuildId,
                "No completed Schedule I Installed index exists for the verified extraction.");

        return new InstalledBuildAuthority(
            InstalledBuildAuthorityStatus.Resolved, requestedBuildId, resolvedBuildId,
            extractionId, run.IndexId, run, null);
    }

    private static InstalledBuildAuthority Fail(
        InstalledBuildAuthorityStatus status, string? requested, string? resolved, string message) =>
        new(status, requested, resolved, null, null, null, message);
}
```

- [ ] **Step 5: Run the failing test to verify it passes**

Run: `dotnet test tests/S1Atlas.Indexing.Tests --filter InstalledBuildAuthorityResolverTests`
Expected: PASS.

- [ ] **Step 6: Add tests for every remaining branch**

Add one test each (fully seeded via `AuthorityHarness`) asserting the exact status:
`Resolve_ExplicitUnknownBuild_ReturnsBuildNotFound`,
`Resolve_NoPreference_ReturnsNoPreferredVerifiedExtraction`,
`Resolve_CorruptedPreferredExtraction_ReturnsExtractionIntegrityFailure`,
`Resolve_PreferredButNoIndex_ReturnsNoCompletedIndex`,
`Resolve_HealthyBuild_ReturnsResolvedWithIndexId`.

```csharp
    [Fact]
    public async Task Resolve_HealthyBuild_ReturnsResolvedWithIndexId()
    {
        var harness = AuthorityHarness.Empty();
        var seeded = await harness.SeedHealthyInstalledBuildAsync(); // seeds build+preferred+verified extraction+completed index
        var resolver = harness.CreateResolver();

        var result = await resolver.ResolveAsync(requestedBuildId: null, CancellationToken.None);

        Assert.Equal(InstalledBuildAuthorityStatus.Resolved, result.Status);
        Assert.Equal(seeded.BuildId, result.ResolvedBuildId);
        Assert.Equal(seeded.ExtractionId, result.ExtractionId);
        Assert.Equal(seeded.IndexId, result.IndexId);
    }
```

- [ ] **Step 7: Run the full resolver test class**

Run: `dotnet test tests/S1Atlas.Indexing.Tests --filter InstalledBuildAuthorityResolverTests`
Expected: PASS (all branches).

- [ ] **Step 8: Commit**

```bash
git add src/S1Atlas.Application tests/S1Atlas.Indexing.Tests S1Atlas.sln
git commit -m "feat: add shared Schedule I build-authority resolver"
```

---

## Task 3: Index-scoped query methods on `IndexQueryService`

**Files:**
- Modify: `src/S1Atlas.Indexing/Query/IndexQueryService.cs`
- Test: `tests/S1Atlas.Indexing.Tests/IndexScopedQueryTests.cs`

**Interfaces:**
- Consumes: `IndexRunRecord` (the resolved run from Task 2), `IIndexRepository`.
- Produces new public methods that run the existing resolution/formatting logic against **one explicit run** instead of selecting "latest per channel":
  - `Task<SymbolSearchResult> SearchInIndexAsync(IndexRunRecord run, CodebaseKind codebase, CodeChannel channel, string query, int limit, SymbolKind? kind, CancellationToken ct)`
  - `Task<IReadOnlyList<SymbolQueryResult>> FindInIndexAsync(IndexRunRecord run, CodebaseKind codebase, CodeChannel channel, string query, SymbolKind kind, int limit, CancellationToken ct)`
  - `Task<RelationshipQuerySetResult> RefsInIndexAsync(IndexRunRecord run, CodebaseKind codebase, CodeChannel channel, string selector, int limit, CancellationToken ct)`
  - `Task<RelationshipQuerySetResult> CallersInIndexAsync(IndexRunRecord run, CodebaseKind codebase, CodeChannel channel, string selector, int limit, CancellationToken ct)`
  - `Task<RelationshipQuerySetResult> CalleesInIndexAsync(IndexRunRecord run, CodebaseKind codebase, CodeChannel channel, string selector, int limit, CancellationToken ct)`
  - `Task<SourceSnippetResolutionResult> SourceInIndexAsync(IndexRunRecord run, CodebaseKind codebase, CodeChannel channel, string selector, int context, CancellationToken ct)`

**Implementation note (real, not a placeholder):** the current private `ResolveAcrossChannelsAsync(selector, options, ct)` loops channels, calls `GetLatestCompletedIndexAsync` per channel, and resolves the symbol; `SearchAsync` does the same for search. Extract the single-run bodies into private helpers `ResolveInRunAsync(IndexRunRecord run, CodebaseKind codebase, CodeChannel channel, string selector, ct)` and `SearchInRunAsync(IndexRunRecord run, CodebaseKind codebase, CodeChannel channel, string query, int limit, SymbolKind? kind, ct)`. The existing cross-channel methods then iterate resolved runs and call these helpers (behavior preserved). The new public `*InIndexAsync` methods call the single-run helpers directly with the caller-supplied run. Source integrity is unchanged: `SourceInIndexAsync` reuses the existing hash-verifying `SourceSnippetReader` path and still throws `InvalidDataException` on a hash mismatch or missing file.

- [ ] **Step 1: Write the failing test**

```csharp
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Query;
using Xunit;

namespace S1Atlas.Indexing.Tests;

public sealed class IndexScopedQueryTests
{
    [Fact]
    public async Task SearchInIndex_TargetsExactRun_NotLatest()
    {
        var fixture = await IndexQueryFixture.WithTwoCompletedInstalledIndexesAsync();
        // fixture.OlderRun contains a symbol "Alpha"; fixture.NewerRun does not.
        var service = new IndexQueryService(fixture.Repository, fixture.DataRoot);

        var result = await service.SearchInIndexAsync(
            fixture.OlderRun, CodebaseKind.ScheduleI, CodeChannel.Installed,
            "Alpha", limit: 50, kind: null, CancellationToken.None);

        Assert.Equal(SymbolResolutionStatus.Resolved is var _ ? result.ResolutionStatus : null, result.ResolutionStatus);
        Assert.Contains(result.Results, r => r.QualifiedName.Contains("Alpha", StringComparison.Ordinal));
        Assert.All(result.Results, r => Assert.Equal(fixture.OlderRun.IndexId, r.IndexId));
    }
}
```

`IndexQueryFixture.WithTwoCompletedInstalledIndexesAsync` seeds two completed Installed index runs whose symbol sets differ, so the test proves index-scoped querying binds to the supplied run rather than the newest. Reuse existing indexing-test seeding utilities.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/S1Atlas.Indexing.Tests --filter IndexScopedQueryTests`
Expected: FAIL — `SearchInIndexAsync` does not exist.

- [ ] **Step 3: Extract single-run helpers and add the public methods**

Refactor per the implementation note. Add the six public methods; each validates `limit > 0` (throw `ArgumentOutOfRangeException`) and delegates to the single-run helper. Keep the existing public `SearchAsync`/`FindAsync`/`RefsAsync`/`CallersAsync`/`CalleesAsync`/`SourceAsync` intact so the API-codebase CLI path is unaffected.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/S1Atlas.Indexing.Tests --filter IndexScopedQueryTests`
Expected: PASS.

- [ ] **Step 5: Add source + relationship index-scoped tests**

Add `SourceInIndex_VerifiesHashAndReturnsSnippet`, `SourceInIndex_TamperedFile_ThrowsInvalidData`, and `RefsInIndex_ReturnsEdgesForResolvedSymbol`, each seeded against a known run.

- [ ] **Step 6: Run the full indexing test project (regression check)**

Run: `dotnet test tests/S1Atlas.Indexing.Tests`
Expected: PASS — existing cross-channel query tests still green.

- [ ] **Step 7: Commit**

```bash
git add src/S1Atlas.Indexing/Query/IndexQueryService.cs tests/S1Atlas.Indexing.Tests/IndexScopedQueryTests.cs
git commit -m "feat: add index-scoped query methods to IndexQueryService"
```

---

## Task 4: Symbol-scoped diff on `BuildDiffService`

**Files:**
- Modify: `src/S1Atlas.Indexing/Diff/BuildDiffService.cs`
- Test: `tests/S1Atlas.Indexing.Tests/DiffSymbolTests.cs`

**Interfaces:**
- Consumes: `IIndexRepository`, the private `Classify`/`ClassifyMethodBody` logic already in `BuildDiffService`.
- Produces: `Task<SymbolDiff?> DiffSymbolAsync(string indexIdA, string indexIdB, string codebase, string channel, string canonicalKey, CancellationToken ct)` returning the single `SymbolDiff` for the symbol identified by `canonicalKey`, or `null` when the key is absent from **both** indexes.

**Implementation note (real, not a placeholder):** `DiffAsync` already loads both indexes into in-memory maps keyed by canonical key and calls the private `Classify`. Add `DiffSymbolAsync` that loads the same per-symbol data for one canonical key from each index (reusing the private classification helpers), returning one `SymbolDiff`. Signature changes are carried in `SignatureBefore`/`SignatureAfter` — there is no separate `SignatureChanged` classification, by design (matches the shipped `DiffClassification` enum).

- [ ] **Step 1: Write the failing test for an unchanged symbol**

```csharp
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Diff;
using Xunit;

namespace S1Atlas.Indexing.Tests;

public sealed class DiffSymbolTests
{
    [Fact]
    public async Task DiffSymbol_IdenticalSymbol_ClassifiesUnchanged()
    {
        var fixture = await BuildDiffFixture.WithIdenticalSymbolAsync("N.T.M()");
        var service = new BuildDiffService(fixture.Repository);

        var diff = await service.DiffSymbolAsync(
            fixture.IndexIdA, fixture.IndexIdB, "ScheduleI", "Installed", "N.T.M()", CancellationToken.None);

        Assert.NotNull(diff);
        Assert.Equal(DiffClassification.Unchanged, diff!.Classification);
    }
}
```

Reuse the diff milestone's existing test fixtures/builders from `tests/S1Atlas.Indexing.Tests` (the build-diffing milestone added them); add `BuildDiffFixture` helpers only if none already produce two seeded indexes.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/S1Atlas.Indexing.Tests --filter DiffSymbolTests`
Expected: FAIL — `DiffSymbolAsync` does not exist.

- [ ] **Step 3: Implement `DiffSymbolAsync`**

Add the method using the existing private classification helpers. Return `null` when the canonical key exists in neither index; otherwise build one `SymbolDiff` (Added when only in B, Removed when only in A, else the body/relationship classification).

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/S1Atlas.Indexing.Tests --filter DiffSymbolTests`
Expected: PASS.

- [ ] **Step 5: Add classification-coverage tests**

Add `DiffSymbol_OnlyInB_ClassifiesAdded`, `DiffSymbol_OnlyInA_ClassifiesRemoved`, `DiffSymbol_ChangedBody_ClassifiesMethodBodyChanged`, `DiffSymbol_ChangedEdges_ClassifiesRelationshipsChanged`, `DiffSymbol_AbsentInBoth_ReturnsNull`.

- [ ] **Step 6: Run the class and commit**

Run: `dotnet test tests/S1Atlas.Indexing.Tests --filter DiffSymbolTests`
Expected: PASS.

```bash
git add src/S1Atlas.Indexing/Diff/BuildDiffService.cs tests/S1Atlas.Indexing.Tests/DiffSymbolTests.cs
git commit -m "feat: add symbol-scoped diff to BuildDiffService"
```

---

## Task 5: Response envelope, provenance, and error types

**Files:**
- Create: `src/S1Atlas.Application/Envelope/ToolEnvelope.cs`
- Test: `tests/S1Atlas.Mcp.Tests/EnvelopeTests.cs` (create the test project in this task)
- Create: `tests/S1Atlas.Mcp.Tests/S1Atlas.Mcp.Tests.csproj`
- Modify: `S1Atlas.sln`

**Interfaces:**
- Produces:
  - `enum ToolStatus { Resolved, NotFound, Ambiguous, Unavailable, Invalid }`
  - `enum ProvenanceClassification { Fact, Derived, Interpretation }`
  - `sealed record BuildContext(string? RequestedBuildId, string? ResolvedBuildId, string? ExtractionId, string? IndexId, string Codebase, string Channel, bool IntegrityVerified)`
  - `sealed record ProvenanceEntry(ProvenanceClassification Classification, string Source, string? BuildId, string? ExtractionId, string? IndexId)`
  - `sealed record ToolError(string Code, string Message)`
  - `sealed record ToolEnvelope<T>(ToolStatus Status, BuildContext? Build, T? Data, IReadOnlyList<object> Candidates, IReadOnlyList<ProvenanceEntry> Provenance, ToolError? Error) where T : class` with static factory helpers `Resolved`, `NotFound`, `Ambiguous`, `Unavailable`, `Invalid`.

- [ ] **Step 1: Create the `S1Atlas.Mcp.Tests` project referencing `S1Atlas.Application`, add to solution**

```bash
dotnet new xunit -o tests/S1Atlas.Mcp.Tests -n S1Atlas.Mcp.Tests
dotnet sln S1Atlas.sln add tests/S1Atlas.Mcp.Tests/S1Atlas.Mcp.Tests.csproj
dotnet add tests/S1Atlas.Mcp.Tests reference src/S1Atlas.Application/S1Atlas.Application.csproj
```

- [ ] **Step 2: Write the failing test**

```csharp
using S1Atlas.Application.Envelope;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class EnvelopeTests
{
    [Fact]
    public void Unavailable_CarriesErrorAndNoData()
    {
        var envelope = ToolEnvelope<string>.Unavailable(
            new ToolError("NoCurrentBuild", "No current build."), build: null);

        Assert.Equal(ToolStatus.Unavailable, envelope.Status);
        Assert.Null(envelope.Data);
        Assert.Equal("NoCurrentBuild", envelope.Error!.Code);
    }

    [Fact]
    public void Resolved_EmptyResultsStillReportsResolvedOnlyWhenIntended()
    {
        var build = new BuildContext(null, "b", "e", "i", "ScheduleI", "Installed", true);
        var envelope = ToolEnvelope<string>.NotFound(build,
            new ProvenanceEntry(ProvenanceClassification.Derived, "installed-index", "b", "e", "i"));

        Assert.Equal(ToolStatus.NotFound, envelope.Status);
        Assert.Null(envelope.Data);
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test tests/S1Atlas.Mcp.Tests --filter EnvelopeTests`
Expected: FAIL — envelope types do not exist.

- [ ] **Step 4: Implement the envelope types**

Implement the records/enums and the static factories in `ToolEnvelope.cs`. Each factory sets `Status`, fills `Provenance` from supplied entries, and leaves `Data`/`Candidates` empty unless provided.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/S1Atlas.Mcp.Tests --filter EnvelopeTests`
Expected: PASS.

- [ ] **Step 6: Add an authority→envelope mapping helper + test**

Add `AuthorityEnvelope.From(InstalledBuildAuthority)` in `src/S1Atlas.Application/Envelope/AuthorityEnvelope.cs` mapping each `InstalledBuildAuthorityStatus` to the correct `ToolStatus` + `ToolError.Code` (e.g. `NoCurrentBuild`→`Unavailable`, `BuildNotFound`→`Invalid`, `ExtractionIntegrityFailure`→`Unavailable`). Test one representative mapping per status.

- [ ] **Step 7: Commit**

```bash
git add src/S1Atlas.Application/Envelope tests/S1Atlas.Mcp.Tests S1Atlas.sln
git commit -m "feat: add MCP response envelope and provenance types"
```

---

## Task 6: `S1Atlas.Mcp` host — `mcp serve` over stdio

**Files:**
- Create: `src/S1Atlas.Mcp/S1Atlas.Mcp.csproj`
- Create: `src/S1Atlas.Mcp/Program.cs`
- Create: `src/S1Atlas.Mcp/McpServerComposition.cs`
- Modify: `S1Atlas.sln`
- Test: `tests/S1Atlas.Mcp.Tests/HostCompositionTests.cs`

**Interfaces:**
- Consumes: `ReadOnlySqliteAtlasRepository`, `InstalledBuildAuthorityResolver`, `IndexQueryService`, `BuildDiffService`, `SceneQueryService`, and `AtlasPaths` for the data-root/`S1ATLAS_HOME` resolution (reuse the CLI's existing path resolution).
- Produces: `McpServerComposition.BuildReadOnlyServices(string dataDirectory)` returning a bundle exposing the authority resolver + query/diff/scene services over a read-only repository; `Program` wiring `mcp serve`.

`S1Atlas.Mcp.csproj` references `S1Atlas.Application`, `S1Atlas.Indexing`, `S1Atlas.Storage`, `S1Atlas.Core`, and adds `ModelContextProtocol` + `Microsoft.Extensions.Hosting`.

- [ ] **Step 1: Write the failing test for read-only composition**

```csharp
using S1Atlas.Mcp;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class HostCompositionTests
{
    [Fact]
    public async Task Composition_OverSeededAtlas_ResolvesAuthorityReadOnly()
    {
        var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync(); // temp data root
        var services = McpServerComposition.BuildReadOnlyServices(atlas.DataRoot);

        var authority = await services.AuthorityResolver.ResolveAsync(null, CancellationToken.None);

        Assert.Equal("Resolved", authority.Status.ToString());
        Assert.False(File.Exists(Path.Combine(atlas.DataRoot, "atlas.db.tmp")),
            "composition must not create scratch files");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/S1Atlas.Mcp.Tests --filter HostCompositionTests`
Expected: FAIL — project/type missing.

- [ ] **Step 3: Implement `McpServerComposition`**

Build the read-only repository from `ReadOnlySqliteConnectionFactory(AtlasPaths.DatabasePath)`, construct `PreferredVerifiedExtractionResolver` (with `ValidatedExtractionIntegrityVerifier`), the `InstalledBuildAuthorityResolver`, `IndexQueryService(repo, dataRoot)`, `BuildDiffService(repo)`, and `SceneQueryService(repo, repo)`. Construct **no** HTTP client, extractor, locator, or installer.

- [ ] **Step 4: Implement `Program.cs` with the hosted stdio server**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args is not ["mcp", "serve", ..])
{
    await Console.Error.WriteLineAsync("Usage: S1Atlas.Mcp mcp serve");
    return 2;
}

var dataDirectory = AtlasHome.Resolve(); // reuse CLI S1ATLAS_HOME resolution
var services = McpServerComposition.BuildReadOnlyServices(dataDirectory);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace); // stderr only
builder.Services.AddSingleton(services);
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;
```

**Implementation note:** confirm the exact `ModelContextProtocol` package version during Step 3 with `dotnet add src/S1Atlas.Mcp package ModelContextProtocol` and pin the resolved version in the csproj. If the SDK's current registration API differs from `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()`, follow the package's README for the hosted stdio pattern; the tool-class attributes in later tasks (`[McpServerToolType]`, `[McpServerTool]`) are the stable surface.

- [ ] **Step 5: Run the composition test to verify it passes**

Run: `dotnet test tests/S1Atlas.Mcp.Tests --filter HostCompositionTests`
Expected: PASS.

- [ ] **Step 6: Add a tool-registration guard test**

```csharp
    [Fact]
    public void RegisteredTools_ContainNoMutationVerbs()
    {
        var toolNames = McpToolCatalog.DiscoverToolNames(); // reflects [McpServerTool] methods in the Mcp assembly
        Assert.All(toolNames, name =>
            Assert.DoesNotContain(name, new[] { "extract", "promote", "cleanup", "install", "scan", "index", "sync", "delete", "write", "set" }
                .Where(verb => name.Contains(verb, StringComparison.OrdinalIgnoreCase))));
    }
```

Add `McpToolCatalog.DiscoverToolNames()` in `src/S1Atlas.Mcp/McpToolCatalog.cs` reflecting over `[McpServerTool]`-attributed methods. (This test starts trivially green with no tools and stays green as read-only tools are added in Tasks 7–10.)

- [ ] **Step 7: Commit**

```bash
git add src/S1Atlas.Mcp tests/S1Atlas.Mcp.Tests/HostCompositionTests.cs S1Atlas.sln
git commit -m "feat: add S1Atlas.Mcp stdio host and read-only composition"
```

---

## Task 7: Code-symbol tools

**Files:**
- Create: `src/S1Atlas.Mcp/Tools/CodeSymbolTools.cs`
- Create: `src/S1Atlas.Mcp/Mapping/EnvelopeMapper.cs`
- Test: `tests/S1Atlas.Mcp.Tests/CodeSymbolToolTests.cs`

**Interfaces:**
- Consumes: the composition bundle (authority resolver + `IndexQueryService` index-scoped methods), envelope types.
- Produces `[McpServerTool]` methods: `search_symbols`, `get_type`, `get_method`, `get_source`, `find_callers`, `find_references`, `find_related_types`. Each resolves authority first, then calls the matching `*InIndexAsync` method, then maps to `ToolEnvelope`.

**Shared pattern (used by every tool in Tasks 7–10):**

```csharp
// EnvelopeMapper.cs
public static async Task<ToolEnvelope<T>> WithAuthorityAsync<T>(
    InstalledBuildAuthorityResolver resolver, string? buildId, CancellationToken ct,
    Func<InstalledBuildAuthority, Task<ToolEnvelope<T>>> onResolved) where T : class
{
    var authority = await resolver.ResolveAsync(buildId, ct);
    if (authority.Status != InstalledBuildAuthorityStatus.Resolved)
        return AuthorityEnvelope.From<T>(authority);
    return await onResolved(authority);
}
```

- [ ] **Step 1: Write the failing test for `search_symbols`**

```csharp
using S1Atlas.Application.Envelope;
using Xunit;

namespace S1Atlas.Mcp.Tests;

public sealed class CodeSymbolToolTests
{
    [Fact]
    public async Task SearchSymbols_HealthyBuild_ResolvesAgainstPreferredIndex()
    {
        var atlas = await McpTestAtlas.SeedHealthyInstalledBuildAsync();
        var tools = McpTestHost.CodeSymbolTools(atlas.DataRoot);

        var envelope = await tools.SearchSymbolsAsync(query: atlas.KnownSymbolFragment,
            buildId: null, kind: null, limit: 50, CancellationToken.None);

        Assert.Equal(ToolStatus.Resolved, envelope.Status);
        Assert.Equal(atlas.IndexId, envelope.Build!.IndexId);
        Assert.All(envelope.Provenance, p => Assert.NotEqual(ProvenanceClassification.Interpretation, p.Classification));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/S1Atlas.Mcp.Tests --filter CodeSymbolToolTests`
Expected: FAIL — tool type missing.

- [ ] **Step 3: Implement the code-symbol tools**

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace S1Atlas.Mcp.Tools;

[McpServerToolType]
public sealed class CodeSymbolTools
{
    private readonly ReadOnlyQueryServices _services;
    public CodeSymbolTools(ReadOnlyQueryServices services) => _services = services;

    [McpServerTool, Description("Search the preferred, integrity-verified Schedule I code index for symbols.")]
    public Task<ToolEnvelope<SymbolSearchResult>> SearchSymbolsAsync(
        [Description("Case-insensitive symbol name fragment or qualified name.")] string query,
        [Description("Optional 64-char build ID; omitted resolves the current build.")] string? buildId,
        [Description("Optional symbol kind filter.")] string? kind,
        [Description("Max results (1-500, default 50).")] int limit,
        CancellationToken ct) =>
        EnvelopeMapper.WithAuthorityAsync<SymbolSearchResult>(_services.AuthorityResolver, buildId, ct,
            async authority =>
            {
                var parsedKind = ToolArguments.ParseKind(kind);
                var result = await _services.Index.SearchInIndexAsync(
                    authority.IndexRun!, CodebaseKind.ScheduleI, CodeChannel.Installed,
                    query, ToolArguments.BoundLimit(limit), parsedKind, ct);
                return EnvelopeMapper.FromSearch(authority, result);
            });

    // get_type, get_method → FindInIndexAsync(SymbolKind.Type/Method)
    // get_source → SourceInIndexAsync (catch InvalidDataException → SourceIntegrityFailure)
    // find_callers → CallersInIndexAsync; find_references → RefsInIndexAsync
    // find_related_types → RefsInIndexAsync then deterministic filter to
    //   Inherits, ImplementsInterface, FieldType, PropertyType, EventType, ParameterType, ReturnType.
}
```

Add `ToolArguments.BoundLimit` (reject `<= 0` → throw mapped to `Invalid`; clamp to 500) and `ToolArguments.ParseKind`. Add `EnvelopeMapper.FromSearch/FromFind/FromRelationships/FromSource` mapping each service result + its `SymbolResolutionStatus`/`SymbolResolutionResult` to the right `ToolStatus` (Resolved/NotFound/Ambiguous, with candidates on Ambiguous) and `DERIVED`/`FACT` provenance (`FACT` for the extracted symbol/source bytes, `DERIVED` for ranking/selection/relationship direction).

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/S1Atlas.Mcp.Tests --filter CodeSymbolToolTests`
Expected: PASS.

- [ ] **Step 5: Add tests for each remaining tool + failure states**

Add: `GetSource_ReturnsHashVerifiedSnippet`, `GetSource_TamperedFile_ReturnsSourceIntegrityFailure`, `GetMethod_AmbiguousSelector_ReturnsAmbiguousWithCandidates`, `GetType_UnknownSelector_ReturnsNotFound`, `FindRelatedTypes_FiltersToTypeRelations`, `FindCallers_PreservesCompletenessNotice`, `SearchSymbols_NoCurrentBuild_ReturnsUnavailable`.

- [ ] **Step 6: Run the class and commit**

Run: `dotnet test tests/S1Atlas.Mcp.Tests --filter CodeSymbolToolTests`
Expected: PASS.

```bash
git add src/S1Atlas.Mcp/Tools/CodeSymbolTools.cs src/S1Atlas.Mcp/Mapping/EnvelopeMapper.cs tests/S1Atlas.Mcp.Tests/CodeSymbolToolTests.cs
git commit -m "feat: add MCP code-symbol tools"
```

---

## Task 8: `compare_symbol` tool

**Files:**
- Create: `src/S1Atlas.Mcp/Tools/CompareTools.cs`
- Test: `tests/S1Atlas.Mcp.Tests/CompareToolTests.cs`

**Interfaces:**
- Consumes: `InstalledBuildAuthorityResolver` (resolved twice — once per build), `BuildDiffService.DiffSymbolAsync`.
- Produces `[McpServerTool] compare_symbol(selector, buildIdA, buildIdB)` requiring **two explicit** build IDs (neither defaults to current), returning `ToolEnvelope<SymbolDiff>` with both build contexts echoed.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task CompareSymbol_MissingBuildId_ReturnsInvalid()
{
    var atlas = await McpTestAtlas.SeedTwoInstalledBuildsAsync();
    var tools = McpTestHost.CompareTools(atlas.DataRoot);

    var envelope = await tools.CompareSymbolAsync(
        selector: "N.T.M()", buildIdA: atlas.BuildIdA, buildIdB: "", CancellationToken.None);

    Assert.Equal(ToolStatus.Invalid, envelope.Status);
    Assert.Equal("InvalidArguments", envelope.Error!.Code);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/S1Atlas.Mcp.Tests --filter CompareToolTests`
Expected: FAIL.

- [ ] **Step 3: Implement `compare_symbol`**

Reject blank/equal-to-current defaulting: if either `buildIdA`/`buildIdB` is null/whitespace → `Invalid`/`InvalidArguments`. Resolve authority for each build; if either is not `Resolved`, return its mapped failure. Call `DiffSymbolAsync(indexA, indexB, "ScheduleI", "Installed", selector, ct)`. `null` → `NotFound`. Provenance for the classification is `DERIVED`.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/S1Atlas.Mcp.Tests --filter CompareToolTests`
Expected: PASS.

- [ ] **Step 5: Add resolved-comparison tests**

Add `CompareSymbol_UnchangedSymbol_ReturnsUnchanged` and `CompareSymbol_BodyChanged_ReturnsMethodBodyChanged`, asserting both build contexts are populated.

- [ ] **Step 6: Commit**

```bash
git add src/S1Atlas.Mcp/Tools/CompareTools.cs tests/S1Atlas.Mcp.Tests/CompareToolTests.cs
git commit -m "feat: add MCP compare_symbol tool"
```

---

## Task 9: `list_builds` and `get_environment` tools

**Files:**
- Create: `src/S1Atlas.Mcp/Tools/BuildEnvironmentTools.cs`
- Test: `tests/S1Atlas.Mcp.Tests/BuildEnvironmentToolTests.cs`

**Interfaces:**
- Consumes: `IAtlasRepository.ListBuildsAsync`/`GetCurrentSnapshotAsync`, `IValidatedExtractionRepository.GetPreferredExtractionAsync`, `IIndexRepository.GetLatestCompletedIndexForBuildAsync`.
- Produces: `list_builds(limit?)` marking the current build and per-build availability of a preferred verified extraction and a completed Installed index; `get_environment(buildId?)`.

**Constraint:** `IAtlasRepository` has no per-historical-build environment read. `get_environment` with an explicit build returns environment facts only when that build equals the current snapshot's build; otherwise `ToolStatus.Unavailable` with code `NoMatchingEnvironmentSnapshot`. It never returns another build's environment.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task GetEnvironment_ExplicitNonCurrentBuild_ReturnsNoMatchingSnapshot()
{
    var atlas = await McpTestAtlas.SeedTwoInstalledBuildsAsync(); // BuildIdB is current
    var tools = McpTestHost.BuildEnvironmentTools(atlas.DataRoot);

    var envelope = await tools.GetEnvironmentAsync(buildId: atlas.BuildIdA, CancellationToken.None);

    Assert.Equal(ToolStatus.Unavailable, envelope.Status);
    Assert.Equal("NoMatchingEnvironmentSnapshot", envelope.Error!.Code);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/S1Atlas.Mcp.Tests --filter BuildEnvironmentToolTests`
Expected: FAIL.

- [ ] **Step 3: Implement both tools**

`list_builds`: read `ListBuildsAsync`, mark the `GetCurrentSnapshotAsync` build, and for each build set `hasPreferredVerifiedExtraction` (via authority resolver returning `Resolved`) and `hasCompletedIndex` (via `GetLatestCompletedIndexForBuildAsync`). `get_environment`: no build → current snapshot facts (`Resolved`); explicit build == current → its facts; else `Unavailable`/`NoMatchingEnvironmentSnapshot`.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/S1Atlas.Mcp.Tests --filter BuildEnvironmentToolTests`
Expected: PASS.

- [ ] **Step 5: Add `ListBuilds_MarksCurrentAndAvailability` and `GetEnvironment_NoBuild_ReturnsCurrent` tests, then commit**

```bash
git add src/S1Atlas.Mcp/Tools/BuildEnvironmentTools.cs tests/S1Atlas.Mcp.Tests/BuildEnvironmentToolTests.cs
git commit -m "feat: add MCP list_builds and get_environment tools"
```

---

## Task 10: Scene tools

**Files:**
- Create: `src/S1Atlas.Mcp/Tools/SceneTools.cs`
- Test: `tests/S1Atlas.Mcp.Tests/SceneToolTests.cs`

**Interfaces:**
- Consumes: `SceneQueryService`, `ISceneRepository.GetLatestCompletedSceneSnapshotAsync`/`GetCompletedSceneSnapshotAsync`, `InstalledBuildAuthorityResolver` (to resolve/validate the build).
- Produces: `list_scenes`, `get_scene`, `get_gameobject`, `get_prefab`, `get_component`. Scene document tools take a `SceneSnapshotId`, so the tool layer resolves it: build → `GetLatestCompletedSceneSnapshotAsync(buildId)`; when a `sceneSnapshotId` is supplied with a build, verify it belongs to that build (else `Invalid`/`SceneSnapshotNotFound` or mismatch).

- [ ] **Step 1: Write the failing test for snapshot/build mismatch**

```csharp
[Fact]
public async Task GetScene_SnapshotFromDifferentBuild_ReturnsInvalid()
{
    var atlas = await McpTestAtlas.SeedTwoSceneBuildsAsync();
    var tools = McpTestHost.SceneTools(atlas.DataRoot);

    var envelope = await tools.GetSceneAsync(
        selector: atlas.SceneNameB, buildId: atlas.BuildIdA, sceneSnapshotId: atlas.SceneSnapshotIdB,
        kind: null, includeChildren: false, includeComponents: false, includeReferences: false,
        limit: 50, CancellationToken.None);

    Assert.Equal(ToolStatus.Invalid, envelope.Status);
    Assert.Equal("SceneSnapshotNotFound", envelope.Error!.Code);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/S1Atlas.Mcp.Tests --filter SceneToolTests`
Expected: FAIL.

- [ ] **Step 3: Implement the scene tools**

Resolve the scene snapshot ID from the build (or validate a supplied one), build the matching `Scene*Request`, call the `SceneQueryService` method, and map `SceneQueryStatus` → `ToolStatus`, preserving partial recovery, unresolved references, bounded pages, containers, and the component→code handoff. Map `SceneQueryStatus` values for missing snapshot to `NoCompletedSceneIndex`/`SceneSnapshotNotFound`, and partial states to `PartialRecovery`/`UnresolvedSceneReference` as data-carrying `Resolved` results (partial recovery is a factual state, not a failure).

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/S1Atlas.Mcp.Tests --filter SceneToolTests`
Expected: PASS.

- [ ] **Step 5: Add `ListScenes_ReturnsBoundedPage`, `GetComponent_WithCode_ReturnsSymbolHandoff`, `GetScene_NoSceneIndex_ReturnsNoCompletedSceneIndex`, then commit**

```bash
git add src/S1Atlas.Mcp/Tools/SceneTools.cs tests/S1Atlas.Mcp.Tests/SceneToolTests.cs
git commit -m "feat: add MCP scene tools"
```

---

## Task 11: CLI parity — route Schedule I Installed queries through the shared authority path

**Files:**
- Modify: `src/S1Atlas.Cli/Commands/IndexQueryCommandFactory.cs`
- Modify: `src/S1Atlas.Cli/Commands/SourceCommand.cs`
- Modify: `src/S1Atlas.Cli/CliApplication.cs` (construct the authority resolver and pass it into the query commands)
- Test: `tests/S1Atlas.IntegrationTests/CliQueryParityTests.cs`

**Interfaces:**
- Consumes: `InstalledBuildAuthorityResolver`, `IndexQueryService` index-scoped methods.
- Produces: a `--build <id>` option on the Schedule I query commands; Schedule I Installed queries now resolve through the authority path and query the exact preferred-verified index. The S1API/S1MAPI/`--channel release|preview|all` paths keep using the existing cross-channel `IndexQueryService` methods unchanged.

- [ ] **Step 1: Write the failing parity test**

```csharp
[Fact]
public async Task Search_ScheduleI_UsesPreferredVerifiedIndex_NotNewerUnverified()
{
    // Seed: preferred verified extraction + its completed index (contains "Alpha"),
    // plus a NEWER completed Installed index built from a non-preferred extraction (contains "Beta").
    var atlas = await CliParityAtlas.SeedPreferredPlusNewerUnverifiedAsync();

    var (exitCode, stdout) = await CliRunner.RunAsync(atlas.DataRoot, "search", "Beta", "--json");

    Assert.Equal(1, exitCode); // Beta lives only in the non-preferred newer index → not found
    Assert.Contains("SymbolNotFound", stdout);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/S1Atlas.IntegrationTests --filter CliQueryParityTests`
Expected: FAIL — the current CLI selects the newest index and finds "Beta".

- [ ] **Step 3: Route Schedule I Installed through the authority path**

In `IndexQueryCommandFactory`, when the parsed codebase is `ScheduleI` and channel is `Installed` (the default), resolve `InstalledBuildAuthorityResolver.ResolveAsync(--build)` and call the `*InIndexAsync` methods against the resolved run. Map authority failures to the existing CLI failure codes (`NoCurrentBuild`, `NoCompletedIndex`, etc.). For other codebases/channels, keep the existing `execute(...)` cross-channel path. Add the `--build` option (valid only for `schedule-i`/`installed`; error otherwise).

- [ ] **Step 4: Run the parity test to verify it passes**

Run: `dotnet test tests/S1Atlas.IntegrationTests --filter CliQueryParityTests`
Expected: PASS.

- [ ] **Step 5: Add regression tests**

Add `Search_ScheduleI_FindsAlphaInPreferredIndex`, `Search_Api_S1Api_PathUnchanged`, and `Search_ScheduleI_ExplicitBuild_SelectsThatBuild`.

- [ ] **Step 6: Run the CLI + indexing test projects (regression)**

Run: `dotnet test tests/S1Atlas.IntegrationTests` then `dotnet test tests/S1Atlas.Indexing.Tests`
Expected: PASS — existing query-command tests still green.

- [ ] **Step 7: Commit**

```bash
git add src/S1Atlas.Cli tests/S1Atlas.IntegrationTests/CliQueryParityTests.cs
git commit -m "feat: route CLI Schedule I queries through shared authority path"
```

---

## Task 12: Host-level integration tests (trust boundary)

**Files:**
- Create: `tests/S1Atlas.Mcp.Tests/McpTrustBoundaryTests.cs`

**Interfaces:**
- Consumes: the full MCP composition + tools, plus a file-hash snapshot helper.

Covers the required cases from the design's testing section.

- [ ] **Step 1: Write the no-mutation file-hash snapshot test**

```csharp
[Fact]
public async Task ExercisingEveryTool_MutatesNoAtlasFile()
{
    var atlas = await McpTestAtlas.SeedHealthyInstalledBuildWithScenesAsync();
    var before = FileTree.HashAll(atlas.DataRoot);

    await McpTestHost.ExerciseEveryToolAsync(atlas); // calls all 13 tools with valid + invalid inputs

    var after = FileTree.HashAll(atlas.DataRoot);
    Assert.Equal(before, after); // no file created, deleted, or modified
}
```

Add `FileTree.HashAll` (relative path → SHA-256 for every file under the root) and `McpTestHost.ExerciseEveryToolAsync`.

- [ ] **Step 2: Run it to verify it fails, then make it pass**

Run: `dotnet test tests/S1Atlas.Mcp.Tests --filter McpTrustBoundaryTests`
Expected: FAIL until helpers exist; PASS once implemented (no production code should need changing — if a file changes, that is a real defect to fix).

- [ ] **Step 3: Add the authority-isolation and integrity-failure cases**

- `OnlyPreferredIntegrityVerifiedExtractionIsReturned`: seed a preferred verified extraction **plus** a Phase 3 candidate, retained failure output, and an unverified row; assert every symbol/source tool answers from the preferred index only.
- `CorruptedIndexedSource_ReturnsSourceIntegrityFailure`: tamper a hash-verified source file; assert `get_source` returns `SourceIntegrityFailure` with no snippet payload.
- `ReadOnlyOpen_DoesNotCreateOrMigrate`: point the host at a data root whose DB is absent; assert an explicit unavailable result and that no DB file is created.
- `DefaultAndExplicitHistoricalBuildResolve`: assert both resolution paths return the correct index IDs.

- [ ] **Step 4: Run the full MCP test project**

Run: `dotnet test tests/S1Atlas.Mcp.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/S1Atlas.Mcp.Tests/McpTrustBoundaryTests.cs
git commit -m "test: prove MCP read-only trust boundary end to end"
```

---

## Task 13: README and milestone documentation

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Document the MCP command and tools**

Add a "Read-only MCP server" section: the `S1Atlas.Mcp mcp serve` launch (stdio), `S1ATLAS_HOME` data-root behavior, the complete V1 tool list (`search_symbols`, `get_type`, `get_method`, `get_source`, `find_callers`, `find_references`, `find_related_types`, `compare_symbol`, `list_builds`, `get_environment`, `list_scenes`, `get_scene`, `get_gameobject`, `get_prefab`, `get_component`), the build/provenance/error rules, and the explicit statement that MCP has no write, patch, network, or game-execution capability and exposes only the Schedule I Installed surface.

- [ ] **Step 2: Update the state and milestone sections**

In the "Current state" preamble and "Next Milestone" section, move the read-only MCP server from outstanding to completed, note the CLI/MCP shared authority parity, and leave the static HTML portal and agent skill as the outstanding V1 milestones. Update "Foundation Architecture" to add `S1Atlas.Application` and `S1Atlas.Mcp`.

- [ ] **Step 3: Full solution build + test**

Run:
```powershell
dotnet build S1Atlas.sln --configuration Release
dotnet test S1Atlas.sln --configuration Release --no-build
```
Expected: build succeeds; all tests pass.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: document read-only MCP server and mark milestone complete"
```

---

## Self-Review

**Spec coverage:**

- §2.1 process/transport → Task 6 (`mcp serve`, stdio, stderr logging).
- §2.2 SDK → Task 6 (`ModelContextProtocol` + hosting).
- §2.3 shared composition / §2.4 parity → Tasks 2, 6, 11.
- §3.1 read-only storage → Task 1.
- §3.2 authority resolution → Task 2 (+ consumed everywhere).
- §3.3 no network/execution → Task 6 composition + Task 12 assertion.
- §4.1 build selection / §4.2 envelope + provenance → Tasks 5, 7–10.
- §5.1 code-symbol tools → Task 7.
- §5.2 compare_symbol → Tasks 4 + 8.
- §5.3 build/environment tools → Task 9.
- §5.4 scene tools → Task 10.
- §6 error semantics → Tasks 5, 7–10 (codes) + 12.
- §7 testing → per-task unit tests + Task 12 integration matrix.
- §8 documentation → Task 13.
- §9 acceptance → satisfied across Tasks 1–13; parity criterion → Task 11.

**Type consistency:** authority produces `InstalledBuildAuthority` with `IndexRun`/`IndexId`; Tasks 7–11 consume `authority.IndexRun!`. `DiffSymbolAsync` (Task 4) returns `SymbolDiff?`; Task 8 maps `null`→`NotFound`. Envelope `ToolEnvelope<T>` (Task 5) is the return type of every tool (Tasks 7–10). Index-scoped methods (Task 3) are the only query entry points used by MCP and by the CLI Schedule I path (Task 11).

**Open confirmations for the implementer (resolve in the owning task, not blockers):**
- Task 6: exact `ModelContextProtocol` registration API + pinned version.
- Task 1: exact set of mutation members to throw from (enumerate from the four interface files).
- Tasks 7–10: `SceneDocumentKind`/`SymbolKind` string parsing and the exact `SceneQueryStatus` values (read the enum in `SceneQueryService.cs`) when writing the status mapping.
