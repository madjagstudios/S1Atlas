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
