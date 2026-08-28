using S1Atlas.ManagedAssemblyFixture;
using S1Atlas.InteropAssemblyFixture;
using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
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

        var dock = Assert.Single(result.Types, candidate => candidate.Name == "LoadingDock");
        Assert.False(Assert.Single(dock.Members, member => member.Name == "SetOccupant").IsPublic);
        Assert.False(Assert.Single(dock.Members, member => member.Name == "ResetOccupant").IsPublic);
        Assert.True(Assert.Single(dock.Members, member => member.Name == "X").IsPublic);
        Assert.Contains(dock.Members, member => member.Name == "<X>k__BackingField" && !member.IsPublic);
    }

    [Fact]
    public async Task InteropFixtureAssemblyClassifiesRuntimeInvokeWrappers()
    {
        var decompiler = new IlSpyManagedDecompiler();

        var result = await decompiler.DecompileAsync(
            typeof(InteropFixtureRoot).Assembly.Location,
            CancellationToken.None);

        var type = Assert.Single(result.Types, candidate => candidate.Name == "InteropFixtureRoot");
        var wrapper = Assert.Single(type.Members, member => member.Name == "InteropWrapper");
        Assert.True(wrapper.HasBody);
        Assert.True(wrapper.BodyFacts!.MatchesInteropWrapperPattern);
        Assert.Equal(BodyRecoveryStatus.StubOrUnavailable, wrapper.BodyRecoveryStatus);

        var convertArgsWrapper = Assert.Single(type.Members, member => member.Name == "InteropWrapperConvertArgs");
        Assert.True(convertArgsWrapper.BodyFacts!.MatchesInteropWrapperPattern);
        Assert.Equal(BodyRecoveryStatus.StubOrUnavailable, convertArgsWrapper.BodyRecoveryStatus);

        var falsePositive = Assert.Single(type.Members, member => member.Name == "NotInteropWrapper");
        Assert.False(falsePositive.BodyFacts!.MatchesInteropWrapperPattern);
        Assert.Equal(BodyRecoveryStatus.Recovered, falsePositive.BodyRecoveryStatus);
    }

    [Fact]
    public async Task CallableMatcherBridgesPrivateMembersAndSanitizedBackingFields()
    {
        var decompiler = new IlSpyManagedDecompiler();
        var game = await decompiler.DecompileAsync(typeof(FixtureRoot).Assembly.Location, CancellationToken.None);
        var interop = await decompiler.DecompileAsync(typeof(InteropFixtureRoot).Assembly.Location, CancellationToken.None);

        var matches = new InteropCallableSurfaceMatcher().Match(game, interop);
        var setOccupant = Assert.Single(matches, match => match.GameMember.Name == "SetOccupant");
        Assert.Equal(CallableSurfaceStatus.Resolved, setOccupant.Status);
        Assert.Equal(CallableSurfaceKind.PublicMethodWrapper, setOccupant.Kind);
        Assert.False(setOccupant.RequiresReflection);
        Assert.Contains("il2cpp_runtime_invoke", setOccupant.Evidence, StringComparison.Ordinal);

        var resetOccupant = Assert.Single(matches, match => match.GameMember.Name == "ResetOccupant");
        Assert.Equal(CallableSurfaceKind.NonPublicWrapper, resetOccupant.Kind);
        Assert.True(resetOccupant.RequiresReflection);

        var backingField = Assert.Single(matches, match => match.GameMember.Name == "<X>k__BackingField");
        Assert.Equal(CallableSurfaceStatus.Resolved, backingField.Status);
        Assert.Equal(CallableSurfaceKind.PublicFieldAccessor, backingField.Kind);
        Assert.Contains("X_k__BackingField", backingField.InteropSignature, StringComparison.Ordinal);

        var publicMember = Assert.Single(matches, match => match.GameMember.Name == "X" && match.GameMember.Kind == ManagedMemberKind.Property);
        Assert.Equal(CallableSurfaceKind.DirectGameMember, publicMember.Kind);
        Assert.False(publicMember.RequiresReflection);
    }

    [Fact]
    public void CallableMatcherReportsAmbiguousFallbacksAndMissingInterop()
    {
        var gameMember = new ManagedMemberFacts(
            "Run",
            ManagedMemberKind.Method,
            "Demo::Run(System.Int32):System.Void",
            true,
            [],
            ["System.Int32"],
            "System.Void");
        var gameType = new ManagedTypeFacts("Demo", "", "Demo", null, [], [gameMember]);
        var game = new ManagedDecompilation("game.dll", "", [gameType]);
        var interopType = new ManagedTypeFacts(
            "Demo",
            "",
            "Demo",
            null,
            [],
            [
                new ManagedMemberFacts("Run", ManagedMemberKind.Method, "Demo::Run(System.String):System.Void", true, [], ["System.Int32"], "System.Void", IsPublic: true),
                new ManagedMemberFacts("Run", ManagedMemberKind.Method, "Demo::Run(System.Double):System.Void", true, [], ["System.Int32"], "System.Void", IsPublic: true)
            ]);

        var ambiguous = Assert.Single(new InteropCallableSurfaceMatcher().Match(
            game,
            new ManagedDecompilation("interop.dll", "", [interopType])));
        Assert.Equal(CallableSurfaceStatus.Ambiguous, ambiguous.Status);

        var unavailable = Assert.Single(new InteropCallableSurfaceMatcher().Match(game, null));
        Assert.Equal(CallableSurfaceStatus.Unavailable, unavailable.Status);
        Assert.Equal(CallableSurfaceKind.NonPublicWrapper, unavailable.Kind);
    }

    [Fact]
    public void CallableMatcherDoesNotResolveAnIncompatibleSignatureByNameAndArityAlone()
    {
        var gameMember = new ManagedMemberFacts(
            "Run",
            ManagedMemberKind.Method,
            "Demo::Run(System.Int32):System.Void",
            true,
            [],
            ["System.Int32"],
            "System.Void");
        var game = new ManagedDecompilation(
            "game.dll",
            "",
            [new ManagedTypeFacts("Demo", "", "Demo", null, [], [gameMember])]);
        var interop = new ManagedDecompilation(
            "interop.dll",
            "",
            [new ManagedTypeFacts(
                "Demo",
                "",
                "Demo",
                null,
                [],
                [new ManagedMemberFacts(
                    "Run",
                    ManagedMemberKind.Method,
                    "Demo::Run(System.String):System.Void",
                    true,
                    [],
                    ["System.String"],
                    "System.Void",
                    IsPublic: true)])]);

        var result = Assert.Single(new InteropCallableSurfaceMatcher().Match(game, interop));
        Assert.Equal(CallableSurfaceStatus.Unavailable, result.Status);
        Assert.Null(result.InteropMember);
    }

    [Fact]
    public void CallableMatcherDoesNotTreatAnUnrelatedMangledFieldAsAPropertyBridge()
    {
        var field = new ManagedMemberFacts(
            "<X>k__BackingField",
            ManagedMemberKind.Field,
            "Demo::<X>k__BackingField:System.Int32",
            false,
            [],
            ValueType: "System.Int32");
        var game = new ManagedDecompilation(
            "game.dll",
            "",
            [new ManagedTypeFacts("Demo", "", "Demo", null, [], [field])]);
        var interop = new ManagedDecompilation(
            "interop.dll",
            "",
            [new ManagedTypeFacts(
                "Demo",
                "",
                "Demo",
                null,
                [],
                [new ManagedMemberFacts(
                    "X_k__BackingField",
                    ManagedMemberKind.Field,
                    "Demo::X_k__BackingField:System.Int32",
                    false,
                    [],
                    ValueType: "System.Int32",
                    IsPublic: true)])]);

        var result = Assert.Single(new InteropCallableSurfaceMatcher().Match(game, interop));
        Assert.Equal(CallableSurfaceStatus.Unavailable, result.Status);
    }
}
