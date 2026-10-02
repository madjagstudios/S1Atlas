using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
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
            new IndexSymbolRecord("type", snapshot.SnapshotId, "ScheduleI:Installed:Type:Demo.Widget", "Type", "Demo.Widget", "Demo.Widget", false, null, true),
            new IndexSymbolRecord("no-body", snapshot.SnapshotId, "ScheduleI:Installed:Method:Demo.Widget::Abstract()", "Method", "Demo.Widget.Abstract", "System.Void Demo.Widget::Abstract()", false, BodyRecoveryStatus.NoBodyByDesign),
            new IndexSymbolRecord("recovered", snapshot.SnapshotId, "ScheduleI:Installed:Method:Demo.Widget::Recovered()", "Method", "Demo.Widget.Recovered", "System.Void Demo.Widget::Recovered()", false, BodyRecoveryStatus.Recovered),
            new IndexSymbolRecord("stub", snapshot.SnapshotId, "ScheduleI:Installed:Method:Demo.Widget::Stub()", "Method", "Demo.Widget.Stub", "System.Void Demo.Widget::Stub()", true, BodyRecoveryStatus.StubOrUnavailable),
            new IndexSymbolRecord("unknown", snapshot.SnapshotId, "ScheduleI:Installed:Method:Demo.Widget::Unknown()", "Method", "Demo.Widget.Unknown", "System.Void Demo.Widget::Unknown()", false, BodyRecoveryStatus.Unknown)
        };

        await _repository.CompleteIndexRunAsync(
            "index-body",
            new IndexWriteSet(
                symbols,
                [],
                [],
                [],
                [],
                [new IndexCallableSurfaceRecord(
                    "surface-body",
                    "index-body",
                    snapshot.SnapshotId,
                    "recovered",
                    "ScheduleI:Installed:Method:Demo.Widget::Recovered()",
                    "Assembly-CSharp.dll",
                    "interop-hash",
                    "public void Demo.Widget::Recovered()",
                    CallableSurfaceKind.PublicMethodWrapper,
                    false,
                    CallableSurfaceStatus.Resolved,
                    InteropInputTrust.LocalOnly,
                    "matched interop wrapper")]),
            "2026-08-14T00:01:00Z",
            cancellationToken);

        var roundTripped = await _repository.GetCompletedSymbolsAsync("index-body", cancellationToken);
        Assert.Equal(symbols.OrderBy(symbol => symbol.CanonicalKey, StringComparer.Ordinal), roundTripped);
        Assert.True(Assert.Single(roundTripped, symbol => symbol.SymbolId == "type").IsPublic);
        Assert.Null(Assert.Single(roundTripped, symbol => symbol.SymbolId == "type").BodyRecoveryStatus);
        var callable = Assert.Single(await _repository.GetCompletedCallableSurfaceAsync("index-body", cancellationToken));
        Assert.Equal(CallableSurfaceStatus.Resolved, callable.Status);
        Assert.Equal(InteropInputTrust.LocalOnly, callable.InteropInputTrust);
        Assert.Equal(
            callable,
            Assert.Single(await _repository.GetCompletedCallableSurfaceByGameSymbolIdAsync(
                "index-body",
                "recovered",
                cancellationToken)));
    }

    [Fact]
    public async Task Callable_surface_write_rejects_a_symbol_from_another_snapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-ownership",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "extraction-ownership",
            "2026-08-14T00:02:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-ownership", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        var symbol = new IndexSymbolRecord(
            "owned-symbol",
            snapshot.SnapshotId,
            "ScheduleI:Installed:Method:Demo.Widget::Run()",
            "Method",
            "Demo.Widget.Run",
            "System.Void Demo.Widget::Run()",
            false);
        var callable = new IndexCallableSurfaceRecord(
            "surface-ownership",
            "index-ownership",
            snapshot.SnapshotId,
            "owned-symbol",
            "ScheduleI:Installed:Method:Demo.Widget::Other()",
            "Assembly-CSharp.dll",
            "interop-hash",
            "System.Void Demo.Widget::Run()",
            CallableSurfaceKind.PublicMethodWrapper,
            false,
            CallableSurfaceStatus.Resolved,
            InteropInputTrust.LocalOnly,
            "matched wrapper");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.CompleteIndexRunAsync(
                "index-ownership",
                new IndexWriteSet([symbol], [], [], [], [], [callable]),
                "2026-08-14T00:03:00Z",
                cancellationToken));

        await _repository.FailIndexRunAsync(
            "index-ownership",
            "test cleanup",
            "2026-08-14T00:04:00Z",
            cancellationToken);
        Assert.Empty(await _repository.GetCompletedCallableSurfaceAsync("index-ownership", cancellationToken));
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
    public async Task Completed_canonical_key_lookup_returns_only_the_exact_candidate_from_the_requested_completed_index()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var expected = await SeedSearchIndexAsync(cancellationToken);

        var found = await _repository.GetCompletedSymbolByCanonicalKeyAsync(
            "index-search",
            expected.CanonicalKey,
            cancellationToken);

        Assert.Equal(expected, Assert.Single(found));
        Assert.Empty(await _repository.GetCompletedSymbolByCanonicalKeyAsync(
            "index-search",
            expected.CanonicalKey + "Proxy",
            cancellationToken));
        Assert.Empty(await _repository.GetCompletedSymbolByCanonicalKeyAsync(
            "missing-index",
            expected.CanonicalKey,
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
    public async Task Completed_symbol_search_stays_bounded_with_thousands_of_matches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-large-search",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "extraction-large-search",
            "2026-08-14T02:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord(
                "index-large-search",
                snapshot.SnapshotId,
                IndexRunStatus.Running,
                snapshot.CreatedAtUtc),
            cancellationToken);

        var symbols = Enumerable.Range(0, 2000)
            .Select(index =>
            {
                var suffix = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                return new IndexSymbolRecord(
                    "large-" + suffix,
                    snapshot.SnapshotId,
                    "ScheduleI:Installed:Type:Bulk.Dealer" + suffix,
                    "Type",
                    "Bulk.Dealer" + suffix,
                    "Bulk.Dealer" + suffix,
                    false);
            })
            .ToArray();
        await _repository.CompleteIndexRunAsync(
            "index-large-search",
            new IndexWriteSet(symbols, [], [], [], []),
            "2026-08-14T02:01:00Z",
            cancellationToken);

        var count = await _repository.CountCompletedSymbolMatchesAsync(
            "index-large-search",
            "dealer",
            cancellationToken);
        var page = await _repository.SearchCompletedSymbolsAsync(
            "index-large-search",
            "dealer",
            37,
            cancellationToken);

        Assert.Equal(2000, count);
        Assert.Equal(37, page.Count);
        Assert.Equal("large-0000", page[0].SymbolId);
        Assert.Equal("large-0036", page[^1].SymbolId);
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

    [Fact]
    public async Task Completed_symbol_prefix_lookup_returns_binary_ordered_matches_honoring_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedPrefixIndexAsync(cancellationToken);

        var matches = await _repository.GetCompletedSymbolsByIdPrefixAsync(
            "index-prefix", "abc", 10, cancellationToken);

        Assert.Equal(["abc001", "abc002"], matches.Select(symbol => symbol.SymbolId));

        var limited = await _repository.GetCompletedSymbolsByIdPrefixAsync(
            "index-prefix", "abc", 1, cancellationToken);

        Assert.Equal(["abc001"], limited.Select(symbol => symbol.SymbolId));
    }

    [Fact]
    public async Task Completed_symbol_prefix_lookup_is_scoped_to_completed_indexes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedPrefixIndexAsync(cancellationToken);

        Assert.Empty(await _repository.GetCompletedSymbolsByIdPrefixAsync(
            "index-prefix-failed", "abc", 10, cancellationToken));
        Assert.Empty(await _repository.GetCompletedSymbolsByIdPrefixAsync(
            "index-prefix", "zzz", 10, cancellationToken));

        var other = await _repository.GetCompletedSymbolsByIdPrefixAsync(
            "index-prefix-other", "abc", 10, cancellationToken);

        Assert.Equal(["abc888"], other.Select(symbol => symbol.SymbolId));
    }

    [Fact]
    public async Task Completed_symbol_prefix_lookup_matches_metacharacters_literally()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedPrefixIndexAsync(cancellationToken);

        var percent = await _repository.GetCompletedSymbolsByIdPrefixAsync(
            "index-prefix", "ab%", 10, cancellationToken);
        var underscore = await _repository.GetCompletedSymbolsByIdPrefixAsync(
            "index-prefix", "ab_", 10, cancellationToken);
        var backslash = await _repository.GetCompletedSymbolsByIdPrefixAsync(
            "index-prefix", "ab\\", 10, cancellationToken);
        var bracket = await _repository.GetCompletedSymbolsByIdPrefixAsync(
            "index-prefix", "ab[", 10, cancellationToken);
        var star = await _repository.GetCompletedSymbolsByIdPrefixAsync(
            "index-prefix", "ab*", 10, cancellationToken);
        var question = await _repository.GetCompletedSymbolsByIdPrefixAsync(
            "index-prefix", "ab?", 10, cancellationToken);
        var closeBracket = await _repository.GetCompletedSymbolsByIdPrefixAsync(
            "index-prefix", "ab]", 10, cancellationToken);

        Assert.Equal(["ab%001"], percent.Select(symbol => symbol.SymbolId));
        Assert.Equal(["ab_002"], underscore.Select(symbol => symbol.SymbolId));
        Assert.Equal(["ab\\001"], backslash.Select(symbol => symbol.SymbolId));
        Assert.Equal(["ab[003"], bracket.Select(symbol => symbol.SymbolId));
        Assert.Equal(["ab*004"], star.Select(symbol => symbol.SymbolId));
        Assert.Equal(["ab?005"], question.Select(symbol => symbol.SymbolId));
        Assert.Equal(["ab]006"], closeBracket.Select(symbol => symbol.SymbolId));
    }

    [Fact]
    public async Task Completed_symbol_prefix_lookup_rejects_empty_prefix_and_nonpositive_limits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedPrefixIndexAsync(cancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repository.GetCompletedSymbolsByIdPrefixAsync(
                "index-prefix", string.Empty, 10, cancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _repository.GetCompletedSymbolsByIdPrefixAsync(
                "index-prefix", "abc", 0, cancellationToken));
    }

    [Fact]
    public async Task Completed_symbol_prefix_count_matches_listed_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedPrefixIndexAsync(cancellationToken);

        Assert.Equal(2, await _repository.CountCompletedSymbolsByIdPrefixAsync(
            "index-prefix", "abc", cancellationToken));
        Assert.Equal(10, await _repository.CountCompletedSymbolsByIdPrefixAsync(
            "index-prefix", "ab", cancellationToken));
        Assert.Equal(0, await _repository.CountCompletedSymbolsByIdPrefixAsync(
            "index-prefix", "zzz", cancellationToken));
        Assert.Equal(0, await _repository.CountCompletedSymbolsByIdPrefixAsync(
            "index-prefix-failed", "ab", cancellationToken));
    }

    [Fact]
    public async Task Completed_symbol_prefix_lookup_matches_between_read_write_and_read_only_repositories()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedPrefixIndexAsync(cancellationToken);
        var readOnly = new ReadOnlySqliteAtlasRepository(
            new ReadOnlySqliteConnectionFactory(Path.Combine(_root, "atlas.db")));

        var rows = await readOnly.GetCompletedSymbolsByIdPrefixAsync(
            "index-prefix", "abc", 10, cancellationToken);
        var count = await readOnly.CountCompletedSymbolsByIdPrefixAsync(
            "index-prefix", "ab", cancellationToken);

        Assert.Equal(["abc001", "abc002"], rows.Select(symbol => symbol.SymbolId));
        Assert.Equal(10, count);
    }

    private async Task SeedPrefixIndexAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        var snapshot = new CodeSnapshotRecord(
            "snapshot-prefix",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "extraction-prefix",
            "2026-08-14T02:00:00Z");
        await _repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-prefix", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc),
            cancellationToken);

        IndexSymbolRecord Symbol(string id) =>
            new(
                id,
                snapshot.SnapshotId,
                "ScheduleI:Installed:Type:Prefix." + id.Replace("%", "Percent", StringComparison.Ordinal).Replace("_", "Under", StringComparison.Ordinal).Replace("\\", "Back", StringComparison.Ordinal).Replace("[", "Open", StringComparison.Ordinal).Replace("*", "Star", StringComparison.Ordinal).Replace("?", "Q", StringComparison.Ordinal).Replace("]", "Close", StringComparison.Ordinal),
                "Type",
                "Prefix." + id,
                "Prefix." + id,
                false);

        await _repository.CompleteIndexRunAsync(
            "index-prefix",
            new IndexWriteSet(
                [Symbol("abc001"), Symbol("abc002"), Symbol("abd001"), Symbol("ABC999"), Symbol("ab%001"), Symbol("ab_002"), Symbol("ab\\001"), Symbol("ab[003"), Symbol("ab*004"), Symbol("ab?005"), Symbol("ab]006"), Symbol("xyz001")],
                [], [], [], []),
            "2026-08-14T02:01:00Z",
            cancellationToken);

        var otherSnapshot = new CodeSnapshotRecord(
            "snapshot-prefix-other",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "extraction-prefix-other",
            "2026-08-14T02:02:00Z");
        await _repository.CreateCodeSnapshotAsync(otherSnapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-prefix-other", otherSnapshot.SnapshotId, IndexRunStatus.Running, otherSnapshot.CreatedAtUtc),
            cancellationToken);
        await _repository.CompleteIndexRunAsync(
            "index-prefix-other",
            new IndexWriteSet(
                [new IndexSymbolRecord("abc888", otherSnapshot.SnapshotId, "ScheduleI:Installed:Type:Prefix.Other", "Type", "Prefix.Other", "Prefix.Other", false)],
                [], [], [], []),
            "2026-08-14T02:03:00Z",
            cancellationToken);

        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-prefix-failed", snapshot.SnapshotId, IndexRunStatus.Running, "2026-08-14T02:04:00Z"),
            cancellationToken);
        await _repository.FailIndexRunAsync("index-prefix-failed", "prefix test cleanup", "2026-08-14T02:05:00Z", cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }
}
