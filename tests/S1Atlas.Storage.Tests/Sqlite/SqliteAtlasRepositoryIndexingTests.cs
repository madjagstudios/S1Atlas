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
        Assert.Equal(symbols, roundTripped);
        Assert.Null(Assert.Single(roundTripped, symbol => symbol.SymbolId == "type").BodyRecoveryStatus);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}