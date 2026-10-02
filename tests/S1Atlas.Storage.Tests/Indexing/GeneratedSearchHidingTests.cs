using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Storage.Tests.Indexing;

public sealed class GeneratedSearchHidingTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-generated-search-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;
    private readonly string _databasePath;

    public GeneratedSearchHidingTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
        _repository = new SqliteAtlasRepository(_databasePath);
    }

    [Fact]
    public async Task CompletedSearch_HidesGeneratedByDefault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHidingIndexAsync(cancellationToken);

        var hiddenTotal = await _repository.CountCompletedSymbolMatchesAsync(
            "index-hiding", "Gizmo", cancellationToken);
        var hiddenRows = await _repository.SearchCompletedSymbolsAsync(
            "index-hiding", "Gizmo", 50, cancellationToken);
        var rawTotal = await _repository.CountCompletedSymbolMatchesAsync(
            "index-hiding", "Gizmo", cancellationToken, includeGenerated: true);
        var rawRows = await _repository.SearchCompletedSymbolsAsync(
            "index-hiding", "Gizmo", 50, cancellationToken, includeGenerated: true);

        Assert.Equal(1, hiddenTotal);
        Assert.Equal("symbol-gizmo", Assert.Single(hiddenRows).SymbolId);
        Assert.Equal(2, rawTotal);
        Assert.Equal(
            ["symbol-gizmo", "symbol-gizmo-gen"],
            rawRows.Select(symbol => symbol.SymbolId).ToArray());
    }

    [Fact]
    public async Task RankedSearchFts_HidesGeneratedByDefault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHidingIndexAsync(cancellationToken);

        var hiddenTotal = await _repository.CountRankedSymbolMatchesAsync(
            "index-hiding", "Gizmo", cancellationToken);
        var hiddenRows = await _repository.SearchRankedSymbolsAsync(
            "index-hiding", "Gizmo", 50, cancellationToken);
        var rawTotal = await _repository.CountRankedSymbolMatchesAsync(
            "index-hiding", "Gizmo", cancellationToken, includeGenerated: true);
        var rawRows = await _repository.SearchRankedSymbolsAsync(
            "index-hiding", "Gizmo", 50, cancellationToken, includeGenerated: true);

        Assert.Equal(1, hiddenTotal);
        Assert.Equal("symbol-gizmo", Assert.Single(hiddenRows).SymbolId);
        Assert.Equal(2, rawTotal);
        Assert.Equal(
            ["symbol-gizmo", "symbol-gizmo-gen"],
            rawRows.Select(symbol => symbol.SymbolId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task RankedSearchPrefix_HidesGeneratedByDefault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHidingIndexAsync(cancellationToken);

        var hiddenTotal = await _repository.CountRankedSymbolMatchesAsync(
            "index-hiding", "Al", cancellationToken);
        var hiddenRows = await _repository.SearchRankedSymbolsAsync(
            "index-hiding", "Al", 50, cancellationToken);
        var rawTotal = await _repository.CountRankedSymbolMatchesAsync(
            "index-hiding", "Al", cancellationToken, includeGenerated: true);
        var rawRows = await _repository.SearchRankedSymbolsAsync(
            "index-hiding", "Al", 50, cancellationToken, includeGenerated: true);

        Assert.Equal(1, hiddenTotal);
        Assert.Equal("symbol-alpha", Assert.Single(hiddenRows).SymbolId);
        Assert.Equal(2, rawTotal);
        Assert.Equal(
            ["symbol-alpha", "symbol-alpine"],
            rawRows.Select(symbol => symbol.SymbolId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ReadOnlyRepository_HidesGeneratedByDefault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHidingIndexAsync(cancellationToken);
        var readOnly = new ReadOnlySqliteAtlasRepository(
            new ReadOnlySqliteConnectionFactory(_databasePath));

        var completedHidden = await readOnly.CountCompletedSymbolMatchesAsync(
            "index-hiding", "Gizmo", cancellationToken);
        var completedRaw = await readOnly.CountCompletedSymbolMatchesAsync(
            "index-hiding", "Gizmo", cancellationToken, includeGenerated: true);
        var rankedHidden = await readOnly.CountRankedSymbolMatchesAsync(
            "index-hiding", "Al", cancellationToken);
        var rankedRaw = await readOnly.CountRankedSymbolMatchesAsync(
            "index-hiding", "Al", cancellationToken, includeGenerated: true);
        var rankedRows = await readOnly.SearchRankedSymbolsAsync(
            "index-hiding", "Al", 50, cancellationToken);
        var completedRows = await readOnly.SearchCompletedSymbolsAsync(
            "index-hiding", "Gizmo", 50, cancellationToken, includeGenerated: true);

        Assert.Equal(1, completedHidden);
        Assert.Equal(2, completedRaw);
        Assert.Equal(1, rankedHidden);
        Assert.Equal(2, rankedRaw);
        Assert.Equal("symbol-alpha", Assert.Single(rankedRows).SymbolId);
        Assert.Equal(2, completedRows.Count);
    }

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
            await TestDirectory.DeleteTreeAsync(_root);
    }

    private async Task SeedHidingIndexAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-hiding",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "source-hiding",
            "2026-09-01T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-hiding", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);
        await _repository.CompleteIndexRunAsync(
            "index-hiding",
            new IndexWriteSet(
                [
                    Symbol("symbol-gizmo", snapshot.SnapshotId, "Acme.Gizmo"),
                    Symbol("symbol-gizmo-gen", snapshot.SnapshotId, "Acme.Gizmo+<>c::<Run>b__0_0", isGenerated: true),
                    Symbol("symbol-sprocket", snapshot.SnapshotId, "Acme.Sprocket"),
                    Symbol("symbol-alpha", snapshot.SnapshotId, "Acme.Alpha"),
                    Symbol("symbol-alpine", snapshot.SnapshotId, "Acme.Alpine", isGenerated: true)
                ],
                [], [], [], []),
            "2026-09-01T00:01:00Z",
            cancellationToken);
    }

    private static IndexSymbolRecord Symbol(
        string id,
        string snapshotId,
        string qualifiedName,
        bool isGenerated = false) =>
        new(
            id,
            snapshotId,
            "key-" + id,
            "Method",
            qualifiedName,
            "System.Void " + qualifiedName + "()",
            false,
            BodyRecoveryStatus.Recovered,
            IsGenerated: isGenerated);
}
