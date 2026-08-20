using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests.Indexing;

public sealed class IndexBulkQueryTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-index-bulk-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public IndexBulkQueryTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task GetCompletedSymbolPageAsync_ReturnsStableCompletedOnlyCoverage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-bulk",
            CodebaseKind.S1Api,
            CodeChannel.Release,
            "s1api:release:bulk",
            "2026-08-20T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-bulk", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        var symbols = new[]
        {
            Symbol("a", "Alpha.Type", "Type"),
            Symbol("b", "Alpha.Type::Field", "Field"),
            Symbol("c", "Beta.Type::Run()", "Method"),
            Symbol("d", "Beta.Other", "Type")
        };
        await _repository.CompleteIndexRunAsync(
            "index-bulk",
            new IndexWriteSet(symbols, [], [], [], []),
            "2026-08-20T00:01:00Z",
            cancellationToken);

        var page = await _repository.GetCompletedSymbolPageAsync(
            "index-bulk",
            offset: 0,
            limit: 2,
            cancellationToken);

        Assert.Equal(4, await _repository.CountCompletedSymbolsAsync("index-bulk", cancellationToken));
        Assert.Equal(2, page.Count);
        Assert.Equal(
            symbols.OrderBy(symbol => symbol.CanonicalKey, StringComparer.Ordinal).Take(2),
            page);
    }

    private static IndexSymbolRecord Symbol(string id, string key, string kind) =>
        new(id, "snapshot-bulk", "S1Api:Release:" + kind + ":" + key, kind, key, key, false);

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
