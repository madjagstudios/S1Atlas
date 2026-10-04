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

    [Fact]
    public async Task CompletedSearch_ExactMemberSimpleNameSharesTopTier()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedTopTierIndexAsync(cancellationToken);

        var results = await _repository.SearchCompletedSymbolsAsync(
            "index-top-tier", "Run", 50, cancellationToken);

        Assert.Equal(
            new[] { "t-run", "m-run", "t-runfoo", "m-runfast", "m-go" },
            results.Select(symbol => symbol.SymbolId).ToArray());
    }

    [Fact]
    public async Task CompletedSearch_ReadOnlyExactMemberSimpleNameSharesTopTier()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedTopTierIndexAsync(cancellationToken);
        var readOnly = new ReadOnlySqliteAtlasRepository(
            new ReadOnlySqliteConnectionFactory(_databasePath));

        var results = await readOnly.SearchCompletedSymbolsAsync(
            "index-top-tier", "Run", 50, cancellationToken);

        Assert.Equal(
            new[] { "t-run", "m-run", "t-runfoo", "m-runfast", "m-go" },
            results.Select(symbol => symbol.SymbolId).ToArray());
    }

    [Fact]
    public async Task RankedSearch_ExactMemberSimpleNameSharesTopTier()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedTopTierIndexAsync(cancellationToken);

        var total = await _repository.CountRankedSymbolMatchesAsync(
            "index-top-tier", "Run", cancellationToken);
        var results = await _repository.SearchRankedSymbolsAsync(
            "index-top-tier", "Run", 50, cancellationToken);

        Assert.Equal(5, total);
        Assert.Equal(
            new[] { "m-run", "t-run" },
            results.Take(2).Select(symbol => symbol.SymbolId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal("t-runfoo", results[2].SymbolId);
        Assert.Equal(
            new[] { "m-go", "m-runfast" },
            results.Skip(3).Select(symbol => symbol.SymbolId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task RankedSearch_ReadOnlyExactMemberSimpleNameSharesTopTier()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedTopTierIndexAsync(cancellationToken);
        var readOnly = new ReadOnlySqliteAtlasRepository(
            new ReadOnlySqliteConnectionFactory(_databasePath));

        var results = await readOnly.SearchRankedSymbolsAsync(
            "index-top-tier", "Run", 50, cancellationToken);

        Assert.Equal(
            new[] { "m-run", "t-run" },
            results.Take(2).Select(symbol => symbol.SymbolId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal("t-runfoo", results[2].SymbolId);
        Assert.Equal(
            new[] { "m-go", "m-runfast" },
            results.Skip(3).Select(symbol => symbol.SymbolId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task CompletedSearch_TerminalTypeStaysAboveSubstringMembers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedTopTierIndexAsync(cancellationToken);

        var results = await _repository.SearchCompletedSymbolsAsync(
            "index-top-tier", "Run", 50, cancellationToken);
        var ids = results.Select(symbol => symbol.SymbolId).ToArray();

        Assert.True(Array.IndexOf(ids, "t-run") < Array.IndexOf(ids, "m-runfast"));
        Assert.True(Array.IndexOf(ids, "t-run") < Array.IndexOf(ids, "m-go"));
    }

    [Fact]
    public async Task RankedSearch_TerminalTypeStaysAboveSubstringMembers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedTopTierIndexAsync(cancellationToken);

        var results = await _repository.SearchRankedSymbolsAsync(
            "index-top-tier", "Run", 50, cancellationToken);
        var ids = results.Select(symbol => symbol.SymbolId).ToArray();

        Assert.True(Array.IndexOf(ids, "t-run") < Array.IndexOf(ids, "m-go"));
        Assert.True(Array.IndexOf(ids, "t-run") < Array.IndexOf(ids, "m-runfast"));
    }

    [Theory]
    [InlineData(false, false, "Manager")]
    [InlineData(false, true, "Manager")]
    [InlineData(true, false, "Manager")]
    [InlineData(true, true, "Manager")]
    [InlineData(false, false, "Tools.Manager")]
    [InlineData(false, true, "Tools.Manager")]
    [InlineData(true, false, "Tools.Manager")]
    [InlineData(true, true, "Tools.Manager")]
    public async Task Search_TerminalTierExcludesReturnTypeOnlyMatches(bool readOnly, bool ranked, string query)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedTerminalIndexAsync(cancellationToken, query);
        IIndexRepository repository = readOnly
            ? new ReadOnlySqliteAtlasRepository(new ReadOnlySqliteConnectionFactory(_databasePath))
            : _repository;

        var results = ranked
            ? await repository.SearchRankedSymbolsAsync("index-terminal", query, 50, cancellationToken)
            : await repository.SearchCompletedSymbolsAsync("index-terminal", query, 50, cancellationToken);
        var ids = results.Select(symbol => symbol.SymbolId).ToArray();

        Assert.Equal(new[] { "terminal-namespace", "terminal-type" }, ids.Take(2).Order(StringComparer.Ordinal));
        Assert.Equal("prefix", ids[2]);
        Assert.Equal("return-only", ids[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedSearch_ReturnTypeDoesNotChangeExactMemberSecondaryOrder(bool readOnly)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedTerminalIndexAsync(cancellationToken);
        IIndexRepository repository = readOnly
            ? new ReadOnlySqliteAtlasRepository(new ReadOnlySqliteConnectionFactory(_databasePath))
            : _repository;

        var results = await repository.SearchCompletedSymbolsAsync("index-terminal", "Run", 50, cancellationToken);

        Assert.Equal(new[] { "exact-void", "exact-return" }, results.Select(symbol => symbol.SymbolId));
    }

    private async Task SeedTerminalIndexAsync(CancellationToken cancellationToken, string query = "Manager")
    {
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord("snapshot-terminal", CodebaseKind.ScheduleI, CodeChannel.Installed, "source-terminal", "2026-08-20T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(new IndexRunRecord("index-terminal", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc), cancellationToken);
        IndexSymbolRecord symbol(string id, string kind, string qualified) =>
            new(id, snapshot.SnapshotId, "key-" + id, kind, qualified, qualified, false);
        await _repository.CompleteIndexRunAsync(
            "index-terminal",
            new IndexWriteSet(
                [
                    symbol("terminal-type", "Type", "Demo." + query),
                    symbol("terminal-namespace", "Namespace", "Demo.Tools." + query),
                    symbol("prefix", "Type", query + ".Tools"),
                    symbol("return-only", "Method", "Demo.Factory::Make():Demo." + query),
                    symbol("exact-void", "Method", "Demo.Aaa::Run():System.Void"),
                    symbol("exact-return", "Method", "Demo.Zzz::Run():Demo.Run")
                ],
                [], [], [], []),
            "2026-08-20T00:01:00Z",
            cancellationToken);
    }

    private async Task SeedTopTierIndexAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-top-tier",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "source-top-tier",
            "2026-08-20T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-top-tier", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        IndexSymbolRecord symbol(string id, string kind, string qualified) =>
            new(id, snapshot.SnapshotId, "key-" + id, kind, qualified, qualified, false);
        await _repository.CompleteIndexRunAsync(
            "index-top-tier",
            new IndexWriteSet(
                [
                    symbol("m-run", "Method", "Demo.Widget::Run():System.Void"),
                    symbol("t-run", "Type", "Demo.Run"),
                    symbol("m-runfast", "Method", "Demo.Aaa::RunFast():System.Void"),
                    symbol("t-runfoo", "Type", "Run.Foo"),
                    symbol("m-go", "Method", "Demo.Run::Go():System.Void")
                ],
                [], [], [], []),
            "2026-08-20T00:01:00Z",
            cancellationToken);
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
