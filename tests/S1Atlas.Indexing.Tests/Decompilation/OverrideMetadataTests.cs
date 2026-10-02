using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Decompilation;
using Xunit;

namespace S1Atlas.Indexing.Tests.Decompilation;

public sealed class OverrideMetadataTests
{
    private const string FixtureNamespace = "S1Atlas.ParityFixture";

    [Fact]
    public async Task OverrideMethod_ExposesVirtualReuseSlot()
    {
        var method = await FindMemberAsync("DispatchDerived", "Foo");
        Assert.True(method.IsVirtual);
        Assert.False(method.IsNewSlot);
    }

    [Fact]
    public async Task RootVirtualMethod_ExposesNewSlot()
    {
        var method = await FindMemberAsync("DispatchBase", "Foo");
        Assert.True(method.IsVirtual);
        Assert.True(method.IsNewSlot);
    }

    [Fact]
    public async Task AbstractMethod_ExposesVirtual()
    {
        var method = await FindMemberAsync("DispatchAbstract", "Compute");
        Assert.True(method.IsVirtual);
    }

    [Fact]
    public async Task NewVirtualMethod_ExposesNewSlot()
    {
        var method = await FindMemberAsync("DispatchNewVirtual", "Foo");
        Assert.True(method.IsVirtual);
        Assert.True(method.IsNewSlot);
    }

    [Fact]
    public async Task NewNonVirtualMethod_IsNotVirtual()
    {
        var method = await FindMemberAsync("DispatchHider", "Bar");
        Assert.False(method.IsVirtual);
        Assert.False(method.IsNewSlot);
    }

    [Fact]
    public async Task ExplicitImplementation_ExposesMethodImplDeclaration()
    {
        var method = await FindMemberAsync("DispatchExplicit", $"{FixtureNamespace}.IDispatchContract.Serve");
        Assert.Contains(
            $"{FixtureNamespace}.IDispatchContract::Serve():System.Int32",
            method.MethodImplDeclarationsOrEmpty);
    }

    [Fact]
    public async Task InterfaceType_ExposesIsInterface()
    {
        var contract = await FindTypeAsync("IDispatchContract");
        Assert.True(contract.IsInterface);
        var @class = await FindTypeAsync("DispatchBase");
        Assert.False(@class.IsInterface);
    }

    [Fact]
    public async Task PropertyOverrideAccessor_ExposesVirtualReuseSlot()
    {
        var accessor = await FindMemberAsync("PropDerived", "get_Label");
        Assert.True(accessor.IsVirtual);
        Assert.False(accessor.IsNewSlot);
    }

    private static async Task<ManagedMemberFacts> FindMemberAsync(string typeName, string memberName)
    {
        var type = await FindTypeAsync(typeName);
        return Assert.Single(type.Members, member => member.Name == memberName);
    }

    private static async Task<ManagedTypeFacts> FindTypeAsync(string typeName)
    {
        var result = await DecompileFixtureAsync();
        return Assert.Single(result.Types, candidate => candidate.Name == typeName);
    }

    private static Task<ManagedDecompilation> DecompileFixtureAsync() =>
        new IlSpyManagedDecompiler().DecompileAsync(
            Path.Combine(AppContext.BaseDirectory, "parity-fixture", "S1Atlas.ParityFixture.dll"),
            TestContext.Current.CancellationToken);
}
