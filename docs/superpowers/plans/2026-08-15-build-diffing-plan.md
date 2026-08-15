# Build Diffing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `diff <build-a> <build-b>` CLI command that compares two indexed builds and reports per-symbol change classifications: Added, Removed, MethodBodyChanged, RelationshipsChanged, Unchanged.

**Architecture:** Domain models in S1Atlas.Core. Classification engine in S1Atlas.Indexing/Diff. Repository query additions on IIndexRepository, implemented in SqliteAtlasRepository.Indexing.cs. CLI command directly registered on the root command (not through IndexQueryCommandFactory — different argument shape). Build ID resolution in the CLI layer using IValidatedExtractionRepository and IExtractionRepository.

**Tech Stack:** C# / .NET 8, Microsoft.Data.Sqlite 8.0.29, System.CommandLine 2.0.10, xUnit v3.

## Global Constraints

- Both indexes must be installed-channel. Release/preview channels are rejected with `UnsupportedChannel`.
- The diff reads existing indexed data only — no decompilation, no network, no new migration.
- Build IDs are 64-character lowercase hex strings (SHA-256).
- Default `--limit` is 50 (consistent with other commands). Must be > 0.
- JSON output uses the standard `CliEnvelope<T>` with `schemaVersion: 1`.
- `BodyRecoveryStatus` must be used to distinguish known-empty body evidence from unavailable evidence — unavailable evidence never produces a factual change claim.
- The kind filter is applied inside BuildDiffService so that per-kind unchanged counts are correct.

---

## File Map

| Area | Files | Responsibility |
|---|---|---|
| Core domain | `src/S1Atlas.Core/Indexing/DiffModels.cs` | DiffClassification enum, SymbolDiff record, BuildDiffResult record |
| Repository contract | `src/S1Atlas.Core/Storage/IIndexRepository.cs` | 3 new method signatures |
| SQLite implementation | `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs` | GetCompletedFingerprintsAsync, GetLatestCompletedIndexBySourceIdentityAsync, GetLatestCompletedIndexForBuildAsync |
| Diff engine | `src/S1Atlas.Indexing/Diff/BuildDiffService.cs` | Classification logic, relationship hashing, kind filtering, sorting |
| CLI command | `src/S1Atlas.Cli/Commands/DiffCommand.cs` | Argument parsing, build resolution, output formatting |
| CLI output models | `src/S1Atlas.Cli/Output/DiffOutputModels.cs` | JSON-serializable data records for the envelope |
| CLI wiring | `src/S1Atlas.Cli/CliApplication.cs` | Register DiffCommand on root |
| Repository tests | `tests/S1Atlas.Indexing.Tests/Diff/DiffRepositoryTests.cs` | New repository query method tests |
| Unit tests | `tests/S1Atlas.Indexing.Tests/Diff/BuildDiffServiceTests.cs` | Classification, priority, edge cases |
| Integration tests | `tests/S1Atlas.IntegrationTests/Diff/DiffCommandTests.cs` | End-to-end CLI with real SQLite |

---

### Task 1: Domain Models and Repository Layer

**Files:**

- Create: `src/S1Atlas.Core/Indexing/DiffModels.cs`
- Modify: `src/S1Atlas.Core/Storage/IIndexRepository.cs`
- Modify: `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Diff/DiffRepositoryTests.cs`

**Interfaces:**

- Consumes: existing `IndexSymbolRecord`, `IndexFingerprintRecord`, `IndexRelationshipRecord`, `IndexRunRecord`, `CodeSnapshotRecord`, `CodebaseKind`, `CodeChannel`, `BodyRecoveryStatus` from `S1Atlas.Core`
- Produces: `DiffClassification` enum, `SymbolDiff` record, `BuildDiffResult` record (used by Task 2). Three new `IIndexRepository` methods: `GetCompletedFingerprintsAsync`, `GetLatestCompletedIndexBySourceIdentityAsync`, `GetLatestCompletedIndexForBuildAsync` (used by Tasks 2 and 3).

- [ ] **Step 1: Create DiffModels.cs**

```csharp
// src/S1Atlas.Core/Indexing/DiffModels.cs
namespace S1Atlas.Core.Indexing;

public enum DiffClassification
{
    Added,
    Removed,
    MethodBodyChanged,
    RelationshipsChanged,
    Unchanged
}

public sealed record SymbolDiff(
    string CanonicalKey,
    string QualifiedName,
    string Kind,
    DiffClassification Classification,
    string? SignatureBefore,
    string? SignatureAfter);

public sealed record BuildDiffResult(
    string IndexIdA,
    string IndexIdB,
    string Codebase,
    string Channel,
    int TotalSymbolsA,
    int TotalSymbolsB,
    IReadOnlyDictionary<DiffClassification, int> CountsByClassification,
    IReadOnlyList<SymbolDiff> Changes);
```

- [ ] **Step 2: Add three methods to IIndexRepository**

Add these three method signatures to the `IIndexRepository` interface in `src/S1Atlas.Core/Storage/IIndexRepository.cs`, after the existing `GetCompletedSourceLocationsAsync` method:

```csharp
Task<IReadOnlyList<IndexFingerprintRecord>> GetCompletedFingerprintsAsync(
    string indexId,
    CancellationToken cancellationToken);

Task<IndexRunRecord?> GetLatestCompletedIndexBySourceIdentityAsync(
    CodebaseKind codebase,
    CodeChannel channel,
    string sourceIdentity,
    CancellationToken cancellationToken);

Task<IndexRunRecord?> GetLatestCompletedIndexForBuildAsync(
    CodebaseKind codebase,
    CodeChannel channel,
    string buildId,
    CancellationToken cancellationToken);
```

- [ ] **Step 3: Build and verify it compiles**

Run: `dotnet build src/S1Atlas.Storage/S1Atlas.Storage.csproj`
Expected: Build failure — the three methods are not implemented yet.

- [ ] **Step 4: Implement GetCompletedFingerprintsAsync in SqliteAtlasRepository**

Add to `src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs`, before the `ReadSnapshot` helper:

```csharp
public async Task<IReadOnlyList<IndexFingerprintRecord>> GetCompletedFingerprintsAsync(
    string indexId,
    CancellationToken cancellationToken)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
    await using var connection = await OpenConnectionAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT fp.symbol_id, fp.fingerprint_kind, fp.fingerprint_value
        FROM symbol_fingerprints AS fp
        INNER JOIN symbols AS symbol ON symbol.symbol_id = fp.symbol_id
        INNER JOIN index_runs AS run ON run.snapshot_id = symbol.snapshot_id
        WHERE run.index_id = $id AND run.status = 'Completed'
        ORDER BY fp.symbol_id COLLATE BINARY, fp.fingerprint_kind COLLATE BINARY;
        """;
    command.Parameters.AddWithValue("$id", indexId);
    var result = new List<IndexFingerprintRecord>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
        result.Add(new IndexFingerprintRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
    return result;
}
```

- [ ] **Step 5: Implement GetLatestCompletedIndexBySourceIdentityAsync**

Add to the same file:

```csharp
public async Task<IndexRunRecord?> GetLatestCompletedIndexBySourceIdentityAsync(
    CodebaseKind codebase,
    CodeChannel channel,
    string sourceIdentity,
    CancellationToken cancellationToken)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
    await using var connection = await OpenConnectionAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT run.index_id, run.snapshot_id, run.status, run.started_at_utc,
               run.completed_at_utc, run.failure_message
        FROM index_runs AS run
        INNER JOIN code_snapshots AS snapshot ON snapshot.snapshot_id = run.snapshot_id
        WHERE run.status = 'Completed'
          AND snapshot.codebase = $codebase
          AND snapshot.channel = $channel
          AND snapshot.source_identity = $sourceIdentity
        ORDER BY run.completed_at_utc DESC, run.index_id COLLATE BINARY DESC
        LIMIT 1;
        """;
    command.Parameters.AddWithValue("$codebase", codebase.ToString());
    command.Parameters.AddWithValue("$channel", channel.ToString());
    command.Parameters.AddWithValue("$sourceIdentity", sourceIdentity);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    return await reader.ReadAsync(cancellationToken) ? ReadRun(reader) : null;
}
```

- [ ] **Step 6: Implement GetLatestCompletedIndexForBuildAsync**

Add to the same file:

```csharp
public async Task<IndexRunRecord?> GetLatestCompletedIndexForBuildAsync(
    CodebaseKind codebase,
    CodeChannel channel,
    string buildId,
    CancellationToken cancellationToken)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
    await using var connection = await OpenConnectionAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT run.index_id, run.snapshot_id, run.status, run.started_at_utc,
               run.completed_at_utc, run.failure_message
        FROM index_runs AS run
        INNER JOIN code_snapshots AS snapshot ON snapshot.snapshot_id = run.snapshot_id
        INNER JOIN environment_snapshots AS env ON env.snapshot_id = snapshot.environment_snapshot_id
        WHERE run.status = 'Completed'
          AND snapshot.codebase = $codebase
          AND snapshot.channel = $channel
          AND env.build_id = $buildId
        ORDER BY run.completed_at_utc DESC, run.index_id COLLATE BINARY DESC
        LIMIT 1;
        """;
    command.Parameters.AddWithValue("$codebase", codebase.ToString());
    command.Parameters.AddWithValue("$channel", channel.ToString());
    command.Parameters.AddWithValue("$buildId", buildId);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    return await reader.ReadAsync(cancellationToken) ? ReadRun(reader) : null;
}
```

- [ ] **Step 7: Build and verify it compiles**

Run: `dotnet build src/S1Atlas.Storage/S1Atlas.Storage.csproj`
Expected: PASS

- [ ] **Step 8: Write repository tests**

Create `tests/S1Atlas.Indexing.Tests/Diff/DiffRepositoryTests.cs`:

```csharp
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests.Diff;

public sealed class DiffRepositoryTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "s1atlas-diff-repo-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public DiffRepositoryTests()
    {
        Directory.CreateDirectory(_directory);
        _repository = new SqliteAtlasRepository(Path.Combine(_directory, "atlas.db"));
    }

    public async ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await Task.Delay(50);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task GetCompletedFingerprintsAsync_returns_all_fingerprints_for_index()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var snapshotId = "snap-fp-test";
        var indexId = "idx-fp-test";
        await _repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(snapshotId, CodebaseKind.ScheduleI, CodeChannel.Installed, "ext-001", DateTimeOffset.UtcNow.ToString("O")),
            ct);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, DateTimeOffset.UtcNow.ToString("O")),
            ct);

        var symbol = new IndexSymbolRecord("sym-1", snapshotId, "ScheduleI:Installed:Method:Test::Foo():System.Void", "Method", "Test.Foo", "Test::Foo():System.Void", false, BodyRecoveryStatus.Recovered);
        var fingerprints = new[]
        {
            new IndexFingerprintRecord("sym-1", "declaration", "aaa111"),
            new IndexFingerprintRecord("sym-1", "structural", "bbb222"),
            new IndexFingerprintRecord("sym-1", "method-body", "ccc333")
        };
        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet([symbol], [], [], fingerprints, []),
            DateTimeOffset.UtcNow.ToString("O"),
            ct);

        var result = await _repository.GetCompletedFingerprintsAsync(indexId, ct);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, fp => fp.Kind == "declaration" && fp.Fingerprint == "aaa111");
        Assert.Contains(result, fp => fp.Kind == "structural" && fp.Fingerprint == "bbb222");
        Assert.Contains(result, fp => fp.Kind == "method-body" && fp.Fingerprint == "ccc333");
    }

    [Fact]
    public async Task GetLatestCompletedIndexBySourceIdentityAsync_finds_index_by_extraction_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var snapshotId = "snap-si-test";
        var indexId = "idx-si-test";
        var extractionId = "extraction-abc123";
        await _repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(snapshotId, CodebaseKind.ScheduleI, CodeChannel.Installed, extractionId, DateTimeOffset.UtcNow.ToString("O")),
            ct);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, DateTimeOffset.UtcNow.ToString("O")),
            ct);
        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet([], [], [], [], []),
            DateTimeOffset.UtcNow.ToString("O"),
            ct);

        var result = await _repository.GetLatestCompletedIndexBySourceIdentityAsync(
            CodebaseKind.ScheduleI, CodeChannel.Installed, extractionId, ct);

        Assert.NotNull(result);
        Assert.Equal(indexId, result!.IndexId);
    }

    [Fact]
    public async Task GetLatestCompletedIndexBySourceIdentityAsync_returns_null_when_no_match()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var result = await _repository.GetLatestCompletedIndexBySourceIdentityAsync(
            CodebaseKind.ScheduleI, CodeChannel.Installed, "nonexistent", ct);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestCompletedIndexForBuildAsync_finds_index_via_environment_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var buildId = "b" + new string('0', 63);
        var snapshotId = "snap-build-test";
        var indexId = "idx-build-test";

        var envSnapshotId = await SeedBuildAndEnvironmentAsync(buildId, ct);
        await _repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(snapshotId, CodebaseKind.S1Api, CodeChannel.Installed, "api-source", DateTimeOffset.UtcNow.ToString("O"), envSnapshotId),
            ct);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, DateTimeOffset.UtcNow.ToString("O")),
            ct);
        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet([], [], [], [], []),
            DateTimeOffset.UtcNow.ToString("O"),
            ct);

        var result = await _repository.GetLatestCompletedIndexForBuildAsync(
            CodebaseKind.S1Api, CodeChannel.Installed, buildId, ct);

        Assert.NotNull(result);
        Assert.Equal(indexId, result!.IndexId);
    }

    private async Task<string> SeedBuildAndEnvironmentAsync(string buildId, CancellationToken ct)
    {
        var envSnapshot = new S1Atlas.Core.Environment.EnvironmentSnapshot(
            IdentityVersion: 2,
            Build: new S1Atlas.Core.Builds.GameBuild(buildId, "asm-hash", "meta-hash", DateTimeOffset.UtcNow, IsValid: true),
            Installation: S1Atlas.Core.Environment.InstallationObservation.Unknown,
            Dependencies: [],
            AtlasVersion: "0.1.0-test",
            CapturedAtUtc: DateTimeOffset.UtcNow);
        await ((IAtlasRepository)_repository).SaveSnapshotAsync(envSnapshot, ct);
        return S1Atlas.Storage.Sqlite.EnvironmentSnapshotId.Create(envSnapshot);
    }
}
```

- [ ] **Step 9: Run the tests**

Run: `dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter "FullyQualifiedName~DiffRepositoryTests"`
Expected: All 4 tests PASS.

- [ ] **Step 10: Commit**

```bash
git add src/S1Atlas.Core/Indexing/DiffModels.cs src/S1Atlas.Core/Storage/IIndexRepository.cs src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs tests/S1Atlas.Indexing.Tests/Diff/DiffRepositoryTests.cs
git commit -m "feat(diff): add domain models and repository query methods for build diffing"
```

---

### Task 2: BuildDiffService Classification Engine

**Files:**

- Create: `src/S1Atlas.Indexing/Diff/BuildDiffService.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Diff/BuildDiffServiceTests.cs`

**Interfaces:**

- Consumes: `IIndexRepository.GetCompletedSymbolsAsync`, `GetCompletedFingerprintsAsync`, `GetCompletedRelationshipsAsync` (from Task 1). Domain records `DiffClassification`, `SymbolDiff`, `BuildDiffResult`, `IndexSymbolRecord`, `IndexFingerprintRecord`, `IndexRelationshipRecord`, `BodyRecoveryStatus`.
- Produces: `BuildDiffService` with `Task<BuildDiffResult> DiffAsync(string indexIdA, string indexIdB, string codebase, string channel, string? kindFilter, CancellationToken ct)` (used by Task 3).

- [ ] **Step 1: Write the failing test — Added classification**

Create `tests/S1Atlas.Indexing.Tests/Diff/BuildDiffServiceTests.cs`:

```csharp
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Diff;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests.Diff;

public sealed class BuildDiffServiceTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "s1atlas-diff-svc-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;
    private readonly BuildDiffService _service;

    public BuildDiffServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _repository = new SqliteAtlasRepository(Path.Combine(_directory, "atlas.db"));
        _service = new BuildDiffService(_repository);
    }

    public async ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await Task.Delay(50);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Symbol_in_B_only_is_Added()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [],
            symbolsB: [MakeSymbol("ScheduleI:Installed:Method:Foo::Bar():System.Void", "Method", "Foo.Bar", "Foo::Bar():System.Void")],
            fingerprintsA: [], fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "abc")],
            relationshipsA: [], relationshipsB: [],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", null, ct);

        Assert.Single(result.Changes);
        Assert.Equal(DiffClassification.Added, result.Changes[0].Classification);
        Assert.Equal("Foo.Bar", result.Changes[0].QualifiedName);
        Assert.Null(result.Changes[0].SignatureBefore);
        Assert.Equal("Foo::Bar():System.Void", result.Changes[0].SignatureAfter);
        Assert.Equal(1, result.CountsByClassification[DiffClassification.Added]);
    }

    [Fact]
    public async Task Symbol_in_A_only_is_Removed()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [MakeSymbol("ScheduleI:Installed:Method:Foo::Bar():System.Void", "Method", "Foo.Bar", "Foo::Bar():System.Void")],
            symbolsB: [],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "abc")],
            fingerprintsB: [],
            relationshipsA: [], relationshipsB: [],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", null, ct);

        Assert.Single(result.Changes);
        Assert.Equal(DiffClassification.Removed, result.Changes[0].Classification);
        Assert.Equal("Foo::Bar():System.Void", result.Changes[0].SignatureBefore);
        Assert.Null(result.Changes[0].SignatureAfter);
    }

    [Fact]
    public async Task Matching_symbol_with_different_method_body_fingerprint_is_MethodBodyChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var key = "ScheduleI:Installed:Method:Foo::Do():System.Void";
        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [MakeSymbol(key, "Method", "Foo.Do", "Foo::Do():System.Void", BodyRecoveryStatus.Recovered)],
            symbolsB: [MakeSymbol(key, "Method", "Foo.Do", "Foo::Do():System.Void", BodyRecoveryStatus.Recovered)],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same"), MakeFingerprint("sym-a-0", "method-body", "old-hash")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same"), MakeFingerprint("sym-b-0", "method-body", "new-hash")],
            relationshipsA: [], relationshipsB: [],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", null, ct);

        Assert.Single(result.Changes);
        Assert.Equal(DiffClassification.MethodBodyChanged, result.Changes[0].Classification);
    }

    [Fact]
    public async Task Matching_symbol_with_different_relationships_is_RelationshipsChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var key = "ScheduleI:Installed:Type:MyNs.MyType";
        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [MakeSymbol(key, "Type", "MyNs.MyType", "MyNs.MyType")],
            symbolsB: [MakeSymbol(key, "Type", "MyNs.MyType", "MyNs.MyType")],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same")],
            relationshipsA: [MakeRelationship("rel-a-0", "sym-a-0", null, "System.Object", "Inherits")],
            relationshipsB: [MakeRelationship("rel-b-0", "sym-b-0", null, "System.Exception", "Inherits")],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", null, ct);

        Assert.Single(result.Changes);
        Assert.Equal(DiffClassification.RelationshipsChanged, result.Changes[0].Classification);
    }

    [Fact]
    public async Task Matching_symbol_with_identical_evidence_is_Unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var key = "ScheduleI:Installed:Type:MyNs.Stable";
        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [MakeSymbol(key, "Type", "MyNs.Stable", "MyNs.Stable")],
            symbolsB: [MakeSymbol(key, "Type", "MyNs.Stable", "MyNs.Stable")],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same")],
            relationshipsA: [MakeRelationship("rel-a-0", "sym-a-0", null, "System.Object", "Inherits")],
            relationshipsB: [MakeRelationship("rel-b-0", "sym-b-0", null, "System.Object", "Inherits")],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", null, ct);

        Assert.Empty(result.Changes);
        Assert.Equal(1, result.CountsByClassification[DiffClassification.Unchanged]);
    }

    [Fact]
    public async Task Asymmetric_body_fingerprint_with_Recovered_status_is_MethodBodyChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var key = "ScheduleI:Installed:Method:Foo::Act():System.Void";
        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [MakeSymbol(key, "Method", "Foo.Act", "Foo::Act():System.Void", BodyRecoveryStatus.Recovered)],
            symbolsB: [MakeSymbol(key, "Method", "Foo.Act", "Foo::Act():System.Void", BodyRecoveryStatus.Recovered)],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same"), MakeFingerprint("sym-a-0", "method-body", "has-refs")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same")],
            relationshipsA: [], relationshipsB: [],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", null, ct);

        Assert.Single(result.Changes);
        Assert.Equal(DiffClassification.MethodBodyChanged, result.Changes[0].Classification);
    }

    [Fact]
    public async Task Asymmetric_body_fingerprint_with_unavailable_status_skips_MethodBodyChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var key = "ScheduleI:Installed:Method:Foo::Stub():System.Void";
        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [MakeSymbol(key, "Method", "Foo.Stub", "Foo::Stub():System.Void", BodyRecoveryStatus.Recovered)],
            symbolsB: [MakeSymbol(key, "Method", "Foo.Stub", "Foo::Stub():System.Void", BodyRecoveryStatus.StubOrUnavailable)],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same"), MakeFingerprint("sym-a-0", "method-body", "has-refs")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same")],
            relationshipsA: [], relationshipsB: [],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", null, ct);

        Assert.Empty(result.Changes);
        Assert.Equal(1, result.CountsByClassification[DiffClassification.Unchanged]);
    }

    [Fact]
    public async Task Kind_filter_excludes_non_matching_symbols_from_counts()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var methodKey = "ScheduleI:Installed:Method:Foo::Do():System.Void";
        var typeKey = "ScheduleI:Installed:Type:MyNs.MyType";
        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [
                MakeSymbol(methodKey, "Method", "Foo.Do", "Foo::Do():System.Void"),
                MakeSymbol(typeKey, "Type", "MyNs.MyType", "MyNs.MyType")
            ],
            symbolsB: [
                MakeSymbol(methodKey, "Method", "Foo.Do", "Foo::Do():System.Void"),
                MakeSymbol(typeKey, "Type", "MyNs.MyType", "MyNs.MyType")
            ],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same"), MakeFingerprint("sym-a-1", "declaration", "same")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same"), MakeFingerprint("sym-b-1", "declaration", "same")],
            relationshipsA: [], relationshipsB: [],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", "Method", ct);

        Assert.Equal(1, result.CountsByClassification[DiffClassification.Unchanged]);
        Assert.Equal(1, result.TotalSymbolsA);
        Assert.Equal(1, result.TotalSymbolsB);
    }

    [Fact]
    public async Task Changes_are_sorted_by_classification_priority_then_name()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var existingKey = "ScheduleI:Installed:Type:Alpha";
        var (idA, idB) = await SeedTwoIndexesAsync(
            symbolsA: [
                MakeSymbol(existingKey, "Type", "Alpha", "Alpha"),
                MakeSymbol("ScheduleI:Installed:Type:Zebra", "Type", "Zebra", "Zebra")
            ],
            symbolsB: [
                MakeSymbol(existingKey, "Type", "Alpha", "Alpha"),
                MakeSymbol("ScheduleI:Installed:Type:Beta", "Type", "Beta", "Beta")
            ],
            fingerprintsA: [MakeFingerprint("sym-a-0", "declaration", "same"), MakeFingerprint("sym-a-1", "declaration", "z")],
            fingerprintsB: [MakeFingerprint("sym-b-0", "declaration", "same"), MakeFingerprint("sym-b-1", "declaration", "b")],
            relationshipsA: [], relationshipsB: [],
            ct);

        var result = await _service.DiffAsync(idA, idB, "ScheduleI", "Installed", null, ct);

        Assert.Equal(2, result.Changes.Count);
        Assert.Equal(DiffClassification.Added, result.Changes[0].Classification);
        Assert.Equal("Beta", result.Changes[0].QualifiedName);
        Assert.Equal(DiffClassification.Removed, result.Changes[1].Classification);
        Assert.Equal("Zebra", result.Changes[1].QualifiedName);
    }

    // --- Helpers ---

    private IndexSymbolRecord MakeSymbol(string canonicalKey, string kind, string qualifiedName, string signature, BodyRecoveryStatus? bodyStatus = null) =>
        new("placeholder", "placeholder", canonicalKey, kind, qualifiedName, signature, false, bodyStatus);

    private IndexFingerprintRecord MakeFingerprint(string symbolId, string kind, string hash) =>
        new(symbolId, kind, hash);

    private IndexRelationshipRecord MakeRelationship(string relId, string sourceSymbolId, string? targetSymbolId, string targetText, string kind) =>
        new(relId, "placeholder", sourceSymbolId, targetSymbolId, targetText, kind, "Metadata");

    private async Task<(string idA, string idB)> SeedTwoIndexesAsync(
        IReadOnlyList<IndexSymbolRecord> symbolsA,
        IReadOnlyList<IndexSymbolRecord> symbolsB,
        IReadOnlyList<IndexFingerprintRecord> fingerprintsA,
        IReadOnlyList<IndexFingerprintRecord> fingerprintsB,
        IReadOnlyList<IndexRelationshipRecord> relationshipsA,
        IReadOnlyList<IndexRelationshipRecord> relationshipsB,
        CancellationToken ct)
    {
        var idA = "idx-a-" + Guid.NewGuid().ToString("N")[..8];
        var idB = "idx-b-" + Guid.NewGuid().ToString("N")[..8];
        var snapA = "snap-a-" + Guid.NewGuid().ToString("N")[..8];
        var snapB = "snap-b-" + Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow.ToString("O");

        await _repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(snapA, CodebaseKind.ScheduleI, CodeChannel.Installed, "ext-a", now), ct);
        await _repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(snapB, CodebaseKind.ScheduleI, CodeChannel.Installed, "ext-b", now), ct);
        await _repository.StartIndexRunAsync(new IndexRunRecord(idA, snapA, IndexRunStatus.Running, now), ct);
        await _repository.StartIndexRunAsync(new IndexRunRecord(idB, snapB, IndexRunStatus.Running, now), ct);

        var realSymbolsA = symbolsA.Select((s, i) => s with { SymbolId = $"sym-a-{i}", SnapshotId = snapA }).ToArray();
        var realSymbolsB = symbolsB.Select((s, i) => s with { SymbolId = $"sym-b-{i}", SnapshotId = snapB }).ToArray();
        var realFpA = fingerprintsA.ToArray();
        var realFpB = fingerprintsB.ToArray();
        var realRelA = relationshipsA.Select(r => r with { SnapshotId = snapA }).ToArray();
        var realRelB = relationshipsB.Select(r => r with { SnapshotId = snapB }).ToArray();

        await _repository.CompleteIndexRunAsync(idA, new IndexWriteSet(realSymbolsA, [], [], realFpA, realRelA), now, ct);
        await _repository.CompleteIndexRunAsync(idB, new IndexWriteSet(realSymbolsB, [], [], realFpB, realRelB), now, ct);
        return (idA, idB);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter "FullyQualifiedName~BuildDiffServiceTests"`
Expected: Compilation failure — `BuildDiffService` does not exist.

- [ ] **Step 3: Implement BuildDiffService**

Create `src/S1Atlas.Indexing/Diff/BuildDiffService.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;

namespace S1Atlas.Indexing.Diff;

public sealed class BuildDiffService
{
    private static readonly int[] ClassificationPriority = Enum.GetValues<DiffClassification>()
        .Select((c, i) => i)
        .ToArray();

    private readonly IIndexRepository _repository;

    public BuildDiffService(IIndexRepository repository)
    {
        _repository = repository;
    }

    public async Task<BuildDiffResult> DiffAsync(
        string indexIdA,
        string indexIdB,
        string codebase,
        string channel,
        string? kindFilter,
        CancellationToken cancellationToken)
    {
        var symbolsA = await _repository.GetCompletedSymbolsAsync(indexIdA, cancellationToken);
        var symbolsB = await _repository.GetCompletedSymbolsAsync(indexIdB, cancellationToken);
        var fingerprintsA = await _repository.GetCompletedFingerprintsAsync(indexIdA, cancellationToken);
        var fingerprintsB = await _repository.GetCompletedFingerprintsAsync(indexIdB, cancellationToken);
        var relationshipsA = await _repository.GetCompletedRelationshipsAsync(indexIdA, cancellationToken);
        var relationshipsB = await _repository.GetCompletedRelationshipsAsync(indexIdB, cancellationToken);

        var mapA = symbolsA.ToDictionary(s => s.CanonicalKey, StringComparer.Ordinal);
        var mapB = symbolsB.ToDictionary(s => s.CanonicalKey, StringComparer.Ordinal);

        var fpBySymbolA = GroupFingerprints(fingerprintsA);
        var fpBySymbolB = GroupFingerprints(fingerprintsB);

        var symIdToKeyA = symbolsA.ToDictionary(s => s.SymbolId, s => s.CanonicalKey, StringComparer.Ordinal);
        var symIdToKeyB = symbolsB.ToDictionary(s => s.SymbolId, s => s.CanonicalKey, StringComparer.Ordinal);

        var relBySourceA = GroupRelationships(relationshipsA);
        var relBySourceB = GroupRelationships(relationshipsB);

        var allKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in mapA.Keys) allKeys.Add(key);
        foreach (var key in mapB.Keys) allKeys.Add(key);

        var changes = new List<SymbolDiff>();
        var counts = new Dictionary<DiffClassification, int>();
        foreach (var c in Enum.GetValues<DiffClassification>())
            counts[c] = 0;

        int totalA = 0, totalB = 0;

        foreach (var key in allKeys)
        {
            var inA = mapA.TryGetValue(key, out var symA);
            var inB = mapB.TryGetValue(key, out var symB);

            var classification = Classify(
                inA, inB, symA, symB,
                fpBySymbolA, fpBySymbolB,
                relBySourceA, relBySourceB,
                symIdToKeyA, symIdToKeyB);

            var kind = (inB ? symB! : symA!).Kind;

            if (kindFilter is not null && !string.Equals(kind, kindFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (inA) totalA++;
            if (inB) totalB++;

            counts[classification]++;

            if (classification == DiffClassification.Unchanged)
                continue;

            var qualifiedName = (inB ? symB! : symA!).QualifiedName;
            string? sigBefore = inA ? symA!.Signature : null;
            string? sigAfter = inB ? symB!.Signature : null;

            if (classification is DiffClassification.MethodBodyChanged or DiffClassification.RelationshipsChanged)
            {
                sigBefore = null;
                sigAfter = null;
            }

            changes.Add(new SymbolDiff(key, qualifiedName, kind, classification, sigBefore, sigAfter));
        }

        changes.Sort((a, b) =>
        {
            var cmp = ((int)a.Classification).CompareTo((int)b.Classification);
            return cmp != 0 ? cmp : string.Compare(a.QualifiedName, b.QualifiedName, StringComparison.Ordinal);
        });

        if (kindFilter is null)
        {
            totalA = symbolsA.Count;
            totalB = symbolsB.Count;
        }

        return new BuildDiffResult(
            indexIdA, indexIdB,
            codebase, channel,
            totalA, totalB,
            counts, changes);
    }

    private static DiffClassification Classify(
        bool inA, bool inB,
        IndexSymbolRecord? symA, IndexSymbolRecord? symB,
        Dictionary<string, Dictionary<string, string>> fpA,
        Dictionary<string, Dictionary<string, string>> fpB,
        Dictionary<string, IReadOnlyList<IndexRelationshipRecord>> relA,
        Dictionary<string, IReadOnlyList<IndexRelationshipRecord>> relB,
        Dictionary<string, string> symIdToKeyA,
        Dictionary<string, string> symIdToKeyB)
    {
        if (!inA) return DiffClassification.Added;
        if (!inB) return DiffClassification.Removed;

        var kindIsMethodLike = symA!.Kind is "Method" or "Constructor";

        if (kindIsMethodLike)
        {
            var bodyResult = ClassifyMethodBody(symA, symB!, fpA, fpB);
            if (bodyResult == DiffClassification.MethodBodyChanged)
                return DiffClassification.MethodBodyChanged;
        }

        var relHashA = HashRelationships(symA!.SymbolId, relA, symIdToKeyA);
        var relHashB = HashRelationships(symB!.SymbolId, relB, symIdToKeyB);
        if (!string.Equals(relHashA, relHashB, StringComparison.Ordinal))
            return DiffClassification.RelationshipsChanged;

        return DiffClassification.Unchanged;
    }

    private static DiffClassification? ClassifyMethodBody(
        IndexSymbolRecord symA,
        IndexSymbolRecord symB,
        Dictionary<string, Dictionary<string, string>> fpA,
        Dictionary<string, Dictionary<string, string>> fpB)
    {
        var hasBodyFpA = TryGetFingerprint(symA.SymbolId, "method-body", fpA, out var bodyHashA);
        var hasBodyFpB = TryGetFingerprint(symB.SymbolId, "method-body", fpB, out var bodyHashB);

        if (hasBodyFpA && hasBodyFpB)
            return string.Equals(bodyHashA, bodyHashB, StringComparison.Ordinal) ? null : DiffClassification.MethodBodyChanged;

        var statusA = symA.BodyRecoveryStatus;
        var statusB = symB.BodyRecoveryStatus;

        if (hasBodyFpA && !hasBodyFpB)
        {
            if (statusB == BodyRecoveryStatus.Recovered)
                return DiffClassification.MethodBodyChanged;
            return null;
        }

        if (!hasBodyFpA && hasBodyFpB)
        {
            if (statusA == BodyRecoveryStatus.Recovered)
                return DiffClassification.MethodBodyChanged;
            return null;
        }

        return null;
    }

    private static string HashRelationships(
        string symbolId,
        Dictionary<string, IReadOnlyList<IndexRelationshipRecord>> relBySource,
        Dictionary<string, string> symIdToKey)
    {
        if (!relBySource.TryGetValue(symbolId, out var rels) || rels.Count == 0)
            return string.Empty;

        var tuples = rels
            .Select(r =>
            {
                var target = r.TargetSymbolId is not null && symIdToKey.TryGetValue(r.TargetSymbolId, out var key)
                    ? key
                    : r.TargetText ?? string.Empty;
                return r.Kind + "\n" + target;
            })
            .Order(StringComparer.Ordinal);

        var input = string.Join("\n\n", tuples);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static bool TryGetFingerprint(
        string symbolId, string kind,
        Dictionary<string, Dictionary<string, string>> grouped,
        out string hash)
    {
        hash = string.Empty;
        return grouped.TryGetValue(symbolId, out var kinds) && kinds.TryGetValue(kind, out hash!);
    }

    private static Dictionary<string, Dictionary<string, string>> GroupFingerprints(
        IReadOnlyList<IndexFingerprintRecord> fingerprints)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var fp in fingerprints)
        {
            if (!result.TryGetValue(fp.SymbolId, out var inner))
            {
                inner = new Dictionary<string, string>(StringComparer.Ordinal);
                result[fp.SymbolId] = inner;
            }
            inner[fp.Kind] = fp.Fingerprint;
        }
        return result;
    }

    private static Dictionary<string, IReadOnlyList<IndexRelationshipRecord>> GroupRelationships(
        IReadOnlyList<IndexRelationshipRecord> relationships)
    {
        return relationships
            .GroupBy(r => r.SourceSymbolId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<IndexRelationshipRecord>)g.ToArray(), StringComparer.Ordinal);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter "FullyQualifiedName~BuildDiffServiceTests"`
Expected: All 9 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/S1Atlas.Indexing/Diff/BuildDiffService.cs tests/S1Atlas.Indexing.Tests/Diff/BuildDiffServiceTests.cs
git commit -m "feat(diff): implement BuildDiffService classification engine with tests"
```

---

### Task 3: DiffCommand CLI and Integration Tests

**Files:**

- Create: `src/S1Atlas.Cli/Output/DiffOutputModels.cs`
- Create: `src/S1Atlas.Cli/Commands/DiffCommand.cs`
- Modify: `src/S1Atlas.Cli/CliApplication.cs`
- Create: `tests/S1Atlas.IntegrationTests/Diff/DiffCommandTests.cs`

**Interfaces:**

- Consumes: `BuildDiffService.DiffAsync(indexIdA, indexIdB, codebase, channel, kindFilter, ct)` (from Task 2). `IValidatedExtractionRepository.GetPreferredExtractionAsync(buildId, ct)`, `IExtractionRepository.GetBuildAsync(buildId, ct)`, `IIndexRepository.GetLatestCompletedIndexBySourceIdentityAsync(...)`, `IIndexRepository.GetLatestCompletedIndexForBuildAsync(...)` (from Task 1).
- Produces: The `diff` CLI command registered on the root. Human-readable and JSON output per the design spec.

- [ ] **Step 1: Create DiffOutputModels.cs**

Create `src/S1Atlas.Cli/Output/DiffOutputModels.cs`:

```csharp
namespace S1Atlas.Cli.Output;

internal sealed record DiffOutputData(
    string IdentifierA,
    string IdentifierB,
    string IndexIdA,
    string IndexIdB,
    string Codebase,
    string Channel,
    int TotalSymbolsA,
    int TotalSymbolsB,
    DiffOutputCounts Counts,
    int TotalChanged,
    int ReturnedCount,
    IReadOnlyList<DiffOutputChange> Changes);

internal sealed record DiffOutputCounts(
    int Added,
    int Removed,
    int MethodBodyChanged,
    int RelationshipsChanged,
    int Unchanged);

internal sealed record DiffOutputChange(
    string CanonicalKey,
    string QualifiedName,
    string Kind,
    string Classification,
    string? SignatureBefore,
    string? SignatureAfter);
```

- [ ] **Step 2: Create DiffCommand.cs**

Create `src/S1Atlas.Cli/Commands/DiffCommand.cs`:

```csharp
using System.CommandLine;
using S1Atlas.Cli.Output;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Diff;

namespace S1Atlas.Cli.Commands;

internal static class DiffCommand
{
    public static Command Create(
        BuildDiffService diffService,
        IIndexRepository indexRepository,
        IExtractionRepository extractionRepository,
        IValidatedExtractionRepository validatedExtractionRepository,
        IAtlasRepository atlasRepository,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var idAArgument = new Argument<string>("id-a") { Description = "Build ID for the baseline (before)." };
        var idBArgument = new Argument<string>("id-b") { Description = "Build ID for the target (after)." };
        var codebaseOption = new Option<string>("--codebase") { Description = "schedule-i, s1api, or s1mapi." };
        var channelOption = new Option<string>("--channel") { Description = "installed (default). Release and preview are not supported." };
        var kindOption = new Option<string>("--kind") { Description = "Filter by symbol kind: type, method, constructor, field, property, event." };
        var limitOption = new Option<int>("--limit") { Description = "Maximum changed symbols to list.", DefaultValueFactory = _ => 50 };
        var jsonOption = CommandOutput.CreateJsonOption();

        var command = new Command("diff", "Compare two indexed builds and report per-symbol changes.");
        command.Arguments.Add(idAArgument);
        command.Arguments.Add(idBArgument);
        command.Options.Add(codebaseOption);
        command.Options.Add(channelOption);
        command.Options.Add(kindOption);
        command.Options.Add(limitOption);
        command.Options.Add(jsonOption);

        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput("diff", parseResult.GetValue(jsonOption), output, error);
            return CommandExecution.Run(
                () =>
                {
                    var idA = parseResult.GetValue(idAArgument)!;
                    var idB = parseResult.GetValue(idBArgument)!;
                    var limit = parseResult.GetValue(limitOption);
                    var kindFilter = parseResult.GetValue(kindOption);

                    if (limit <= 0)
                        return commandOutput.Failure(1, "InvalidLimit", "--limit must be greater than zero.");

                    var codebase = IndexQueryCommandFactory.ParseOptions(
                        parseResult.GetValue(codebaseOption), null).Codebase;

                    var channelRaw = (parseResult.GetValue(channelOption) ?? "installed").ToLowerInvariant();
                    if (channelRaw is "release" or "preview")
                        return commandOutput.Failure(1, "UnsupportedChannel",
                            "Build diffing requires installed-channel indexes. Release and preview channels are not supported in V1.");

                    if (channelRaw != "installed")
                        return commandOutput.Failure(1, "InvalidChannel",
                            "Channel must be installed. Release and preview are not supported for diffing.");

                    var channel = CodeChannel.Installed;

                    atlasRepository.InitializeAsync(cancellationToken).GetAwaiter().GetResult();

                    var indexIdA = ResolveIndexId(
                        idA, codebase, channel, indexRepository, extractionRepository, validatedExtractionRepository, cancellationToken);
                    var indexIdB = ResolveIndexId(
                        idB, codebase, channel, indexRepository, extractionRepository, validatedExtractionRepository, cancellationToken);

                    if (string.Equals(indexIdA, indexIdB, StringComparison.Ordinal))
                        return commandOutput.Failure(1, "SameIndex",
                            "Both build identifiers resolve to the same index. Provide two different builds.");

                    var result = diffService.DiffAsync(indexIdA, indexIdB, codebase.ToString(), channel.ToString(), kindFilter, cancellationToken)
                        .GetAwaiter().GetResult();

                    var totalChanged = result.Changes.Count;
                    var limitedChanges = limit < result.Changes.Count
                        ? result.Changes.Take(limit).ToArray()
                        : result.Changes;

                    var data = new DiffOutputData(
                        idA, idB,
                        result.IndexIdA, result.IndexIdB,
                        result.Codebase, result.Channel,
                        result.TotalSymbolsA, result.TotalSymbolsB,
                        new DiffOutputCounts(
                            result.CountsByClassification.GetValueOrDefault(DiffClassification.Added),
                            result.CountsByClassification.GetValueOrDefault(DiffClassification.Removed),
                            result.CountsByClassification.GetValueOrDefault(DiffClassification.MethodBodyChanged),
                            result.CountsByClassification.GetValueOrDefault(DiffClassification.RelationshipsChanged),
                            result.CountsByClassification.GetValueOrDefault(DiffClassification.Unchanged)),
                        totalChanged,
                        limitedChanges.Count,
                        limitedChanges.Select(c => new DiffOutputChange(
                            c.CanonicalKey, c.QualifiedName, c.Kind,
                            c.Classification.ToString(), c.SignatureBefore, c.SignatureAfter)).ToArray());

                    return commandOutput.Success(data, writer => WriteHuman(writer, data, idA, idB));
                },
                commandOutput,
                cancellationToken);
        });

        return command;
    }

    private static string ResolveIndexId(
        string buildId,
        CodebaseKind codebase,
        CodeChannel channel,
        IIndexRepository indexRepository,
        IExtractionRepository extractionRepository,
        IValidatedExtractionRepository validatedExtractionRepository,
        CancellationToken ct)
    {
        var build = extractionRepository.GetBuildAsync(buildId, ct).GetAwaiter().GetResult();
        if (build is null)
            throw new InvalidOperationException($"Build '{Truncate(buildId)}' not found.");

        IndexRunRecord? index;
        if (codebase == CodebaseKind.ScheduleI)
        {
            var preferred = validatedExtractionRepository.GetPreferredExtractionAsync(buildId, ct)
                .GetAwaiter().GetResult();
            if (preferred is null)
                throw new InvalidOperationException($"No preferred validated extraction for build '{Truncate(buildId)}'.");

            index = indexRepository.GetLatestCompletedIndexBySourceIdentityAsync(
                codebase, channel, preferred.ExtractionId, ct).GetAwaiter().GetResult();
        }
        else
        {
            index = indexRepository.GetLatestCompletedIndexForBuildAsync(
                codebase, channel, buildId, ct).GetAwaiter().GetResult();
        }

        if (index is null)
            throw new InvalidOperationException($"No completed index for build '{Truncate(buildId)}' ({codebase}/{channel}).");

        return index.IndexId;
    }

    private static void WriteHuman(TextWriter writer, DiffOutputData data, string idA, string idB)
    {
        writer.WriteLine($"Build diff: {Truncate(idA)} (before) → {Truncate(idB)} (after)");
        writer.WriteLine($"Codebase: {data.Codebase}  Channel: {data.Channel}");
        writer.WriteLine();
        writer.WriteLine($"  Added:                {data.Counts.Added,6:N0}");
        writer.WriteLine($"  Removed:              {data.Counts.Removed,6:N0}");
        writer.WriteLine($"  Method body changed:  {data.Counts.MethodBodyChanged,6:N0}");
        writer.WriteLine($"  Relationships changed:{data.Counts.RelationshipsChanged,6:N0}");
        writer.WriteLine($"  Unchanged:            {data.Counts.Unchanged,6:N0}");
        writer.WriteLine($"  ─────────────────────────");
        writer.WriteLine($"  Total (before):       {data.TotalSymbolsA,6:N0}");
        writer.WriteLine($"  Total (after):        {data.TotalSymbolsB,6:N0}");

        if (data.TotalChanged > 0)
        {
            writer.WriteLine();
            writer.WriteLine($"Changed symbols ({data.ReturnedCount} of {data.TotalChanged}):");
            writer.WriteLine();
            foreach (var change in data.Changes)
            {
                var tag = change.Classification switch
                {
                    "Added" => "[Added]     ",
                    "Removed" => "[Removed]   ",
                    "MethodBodyChanged" => "[BodyChange]",
                    "RelationshipsChanged" => "[RelChange] ",
                    _ => "[?]         "
                };
                writer.WriteLine($"  {tag} {change.Kind,-12} {change.QualifiedName}");
            }
        }
    }

    private static string Truncate(string id) =>
        id.Length > 16 ? id[..8] + "..." + id[^8..] : id;
}
```

- [ ] **Step 3: Wire DiffCommand into CliApplication**

In `src/S1Atlas.Cli/CliApplication.cs`, add the import at the top:

```csharp
using S1Atlas.Indexing.Diff;
```

After the line that creates `indexQueryService` (around line 288), add:

```csharp
var diffService = new BuildDiffService(sqliteRepository);
```

After the last `root.Subcommands.Add(...)` call (around line 359), add:

```csharp
root.Subcommands.Add(DiffCommand.Create(
    diffService, sqliteRepository, sqliteRepository, sqliteRepository, repository,
    output, error, cancellationToken));
```

- [ ] **Step 4: Build and verify it compiles**

Run: `dotnet build src/S1Atlas.Cli/S1Atlas.Cli.csproj`
Expected: PASS

- [ ] **Step 5: Write integration tests**

Create `tests/S1Atlas.IntegrationTests/Diff/DiffCommandTests.cs`:

```csharp
using System.Text.Json;
using S1Atlas.Cli;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.IntegrationTests.Diff;

public sealed class DiffCommandTests : IAsyncDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "s1atlas-diff-cli-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public DiffCommandTests()
    {
        Directory.CreateDirectory(_dataDirectory);
        _repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
    }

    public async ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await Task.Delay(50);
        if (Directory.Exists(_dataDirectory))
            Directory.Delete(_dataDirectory, recursive: true);
    }

    [Fact]
    public async Task Diff_json_reports_added_and_removed_symbols()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var buildIdA = "a" + new string('0', 63);
        var buildIdB = "b" + new string('0', 63);
        await SeedScheduleIBuildWithIndexAsync(buildIdA, "ext-a", "idx-a",
            [MakeSymbol("ScheduleI:Installed:Method:Old::Run():System.Void", "Method", "Old.Run", "Old::Run():System.Void")],
            [MakeFingerprint("sym-0", "declaration", "aaa")],
            [], ct);
        await SeedScheduleIBuildWithIndexAsync(buildIdB, "ext-b", "idx-b",
            [MakeSymbol("ScheduleI:Installed:Method:New::Start():System.Void", "Method", "New.Start", "New::Start():System.Void")],
            [MakeFingerprint("sym-0", "declaration", "bbb")],
            [], ct);

        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["diff", buildIdA, buildIdB, "--json"], output, error, ct);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetProperty("counts").GetProperty("added").GetInt32());
        Assert.Equal(1, data.GetProperty("counts").GetProperty("removed").GetInt32());
        Assert.Equal(2, data.GetProperty("totalChanged").GetInt32());
    }

    [Fact]
    public async Task Diff_human_output_contains_summary_and_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var buildIdA = "a" + new string('1', 63);
        var buildIdB = "b" + new string('1', 63);
        await SeedScheduleIBuildWithIndexAsync(buildIdA, "ext-a1", "idx-a1",
            [MakeSymbol("ScheduleI:Installed:Type:Ns.Stable", "Type", "Ns.Stable", "Ns.Stable")],
            [MakeFingerprint("sym-0", "declaration", "same")],
            [], ct);
        await SeedScheduleIBuildWithIndexAsync(buildIdB, "ext-b1", "idx-b1",
            [MakeSymbol("ScheduleI:Installed:Type:Ns.Stable", "Type", "Ns.Stable", "Ns.Stable")],
            [MakeFingerprint("sym-0", "declaration", "same")],
            [], ct);

        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["diff", buildIdA, buildIdB], output, error, ct);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Build diff:", text, StringComparison.Ordinal);
        Assert.Contains("Unchanged:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_unknown_build_returns_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var fakeA = "f" + new string('0', 63);
        var fakeB = "f" + new string('1', 63);

        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["diff", fakeA, fakeB, "--json"], output, error, ct);

        Assert.Equal(1, exitCode);
        var text = output.ToString();
        Assert.Contains("not found", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Diff_unsupported_channel_returns_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["diff", "a", "b", "--channel", "release", "--json"], output, error, ct);

        Assert.Equal(1, exitCode);
        Assert.Contains("UnsupportedChannel", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_same_index_returns_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var buildId = "c" + new string('0', 63);
        await SeedScheduleIBuildWithIndexAsync(buildId, "ext-same", "idx-same", [], [], [], ct);

        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["diff", buildId, buildId, "--json"], output, error, ct);

        Assert.Equal(1, exitCode);
        Assert.Contains("SameIndex", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_limit_truncates_changes_but_counts_remain_complete()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var buildIdA = "a" + new string('2', 63);
        var buildIdB = "b" + new string('2', 63);
        var symbols = Enumerable.Range(0, 5)
            .Select(i => MakeSymbol($"ScheduleI:Installed:Method:Ns::M{i}():System.Void", "Method", $"Ns.M{i}", $"Ns::M{i}():System.Void"))
            .ToArray();
        var fps = symbols.Select((_, i) => MakeFingerprint($"sym-{i}", "declaration", $"hash-{i}")).ToArray();

        await SeedScheduleIBuildWithIndexAsync(buildIdA, "ext-a2", "idx-a2", [], [], [], ct);
        await SeedScheduleIBuildWithIndexAsync(buildIdB, "ext-b2", "idx-b2", symbols, fps, [], ct);

        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["diff", buildIdA, buildIdB, "--limit", "2", "--json"], output, error, ct);

        Assert.Equal(0, exitCode);
        var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(5, data.GetProperty("counts").GetProperty("added").GetInt32());
        Assert.Equal(5, data.GetProperty("totalChanged").GetInt32());
        Assert.Equal(2, data.GetProperty("returnedCount").GetInt32());
        Assert.Equal(2, data.GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public async Task Diff_kind_filter_restricts_counts_and_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);

        var buildIdA = "a" + new string('3', 63);
        var buildIdB = "b" + new string('3', 63);
        await SeedScheduleIBuildWithIndexAsync(buildIdA, "ext-a3", "idx-a3", [], [], [], ct);
        await SeedScheduleIBuildWithIndexAsync(buildIdB, "ext-b3", "idx-b3",
            [
                MakeSymbol("ScheduleI:Installed:Method:Ns::Do():System.Void", "Method", "Ns.Do", "Ns::Do():System.Void"),
                MakeSymbol("ScheduleI:Installed:Type:Ns.MyType", "Type", "Ns.MyType", "Ns.MyType")
            ],
            [
                MakeFingerprint("sym-0", "declaration", "m-hash"),
                MakeFingerprint("sym-1", "declaration", "t-hash")
            ],
            [], ct);

        var application = new CliApplication(_dataDirectory, "0.1.0-test");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = application.Invoke(["diff", buildIdA, buildIdB, "--kind", "Method", "--json"], output, error, ct);

        Assert.Equal(0, exitCode);
        var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetProperty("counts").GetProperty("added").GetInt32());
        Assert.Equal(1, data.GetProperty("totalChanged").GetInt32());
    }

    // --- Helpers ---

    private IndexSymbolRecord MakeSymbol(string canonicalKey, string kind, string qualifiedName, string signature) =>
        new("placeholder", "placeholder", canonicalKey, kind, qualifiedName, signature, false);

    private IndexFingerprintRecord MakeFingerprint(string symbolId, string kind, string hash) =>
        new(symbolId, kind, hash);

    private async Task SeedScheduleIBuildWithIndexAsync(
        string buildId, string extractionId, string indexId,
        IReadOnlyList<IndexSymbolRecord> symbols,
        IReadOnlyList<IndexFingerprintRecord> fingerprints,
        IReadOnlyList<IndexRelationshipRecord> relationships,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var build = await _repository.GetBuildAsync(buildId, ct);
        if (build is null)
        {
            var envSnapshot = new EnvironmentSnapshot(
                IdentityVersion: 2,
                Build: new GameBuild(buildId, "asm-hash", "meta-hash", now, IsValid: true),
                Installation: InstallationObservation.Unknown,
                Dependencies: [],
                AtlasVersion: "0.1.0-test",
                CapturedAtUtc: now);
            await ((IAtlasRepository)_repository).SaveSnapshotAsync(envSnapshot, ct);
        }

        var pref = await _repository.GetPreferredExtractionAsync(buildId, ct);
        if (pref is null)
        {
            await _repository.SetPreferredExtractionAsync(
                new PreferredExtraction(
                    buildId, extractionId, now,
                    ExtractionPreferenceReason.ManagedAutomatic),
                ct);
        }

        var snapshotId = "snap-" + indexId;
        await _repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(snapshotId, CodebaseKind.ScheduleI, CodeChannel.Installed, extractionId, now.ToString("O")), ct);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, now.ToString("O")), ct);

        var realSymbols = symbols.Select((s, i) => s with { SymbolId = $"sym-{i}", SnapshotId = snapshotId }).ToArray();
        var realFps = fingerprints.ToArray();
        var realRels = relationships.Select(r => r with { SnapshotId = snapshotId }).ToArray();

        await _repository.CompleteIndexRunAsync(indexId, new IndexWriteSet(realSymbols, [], [], realFps, realRels), now.ToString("O"), ct);
    }
}
```

- [ ] **Step 6: Run the integration tests**

Run: `dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter "FullyQualifiedName~DiffCommandTests"`
Expected: All 7 tests PASS.

- [ ] **Step 7: Run the full test suite to check for regressions**

Run: `dotnet test`
Expected: All existing tests PASS alongside the new ones.

- [ ] **Step 8: Commit**

```bash
git add src/S1Atlas.Cli/Output/DiffOutputModels.cs src/S1Atlas.Cli/Commands/DiffCommand.cs src/S1Atlas.Cli/CliApplication.cs tests/S1Atlas.IntegrationTests/Diff/DiffCommandTests.cs
git commit -m "feat(diff): add diff CLI command with build resolution and integration tests"
```

---

### Task 4: README Update

**Files:**

- Modify: `README.md`

**Interfaces:**

- Consumes: nothing
- Produces: Updated command table and milestone status in the README

- [ ] **Step 1: Add the diff command to the command table**

In the "Commands" section of `README.md`, add a row for the diff command after the existing query commands:

```markdown
| `diff <id-a> <id-b>` | Compare two indexed builds | `--codebase`, `--channel`, `--kind`, `--limit`, `--json` |
```

- [ ] **Step 2: Update milestone status**

In the milestones section, update the Build Diffing milestone status from planned to completed.

- [ ] **Step 3: Add a diff example**

In the examples section, add a usage example:

```markdown
### Compare two builds

```
s1atlas diff a1b2c3d4...full64charhex e5f6g7h8...full64charhex
s1atlas diff a1b2c3d4...full64charhex e5f6g7h8...full64charhex --kind Method --json
```
```

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: add diff command to README command table and examples"
```
