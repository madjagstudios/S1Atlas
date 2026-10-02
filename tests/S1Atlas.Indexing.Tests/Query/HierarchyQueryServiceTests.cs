using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Indexing.Tests.Relationships.Parity;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class HierarchyQueryServiceTests : IAsyncDisposable
{
    private const string Ns = "S1Atlas.ParityFixture";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-hierarchy-query-" + Guid.NewGuid().ToString("N"));
    private readonly List<OwnedFixtureIndex> _indexes = [];
    private bool _disposed;

    [Fact]
    public async Task Overrides_ReturnsChainToRoot()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.OverridesInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", $"{Ns}.DispatchGrandchild::Foo():System.Int32")], 50,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.ReturnedCount);
        Assert.Collection(
            result.Nodes.OrderBy(node => node.Depth),
            first =>
            {
                Assert.Equal(1, first.Depth);
                Assert.True(first.IsDirect);
                Assert.Equal("Overrides", first.Edge.Kind);
                Assert.Equal($"{Ns}.DispatchDerived::Foo():System.Int32", first.Edge.Target.QualifiedName);
            },
            second =>
            {
                Assert.Equal(2, second.Depth);
                Assert.False(second.IsDirect);
                Assert.Equal("Overrides", second.Edge.Kind);
                Assert.Equal($"{Ns}.DispatchBase::Foo():System.Int32", second.Edge.Target.QualifiedName);
            });
    }

    [Fact]
    public async Task Overrides_IncludesInterfaceSlot()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.OverridesInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", $"{Ns}.DispatchImplicit::Serve():System.Int32")], 50,
            TestContext.Current.CancellationToken);

        var node = Assert.Single(result.Nodes);
        Assert.Equal(1, node.Depth);
        Assert.True(node.IsDirect);
        Assert.Equal("ImplementsMethod", node.Edge.Kind);
        Assert.Equal($"{Ns}.IDispatchContract::Serve():System.Int32", node.Edge.Target.QualifiedName);
    }

    [Fact]
    public async Task Overrides_ExternalTarget_ListsUnresolvedLeaf()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.OverridesInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", $"{Ns}.ExternalToString::ToString():System.String")], 50,
            TestContext.Current.CancellationToken);

        var node = Assert.Single(result.Nodes);
        Assert.Equal("Overrides", node.Edge.Kind);
        Assert.False(node.Edge.Target.Resolved);
        Assert.Equal("System.Object::ToString():System.String", node.Edge.Target.RawText);
    }

    [Fact]
    public async Task Overrides_OnType_ReturnsEmpty()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.OverridesInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Type", $"{Ns}.DispatchBase")], 50,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        Assert.Empty(result.Nodes);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task OverriddenBy_ReturnsTransitiveWithDirectFlags()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.OverriddenByInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", $"{Ns}.DispatchBase::Foo():System.Int32")], 50, 10,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Collection(
            result.Nodes.OrderBy(node => node.Depth),
            first =>
            {
                Assert.Equal(1, first.Depth);
                Assert.True(first.IsDirect);
                Assert.Equal($"{Ns}.DispatchDerived::Foo():System.Int32", first.Edge.Source.QualifiedName);
            },
            second =>
            {
                Assert.Equal(2, second.Depth);
                Assert.False(second.IsDirect);
                Assert.Equal($"{Ns}.DispatchGrandchild::Foo():System.Int32", second.Edge.Source.QualifiedName);
            });
        Assert.DoesNotContain(
            result.Nodes,
            node => node.Edge.Source.QualifiedName == $"{Ns}.DispatchNewVirtual::Foo():System.Int32");
    }

    [Fact]
    public async Task OverriddenBy_RespectsDepthLimit()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.OverriddenByInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", $"{Ns}.DispatchBase::Foo():System.Int32")], 50, 1,
            TestContext.Current.CancellationToken);

        var node = Assert.Single(result.Nodes);
        Assert.Equal($"{Ns}.DispatchDerived::Foo():System.Int32", node.Edge.Source.QualifiedName);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task OverriddenBy_InterfaceMethod_ReturnsBothImplementations()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.OverriddenByInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", $"{Ns}.IDispatchContract::Serve():System.Int32")], 50, 10,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Contains(
            result.Nodes,
            node => node.IsDirect
                && node.Edge.Kind == "ImplementsMethod"
                && node.Edge.Source.QualifiedName == $"{Ns}.DispatchImplicit::Serve():System.Int32");
        Assert.Contains(
            result.Nodes,
            node => node.IsDirect
                && node.Edge.Kind == "ImplementsMethod"
                && node.Edge.Source.QualifiedName == $"{Ns}.DispatchExplicit::{Ns}.IDispatchContract.Serve():System.Int32");
    }

    [Fact]
    public async Task OverriddenBy_InvalidDepth_Throws()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.OverriddenByInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", $"{Ns}.DispatchBase::Foo():System.Int32")], 50, 0,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Derived_ReturnsTransitiveSubtypes()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var classes = await service.DerivedInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Type", $"{Ns}.DispatchBase")], 50, 10, 0,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, classes.TotalCount);
        Assert.Contains(
            classes.Nodes,
            node => node.IsDirect && node.Edge.Source.QualifiedName == $"{Ns}.DispatchDerived");
        Assert.Contains(
            classes.Nodes,
            node => node is { Depth: 2, IsDirect: false }
                && node.Edge.Source.QualifiedName == $"{Ns}.DispatchGrandchild");

        var contracts = await service.DerivedInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Type", $"{Ns}.IDispatchContract")], 50, 10, 0,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, contracts.TotalCount);
        Assert.Contains(
            contracts.Nodes,
            node => node.IsDirect && node.Edge.Source.QualifiedName == $"{Ns}.DispatchImplicit");
        Assert.Contains(
            contracts.Nodes,
            node => node is { Depth: 2, IsDirect: false }
                && node.Edge.Source.QualifiedName == $"{Ns}.DispatchInherited");
    }

    [Fact]
    public async Task Derived_Paging_ReturnsTrueTotals()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.DerivedInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Type", $"{Ns}.IDispatchContract")], 2, 10, 1,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.ReturnedCount);
        Assert.Equal(
            [$"{Ns}.DispatchImplicit", $"{Ns}.DispatchInherited"],
            result.Nodes.Select(node => node.Edge.Source.QualifiedName));
    }

    [Fact]
    public async Task Derived_RespectsDepthLimit()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.DerivedInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Type", $"{Ns}.IDispatchContract")], 50, 1, 0,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.All(result.Nodes, node => Assert.True(node.IsDirect));
    }

    [Fact]
    public async Task Overrides_NotFound_ReturnsNotFound()
    {
        var (service, run, _) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.OverridesInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            "No.Such.Type::Nothing():System.Void", 50,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Resolution.Status);
        Assert.Empty(result.Nodes);
    }

    [Fact]
    public async Task Overrides_Ambiguous_ReturnsAmbiguous()
    {
        var (service, run, _) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.OverridesInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            "Foo", 50,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Ambiguous, result.Resolution.Status);
        Assert.Empty(result.Nodes);
    }

    [Fact]
    public async Task CurrentIndex_Variants_MatchInIndexResults()
    {
        var (service, _, ids) = await GameAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game);

        var overrides = await service.OverridesAsync(
            ids[("Method", $"{Ns}.DispatchDerived::Foo():System.Int32")], options,
            TestContext.Current.CancellationToken);
        var overriddenBy = await service.OverriddenByAsync(
            ids[("Method", $"{Ns}.DispatchBase::Foo():System.Int32")], options, 10,
            TestContext.Current.CancellationToken);
        var derived = await service.DerivedAsync(
            ids[("Type", $"{Ns}.DispatchBase")], options, 10, 0,
            TestContext.Current.CancellationToken);

        Assert.Equal($"{Ns}.DispatchBase::Foo():System.Int32", Assert.Single(overrides.Nodes).Edge.Target.QualifiedName);
        Assert.Equal(2, overriddenBy.TotalCount);
        Assert.Equal(4, derived.TotalCount);
    }

    [Fact]
    public async Task FederatedOverriddenBy_MergesScopes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new SqliteAtlasRepository(Path.Combine(_root, "atlas.db"));
        await repository.InitializeAsync(cancellationToken);
        const string buildId = "build-hierarchy";
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
        var collection = await SeedReferenceRunAsync(repository, gameIndexId, buildId, cancellationToken);
        var service = new FederatedIndexQueryService(repository);
        var selector = "Demo.Base::Run():System.Void";

        var game = await service.OverriddenByAsync(
            selector,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game),
            10,
            cancellationToken);
        var reference = await service.OverriddenByAsync(
            selector,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Reference, collection),
            10,
            cancellationToken);
        var ambiguous = await service.OverriddenByAsync(
            selector,
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.All, collection),
            10,
            cancellationToken);

        Assert.Equal(["Demo.Game::Run():System.Void"], game.Nodes.Select(node => node.Edge.Source.QualifiedName));
        Assert.Equal(["Demo.Reference::Run():System.Void"], reference.Nodes.Select(node => node.Edge.Source.QualifiedName));
        Assert.Equal(SymbolResolutionStatus.Ambiguous, ambiguous.Resolution.Status);
        Assert.Empty(ambiguous.Nodes);

        var merged = await service.OverriddenByAsync(
            "Demo.Solo::Run():System.Void",
            new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.All, collection),
            10,
            cancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, merged.Resolution.Status);
        Assert.Equal(2, merged.TotalCount);
        Assert.Equal(
            ["Demo.Mod::Run():System.Void", "Demo.SoloGame::Run():System.Void"],
            merged.Nodes.Select(node => node.Edge.Source.QualifiedName));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var index in _indexes)
            await index.DisposeAsync();
        if (Directory.Exists(_root))
            await TestDirectory.DeleteTreeAsync(_root);
    }

    private async Task<(IndexQueryService Service, IndexRunRecord Run, Dictionary<(string Kind, string Name), string> Ids)> GameAsync(
        CancellationToken cancellationToken)
    {
        var index = await RelationshipParityHarness.IndexFixtureAsync(cancellationToken);
        _indexes.Add(index);
        var service = new IndexQueryService(index.Repository);
        var ids = await RelationshipParityHarness.MapKindsAndNamesToIdsAsync(
            index.Repository, index.Run.IndexId, cancellationToken);
        return (service, index.Run, ids);
    }

    private static async Task<string> SeedGameRunAsync(SqliteAtlasRepository repository, CancellationToken cancellationToken)
    {
        var snapshot = new CodeSnapshotRecord(
            "snapshot-hierarchy-game", CodebaseKind.ScheduleI, CodeChannel.Installed,
            "extraction-hierarchy-game", "2026-09-01T12:00:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        var run = new IndexRunRecord("index-hierarchy-game", snapshot.SnapshotId, IndexRunStatus.Running, "2026-09-01T12:00:00Z");
        await repository.StartIndexRunAsync(run, cancellationToken);
        var baseMethod = Method("symbol-base", snapshot.SnapshotId, "Demo.Base::Run():System.Void");
        var gameMethod = Method("symbol-game", snapshot.SnapshotId, "Demo.Game::Run():System.Void");
        var soloMethod = Method("symbol-solo", snapshot.SnapshotId, "Demo.Solo::Run():System.Void");
        var soloGameMethod = Method("symbol-solo-game", snapshot.SnapshotId, "Demo.SoloGame::Run():System.Void");
        await repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                [baseMethod, gameMethod, soloMethod, soloGameMethod],
                [],
                [],
                [],
                [
                    new IndexRelationshipRecord(
                        "rel-game", snapshot.SnapshotId, gameMethod.SymbolId, baseMethod.SymbolId,
                        baseMethod.QualifiedName, "Overrides", "Metadata"),
                    new IndexRelationshipRecord(
                        "rel-solo-game", snapshot.SnapshotId, soloGameMethod.SymbolId, soloMethod.SymbolId,
                        soloMethod.QualifiedName, "Overrides", "Metadata")
                ]),
            "2026-09-01T12:00:00Z",
            cancellationToken);
        return run.IndexId;
    }

    private static async Task<string> SeedReferenceRunAsync(
        SqliteAtlasRepository repository,
        string gameIndexId,
        string buildId,
        CancellationToken cancellationToken)
    {
        const string collection = "hierarchy";
        var snapshot = new CodeSnapshotRecord(
            "snapshot-hierarchy-reference", CodebaseKind.ReferenceMod, CodeChannel.Installed,
            collection, "2026-09-01T13:00:00Z");
        await repository.CreateCodeSnapshotAsync(snapshot, cancellationToken);
        var run = new IndexRunRecord("index-hierarchy-reference", snapshot.SnapshotId, IndexRunStatus.Running, snapshot.CreatedAtUtc);
        await repository.StartIndexRunAsync(run, cancellationToken);
        var baseMethod = Method("symbol-ref-base", snapshot.SnapshotId, "Demo.Base::Run():System.Void");
        var referenceMethod = Method("symbol-reference", snapshot.SnapshotId, "Demo.Reference::Run():System.Void");
        var modMethod = Method("symbol-mod", snapshot.SnapshotId, "Demo.Mod::Run():System.Void");
        var symbols = new List<IndexSymbolRecord> { baseMethod, referenceMethod, modMethod };
        await repository.CompleteIndexRunAsync(
            run.IndexId,
            new IndexWriteSet(
                symbols,
                [],
                [],
                [],
                [
                    new IndexRelationshipRecord(
                        "rel-reference", snapshot.SnapshotId, referenceMethod.SymbolId, baseMethod.SymbolId,
                        baseMethod.QualifiedName, "Overrides", "Metadata"),
                    new IndexRelationshipRecord(
                        "rel-mod-solo", snapshot.SnapshotId, modMethod.SymbolId, "symbol-solo",
                        "Demo.Solo::Run():System.Void", "Overrides", "Metadata")
                ],
                ReferenceIndexContext: new ReferenceIndexContextRecord(run.IndexId, gameIndexId, buildId),
                ReferenceMods:
                [
                    new IndexReferenceModRecord(
                        collection,
                        "Hierarchy",
                        "1.0.0",
                        "MIT",
                        "mods/hierarchy",
                        "hierarchy-content",
                        symbols.Select(symbol => symbol.SymbolId).ToArray())
                ]),
            "2026-09-01T13:01:00Z",
            cancellationToken);
        return collection;
    }

    private static IndexSymbolRecord Method(string id, string snapshotId, string qualifiedName) =>
        new(
            id,
            snapshotId,
            "ScheduleI:Installed:Method:" + qualifiedName,
            "Method",
            qualifiedName,
            "System.Void " + qualifiedName,
            false,
            BodyRecoveryStatus.Recovered);
}
