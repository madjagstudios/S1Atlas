using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class MemberRankSearchTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-member-rank-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAtlasRepository _repository;

    public MemberRankSearchTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
    }

    [Fact]
    public async Task Game_SearchAsync_ExactMemberSimpleNameSharesTopTier()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRankIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.SearchAsync(
            "Run",
            new IndexQueryOptions(CodebaseKind.ScheduleI, Scope: IndexQueryScope.Game),
            cancellationToken);

        Assert.Equal(
            new[] { "g-typerun", "g-run", "g-runfast" },
            result.Results.Select(symbol => symbol.SymbolId).ToArray());
    }

    [Fact]
    public async Task Game_SearchAsync_TerminalTypeStaysAboveSubstringMember()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRankIndexAsync(cancellationToken);
        var service = new IndexQueryService(_repository);

        var result = await service.SearchAsync(
            "Run",
            new IndexQueryOptions(CodebaseKind.ScheduleI, Scope: IndexQueryScope.Game),
            cancellationToken);
        var ids = result.Results.Select(symbol => symbol.SymbolId).ToArray();

        Assert.True(Array.IndexOf(ids, "g-typerun") < Array.IndexOf(ids, "g-runfast"));
    }

    [Fact]
    public async Task Reference_SearchAsync_ExactMemberSimpleNameSharesTopTier()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRankIndexAsync(cancellationToken);
        var service = new ReferenceModQueryService(_repository);

        var result = await service.SearchAsync(
            "Run",
            new IndexQueryOptions(CodebaseKind.ReferenceMod, Scope: IndexQueryScope.Reference, ReferenceCollection: "refcol"),
            cancellationToken);

        Assert.Equal(
            new[] { "r-run", "r-typerun" },
            result.Results.Select(symbol => symbol.SymbolId).ToArray());
    }

    [Fact]
    public async Task Federated_SearchAsync_ExactMemberSimpleNameSharesTopTierAcrossOrigins()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRankIndexAsync(cancellationToken);
        var service = new FederatedIndexQueryService(_repository);

        var result = await service.SearchAsync(
            "Run",
            new IndexQueryOptions(CodebaseKind.ScheduleI, Scope: IndexQueryScope.All, ReferenceCollection: "refcol"),
            cancellationToken);

        Assert.Equal(
            new[] { "g-typerun", "g-run", "r-run", "r-typerun", "g-runfast" },
            result.Results.Select(symbol => symbol.SymbolId).ToArray());
    }

    [Theory]
    [InlineData(IndexQueryScope.Game, "Manager")]
    [InlineData(IndexQueryScope.Reference, "Manager")]
    [InlineData(IndexQueryScope.All, "Manager")]
    [InlineData(IndexQueryScope.Game, "Tools.Manager")]
    [InlineData(IndexQueryScope.Reference, "Tools.Manager")]
    [InlineData(IndexQueryScope.All, "Tools.Manager")]
    public async Task Search_TerminalTierExcludesReturnTypeOnlyMatches(IndexQueryScope scope, string query)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedRankIndexAsync(cancellationToken, query);
        var options = new IndexQueryOptions(
            scope == IndexQueryScope.Reference ? CodebaseKind.ReferenceMod : CodebaseKind.ScheduleI,
            Scope: scope, ReferenceCollection: scope == IndexQueryScope.Game ? null : "refcol");

        var result = scope switch
        {
            IndexQueryScope.Game => await new IndexQueryService(_repository).SearchAsync(query, options, cancellationToken),
            IndexQueryScope.Reference => await new ReferenceModQueryService(_repository).SearchAsync(query, options, cancellationToken),
            _ => await new FederatedIndexQueryService(_repository).SearchAsync(query, options, cancellationToken)
        };
        var expectedOrigins = scope == IndexQueryScope.All ? new[] { "g", "r" } : new[] { scope == IndexQueryScope.Game ? "g" : "r" };
        var ids = result.Results.Select(symbol => symbol.SymbolId).ToArray();

        Assert.Equal(
            expectedOrigins.SelectMany(origin => new[] { origin + "-manager-type", origin + "-manager-namespace" }).Order(StringComparer.Ordinal),
            ids.Take(expectedOrigins.Length * 2).Order(StringComparer.Ordinal));
        Assert.Equal(expectedOrigins.Select(origin => origin + "-manager-prefix"), ids.Skip(expectedOrigins.Length * 2).Take(expectedOrigins.Length));
        Assert.Equal(expectedOrigins.Select(origin => origin + "-manager-return"), ids.TakeLast(expectedOrigins.Length));
    }

    private async Task SeedRankIndexAsync(CancellationToken cancellationToken, string terminalQuery = "Manager")
    {
        await _repository.InitializeAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var buildId = new string('a', 64);
        await _repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                2,
                new GameBuild(buildId, "assembly", "metadata", now, true),
                new InstallationObservation("1", "2", "fixture", _root, null, null),
                [],
                "test",
                now),
            cancellationToken);
        var gameSnapshot = new CodeSnapshotRecord(
            "snapshot-game",
            CodebaseKind.ScheduleI,
            CodeChannel.Installed,
            "source-game",
            "2026-08-20T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(gameSnapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-game", gameSnapshot.SnapshotId, IndexRunStatus.Running, gameSnapshot.CreatedAtUtc),
            cancellationToken);

        IndexSymbolRecord symbol(string id, string snapshotId, string kind, string qualified) =>
            new(id, snapshotId, "key-" + id, kind, qualified, qualified, false);
        await _repository.CompleteIndexRunAsync(
            "index-game",
            new IndexWriteSet(
                [
                    symbol("g-run", gameSnapshot.SnapshotId, "Method", "Demo.Widget::Run():System.Void"),
                    symbol("g-typerun", gameSnapshot.SnapshotId, "Type", "Demo.Run"),
                    symbol("g-runfast", gameSnapshot.SnapshotId, "Method", "Demo.Aaa::RunFast():System.Void"),
                    symbol("g-manager-type", gameSnapshot.SnapshotId, "Type", "Demo." + terminalQuery),
                    symbol("g-manager-namespace", gameSnapshot.SnapshotId, "Namespace", "Demo.More." + terminalQuery),
                    symbol("g-manager-prefix", gameSnapshot.SnapshotId, "Type", terminalQuery + ".Tools"),
                    symbol("g-manager-return", gameSnapshot.SnapshotId, "Method", "Demo.Factory::Make():Demo." + terminalQuery)
                ],
                [], [], [], []),
            "2026-08-20T00:01:00Z",
            cancellationToken);

        var refSnapshot = new CodeSnapshotRecord(
            "snapshot-ref",
            CodebaseKind.ReferenceMod,
            CodeChannel.Installed,
            "refcol",
            "2026-08-20T00:00:00Z");
        await _repository.CreateCodeSnapshotAsync(refSnapshot, cancellationToken);
        await _repository.StartIndexRunAsync(
            new IndexRunRecord("index-ref", refSnapshot.SnapshotId, IndexRunStatus.Running, refSnapshot.CreatedAtUtc),
            cancellationToken);
        await _repository.CompleteIndexRunAsync(
            "index-ref",
            new IndexWriteSet(
                [
                    symbol("r-run", refSnapshot.SnapshotId, "Method", "Qol.Mod::Run():System.Void"),
                    symbol("r-typerun", refSnapshot.SnapshotId, "Type", "Qol.Run"),
                    symbol("r-manager-type", refSnapshot.SnapshotId, "Type", "Qol." + terminalQuery),
                    symbol("r-manager-namespace", refSnapshot.SnapshotId, "Namespace", "Qol.More." + terminalQuery),
                    symbol("r-manager-prefix", refSnapshot.SnapshotId, "Type", terminalQuery + ".Utilities"),
                    symbol("r-manager-return", refSnapshot.SnapshotId, "Method", "Qol.Factory::Make():Qol." + terminalQuery)
                ],
                [], [], [], [],
                ReferenceIndexContext: new ReferenceIndexContextRecord("index-ref", "index-game", buildId),
                ReferenceMods:
                [
                    new IndexReferenceModRecord(
                        "qol",
                        "Quality of Life",
                        "1.0.0",
                        "MIT",
                        _root,
                        new string('c', 64),
                        ["r-run", "r-typerun", "r-manager-type", "r-manager-namespace", "r-manager-prefix", "r-manager-return"])
                ]),
            "2026-08-20T00:02:00Z",
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }
}
