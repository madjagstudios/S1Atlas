using S1Atlas.Core.Extraction;
using Xunit;

namespace S1Atlas.Core.Tests.Extraction;

public sealed class ExtractionConfigurationFingerprintTests
{
    public static IEnumerable<object[]> ProfileMutations()
    {
        yield return [new Func<ExtractionProfile, ExtractionProfile>(value => value with { SchemaVersion = 2 })];
        yield return [new Func<ExtractionProfile, ExtractionProfile>(value => value with { ProfileId = "another-profile" })];
        yield return [new Func<ExtractionProfile, ExtractionProfile>(value => value with { ProfileVersion = 2 })];
        yield return [new Func<ExtractionProfile, ExtractionProfile>(value => value with { AdapterVersion = 2 })];
        yield return [new Func<ExtractionProfile, ExtractionProfile>(value => value with { ExtractionSchemaVersion = 2 })];
        yield return [new Func<ExtractionProfile, ExtractionProfile>(value => value with { ExecutableName = "Other" })];
        yield return [new Func<ExtractionProfile, ExtractionProfile>(value => value with { OutputFormat = "other" })];
        yield return [new Func<ExtractionProfile, ExtractionProfile>(value => value with { Timeout = TimeSpan.FromSeconds(1) })];
        yield return [new Func<ExtractionProfile, ExtractionProfile>(value => value with { MaximumRetainedStandardOutputBytes = 1 })];
        yield return [new Func<ExtractionProfile, ExtractionProfile>(value => value with { MaximumRetainedStandardErrorBytes = 1 })];
        yield return [new Func<ExtractionProfile, ExtractionProfile>(value => value with { AcceptedExitCodes = [1] })];
        yield return [new Func<ExtractionProfile, ExtractionProfile>(value => value with { RequiredAssemblyIdentities = ["Other"] })];
        yield return [new Func<ExtractionProfile, ExtractionProfile>(value => value with { SnapshotInputs = [new SnapshotInputDefinition("Other.dll", "gameAssembly")] })];
        yield return [new Func<ExtractionProfile, ExtractionProfile>(value => value with { SnapshotInputs = [new SnapshotInputDefinition("GameAssembly.dll", "otherRole")] })];
        yield return [new Func<ExtractionProfile, ExtractionProfile>(value => value with { UnityVersionSources = ["Other"] })];
    }

    public static IEnumerable<object[]> PolicyMutations()
    {
        yield return [new Func<ValidationPolicy, ValidationPolicy>(value => value with { SchemaVersion = 2 })];
        yield return [new Func<ValidationPolicy, ValidationPolicy>(value => value with { PolicyId = "another-policy" })];
        yield return [new Func<ValidationPolicy, ValidationPolicy>(value => value with { PolicyVersion = 2 })];
        yield return [new Func<ValidationPolicy, ValidationPolicy>(value => value with { RequiredAssemblyIdentities = ["Other"] })];
        yield return [new Func<ValidationPolicy, ValidationPolicy>(value => value with { MinimumManagedAssemblyCount = 2 })];
        yield return [new Func<ValidationPolicy, ValidationPolicy>(value => value with { MinimumTypeDefinitionCount = 2 })];
        yield return [new Func<ValidationPolicy, ValidationPolicy>(value => value with { MinimumMethodDefinitionCount = 2 })];
        yield return [new Func<ValidationPolicy, ValidationPolicy>(value => value with { MinimumTotalManagedBytes = 2 })];
        yield return [new Func<ValidationPolicy, ValidationPolicy>(value => value with { ComparativeWarningRelativeChange = 0.5 })];
        yield return [new Func<ValidationPolicy, ValidationPolicy>(value => value with { CatastrophicDecreaseRelativeChange = 0.9 })];
    }

    [Theory]
    [MemberData(nameof(ProfileMutations))]
    public void Create_WhenAnyEffectiveProfileFieldChanges_ChangesDigest(
        Func<ExtractionProfile, ExtractionProfile> mutate)
    {
        Assert.NotEqual(
            ExtractionProfileFingerprint.Create(ProfileFixture.Valid),
            ExtractionProfileFingerprint.Create(mutate(ProfileFixture.Valid)));
    }

    [Theory]
    [MemberData(nameof(PolicyMutations))]
    public void Create_WhenAnyEffectivePolicyFieldChanges_ChangesDigest(
        Func<ValidationPolicy, ValidationPolicy> mutate)
    {
        Assert.NotEqual(
            ValidationPolicyFingerprint.Create(PolicyFixture.Valid),
            ValidationPolicyFingerprint.Create(mutate(PolicyFixture.Valid)));
    }

    [Fact]
    public void Create_WhenSemanticallyUnorderedProfileCollectionsReordered_ReturnsSameDigest()
    {
        var reordered = ProfileFixture.Valid with
        {
            AcceptedExitCodes = [1, 0],
            RequiredAssemblyIdentities = ["UnityEngine", "Assembly-CSharp"]
        };
        var baseline = reordered with
        {
            AcceptedExitCodes = [0, 1],
            RequiredAssemblyIdentities = ["Assembly-CSharp", "UnityEngine"]
        };

        Assert.Equal(
            ExtractionProfileFingerprint.Create(baseline),
            ExtractionProfileFingerprint.Create(reordered));
    }

    [Fact]
    public void Create_WhenSemanticallyUnorderedPolicyIdentitiesReordered_ReturnsSameDigest()
    {
        var reordered = PolicyFixture.Valid with
        {
            RequiredAssemblyIdentities = ["UnityEngine", "Assembly-CSharp"]
        };
        var baseline = reordered with
        {
            RequiredAssemblyIdentities = ["Assembly-CSharp", "UnityEngine"]
        };

        Assert.Equal(
            ValidationPolicyFingerprint.Create(baseline),
            ValidationPolicyFingerprint.Create(reordered));
    }

    private static class ProfileFixture
    {
        public static readonly ExtractionProfile Valid = new(
            1, "cpp2il-reconstructed-assemblies-v1", 1, 1, 1,
            "Schedule I", "dll_il_recovery", TimeSpan.FromSeconds(1800),
            64L * 1024 * 1024, 64L * 1024 * 1024, [0], ["Assembly-CSharp"],
            [new SnapshotInputDefinition("GameAssembly.dll", "gameAssembly")],
            ["Schedule I_Data/globalgamemanagers"]);
    }

    private static class PolicyFixture
    {
        public static readonly ValidationPolicy Valid = new(
            1, "managed-assemblies-v1", 1, ["Assembly-CSharp"], 1, 1, 1,
            1024L * 1024, 0.25, 0.80);
    }
}
