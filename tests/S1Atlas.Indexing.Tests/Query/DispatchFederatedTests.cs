using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class DispatchFederatedTests : IAsyncDisposable
{
    private const string Collection = "dispatch";
    private const string GameBase = "Demo.GameBase::Run():System.Void";
    private const string GameOverride = "Demo.GameOverride::Run():System.Void";
    private const string GameCaller = "Demo.GameCaller::Run():System.Void";
    private const string GameDirect = "Demo.GameDirect::Run():System.Void";
    private const string ModCaller = "Demo.ModCaller::Run():System.Void";
    private const string RefBase = "Demo.RefBase::Run():System.Void";
    private const string RefOverride = "Demo.RefOverride::Run():System.Void";
    private const string RefCaller = "Demo.RefCaller::Run():System.Void";
    private const string ApiBase = "Demo.ApiBase::Run():System.Void";
    private const string ApiOverride = "Demo.ApiOverride::Run():System.Void";
    private const string ApiCaller = "Demo.ApiCaller::Run():System.Void";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-dispatch-federated-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task All_GameOverride_MergesGameAndReferenceDerived()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersAsync(
            GameOverride,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.All, Collection),
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        Assert.Equal(1, result.ExactCount);
        Assert.Equal(2, result.DerivedCount);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(
            [GameDirect, GameCaller, ModCaller],
            result.Relationships.Select(edge => edge.Source.QualifiedName));

        var exact = result.Relationships[0];
        Assert.False(exact.IsDerived);
        Assert.Null(exact.Routes);

        var route = $"via {GameBase}";
        Assert.All(result.Relationships.Skip(1), edge =>
        {
            Assert.True(edge.IsDerived);
            Assert.Equal([route], edge.Routes);
        });
        Assert.Equal("reference", result.Relationships[2].Source.Origin);
    }

    [Fact]
    public async Task All_Exact_ReturnsOnlyFactRows()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersAsync(
            GameOverride,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.All, Collection),
            TestContext.Current.CancellationToken,
            exact: true);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(1, result.ExactCount);
        Assert.Equal(0, result.DerivedCount);
        var row = Assert.Single(result.Relationships);
        Assert.Equal(GameDirect, row.Source.QualifiedName);
        Assert.False(row.IsDerived);
    }

    [Fact]
    public async Task GameScope_ReturnsGameShape()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersAsync(
            GameOverride,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExactCount);
        Assert.Equal(1, result.DerivedCount);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(
            [GameDirect, GameCaller],
            result.Relationships.Select(edge => edge.Source.QualifiedName));
        Assert.True(result.Relationships[1].IsDerived);
    }

    [Fact]
    public async Task ReferenceScope_ExpandsReferenceHierarchy()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersAsync(
            RefOverride,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Reference, Collection),
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        Assert.Equal(0, result.ExactCount);
        Assert.Equal(1, result.DerivedCount);
        var row = Assert.Single(result.Relationships);
        Assert.Equal(RefCaller, row.Source.QualifiedName);
        Assert.True(row.IsDerived);
        Assert.Equal([$"via {RefBase}"], row.Routes);
    }

    [Fact]
    public async Task ReferenceScope_GameSelector_ReturnsNotFound()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersAsync(
            GameOverride,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Reference, Collection),
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Resolution.Status);
        Assert.Empty(result.Relationships);
    }

    [Fact]
    public async Task All_ReferenceResolved_StaysReferenceOnly()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersAsync(
            RefOverride,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.All, Collection),
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        Assert.Equal("reference", result.Resolution.Symbol!.Origin);
        Assert.Equal(0, result.ExactCount);
        Assert.Equal(1, result.DerivedCount);
        var row = Assert.Single(result.Relationships);
        Assert.Equal(RefCaller, row.Source.QualifiedName);
        Assert.True(row.IsDerived);
    }

    [Fact]
    public async Task All_Paging_KeepsFactFirstWithUnknownTotals()
    {
        var service = await SeedAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersAsync(
            GameOverride,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 1, IndexQueryScope.All, Collection),
            TestContext.Current.CancellationToken);

        var row = Assert.Single(result.Relationships);
        Assert.Equal(GameDirect, row.Source.QualifiedName);
        Assert.False(row.IsDerived);
        Assert.Null(result.TotalCount);
        Assert.Null(result.ExactCount);
        Assert.Null(result.DerivedCount);
    }

    [Fact]
    public async Task ApiRelationshipsSelected_ExpandsByDefaultAndHonorsExact()
    {
        var repository = await SeedRepositoryAsync(TestContext.Current.CancellationToken);
        var service = new ApiIndexQueryService(
            repository,
            new IndexQueryService(repository));
        var selection = new ApiIndexSelection(
            CodebaseKind.S1Api,
            CodeChannel.Installed,
            ApiIndexAvailability.Current,
            "index-dispatch-api",
            "snapshot-dispatch-api",
            "api-source",
            null,
            "current");

        var expanded = await service.RelationshipsSelectedAsync(
            selection, ApiOverride, 50, ApiRelationshipDirection.Callers, null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, expanded.ExactCount);
        Assert.Equal(1, expanded.DerivedCount);
        var row = Assert.Single(expanded.Relationships);
        Assert.Equal(ApiCaller, row.Source.QualifiedName);
        Assert.True(row.IsDerived);
        Assert.Equal([$"via {ApiBase}"], row.Routes);

        var exact = await service.RelationshipsSelectedAsync(
            selection, ApiOverride, 50, ApiRelationshipDirection.Callers, null,
            TestContext.Current.CancellationToken,
            exact: true);

        Assert.Equal(0, exact.TotalCount);
        Assert.Equal(0, exact.ExactCount);
        Assert.Equal(0, exact.DerivedCount);
        Assert.Empty(exact.Relationships);
    }

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
            await TestDirectory.DeleteTreeAsync(_root);
    }

    private async Task<FederatedIndexQueryService> SeedAsync(CancellationToken cancellationToken)
    {
        var repository = await SeedRepositoryAsync(cancellationToken);
        return new FederatedIndexQueryService(repository);
    }

    private async Task<SqliteAtlasRepository> SeedRepositoryAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
        await repository.InitializeAsync(cancellationToken);
        const string buildId = "build-dispatch";
        await repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                2,
                new GameBuild(buildId, "assembly-" + buildId, "metadata-" + buildId, DateTimeOffset.Parse("2026-09-01T11:00:00Z"), true),
                new InstallationObservation("2022.3", "3164500", buildId, "C:\\game\\" + buildId, null, null),
                [],
                "0.1.0-test",
                DateTimeOffset.Parse("2026-09-01T11:00:00Z")),
            cancellationToken);
        var gameIndexId = await SeedGameRunAsync(repository, cancellationToken);
        await SeedReferenceRunAsync(repository, gameIndexId, buildId, cancellationToken);
        await SeedApiRunAsync(repository, cancellationToken);
        return repository;
    }

    private static async Task<string> SeedGameRunAsync(SqliteAtlasRepository repository, CancellationToken cancellationToken)
    {
        var snapshot = new CodeSnapshotRecord(
            "snapshot-dispatch-game", CodebaseKind.ScheduleI, CodeChannel.Installed,
            "extraction-dispatch-game", "2026-09-01T12:00:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        var run = new IndexRunRecord("index-dispatch-game", snapshot.SnapshotId, IndexRunStatus.Running, "2026-09-01T12:00:00Z");
        await repository.StartIndexRunAsync(run, cancellationToken);
        var baseMethod = Method("symbol-game-base", snapshot.SnapshotId, GameBase, CodebaseKind.ScheduleI, CodeChannel.Installed);
        var overrideMethod = Method("symbol-game-override", snapshot.SnapshotId, GameOverride, CodebaseKind.ScheduleI, CodeChannel.Installed);
        var caller = Method("symbol-game-caller", snapshot.SnapshotId, GameCaller, CodebaseKind.ScheduleI, CodeChannel.Installed);
        var direct = Method("symbol-game-direct", snapshot.SnapshotId, GameDirect, CodebaseKind.ScheduleI, CodeChannel.Installed);
        await repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                [baseMethod, overrideMethod, caller, direct],
                [],
                [],
                [],
                [
                    new IndexRelationshipRecord(
                        "rel-game-override", snapshot.SnapshotId, overrideMethod.SymbolId, baseMethod.SymbolId,
                        baseMethod.QualifiedName, "Overrides", "Metadata"),
                    new IndexRelationshipRecord(
                        "rel-game-call", snapshot.SnapshotId, caller.SymbolId, baseMethod.SymbolId,
                        baseMethod.QualifiedName, "CallsVirtual", "Body"),
                    new IndexRelationshipRecord(
                        "rel-game-direct", snapshot.SnapshotId, direct.SymbolId, overrideMethod.SymbolId,
                        overrideMethod.QualifiedName, "Calls", "Body")
                ]),
            "2026-09-01T12:00:00Z",
            cancellationToken);
        return run.IndexId;
    }

    private static async Task SeedReferenceRunAsync(
        SqliteAtlasRepository repository,
        string gameIndexId,
        string buildId,
        CancellationToken cancellationToken)
    {
        var snapshot = new CodeSnapshotRecord(
            "snapshot-dispatch-reference", CodebaseKind.ReferenceMod, CodeChannel.Installed,
            Collection, "2026-09-01T13:00:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        var run = new IndexRunRecord("index-dispatch-reference", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc);
        await repository.StartIndexRunAsync(run, cancellationToken);
        var modCaller = Method("symbol-mod-caller", snapshot.SnapshotId, ModCaller, CodebaseKind.ReferenceMod, CodeChannel.Installed);
        var refBase = Method("symbol-ref-base", snapshot.SnapshotId, RefBase, CodebaseKind.ReferenceMod, CodeChannel.Installed);
        var refOverride = Method("symbol-ref-override", snapshot.SnapshotId, RefOverride, CodebaseKind.ReferenceMod, CodeChannel.Installed);
        var refCaller = Method("symbol-ref-caller", snapshot.SnapshotId, RefCaller, CodebaseKind.ReferenceMod, CodeChannel.Installed);
        var symbols = new List<IndexSymbolRecord> { modCaller, refBase, refOverride, refCaller };
        await repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                symbols,
                [],
                [],
                [],
                [
                    new IndexRelationshipRecord(
                        "rel-mod-call", snapshot.SnapshotId, modCaller.SymbolId, "symbol-game-base",
                        GameBase, "CallsVirtual", "Body"),
                    new IndexRelationshipRecord(
                        "rel-ref-override", snapshot.SnapshotId, refOverride.SymbolId, refBase.SymbolId,
                        refBase.QualifiedName, "Overrides", "Metadata"),
                    new IndexRelationshipRecord(
                        "rel-ref-call", snapshot.SnapshotId, refCaller.SymbolId, refBase.SymbolId,
                        refBase.QualifiedName, "CallsVirtual", "Body")
                ],
                ReferenceIndexContext: new ReferenceIndexContextRecord(run.IndexId, gameIndexId, buildId),
                ReferenceMods:
                [
                    new IndexReferenceModRecord(
                        Collection,
                        "Dispatch",
                        "1.0.0",
                        "MIT",
                        "mods/dispatch",
                        "dispatch-content",
                        symbols.Select(symbol => symbol.SymbolId).ToArray())
                ]),
            "2026-09-01T13:01:00Z",
            cancellationToken);
    }

    private static async Task SeedApiRunAsync(SqliteAtlasRepository repository, CancellationToken cancellationToken)
    {
        var snapshot = new CodeSnapshotRecord(
            "snapshot-dispatch-api", CodebaseKind.S1Api, CodeChannel.Installed,
            "extraction-dispatch-api", "2026-09-01T14:00:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        var run = new IndexRunRecord("index-dispatch-api", snapshot.SnapshotId, IndexRunStatus.Running, "2026-09-01T14:00:00Z");
        await repository.StartIndexRunAsync(run, cancellationToken);
        var baseMethod = Method("symbol-api-base", snapshot.SnapshotId, ApiBase, CodebaseKind.S1Api, CodeChannel.Installed);
        var overrideMethod = Method("symbol-api-override", snapshot.SnapshotId, ApiOverride, CodebaseKind.S1Api, CodeChannel.Installed);
        var caller = Method("symbol-api-caller", snapshot.SnapshotId, ApiCaller, CodebaseKind.S1Api, CodeChannel.Installed);
        await repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                [baseMethod, overrideMethod, caller],
                [],
                [],
                [],
                [
                    new IndexRelationshipRecord(
                        "rel-api-override", snapshot.SnapshotId, overrideMethod.SymbolId, baseMethod.SymbolId,
                        baseMethod.QualifiedName, "Overrides", "Metadata"),
                    new IndexRelationshipRecord(
                        "rel-api-call", snapshot.SnapshotId, caller.SymbolId, baseMethod.SymbolId,
                        baseMethod.QualifiedName, "CallsVirtual", "Body")
                ]),
            "2026-09-01T14:01:00Z",
            cancellationToken);
    }

    private static IndexSymbolRecord Method(
        string id,
        string snapshotId,
        string qualifiedName,
        CodebaseKind codebase,
        CodeChannel channel) =>
        new(
            id,
            snapshotId,
            codebase + ":" + channel + ":Method:" + qualifiedName,
            "Method",
            qualifiedName,
            "System.Void " + qualifiedName,
            false,
            BodyRecoveryStatus.Recovered);
}
