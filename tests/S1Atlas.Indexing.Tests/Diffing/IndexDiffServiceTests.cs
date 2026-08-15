using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Diffing;
using Xunit;

namespace S1Atlas.Indexing.Tests.Diffing;

public sealed class IndexDiffServiceTests
{
    [Fact]
    public async Task Missing_completed_index_has_stable_snapshot_not_found_code()
    {
        var repository = new FakeIndexRepository();
        var exception = await Assert.ThrowsAsync<IndexDiffException>(() =>
            new IndexDiffService(repository).CompareAsync("missing", "missing", TestContext.Current.CancellationToken));

        Assert.Equal("SnapshotNotFound", exception.Code);
    }

    [Fact]
    public async Task Selector_resolves_channel_and_source_identity_without_mutating_repository()
    {
        var repository = new FakeIndexRepository();
        repository.Add(
            new CodeSnapshotRecord("snapshot-release", CodebaseKind.S1Api, CodeChannel.Release, "commit-release", "2026-08-14T00:00:00Z"),
            new IndexRunRecord("index-release", "snapshot-release", IndexRunStatus.Completed, "2026-08-14T00:00:00Z", "2026-08-14T00:01:00Z"));
        var service = new IndexDiffService(repository);

        var byChannel = await service.ResolveSelectorAsync(CodebaseKind.S1Api, "release", CodeChannel.Installed, TestContext.Current.CancellationToken);
        var bySource = await service.ResolveSelectorAsync(CodebaseKind.S1Api, "commit-release", CodeChannel.Installed, TestContext.Current.CancellationToken);

        Assert.Equal("index-release", byChannel.IndexId);
        Assert.Equal("index-release", bySource.IndexId);
        Assert.Equal(0, repository.WriteCount);
    }

    [Fact]
    public async Task Selector_rejects_an_explicit_index_from_a_different_codebase()
    {
        var repository = new FakeIndexRepository();
        repository.Add(
            new CodeSnapshotRecord("snapshot-game", CodebaseKind.ScheduleI, CodeChannel.Installed, "build-game", "2026-08-14T00:00:00Z"),
            new IndexRunRecord("index-game", "snapshot-game", IndexRunStatus.Completed, "2026-08-14T00:00:00Z", "2026-08-14T00:01:00Z"));

        var exception = await Assert.ThrowsAsync<IndexDiffException>(() =>
            new IndexDiffService(repository).ResolveSelectorAsync(CodebaseKind.S1Api, "index-game", CodeChannel.Installed, TestContext.Current.CancellationToken));

        Assert.Equal("NotComparable", exception.Code);
    }

    private sealed class FakeIndexRepository : IIndexRepository
    {
        private readonly Dictionary<string, (CodeSnapshotRecord Snapshot, IndexRunRecord Run)> _runs = new(StringComparer.Ordinal);
        public int WriteCount { get; private set; }
        public void Add(CodeSnapshotRecord snapshot, IndexRunRecord run) => _runs[run.IndexId] = (snapshot, run);
        public Task CreateCodeSnapshotAsync(CodeSnapshotRecord snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CodeSnapshotRecord?> GetCodeSnapshotAsync(string snapshotId, CancellationToken cancellationToken) => Task.FromResult(_runs.Values.Select(item => item.Snapshot).SingleOrDefault(item => item.SnapshotId == snapshotId));
        public Task StartIndexRunAsync(IndexRunRecord run, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteIndexRunAsync(string indexId, IndexWriteSet writeSet, string completedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailIndexRunAsync(string indexId, string failureMessage, string completedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IndexRunRecord?> GetCompletedIndexAsync(string indexId, CancellationToken cancellationToken) => Task.FromResult(_runs.TryGetValue(indexId, out var value) ? value.Run : null);
        public Task<IndexRunRecord?> GetLatestCompletedIndexForSnapshotAsync(string snapshotId, CancellationToken cancellationToken) => Task.FromResult(_runs.Values.Where(item => item.Run.SnapshotId == snapshotId).Select(item => item.Run).SingleOrDefault());
        public Task<IndexRunRecord?> GetLatestCompletedIndexBySourceIdentityAsync(CodebaseKind codebase, CodeChannel channel, string sourceIdentity, CancellationToken cancellationToken) => Task.FromResult(_runs.Values.Where(item => item.Snapshot.Codebase == codebase && item.Snapshot.Channel == channel && item.Snapshot.SourceIdentity == sourceIdentity).Select(item => item.Run).SingleOrDefault());
        public Task<IndexRunRecord?> GetLatestCompletedIndexAsync(CodebaseKind codebase, CodeChannel channel, string? environmentSnapshotId, CancellationToken cancellationToken) => Task.FromResult(_runs.Values.Where(item => item.Snapshot.Codebase == codebase && item.Snapshot.Channel == channel).Select(item => item.Run).SingleOrDefault());
        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsAsync(string indexId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IndexSymbolRecord>>([]);
        public Task<IndexSymbolRecord?> GetCompletedSymbolByIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) => Task.FromResult<IndexSymbolRecord?>(null);
        public Task<IReadOnlyList<IndexSymbolRecord>> GetCompletedSymbolsByIdsAsync(string indexId, IReadOnlyList<string> symbolIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IndexSymbolRecord>>([]);
        public Task<IReadOnlyList<IndexFingerprintRecord>> GetCompletedFingerprintsAsync(string indexId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IndexFingerprintRecord>>([]);
        public Task<int> CountCompletedSymbolMatchesAsync(string indexId, string query, CancellationToken cancellationToken, string? kind = null) => Task.FromResult(0);
        public Task<IReadOnlyList<IndexSymbolRecord>> SearchCompletedSymbolsAsync(string indexId, string query, int limit, CancellationToken cancellationToken, string? kind = null) => Task.FromResult<IReadOnlyList<IndexSymbolRecord>>([]);
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsAsync(string indexId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IndexRelationshipRecord>>([]);
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsBySourceSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IndexRelationshipRecord>>([]);
        public Task<IReadOnlyList<IndexRelationshipRecord>> GetCompletedRelationshipsByTargetSymbolIdAsync(string indexId, string symbolId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IndexRelationshipRecord>>([]);
        public Task<IReadOnlyList<IndexSourceFileRecord>> GetCompletedSourceFilesAsync(string indexId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IndexSourceFileRecord>>([]);
        public Task<IReadOnlyList<IndexSourceLocationRecord>> GetCompletedSourceLocationsAsync(string indexId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IndexSourceLocationRecord>>([]);
    }
}
