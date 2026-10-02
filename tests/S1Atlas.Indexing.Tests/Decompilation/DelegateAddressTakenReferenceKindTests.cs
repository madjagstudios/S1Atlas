using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Decompilation;
using Xunit;

namespace S1Atlas.Indexing.Tests.Decompilation;

public sealed class DelegateAddressTakenReferenceKindTests
{
    private const string FixtureNamespace = "S1Atlas.ParityFixture";

    [Fact]
    public async Task Ldftn_records_references_method_not_call()
    {
        var builder = await FindMemberAsync("DelegateCases", "BuildStaticFunc");

        Assert.Contains(
            builder.References,
            reference =>
                reference.Kind == ManagedReferenceKind.ReferencesMethod
                && reference.Target == $"{FixtureNamespace}.DelegateCases::StaticTarget(System.Int32):System.Int32");
        Assert.DoesNotContain(
            builder.References,
            reference => reference.Kind is ManagedReferenceKind.Calls or ManagedReferenceKind.CallsVirtual);

        var instance = await FindMemberAsync("DelegateCases", "BuildInstanceFunc");

        Assert.Contains(
            instance.References,
            reference =>
                reference.Kind == ManagedReferenceKind.ReferencesMethod
                && reference.Target == $"{FixtureNamespace}.DelegateCases::InstanceTarget(System.Int32):System.Int32");
        Assert.DoesNotContain(
            instance.References,
            reference => reference.Kind is ManagedReferenceKind.Calls or ManagedReferenceKind.CallsVirtual);
    }

    [Fact]
    public async Task Ldvirtftn_on_virtual_records_references_method()
    {
        var builder = await FindMemberAsync("VirtualDelegates", "BuildVirtualFunc");

        Assert.Contains(
            builder.References,
            reference =>
                reference.Kind == ManagedReferenceKind.ReferencesMethod
                && reference.Target == $"{FixtureNamespace}.VirtualBase::Compute(System.Int32):System.Int32");
        Assert.DoesNotContain(
            builder.References,
            reference => reference.Kind is ManagedReferenceKind.Calls or ManagedReferenceKind.CallsVirtual);
    }

    [Fact]
    public async Task Ldflda_and_ldsflda_record_takes_field_address()
    {
        var viaRef = await FindMemberAsync("RefFieldCases", "BumpViaRef");

        Assert.Contains(
            viaRef.References,
            reference =>
                reference.Kind == ManagedReferenceKind.TakesFieldAddress
                && reference.Target == $"{FixtureNamespace}.RefFieldCases::System.Int32 Counter");

        var interlocked = await FindMemberAsync("RefFieldCases", "BumpSharedViaInterlocked");

        Assert.Contains(
            interlocked.References,
            reference =>
                reference.Kind == ManagedReferenceKind.TakesFieldAddress
                && reference.Target == $"{FixtureNamespace}.RefFieldCases::System.Int32 Shared");

        var compareExchange = await FindMemberAsync("RefFieldCases", "BumpSharedViaCompareExchange");

        Assert.Contains(
            compareExchange.References,
            reference =>
                reference.Kind == ManagedReferenceKind.TakesFieldAddress
                && reference.Target == $"{FixtureNamespace}.RefFieldCases::System.Int32 Shared");

        var move = await FindMemberAsync("RefFieldCases", "MovePoint");

        Assert.Contains(
            move.References,
            reference =>
                reference.Kind == ManagedReferenceKind.TakesFieldAddress
                && reference.Target == $"{FixtureNamespace}.RefFieldCases::{FixtureNamespace}.MutablePoint Point");
    }

    [Fact]
    public async Task Plain_field_read_and_write_kinds_unchanged()
    {
        var reader = await FindMemberAsync("RefFieldCases", "ReadCounter");

        Assert.Contains(
            reader.References,
            reference =>
                reference.Kind == ManagedReferenceKind.ReadsField
                && reference.Target == $"{FixtureNamespace}.RefFieldCases::System.Int32 Counter");
        Assert.DoesNotContain(reader.References, reference => reference.Kind == ManagedReferenceKind.TakesFieldAddress);

        var writer = await FindMemberAsync("RefFieldCases", "WriteCounter");

        Assert.Contains(
            writer.References,
            reference =>
                reference.Kind == ManagedReferenceKind.WritesField
                && reference.Target == $"{FixtureNamespace}.RefFieldCases::System.Int32 Counter");
        Assert.DoesNotContain(writer.References, reference => reference.Kind == ManagedReferenceKind.TakesFieldAddress);
    }

    [Fact]
    public async Task Ldtoken_of_field_is_metadata_reference_not_read()
    {
        var type = await FindTypeAsync("TokenCases");
        var metadataReferences = type.Members
            .SelectMany(member => member.References)
            .Where(reference => reference.Evidence == RelationshipEvidence.Metadata)
            .ToArray();

        var token = Assert.Single(metadataReferences);
        Assert.Equal(ManagedReferenceKind.TakesFieldAddress, token.Kind);
    }

    [Fact]
    public async Task Ldtoken_of_type_records_nothing()
    {
        var member = await FindMemberAsync("TokenCases", "TypeName");

        Assert.DoesNotContain(
            member.References,
            reference => reference.Kind is ManagedReferenceKind.ReferencesMethod or ManagedReferenceKind.TakesFieldAddress);
    }

    [Fact]
    public async Task Calli_records_nothing()
    {
        var member = await FindMemberAsync("CalliCases", "InvokeViaPointer");

        Assert.Empty(member.References);
    }

    [Fact]
    public async Task Constrained_callvirt_stays_virtual_call()
    {
        var member = await FindMemberAsync("ConstrainedCases", "CallThrough");

        Assert.Contains(
            member.References,
            reference =>
                reference.Kind == ManagedReferenceKind.CallsVirtual
                && reference.Target == $"{FixtureNamespace}.IFoo::Foo():System.Int32");
        Assert.DoesNotContain(member.References, reference => reference.Kind == ManagedReferenceKind.Calls);
    }

    [Fact]
    public async Task Switch_and_goto_do_not_desync_il_decode()
    {
        var member = await FindMemberAsync("SwitchCases", "Dispatch");
        var target = $"{FixtureNamespace}.SwitchCases::Target(System.Int32):System.Int32";

        Assert.Equal(3, member.References.Count);
        Assert.All(
            member.References,
            reference =>
            {
                Assert.Equal(ManagedReferenceKind.Calls, reference.Kind);
                Assert.Equal(target, reference.Target);
            });
    }

    private static async Task<ManagedTypeFacts> FindTypeAsync(string typeName)
    {
        var result = await new IlSpyManagedDecompiler().DecompileAsync(
            Path.Combine(AppContext.BaseDirectory, "parity-fixture", "S1Atlas.ParityFixture.dll"),
            TestContext.Current.CancellationToken);
        return Assert.Single(result.Types, candidate => candidate.Name == typeName);
    }

    private static async Task<ManagedMemberFacts> FindMemberAsync(string typeName, string memberName)
    {
        var type = await FindTypeAsync(typeName);
        return Assert.Single(type.Members, member => member.Name == memberName);
    }
}
