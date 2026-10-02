using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Query;
using S1Atlas.Indexing.Tests.Relationships.Parity;
using S1Atlas.TestSupport;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class DispatchCallersTests : IAsyncDisposable
{
    private const string Ns = "S1Atlas.ParityFixture";
    private const string BaseFoo = $"{Ns}.DispatchBase::Foo():System.Int32";
    private const string DerivedFoo = $"{Ns}.DispatchDerived::Foo():System.Int32";
    private const string GrandchildFoo = $"{Ns}.DispatchGrandchild::Foo():System.Int32";
    private const string CallBase = $"{Ns}.DispatchDerived::CallBase():System.Int32";
    private const string CallBase2 = $"{Ns}.DispatchGrandchild::CallBase2():System.Int32";
    private const string ViaBase = $"{Ns}.DispatchDrivers::ViaBase({Ns}.DispatchBase):System.Int32";
    private const string ViaDerived = $"{Ns}.DispatchDrivers::ViaDerived({Ns}.DispatchDerived):System.Int32";
    private const string ViaInterface = $"{Ns}.DispatchDrivers::ViaInterface({Ns}.IDispatchContract):System.Int32";
    private const string ViaMulti = $"{Ns}.DispatchDrivers::ViaMulti({Ns}.IMulti):System.Int32";
    private const string ViaGenericBase = $"{Ns}.DispatchDrivers::ViaGenericBase({Ns}.GenericBase`1<System.Int32>):System.String";
    private const string ContractServe = $"{Ns}.IDispatchContract::Serve():System.Int32";
    private const string ExplicitServe = $"{Ns}.DispatchExplicit::{Ns}.IDispatchContract.Serve():System.Int32";
    private const string MultiBaseMulti = $"{Ns}.MultiBase::Multi():System.Int32";
    private const string MultiDerivedMulti = $"{Ns}.MultiDerived::Multi():System.Int32";
    private const string IMultiMulti = $"{Ns}.IMulti::Multi():System.Int32";
    private const string GenericBaseDescribe = $"{Ns}.GenericBase`1::Describe():System.String";
    private const string GenericDerivedDescribe = $"{Ns}.GenericDerived::Describe():System.String";

    private readonly List<OwnedFixtureIndex> _indexes = [];
    private bool _disposed;

    [Fact]
    public async Task Callers_GrandchildFoo_ReturnsMultiHopDerivedWithFullChainRoutes()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", GrandchildFoo)], 10000,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Resolved, result.Resolution.Status);
        Assert.Equal(0, result.ExactCount);
        Assert.Equal(2, result.DerivedCount);
        Assert.Equal(2, result.TotalCount);
        var route = $"via {DerivedFoo}, {BaseFoo}";
        Assert.Equal(
            [ViaBase, ViaDerived],
            result.Relationships.Select(edge => edge.Source.QualifiedName));
        Assert.All(result.Relationships, edge =>
        {
            Assert.True(edge.IsDerived);
            Assert.Equal([route], edge.Routes);
            Assert.Equal("CallsVirtual", edge.Kind);
        });
    }

    [Fact]
    public async Task Callers_DerivedFoo_MixesExactBaseCallAndDerivedDispatch()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", DerivedFoo)], 10000,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExactCount);
        Assert.Equal(2, result.DerivedCount);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(
            [CallBase2, ViaBase, ViaDerived],
            result.Relationships.Select(edge => edge.Source.QualifiedName));

        var exact = result.Relationships[0];
        Assert.False(exact.IsDerived);
        Assert.Null(exact.Routes);
        Assert.Equal("Calls", exact.Kind);

        // CallBase reaches Base.Foo with a non-virtual base call, so it can
        // never dispatch to the override and must stay out of the set.
        Assert.DoesNotContain(result.Relationships, edge => edge.Source.QualifiedName == CallBase);
        var route = $"via {BaseFoo}";
        Assert.All(result.Relationships.Skip(1), edge =>
        {
            Assert.True(edge.IsDerived);
            Assert.Equal([route], edge.Routes);
        });
    }

    [Fact]
    public async Task Callers_MultiDerivedMulti_DedupesTwoRoutesIntoOneRow()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", MultiDerivedMulti)], 10000,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExactCount);
        Assert.Equal(1, result.DerivedCount);
        Assert.Equal(1, result.TotalCount);
        var row = Assert.Single(result.Relationships);
        Assert.Equal(ViaMulti, row.Source.QualifiedName);
        Assert.True(row.IsDerived);
        Assert.Equal(
            [$"via {IMultiMulti}", $"via {MultiBaseMulti}, {IMultiMulti}"],
            row.Routes);
    }

    [Fact]
    public async Task Callers_MultiBaseMulti_ResolvesInterfaceCallViaBase()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", MultiBaseMulti)], 10000,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExactCount);
        Assert.Equal(1, result.DerivedCount);
        var row = Assert.Single(result.Relationships);
        Assert.Equal(ViaMulti, row.Source.QualifiedName);
        Assert.True(row.IsDerived);
        Assert.Equal([$"via {IMultiMulti}"], row.Routes);
    }

    [Fact]
    public async Task Callers_ExplicitServe_ResolvesInterfaceDispatch()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", ExplicitServe)], 10000,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExactCount);
        Assert.Equal(1, result.DerivedCount);
        var row = Assert.Single(result.Relationships);
        Assert.Equal(ViaInterface, row.Source.QualifiedName);
        Assert.True(row.IsDerived);
        Assert.Equal([$"via {ContractServe}"], row.Routes);
    }

    [Fact]
    public async Task Callers_GenericDerivedDescribe_ResolvesDispatchThroughOpenGenericBase()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", GenericDerivedDescribe)], 10000,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExactCount);
        Assert.Equal(1, result.DerivedCount);
        var row = Assert.Single(result.Relationships);
        Assert.Equal(ViaGenericBase, row.Source.QualifiedName);
        Assert.True(row.IsDerived);
        Assert.Equal([$"via {GenericBaseDescribe}"], row.Routes);
    }

    [Fact]
    public async Task Callers_BaseFoo_ReturnsExactOnlyInCallerIdentityOrder()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", BaseFoo)], 10000,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExactCount);
        Assert.Equal(0, result.DerivedCount);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(
            [CallBase, ViaBase, ViaDerived],
            result.Relationships.Select(edge => edge.Source.QualifiedName));
        Assert.All(result.Relationships, edge =>
        {
            Assert.False(edge.IsDerived);
            Assert.Null(edge.Routes);
        });
    }

    [Fact]
    public async Task Callers_Paging_ReturnsFactFirstWithTrueTotals()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);
        var selector = ids[("Method", DerivedFoo)];

        var first = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            selector, 1,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, first.TotalCount);
        Assert.Equal(1, first.ExactCount);
        Assert.Equal(2, first.DerivedCount);
        var head = Assert.Single(first.Relationships);
        Assert.Equal(CallBase2, head.Source.QualifiedName);
        Assert.False(head.IsDerived);

        var second = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            selector, 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, second.TotalCount);
        Assert.Equal(
            [CallBase2, ViaBase],
            second.Relationships.Select(edge => edge.Source.QualifiedName));
        Assert.True(second.Relationships[1].IsDerived);
    }

    [Fact]
    public async Task Callers_Exact_ReturnsOnlyFactRowsInRelationshipIdOrder()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", BaseFoo)], 10000,
            TestContext.Current.CancellationToken,
            exact: true);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(3, result.ExactCount);
        Assert.Equal(0, result.DerivedCount);
        Assert.All(result.Relationships, edge =>
        {
            Assert.False(edge.IsDerived);
            Assert.Null(edge.Routes);
        });
        Assert.Equal(
            result.Relationships.Select(edge => edge.RelationshipId).OrderBy(id => id, StringComparer.Ordinal),
            result.Relationships.Select(edge => edge.RelationshipId));
        Assert.Equal(
            [CallBase, ViaBase, ViaDerived],
            result.Relationships.Select(edge => edge.Source.QualifiedName).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Callers_Exact_GrandchildFoo_ReturnsEmptyWithZeroTotals()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", GrandchildFoo)], 10000,
            TestContext.Current.CancellationToken,
            exact: true);

        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.ExactCount);
        Assert.Equal(0, result.DerivedCount);
        Assert.Empty(result.Relationships);
    }

    [Fact]
    public async Task Callers_Ambiguous_ReturnsAmbiguousWithoutCounts()
    {
        var (service, run, _) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            "Foo", 10000,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.Ambiguous, result.Resolution.Status);
        Assert.Empty(result.Relationships);
        Assert.Null(result.TotalCount);
        Assert.Null(result.ExactCount);
        Assert.Null(result.DerivedCount);
    }

    [Fact]
    public async Task Callers_NotFound_ReturnsNotFoundWithoutCounts()
    {
        var (service, run, _) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            "No.Such.Type::Nothing():System.Void", 10000,
            TestContext.Current.CancellationToken);

        Assert.Equal(SymbolResolutionStatus.NotFound, result.Resolution.Status);
        Assert.Empty(result.Relationships);
        Assert.Null(result.TotalCount);
        Assert.Null(result.ExactCount);
        Assert.Null(result.DerivedCount);
    }

    [Fact]
    public async Task Callers_ExternalOverrideSlot_ContributesNothing()
    {
        var (service, run, ids) = await GameAsync(TestContext.Current.CancellationToken);

        var result = await service.CallersInIndexAsync(
            run, CodebaseKind.ScheduleI, CodeChannel.Installed,
            ids[("Method", $"{Ns}.ExternalToString::ToString():System.String")], 10000,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.DerivedCount);
        Assert.All(result.Relationships, edge => Assert.False(edge.IsDerived));
    }

    [Fact]
    public async Task CallersAsync_ExpandsByDefaultAndHonorsExact()
    {
        var (service, _, ids) = await GameAsync(TestContext.Current.CancellationToken);
        var options = new IndexQueryOptions(CodebaseKind.ScheduleI, CodeChannel.Installed, false, 50, IndexQueryScope.Game);
        var selector = ids[("Method", DerivedFoo)];

        var expanded = await service.CallersAsync(selector, options, TestContext.Current.CancellationToken);

        Assert.Equal(1, expanded.ExactCount);
        Assert.Equal(2, expanded.DerivedCount);
        Assert.Equal(3, expanded.TotalCount);

        var exact = await service.CallersAsync(selector, options, TestContext.Current.CancellationToken, exact: true);

        Assert.Equal(1, exact.TotalCount);
        Assert.Equal(1, exact.ExactCount);
        Assert.Equal(0, exact.DerivedCount);
        var row = Assert.Single(exact.Relationships);
        Assert.Equal(CallBase2, row.Source.QualifiedName);
    }

    [Fact]
    public void MergeAndTake_FactWinsOnDuplicateId()
    {
        var exact = Row("edge-1", "Demo.B::Run():System.Void", isDerived: false);
        var derived = Row("edge-1", "Demo.B::Run():System.Void", isDerived: true, routes: ["via Demo.A::Run():System.Void"]);

        var page = DispatchExpansion.MergeAndTake([exact], [derived], 10);

        Assert.Equal(1, page.ExactCount);
        Assert.Equal(0, page.DerivedCount);
        var row = Assert.Single(page.Relationships);
        Assert.False(row.IsDerived);
        Assert.Null(row.Routes);
    }

    [Fact]
    public void MergeAndTake_OrdersFactFirstThenCallerIdentity()
    {
        var exactB = Row("edge-b", "Demo.B::Run():System.Void", isDerived: false);
        var exactA = Row("edge-a", "Demo.A::Run():System.Void", isDerived: false);
        var derived = Row("edge-c", "Demo.C::Run():System.Void", isDerived: true, routes: ["via Demo.Base::Run():System.Void"]);

        var page = DispatchExpansion.MergeAndTake([exactB, exactA], [derived], 10);

        Assert.Equal(["edge-a", "edge-b", "edge-c"], page.Relationships.Select(edge => edge.RelationshipId));
        Assert.Equal(2, page.ExactCount);
        Assert.Equal(1, page.DerivedCount);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var index in _indexes)
            await index.DisposeAsync();
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

    private static RelationshipQueryResult Row(
        string relationshipId,
        string caller,
        bool isDerived,
        string[]? routes = null) =>
        new(
            relationshipId,
            "CallsVirtual",
            "fixture",
            "Incoming",
            new RelationshipEndpointQueryResult("source", caller, null, null, true),
            new RelationshipEndpointQueryResult("target", "Demo.Target::Run():System.Void", null, null, true),
            isDerived,
            routes);
}
