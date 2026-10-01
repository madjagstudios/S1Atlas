using Microsoft.Data.Sqlite;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Migrations;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Storage.Tests.Indexing;

public sealed class RankedSymbolSearchTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-ranked-search-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;
    private readonly string _databasePath;

    public RankedSymbolSearchTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "atlas.db");
        _repository = new SqliteAtlasRepository(_databasePath);
    }

    [Fact]
    public async Task RankedSearch_OrdersExactSimpleNameBeforeSubstring()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRankedIndexAsync(cancellationToken);

        var total = await _repository.CountRankedSymbolMatchesAsync(
            "index-ranked", "Movement", cancellationToken);
        var results = await _repository.SearchRankedSymbolsAsync(
            "index-ranked", "Movement", 50, cancellationToken);

        Assert.Equal(2, total);
        Assert.Equal(
            new[] { "method-movement", "method-player-movement" },
            results.Select(symbol => symbol.SymbolId).ToArray());
    }

    [Fact]
    public async Task RankedSearch_SubstringMatchesInsideCamelCaseName()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRankedIndexAsync(cancellationToken);

        var results = await _repository.SearchRankedSymbolsAsync(
            "index-ranked", "Player", 50, cancellationToken);

        Assert.Contains(results, symbol => symbol.SymbolId == "method-player-movement");
        Assert.DoesNotContain(results, symbol => symbol.SymbolId == "type-widget");
    }

    [Fact]
    public async Task RankedSearch_TwoCharacterQueryUsesNamePrefix()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRankedIndexAsync(cancellationToken);

        var total = await _repository.CountRankedSymbolMatchesAsync(
            "index-ranked", "Ac", cancellationToken);
        var results = await _repository.SearchRankedSymbolsAsync(
            "index-ranked", "Ac", 50, cancellationToken);

        Assert.Equal(2, total);
        Assert.Equal(
            new[] { "field-rust", "type-actor" },
            results.Select(symbol => symbol.SymbolId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task RankedSearch_OneCharacterQueryUsesNamePrefix()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRankedIndexAsync(cancellationToken);

        var results = await _repository.SearchRankedSymbolsAsync(
            "index-ranked", "R", 50, cancellationToken);

        Assert.Equal("type-run", Assert.Single(results).SymbolId);
    }

    [Fact]
    public async Task RankedSearch_HonorsKindFilterAndLimitWithTrueTotals()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRankedIndexAsync(cancellationToken);

        var total = await _repository.CountRankedSymbolMatchesAsync(
            "index-ranked", "Widget", cancellationToken, "Method");
        var results = await _repository.SearchRankedSymbolsAsync(
            "index-ranked", "Widget", 1, cancellationToken, "Method");

        Assert.Equal(3, total);
        Assert.Single(results);
        Assert.All(results, symbol => Assert.Equal("Method", symbol.Kind));
    }

    [Fact]
    public async Task RankedSearch_OrderIsDeterministic()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRankedIndexAsync(cancellationToken);

        var first = await _repository.SearchRankedSymbolsAsync(
            "index-ranked", "Run", 50, cancellationToken);
        var second = await _repository.SearchRankedSymbolsAsync(
            "index-ranked", "Run", 50, cancellationToken);

        Assert.Equal(
            first.Select(symbol => symbol.SymbolId).ToArray(),
            second.Select(symbol => symbol.SymbolId).ToArray());
        Assert.Equal("method-runner-start", first[^1].SymbolId);
    }

    [Fact]
    public async Task SymbolWriter_SetsSimpleNameAndTriggerKeepsFtsInSync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-sync",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "source-sync",
            "2026-08-20T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-sync", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);
        await _repository.CompleteIndexRunAsync(
            "index-sync",
            new IndexWriteSet(
                [new IndexSymbolRecord(
                    "symbol-fresh", snapshot.SnapshotId, "key-fresh", "Method",
                    "Zebra.Fresh", "System.Void Zebra::Fresh()", false)],
                [], [], [], []),
            "2026-08-20T00:01:00Z",
            cancellationToken);

        await using var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT simple_name FROM symbols WHERE symbol_id = 'symbol-fresh';";
            Assert.Equal("Fresh", Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)));
        }

        var results = await _repository.SearchRankedSymbolsAsync(
            "index-sync", "Fresh", 10, cancellationToken);
        Assert.Equal("symbol-fresh", Assert.Single(results).SymbolId);
    }

    [Fact]
    public async Task SupportsSymbolSearchIndex_FalseBeforeMigrationTrueAfter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var legacyPath = Path.Combine(_root, "legacy.db");
        await new SqliteMigrationRunner(
            legacyPath,
            Path.Combine(_root, "backups-legacy"),
            SqliteMigrations.All.Take(15).ToArray())
            .MigrateAsync(cancellationToken);
        var legacy = new ReadOnlySqliteAtlasRepository(
            new ReadOnlySqliteConnectionFactory(legacyPath));

        Assert.False(await legacy.SupportsSymbolSearchIndexAsync(cancellationToken));

        await _repository.InitializeAsync(cancellationToken);
        Assert.True(await _repository.SupportsSymbolSearchIndexAsync(cancellationToken));
    }

    [Fact]
    public async Task ShortQuery_UsesQualifiedNameIndexInsteadOfFullScan()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-plan",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "source-plan",
            "2026-08-20T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-plan", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        var symbols = new List<IndexSymbolRecord>(20000);
        for (var number = 0; number < 20000; number++)
        {
            var name = $"P{number % 50:00}.Widget{number}.Run";
            symbols.Add(new IndexSymbolRecord(
                $"symbol-{number}", snapshot.SnapshotId, $"key-{number}",
                "Method", name, $"System.Void {name}()", false));
        }

        await _repository.CompleteIndexRunAsync(
            "index-plan",
            new IndexWriteSet(symbols, [], [], [], []),
            "2026-08-20T00:01:00Z",
            cancellationToken);

        await using var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using (var analyze = connection.CreateCommand())
        {
            analyze.CommandText = "ANALYZE;";
            await analyze.ExecuteNonQueryAsync(cancellationToken);
        }

        // Mirrors the short-query branch of SearchRankedSymbolsAsync: a
        // two-character prefix must seek the NOCASE name index, never scan.
        await using var plan = connection.CreateCommand();
        plan.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT symbol.symbol_id
            FROM symbols AS symbol
            INNER JOIN index_runs AS run ON run.snapshot_id = symbol.snapshot_id
            WHERE run.index_id = $indexId
              AND run.status = 'Completed'
              AND symbol.qualified_name LIKE $prefix ESCAPE '\'
            LIMIT 20;
            """;
        plan.Parameters.AddWithValue("$indexId", "index-plan");
        plan.Parameters.AddWithValue("$prefix", "P07%");
        var details = new List<string>();
        await using var reader = await plan.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            details.Add(reader.GetString(3));
        Assert.Contains(details, detail => detail.Contains("USING INDEX ix_symbols_qualified_name", StringComparison.Ordinal));
        Assert.DoesNotContain(details, detail => detail.Contains("SCAN symbols", StringComparison.Ordinal));
    }

    private async Task SeedRankedIndexAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-ranked",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "source-ranked",
            "2026-08-20T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-ranked", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        IndexSymbolRecord symbol(string id, string qualified, string kind, string signature) =>
            new(id, snapshot.SnapshotId, "key-" + id, kind, qualified, signature, false);
        await _repository.CompleteIndexRunAsync(
            "index-ranked",
            new IndexWriteSet(
                [
                    symbol("type-run", "Run", "Type", "Run"),
                    symbol("method-run", "Demo.Widget.Run", "Method", "System.Void Demo.Widget::Run()"),
                    symbol("method-runner-start", "Demo.Runner.Start", "Method", "System.Void Demo.Runner::Start()"),
                    symbol("type-widget", "Demo.Widget", "Type", "Demo.Widget"),
                    symbol("method-movement", "Demo.Motion.Movement", "Method", "System.Void Demo.Motion::Movement()"),
                    symbol("method-player-movement", "Demo.Motion.PlayerMovement", "Method", "System.Void Demo.Motion::PlayerMovement()"),
                    symbol("field-rust", "Ac.Rust", "Field", "System.Int32 Ac::Rust"),
                    symbol("type-actor", "Actor", "Type", "Actor"),
                    symbol("method-activate", "Demo.Widget.Activate", "Method", "System.Void Demo.Widget::Activate()"),
                    symbol("method-widget-helper", "Demo.Widget.Helper", "Method", "System.Void Demo.Widget::Helper()")
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
