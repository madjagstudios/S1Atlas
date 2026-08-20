using S1Atlas.Application.Authority;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.IntegrationTests.Authority;

public sealed class InstalledBuildHistoryQueryTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-history-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public InstalledBuildHistoryQueryTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task History_preserves_all_builds_but_only_verified_entries_are_navigable_and_adjacent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var builds = await SeedBuildsAsync(cancellationToken);
        var verifiedBefore = await SeedInstalledIndexAsync("build-1", "index-1", includeSymbol: true, cancellationToken);
        var verifiedAfter = await SeedInstalledIndexAsync("build-4", "index-4", includeSymbol: false, cancellationToken);
        var authorities = new Dictionary<string, InstalledBuildAuthority>(StringComparer.Ordinal)
        {
            ["build-1"] = Authority("build-1", "extraction-1", verifiedBefore),
            ["build-2"] = new(InstalledBuildAuthorityStatus.NoCompletedIndex, "build-2", "build-2", null, null, null, "not indexed"),
            ["build-3"] = new(InstalledBuildAuthorityStatus.ExtractionIntegrityFailure, "build-3", "build-3", null, null, null, "integrity failed"),
            ["build-4"] = Authority("build-4", "extraction-4", verifiedAfter)
        };
        var query = new IndexQueryService(_repository);
        var service = new InstalledBuildHistoryQueryService(
            _repository,
            _repository,
            query,
            (buildId, _) => Task.FromResult(authorities[buildId!]));

        var result = await service.GetHistoryAsync(cancellationToken);

        Assert.Equal(4, result.Entries.Count);
        Assert.Equal(
            [InstalledBuildHistoryStatus.IndexedVerified, InstalledBuildHistoryStatus.NotIndexed, InstalledBuildHistoryStatus.IntegrityFailed, InstalledBuildHistoryStatus.IndexedVerified],
            result.Entries.OrderBy(entry => entry.Build.FirstSeenAtUtc).Select(entry => entry.Status));
        Assert.Equal(["build-1", "build-4"], result.NavigableEntries.Select(entry => entry.Build.BuildId));
        var adjacent = Assert.Single(result.AdjacentPairs);
        Assert.Equal("build-1", adjacent.Before.Build.BuildId);
        Assert.Equal("build-4", adjacent.After.Build.BuildId);

        var occurrences = await service.GetSymbolOccurrencesAsync(
            "ScheduleI:Installed:Type:Demo.History",
            result.Entries,
            cancellationToken);
        Assert.Equal(2, occurrences.Count);
        Assert.True(Assert.Single(occurrences, occurrence => occurrence.BuildId == "build-1").Present);
        Assert.False(Assert.Single(occurrences, occurrence => occurrence.BuildId == "build-4").Present);
    }

    private async Task<IReadOnlyList<GameBuild>> SeedBuildsAsync(CancellationToken cancellationToken)
    {
        var builds = Enumerable.Range(1, 4).Select(index => new GameBuild(
            "build-" + index,
            "assembly-" + index,
            "metadata-" + index,
            DateTimeOffset.Parse($"2026-08-1{index}T00:00:00Z"),
            true)).ToArray();
        foreach (var build in builds)
        {
            await _repository.SaveSnapshotAsync(
                new EnvironmentSnapshot(2, build, InstallationObservation.Unknown, [], "test", build.FirstSeenAtUtc),
                cancellationToken);
        }
        return builds;
    }

    private async Task<IndexRunRecord> SeedInstalledIndexAsync(
        string buildId,
        string indexId,
        bool includeSymbol,
        CancellationToken cancellationToken)
    {
        var snapshotId = "snapshot-" + indexId;
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "extraction-" + buildId[6..],
            "2026-08-20T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc), cancellationToken);
        IReadOnlyList<IndexSymbolRecord> symbols = includeSymbol
            ? new[] { new IndexSymbolRecord("symbol-" + indexId, snapshotId, "ScheduleI:Installed:Type:Demo.History", "Type", "Demo.History", "Demo.History", false) }
            : Array.Empty<IndexSymbolRecord>();
        await _repository.CompleteIndexRunAsync(indexId, new IndexWriteSet(symbols, [], [], [], []), "2026-08-20T00:01:00Z", cancellationToken);
        return new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Completed, snapshot.CreatedAtUtc, "2026-08-20T00:01:00Z");
    }

    private static InstalledBuildAuthority Authority(string buildId, string extractionId, IndexRunRecord run) =>
        new(InstalledBuildAuthorityStatus.Resolved, buildId, buildId, extractionId, run.IndexId, run, null);

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
