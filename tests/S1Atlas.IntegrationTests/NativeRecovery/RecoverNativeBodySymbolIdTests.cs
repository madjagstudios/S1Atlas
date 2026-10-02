using S1Atlas.Cli.Commands;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.IntegrationTests.NativeRecovery;

public sealed class RecoverNativeBodySymbolIdTests : IAsyncDisposable
{
    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(), "s1atlas-recover-symbol-id-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public RecoverNativeBodySymbolIdTests()
    {
        Directory.CreateDirectory(_dataDirectory);
        _repository = new SqliteAtlasRepository(Path.Combine(_dataDirectory, "atlas.db"));
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_dataDirectory);
    }

    [Fact]
    public async Task ResolveSymbolIds_unique_prefix_resolves_and_passthroughs_survive()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);
        var fullId = "abcdef12" + new string('0', 56);
        var otherId = "12345678" + new string('0', 56);
        await SeedCompletedIndexAsync(
            "index-unique", "snapshot-unique", [fullId, otherId], ct);

        var resolution = await RecoverNativeBodyCommand.ResolveSymbolIdsAsync(
            _repository, "index-unique", ["ABCDEF12", otherId, "native-target"], ct);

        Assert.Null(resolution.AmbiguousMessage);
        Assert.Equal([fullId, otherId, "native-target"], resolution.Resolved);
    }

    [Fact]
    public async Task ResolveSymbolIds_unknown_prefix_passes_through()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);
        await SeedCompletedIndexAsync(
            "index-unknown", "snapshot-unknown", ["abcdef12" + new string('0', 56)], ct);

        var resolution = await RecoverNativeBodyCommand.ResolveSymbolIdsAsync(
            _repository, "index-unknown", ["99999999"], ct);

        Assert.Null(resolution.AmbiguousMessage);
        Assert.Equal(["99999999"], resolution.Resolved);
    }

    [Fact]
    public async Task ResolveSymbolIds_ambiguous_prefix_lists_matches_with_total()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);
        var first = "abcdef12" + new string('0', 56);
        var second = "abcdef12" + new string('1', 56);
        await SeedCompletedIndexAsync("index-amb", "snapshot-amb", [first, second], ct);

        var resolution = await RecoverNativeBodyCommand.ResolveSymbolIdsAsync(
            _repository, "index-amb", ["abcdef12"], ct);

        Assert.NotNull(resolution.AmbiguousMessage);
        Assert.Contains("matches 2 symbols", resolution.AmbiguousMessage, StringComparison.Ordinal);
        Assert.Contains(first, resolution.AmbiguousMessage, StringComparison.Ordinal);
        Assert.Contains(second, resolution.AmbiguousMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveSymbolIds_truncated_ambiguity_reports_exact_total()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(ct);
        var ids = Enumerable.Range(0, 12)
            .Select(index => "abcdef00" + index.ToString("x2") + new string('f', 54))
            .ToArray();
        await SeedCompletedIndexAsync("index-trunc", "snapshot-trunc", ids, ct);

        var resolution = await RecoverNativeBodyCommand.ResolveSymbolIdsAsync(
            _repository, "index-trunc", ["abcdef00"], ct);

        Assert.NotNull(resolution.AmbiguousMessage);
        Assert.Contains("matches 12 symbols", resolution.AmbiguousMessage, StringComparison.Ordinal);
        Assert.Contains("(showing 10 of 12)", resolution.AmbiguousMessage, StringComparison.Ordinal);
        Assert.Contains(ids[0], resolution.AmbiguousMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(ids[11], resolution.AmbiguousMessage, StringComparison.Ordinal);
    }

    private async Task SeedCompletedIndexAsync(
        string indexId,
        string snapshotId,
        IReadOnlyList<string> symbolIds,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await _repository.CreateCodeSnapshotAsync(
            new CodeSnapshotRecord(
                snapshotId, CodebaseKind.ScheduleI, CodeChannel.Installed, "extraction-" + indexId, now),
            ct);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(indexId, snapshotId, IndexRunStatus.Running, now), ct);
        var symbols = symbolIds
            .Select((symbolId, i) => new IndexSymbolRecord(
                symbolId,
                snapshotId,
                $"ScheduleI:Installed:Method:Ns::M{i}():System.Void",
                "Method",
                $"Ns.M{i}",
                $"Ns::M{i}():System.Void",
                false))
            .ToArray();
        await _repository.CompleteIndexRunAsync(
            indexId, new IndexWriteSet(symbols, [], [], [], []), now, ct);
    }
}
