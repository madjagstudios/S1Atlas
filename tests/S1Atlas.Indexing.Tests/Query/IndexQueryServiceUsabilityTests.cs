using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class IndexQueryServiceUsabilityTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-query-usability-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public IndexQueryServiceUsabilityTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task Search_returns_exact_total_and_bounded_returned_count()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        await SeedChannelAsync(
            CodeChannel.Release,
            [
                Symbol("release-a", CodeChannel.Release, "DealerAlpha"),
                Symbol("release-b", CodeChannel.Release, "DealerBeta"),
                Symbol("release-c", CodeChannel.Release, "DealerGamma")
            ],
            cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.SearchAsync(
            "dealer",
            new IndexQueryOptions(CodebaseKind.S1Api, CodeChannel.Release, Limit: 2),
            cancellationToken);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.ReturnedCount);
        Assert.Equal(2, result.Results.Count);
    }

    [Fact]
    public async Task All_channels_are_merged_in_global_rank_order_before_one_total_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        await SeedChannelAsync(
            CodeChannel.Release,
            [Symbol("release-substring", CodeChannel.Release, "Demo.SuperDealerArchive")],
            cancellationToken);
        await SeedChannelAsync(
            CodeChannel.Preview,
            [Symbol("preview-exact", CodeChannel.Preview, "Dealer")],
            cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.SearchAsync(
            "dealer",
            new IndexQueryOptions(
                CodebaseKind.S1Api,
                Channel: CodeChannel.Release,
                AllChannels: true,
                Limit: 1),
            cancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(1, result.ReturnedCount);
        var best = Assert.Single(result.Results);
        Assert.Equal("Preview", best.Channel);
        Assert.Equal("preview-exact", best.SymbolId);
    }

    [Fact]
    public async Task Search_rejects_nonpositive_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SearchAsync(
                "dealer",
                new IndexQueryOptions(CodebaseKind.S1Api, CodeChannel.Release, Limit: 0),
                cancellationToken));
    }

    private async Task SeedChannelAsync(
        CodeChannel channel,
        IReadOnlyList<IndexSymbolRecord> symbols,
        CancellationToken cancellationToken)
    {
        var snapshotId = "snapshot-" + channel;
        var indexId = "index-" + channel;
        var snapshot = new CodeSnapshotRecord(
            snapshotId,
            CodebaseKind.S1Api,
            channel,
            "source-" + channel,
            "2026-08-14T04:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);
        await _repository.CompleteIndexRunAsync(
            indexId,
            new IndexWriteSet(symbols, [], [], [], []),
            "2026-08-14T04:01:00Z",
            cancellationToken);
    }

    private static IndexSymbolRecord Symbol(string id, CodeChannel channel, string qualifiedName) =>
        new(
            id,
            "snapshot-" + channel,
            "S1Api:" + channel + ":Type:" + qualifiedName,
            "Type",
            qualifiedName,
            qualifiedName,
            false);

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
