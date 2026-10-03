using Microsoft.Data.Sqlite;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Storage.Tests.Indexing;

public sealed class MemberSimpleNameSearchTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-member-simple-name-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;
    private readonly string _databasePath;

    public MemberSimpleNameSearchTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
        _repository = new SqliteAtlasRepository(_databasePath);
    }

    [Fact]
    public async Task SymbolWriter_StoresMemberNameForMemberRow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedMemberIndexAsync(cancellationToken);

        await using var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT simple_name FROM symbols WHERE symbol_id = 'symbol-foo';";
            Assert.Equal("Foo", Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM symbols_fts WHERE symbols_fts MATCH '{simple_name} : \"Foo\"';";
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)));
        }
    }

    [Fact]
    public async Task RankedSearch_MethodFoundByExactSimpleNameFirst()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedMemberIndexAsync(cancellationToken);

        var total = await _repository.CountRankedSymbolMatchesAsync(
            "index-member", "Up", cancellationToken);
        var results = await _repository.SearchRankedSymbolsAsync(
            "index-member", "Up", 50, cancellationToken);

        Assert.Equal(2, total);
        Assert.Equal(
            new[] { "symbol-up", "symbol-update" },
            results.Select(symbol => symbol.SymbolId).ToArray());
    }

    [Fact]
    public async Task RankedSearch_TwoCharacterQueryIgnoresReturnType()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedMemberIndexAsync(cancellationToken);

        var total = await _repository.CountRankedSymbolMatchesAsync(
            "index-member", "In", cancellationToken);
        var results = await _repository.SearchRankedSymbolsAsync(
            "index-member", "In", 50, cancellationToken);

        Assert.Equal(0, total);
        Assert.Empty(results);
    }

    private async Task SeedMemberIndexAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-member",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "source-member",
            "2026-08-20T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-member", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        IndexSymbolRecord symbol(string id, string qualified, string signature) =>
            new(id, snapshot.SnapshotId, "key-" + id, "Method", qualified, signature, false);
        await _repository.CompleteIndexRunAsync(
            "index-member",
            new IndexWriteSet(
                [
                    symbol("symbol-up", "A.B.C::Up():System.Void", "System.Void A.B.C::Up()"),
                    symbol("symbol-update", "A.B.C::Update():System.Void", "System.Void A.B.C::Update()"),
                    symbol("symbol-foo", "A.B.C::Foo():System.Int32", "System.Int32 A.B.C::Foo()")
                ],
                [], [], [], []),
            "2026-08-20T00:01:00Z",
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }
}
