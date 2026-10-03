using S1Atlas.Core;
using S1Atlas.Storage.Sqlite;
using S1Atlas.TestSupport.Seeding;
using Xunit;

namespace S1Atlas.Web.Tests;

// Pins the synthetic atlas to the real indexer name shapes: every member
// qualified name carries ::, method rows store the full identity in both the
// qualified name and the signature, and canonical keys match
// SymbolIdentity.Create exactly (including its whitespace stripping).
public sealed class SyntheticAtlasShapeTests
{
    [Fact]
    public async Task Member_names_use_the_real_renderers()
    {
        await using var atlas = await SyntheticAtlas.SeedServeFixtureAsync(TestContext.Current.CancellationToken);
        var repository = new SqliteAtlasRepository(
            Path.Combine(atlas.DataRoot, "atlas.db"),
            Path.Combine(atlas.DataRoot, "backups"));
        var symbols = await repository.GetCompletedSymbolsAsync(
            SyntheticAtlas.GameIndexIdValue, TestContext.Current.CancellationToken);
        var members = symbols
            .Where(symbol => symbol.Kind is "Method" or "Constructor" or "Field" or "Property" or "Event")
            .ToArray();
        Assert.NotEmpty(members);
        foreach (var member in members)
        {
            Assert.Contains("::", member.QualifiedName);
            Assert.True(SymbolNames.TrySplitMember(member.QualifiedName, out _, out _));
        }

        foreach (var method in members.Where(symbol => symbol.Kind is "Method" or "Constructor"))
            Assert.Equal(method.QualifiedName, method.Signature);

        var byId = symbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        AssertMember(byId, SyntheticAtlas.RunMethodId, "Demo.Widget::Run():System.Void", "Demo.Widget::Run():System.Void", "ScheduleI:Installed:Method:Demo.Widget::Run():System.Void");
        AssertMember(byId, SyntheticAtlas.StateFieldId, "Demo.Widget::System.Int32 _state", "System.Int32 _state", "ScheduleI:Installed:Field:Demo.Widget::System.Int32_state");
        AssertMember(byId, SyntheticAtlas.WidgetCtorId, "Demo.Widget::.ctor():System.Void", "Demo.Widget::.ctor():System.Void", "ScheduleI:Installed:Constructor:Demo.Widget::.ctor():System.Void");
        AssertMember(byId, SyntheticAtlas.WidgetNamePropertyId, "Demo.Widget::System.String Name", "System.String Name", "ScheduleI:Installed:Property:Demo.Widget::System.StringName");
        AssertMember(byId, SyntheticAtlas.CallerMethodId, "Demo.Caller::Invoke():System.Void", "Demo.Caller::Invoke():System.Void", "ScheduleI:Installed:Method:Demo.Caller::Invoke():System.Void");
        AssertMember(byId, SyntheticAtlas.ExecuteMethodId, "Demo.Service::Execute():System.Void", "Demo.Service::Execute():System.Void", "ScheduleI:Installed:Method:Demo.Service::Execute():System.Void");
        AssertMember(byId, SyntheticAtlas.NestedInnerMethodId, "Demo.Widget+Nested::Inner():System.Void", "Demo.Widget+Nested::Inner():System.Void", "ScheduleI:Installed:Method:Demo.Widget+Nested::Inner():System.Void");
        AssertMember(byId, SyntheticAtlas.FactoryCreateMethodId, "Demo.WidgetFactory::Create():System.Void", "Demo.WidgetFactory::Create():System.Void", "ScheduleI:Installed:Method:Demo.WidgetFactory::Create():System.Void");
        AssertMember(byId, SyntheticAtlas.BaseRenderMethodId, "Demo.WidgetBase::Render():System.Void", "Demo.WidgetBase::Render():System.Void", "ScheduleI:Installed:Method:Demo.WidgetBase::Render():System.Void");
        AssertMember(byId, SyntheticAtlas.RenderMethodId, "Demo.Widget::Render():System.Void", "Demo.Widget::Render():System.Void", "ScheduleI:Installed:Method:Demo.Widget::Render():System.Void");
        AssertMember(byId, SyntheticAtlas.CreditFooMethodId, "Demo.Credit::Foo():System.Void", "Demo.Credit::Foo():System.Void", "ScheduleI:Installed:Method:Demo.Credit::Foo():System.Void");
        AssertMember(byId, SyntheticAtlas.CreditLambdaMethodId, "Demo.Credit+<>c::<Foo>b__0_0():System.Void", "Demo.Credit+<>c::<Foo>b__0_0():System.Void", "ScheduleI:Installed:Method:Demo.Credit+<>c::<Foo>b__0_0():System.Void");
        AssertMember(byId, SyntheticAtlas.CreditLeafMethodId, "Demo.Credit::Leaf():System.Void", "Demo.Credit::Leaf():System.Void", "ScheduleI:Installed:Method:Demo.Credit::Leaf():System.Void");
        AssertMember(byId, SyntheticAtlas.CreditCaptureFieldId, "Demo.Capture+<>c__DisplayClass0_0::System.Int32 x", "System.Int32 x", "ScheduleI:Installed:Field:Demo.Capture+<>c__DisplayClass0_0::System.Int32x");
        AssertMember(byId, SyntheticAtlas.DelegateBuildMethodId, "Demo.Delegate::Build():System.Void", "Demo.Delegate::Build():System.Void", "ScheduleI:Installed:Method:Demo.Delegate::Build():System.Void");
        AssertMember(byId, SyntheticAtlas.DelegateTargetMethodId, "Demo.Delegate::Handle():System.Void", "Demo.Delegate::Handle():System.Void", "ScheduleI:Installed:Method:Demo.Delegate::Handle():System.Void");
        AssertMember(byId, SyntheticAtlas.DelegateCountFieldId, "Demo.Delegate::System.Int32 Count", "System.Int32 Count", "ScheduleI:Installed:Field:Demo.Delegate::System.Int32Count");
        AssertMember(byId, SyntheticAtlas.DelegateTakeMethodId, "Demo.Delegate::Take():System.Void", "Demo.Delegate::Take():System.Void", "ScheduleI:Installed:Method:Demo.Delegate::Take():System.Void");
        AssertMember(byId, SyntheticAtlas.CheckPhysicsMethodId, "Demo.Widget::CheckPhysics():System.Void", "Demo.Widget::CheckPhysics():System.Void", "ScheduleI:Installed:Method:Demo.Widget::CheckPhysics():System.Void");

        var apiSymbols = await repository.GetCompletedSymbolsAsync(
            SyntheticAtlas.ApiIndexIdValue, TestContext.Current.CancellationToken);
        var apiById = apiSymbols.ToDictionary(symbol => symbol.SymbolId, StringComparer.Ordinal);
        AssertMember(apiById, SyntheticAtlas.LookupMethodId, "ServeApi.Catalog::Lookup():System.Void", "ServeApi.Catalog::Lookup():System.Void", "S1Api:Release:Method:ServeApi.Catalog::Lookup():System.Void");
        AssertMember(apiById, SyntheticAtlas.AssistMethodId, "ServeApi.Helper::Assist():System.Void", "ServeApi.Helper::Assist():System.Void", "S1Api:Release:Method:ServeApi.Helper::Assist():System.Void");
    }

    private static void AssertMember(
        IReadOnlyDictionary<string, S1Atlas.Core.Storage.IndexSymbolRecord> byId,
        string symbolId,
        string qualifiedName,
        string signature,
        string canonicalKey)
    {
        var symbol = byId[symbolId];
        Assert.Equal(qualifiedName, symbol.QualifiedName);
        Assert.Equal(signature, symbol.Signature);
        Assert.Equal(canonicalKey, symbol.CanonicalKey);
    }
}
