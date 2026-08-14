using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests.Sqlite;

public sealed class SqliteAtlasRepositoryIndexingTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s1atlas-index-repository-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public SqliteAtlasRepositoryIndexingTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task Failed_candidate_does_not_hide_prior_completed_index()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord("snapshot-1", CodebaseKind.ScheduleI, CodeChannel.Installed, "extraction-1", "2026-08-13T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(new IndexRunRecord("index-1", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc), cancellationToken);
        await _repository.CompleteIndexRunAsync("index-1", new IndexWriteSet(
            [new IndexSymbolRecord("symbol-1", snapshot.SnapshotId, "ScheduleI:Installed:Type:Demo.Widget", "Type", "Demo.Widget", "Demo.Widget", false)],
            [], [], [], []), "2026-08-13T00:01:00Z", cancellationToken);

        await _repository.StartIndexRunAsync(new IndexRunRecord("index-2", snapshot.SnapshotId, IndexRunStatus.Running, "2026-08-13T00:02:00Z"), cancellationToken);
        await _repository.FailIndexRunAsync("index-2", "staging failed", "2026-08-13T00:03:00Z", cancellationToken);
        await _repository.StartIndexRunAsync(new IndexRunRecord("index-2", snapshot.SnapshotId, IndexRunStatus.Running, "2026-08-13T00:04:00Z"), cancellationToken);
        await _repository.FailIndexRunAsync("index-2", "retry failed", "2026-08-13T00:05:00Z", cancellationToken);

        var latest = await _repository.GetLatestCompletedIndexAsync(CodebaseKind.ScheduleI, CodeChannel.Installed, null, cancellationToken);
        Assert.NotNull(latest);
        Assert.Equal("index-1", latest.IndexId);
        Assert.Single(await _repository.GetCompletedSymbolsAsync("index-1", cancellationToken));
        Assert.Empty(await _repository.GetCompletedSymbolsAsync("index-2", cancellationToken));
    }

    [Fact]
    public async Task Stale_running_candidate_can_be_restarted_with_the_same_identity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord("snapshot-stale", CodebaseKind.ScheduleI, CodeChannel.Installed, "extraction-stale", "2020-01-01T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(new IndexRunRecord("index-stale", snapshot.SnapshotId, IndexRunStatus.Running, "2020-01-01T00:00:00Z"), cancellationToken);

        await _repository.StartIndexRunAsync(new IndexRunRecord("index-stale", snapshot.SnapshotId, IndexRunStatus.Running, DateTimeOffset.UtcNow.ToString("O")), cancellationToken);
        await _repository.FailIndexRunAsync("index-stale", "test cleanup", DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
    }

    [Fact]
    public async Task BodyRecoveryStatus_RoundTripsForCallableSymbols_AndRemainsNullForNonCallables()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord("snapshot-body", CodebaseKind.ScheduleI, CodeChannel.Installed, "extraction-body", "2026-08-14T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(new IndexRunRecord("index-body", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc), cancellationToken);

        var symbols = new[]
        {
            new IndexSymbolRecord("type", snapshot.SnapshotId, "ScheduleI:Installed:Type:Demo.Widget", "Type", "Demo.Widget", "Demo.Widget", false, null),
            new IndexSymbolRecord("no-body", snapshot.SnapshotId, "ScheduleI:Installed:Method:Demo.Widget::Abstract()", "Method", "Demo.Widget.Abstract", "System.Void Demo.Widget::Abstract()", false, BodyRecoveryStatus.NoBodyByDesign),
            new IndexSymbolRecord("recovered", snapshot.SnapshotId, "ScheduleI:Installed:Method:Demo.Widget::Recovered()", "Method", "Demo.Widget.Recovered", "System.Void Demo.Widget::Recovered()", false, BodyRecoveryStatus.Recovered),
            new IndexSymbolRecord("stub", snapshot.SnapshotId, "ScheduleI:Installed:Method:Demo.Widget::Stub()", "Method", "Demo.Widget.Stub", "System.Void Demo.Widget::Stub()", true, BodyRecoveryStatus.StubOrUnavailable),
            new IndexSymbolRecord("unknown", snapshot.SnapshotId, "ScheduleI:Installed:Method:Demo.Widget::Unknown()", "Method", "Demo.Widget.Unknown", "System.Void Demo.Widget::Unknown()", false, BodyRecoveryStatus.Unknown)
        };

        await _repository.CompleteIndexRunAsync(
            "index-body",
            new IndexWriteSet(symbols, [], [], [], []),
            "2026-08-14T00:01:00Z",
            cancellationToken);

        var roundTripped = await _repository.GetCompletedSymbolsAsync("index-body", cancellationToken);
        Assert.Equal(symbols.OrderBy(symbol => symbol.CanonicalKey, StringComparer.Ordinal), roundTripped);
        Assert.Null(Assert.Single(roundTripped, symbol => symbol.SymbolId == "type").BodyRecoveryStatus);
    }

    [Fact]
    public async Task Completed_symbol_lookup_is_exact_and_scoped_to_the_requested_index()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var expected = await SeedSearchIndexAsync(cancellationToken);

        var found = await _repository.GetCompletedSymbolByIdAsync(
            "index-search",
            expected.SymbolId,
            cancellationToken);

        Assert.Equal(expected, found);
        Assert.Null(await _repository.GetCompletedSymbolByIdAsync(
            "missing-index",
            expected.SymbolId,
            cancellationToken));
        Assert.Null(await _repository.GetCompletedSymbolByIdAsync(
            "index-search",
            "missing-symbol",
            cancellationToken));
    }

    [Fact]
    public async Task Completed_symbol_search_counts_exactly_ranks_deterministically_and_applies_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSearchIndexAsync(cancellationToken);

        var count = await _repository.CountCompletedSymbolMatchesAsync(
            "index-search",
            "dealer",
            cancellationToken);
        var page = await _repository.SearchCompletedSymbolsAsync(
            "index-search",
            "dealer",
            50,
            cancellationToken);

        Assert.Equal(106, count);
        Assert.Equal(50, page.Count);
        Assert.Equal("exact", page[0].SymbolId);
        Assert.Equal("terminal", page[1].SymbolId);
        Assert.Equal("prefix", page[2].SymbolId);
        Assert.Equal("substring-a", page[3].SymbolId);
        Assert.Equal("substring-b", page[4].SymbolId);
        Assert.DoesNotContain(page, symbol => symbol.SymbolId == "signature-only");
    }

    [Fact]
    public async Task Completed_symbol_search_rejects_nonpositive_limits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSearchIndexAsync(cancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _repository.SearchCompletedSymbolsAsync(
                "index-search",
                "dealer",
                0,
                cancellationToken));
    }

    private async Task<IndexSymbolRecord> SeedSearchIndexAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-search",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "extraction-search",
            "2026-08-14T01:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(
                "index-search",
                snapshot.SnapshotId,
                IndexRunStatus.Running,
                snapshot.CreatedAtUtc),
            cancellationToken);

        var exact = new IndexSymbolRecord(
            "exact",
            snapshot.SnapshotId,
            "ScheduleI:Installed:Type:Dealer",
            "Type",
            "Dealer",
            "Dealer",
            false);
        var symbols = new List<IndexSymbolRecord>
        {
            exact,
            new(
                "terminal",
                snapshot.SnapshotId,
                "ScheduleI:Installed:Type:Demo.Dealer",
                "Type",
                "Demo.Dealer",
                "Demo.Dealer",
                false),
            new(
                "prefix",
                snapshot.SnapshotId,
                "ScheduleI:Installed:Type:DealerService",
                "Type",
                "DealerService",
                "DealerService",
                false),
            new(
                "substring-b",
                snapshot.SnapshotId,
                "ScheduleI:Installed:Type:Demo.SuperDealerBeta",
                "Type",
                "Demo.SuperDealerBeta",
                "Demo.SuperDealerBeta",
                false),
            new(
                "substring-a",
                snapshot.SnapshotId,
                "ScheduleI:Installed:Type:Demo.SuperDealerAlpha",
                "Type",
                "Demo.SuperDealerAlpha",
                "Demo.SuperDealerAlpha",
                false),
            new(
                "signature-only",
                snapshot.SnapshotId,
                "ScheduleI:Installed:Method:Demo.Widget::Run()",
                "Method",
                "Demo.Widget.Run",
                "System.Void Demo.Widget::Dealer()",
                false)
        };

        for (var index = 0; index < 100; index++)
        {
            var suffix = index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            symbols.Add(new IndexSymbolRecord(
                "bulk-" + suffix,
                snapshot.SnapshotId,
                "ScheduleI:Installed:Type:Zzz.DealerMatch" + suffix,
                "Type",
                "Zzz.DealerMatch" + suffix,
                "Zzz.DealerMatch" + suffix,
                false));
        }

        await _repository.CompleteIndexRunAsync(
            "index-search",
            new IndexWriteSet(symbols, [], [], [], []),
            "2026-08-14T01:01:00Z",
            cancellationToken);
        return exact;
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
