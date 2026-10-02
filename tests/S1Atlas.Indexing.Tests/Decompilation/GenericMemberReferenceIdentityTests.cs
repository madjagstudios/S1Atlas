using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Decompilation;
using Xunit;

namespace S1Atlas.Indexing.Tests.Decompilation;

public sealed class GenericMemberReferenceIdentityTests
{
    private const string FixtureNamespace = "S1Atlas.ParityFixture";

    [Fact]
    public async Task ConstructedGenericMethodResolvesToOpenDefinition()
    {
        var drivers = await FindMembersAsync("GenericDrivers", "ViaConstructedType");
        Assert.Contains(
            drivers.References,
            reference => reference.Kind == ManagedReferenceKind.CallsVirtual
                && reference.Target == $"{FixtureNamespace}.GenericBox`1::Touch(!0):!0");
        Assert.DoesNotContain(drivers.References, reference => reference.Target.StartsWith("0x"));
    }

    [Fact]
    public async Task ConstructedGenericFieldResolvesToOpenDefinition()
    {
        var driver = await FindMembersAsync("GenericDrivers", "UseStoredField");
        Assert.Contains(
            driver.References,
            reference => reference.Kind == ManagedReferenceKind.ReadsField
                && reference.Target == $"{FixtureNamespace}.GenericBox`1::!0 Stored");
        Assert.Contains(
            driver.References,
            reference => reference.Kind == ManagedReferenceKind.WritesField
                && reference.Target == $"{FixtureNamespace}.GenericBox`1::!0 Stored");
    }

    [Fact]
    public async Task NestedGenericMethodResolvesToOpenDefinition()
    {
        var driver = await FindMembersAsync("GenericDrivers", "UseNested");
        Assert.Contains(
            driver.References,
            reference => reference.Kind == ManagedReferenceKind.Calls
                && reference.Target == $"{FixtureNamespace}.GenericOuter`1+GenericInner`1::Describe(!0,!1):System.String");
    }

    [Fact]
    public async Task GenericMethodOnGenericTypeResolvesThroughMethodSpec()
    {
        var driver = await FindMembersAsync("GenericDrivers", "UseConvert");
        Assert.Contains(
            driver.References,
            reference => reference.Kind == ManagedReferenceKind.Calls
                && reference.Target == $"{FixtureNamespace}.GenericBox`1::Convert`1(!!0):!!0");
    }

    [Fact]
    public async Task ArrayAccessorProducesUnresolvedMarkerWithReason()
    {
        var driver = await FindMembersAsync("GenericDrivers", "UseMultiDimArray");
        Assert.Contains(
            driver.References,
            reference => reference.Kind == ManagedReferenceKind.Calls
                && reference.Target == "unresolved:array accessor:Get");
        Assert.Contains(
            driver.References,
            reference => reference.Kind == ManagedReferenceKind.Calls
                && reference.Target == "unresolved:array accessor:Set");
        Assert.DoesNotContain(driver.References, reference => reference.Target.StartsWith("0x"));
    }

    [Fact]
    public async Task ExternalGenericMethodResolvesToOpenExternalIdentity()
    {
        var driver = await FindMembersAsync("GenericDrivers", "UseList");
        Assert.Contains(
            driver.References,
            reference => reference.Kind == ManagedReferenceKind.CallsVirtual
                && reference.Target == "System.Collections.Generic.List`1::Add(!0):System.Void");
        Assert.DoesNotContain(driver.References, reference => reference.Target.StartsWith("0x"));
    }

    [Fact]
    public async Task NoReferenceTargetIsARawToken()
    {
        var result = await DecompileFixtureAsync();
        foreach (var type in result.Types)
        {
            foreach (var member in type.Members)
            {
                Assert.DoesNotContain(
                    member.References,
                    reference => reference.Target.StartsWith("0x"));
            }
        }
    }

    private static async Task<ManagedMemberFacts> FindMembersAsync(string typeName, string memberName)
    {
        var result = await DecompileFixtureAsync();
        var type = Assert.Single(result.Types, candidate => candidate.Name == typeName);
        return Assert.Single(type.Members, member => member.Name == memberName);
    }

    private static Task<ManagedDecompilation> DecompileFixtureAsync() =>
        new IlSpyManagedDecompiler().DecompileAsync(
            Path.Combine(AppContext.BaseDirectory, "parity-fixture", "S1Atlas.ParityFixture.dll"),
            TestContext.Current.CancellationToken);
}
