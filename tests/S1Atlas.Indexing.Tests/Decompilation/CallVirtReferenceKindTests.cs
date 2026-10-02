using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Decompilation;
using Xunit;

namespace S1Atlas.Indexing.Tests.Decompilation;

public sealed class CallVirtReferenceKindTests
{
    private const string FixtureNamespace = "S1Atlas.ParityFixture";

    [Fact]
    public async Task VirtualCall_ProducesCallsVirtualReference()
    {
        var driver = await FindMemberAsync("DispatchDrivers", "ViaBase");

        Assert.Contains(
            driver.References,
            reference =>
                reference.Kind == ManagedReferenceKind.CallsVirtual
                && reference.Target == $"{FixtureNamespace}.DispatchBase::Foo():System.Int32");
        Assert.DoesNotContain(driver.References, reference => reference.Kind == ManagedReferenceKind.Calls);
    }

    [Fact]
    public async Task BaseCall_ProducesNonVirtualCallsReference()
    {
        var caller = await FindMemberAsync("DispatchDerived", "CallBase");

        Assert.Contains(
            caller.References,
            reference =>
                reference.Kind == ManagedReferenceKind.Calls
                && reference.Target == $"{FixtureNamespace}.DispatchBase::Foo():System.Int32");
        Assert.DoesNotContain(caller.References, reference => reference.Kind == ManagedReferenceKind.CallsVirtual);
    }

    private static async Task<ManagedMemberFacts> FindMemberAsync(string typeName, string memberName)
    {
        var result = await new IlSpyManagedDecompiler().DecompileAsync(
            Path.Combine(AppContext.BaseDirectory, "parity-fixture", "S1Atlas.ParityFixture.dll"),
            TestContext.Current.CancellationToken);
        var type = Assert.Single(result.Types, candidate => candidate.Name == typeName);
        return Assert.Single(type.Members, member => member.Name == memberName);
    }
}
