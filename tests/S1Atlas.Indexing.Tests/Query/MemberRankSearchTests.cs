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

    private async Task SeedRankIndexAsync(CancellationToken cancellationToken)
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
                    symbol("g-runfast", gameSnapshot.SnapshotId, "Method", "Demo.Aaa::RunFast():System.Void")
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
                    symbol("r-typerun", refSnapshot.SnapshotId, "Type", "Qol.Run")
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
                        ["r-run", "r-typerun"])
                ]),
            "2026-08-20T00:02:00Z",
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await TestDirectory.DeleteTreeAsync(_root);
    }
}
