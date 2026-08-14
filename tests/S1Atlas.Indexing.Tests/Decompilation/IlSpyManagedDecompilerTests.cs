using S1Atlas.ManagedAssemblyFixture;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Decompilation;
using Xunit;

namespace S1Atlas.Indexing.Tests.Decompilation;

public sealed class IlSpyManagedDecompilerTests
{
    [Fact]
    public async Task FixtureAssemblyProducesReadableSourceAndMetadataFactsWithoutExecution()
    {
        var decompiler = new IlSpyManagedDecompiler();

        var result = await decompiler.DecompileAsync(
            typeof(FixtureRoot).Assembly.Location,
            CancellationToken.None);

        Assert.Contains("class DerivedFixture", result.SourceText, StringComparison.Ordinal);
        Assert.Contains("interface IFixtureContract", result.SourceText, StringComparison.Ordinal);
        Assert.Contains("class GenericContainer<T>", result.SourceText, StringComparison.Ordinal);
        Assert.Contains("GenericMethod", result.SourceText, StringComparison.Ordinal);
        Assert.Equal(result.Types.Count, result.Types.Select(candidate => candidate.FullName).Distinct(StringComparer.Ordinal).Count());

        var type = Assert.Single(result.Types, candidate => candidate.Name == "DerivedFixture");
        Assert.Equal("S1Atlas.ManagedAssemblyFixture.FixtureBase", type.BaseType);
        Assert.Contains("S1Atlas.ManagedAssemblyFixture.IFixtureContract", type.Interfaces);

        Assert.Contains(type.Members, member => member.Kind == ManagedMemberKind.Constructor);
        Assert.Equal(
            2,
            type.Members.Count(member =>
                member.Kind == ManagedMemberKind.Method && member.Name == "Overload"));
        var overloads = type.Members
            .Where(member => member.Kind == ManagedMemberKind.Method && member.Name == "Overload")
            .Select(member => member.Signature)
            .ToArray();
        Assert.Equal(2, overloads.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(overloads, signature => signature.Contains("System.Int32", StringComparison.Ordinal));
        Assert.Contains(overloads, signature => signature.Contains("System.String", StringComparison.Ordinal));
        Assert.Contains(type.Members, member => member.Kind == ManagedMemberKind.Field);
        Assert.Contains(type.Members, member => member.Kind == ManagedMemberKind.Property);
        Assert.Contains(type.Members, member => member.Kind == ManagedMemberKind.Event);

        var genericMethod = Assert.Single(type.Members, member => member.Name == "GenericMethod");
        Assert.Equal(1, genericMethod.GenericParameterCount);
        Assert.Contains("!!0", genericMethod.Signature, StringComparison.Ordinal);

        var body = Assert.Single(type.Members, member => member.Name == "BuildAndTouch");
        Assert.True(body.HasBody);
        Assert.Contains(body.References, reference => reference.Kind == ManagedReferenceKind.Calls);
        Assert.Contains(body.References, reference => reference.Kind == ManagedReferenceKind.Constructs);
        Assert.Contains(body.References, reference => reference.Kind == ManagedReferenceKind.ReadsField);
        Assert.Contains(body.References, reference => reference.Kind == ManagedReferenceKind.WritesField);
        Assert.Contains(body.References, reference => reference.Target.Contains("DerivedFixture::Overload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FixtureAssemblyProducesConservativeBodyRecoveryFactsWithoutAssemblyLoad()
    {
        var decompiler = new IlSpyManagedDecompiler();

        var result = await decompiler.DecompileAsync(
            typeof(FixtureRoot).Assembly.Location,
            CancellationToken.None);

        var fixtureBase = Assert.Single(result.Types, candidate => candidate.Name == "FixtureBase");
        var arithmetic = Assert.Single(fixtureBase.Members, member => member.Name == "BaseMethod");
        Assert.NotNull(arithmetic.BodyFacts);
        Assert.True(arithmetic.BodyFacts.HasPhysicalBody);
        Assert.False(arithmetic.BodyFacts.NoBodyByDesign);
        Assert.False(arithmetic.BodyFacts.MatchesVerifiedStubPattern);
        Assert.Equal(0, arithmetic.BodyFacts.RecoveredReferenceCount);
        Assert.True(arithmetic.BodyFacts.InstructionCount >= 3);
        Assert.Equal(BodyRecoveryStatus.Recovered, arithmetic.BodyRecoveryStatus);

        var derived = Assert.Single(result.Types, candidate => candidate.Name == "DerivedFixture");
        var trivial = Assert.Single(derived.Members, member => member.Name == "GenericMethod");
        Assert.NotNull(trivial.BodyFacts);
        Assert.True(trivial.BodyFacts.HasPhysicalBody);
        Assert.Equal(BodyRecoveryStatus.Unknown, trivial.BodyRecoveryStatus);

        var fixtureRoot = Assert.Single(result.Types, candidate => candidate.Name == "FixtureRoot");
        var throwStub = Assert.Single(fixtureRoot.Members, member => member.Name == "GetValue");
        Assert.NotNull(throwStub.BodyFacts);
        Assert.True(throwStub.BodyFacts.HasPhysicalBody);
        Assert.True(throwStub.BodyFacts.MatchesVerifiedStubPattern);
        Assert.True(throwStub.BodyFacts.RecoveredReferenceCount > 0);
        Assert.Contains(throwStub.References, reference => reference.Kind == ManagedReferenceKind.Constructs);
        Assert.Equal(BodyRecoveryStatus.StubOrUnavailable, throwStub.BodyRecoveryStatus);

        var contract = Assert.Single(result.Types, candidate => candidate.Name == "IFixtureContract");
        var missing = Assert.Single(contract.Members, member => member.Name == "get_ContractValue");
        Assert.NotNull(missing.BodyFacts);
        Assert.False(missing.BodyFacts.HasPhysicalBody);
        Assert.True(missing.BodyFacts.NoBodyByDesign);
        Assert.Equal(BodyRecoveryStatus.NoBodyByDesign, missing.BodyRecoveryStatus);
    }
}
