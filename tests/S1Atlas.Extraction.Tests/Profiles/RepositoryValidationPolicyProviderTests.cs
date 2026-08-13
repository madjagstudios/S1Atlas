using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Profiles;
using Xunit;

namespace S1Atlas.Extraction.Tests.Profiles;

public sealed class RepositoryValidationPolicyProviderTests
{
    [Fact]
    public void GetRequired_LoadsExactProductionPolicy()
    {
        var provider = new RepositoryValidationPolicyProvider(ProfileTestFixture.ValidationDirectory);

        var policy = provider.GetRequired("managed-assemblies-v1");

        Assert.Equal(1, policy.Policy.SchemaVersion);
        Assert.Equal(1, policy.Policy.PolicyVersion);
        Assert.Equal(["Assembly-CSharp"], policy.Policy.RequiredAssemblyIdentities);
        Assert.Equal(1, policy.Policy.MinimumManagedAssemblyCount);
        Assert.Equal(1, policy.Policy.MinimumTypeDefinitionCount);
        Assert.Equal(1, policy.Policy.MinimumMethodDefinitionCount);
        Assert.Equal(1024L * 1024, policy.Policy.MinimumTotalManagedBytes);
        Assert.Equal(0.25, policy.Policy.ComparativeWarningRelativeChange);
        Assert.Equal(0.80, policy.Policy.CatastrophicDecreaseRelativeChange);
    }

    [Fact]
    public void GetRequired_IsCaseSensitive()
    {
        var provider = new RepositoryValidationPolicyProvider(ProfileTestFixture.ValidationDirectory);

        var exception = Assert.Throws<ToolOperationException>(
            () => provider.GetRequired("MANAGED-ASSEMBLIES-V1"));

        Assert.Equal("UnknownValidationPolicy", exception.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-0.1)]
    public void GetRequired_WhenRatiosAreInvalid_Rejects(double ratio)
    {
        ProfileTestFixture.AssertPolicyRejected(
            ProfileTestFixture.ValidValidationJson.Replace(
                "\"comparativeWarningRelativeChange\": 0.25",
                $"\"comparativeWarningRelativeChange\": {ratio.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                StringComparison.Ordinal));
    }

    [Fact]
    public void GetRequired_WhenCatastrophicRatioIsNotGreaterThanWarning_Rejects()
    {
        ProfileTestFixture.AssertPolicyRejected(
            ProfileTestFixture.ValidValidationJson.Replace(
                "\"catastrophicDecreaseRelativeChange\": 0.80",
                "\"catastrophicDecreaseRelativeChange\": 0.25", StringComparison.Ordinal));
    }

    [Fact]
    public void GetRequired_WhenRequiredAssemblyIdentityRepeats_Rejects()
    {
        ProfileTestFixture.AssertPolicyRejected(
            ProfileTestFixture.ValidValidationJson.Replace(
                "[\"Assembly-CSharp\"]", "[\"Assembly-CSharp\", \"Assembly-CSharp\"]", StringComparison.Ordinal));
    }

    [Fact]
    public void GetAll_WhenPolicyIdRepeats_Rejects()
    {
        var directory = ProfileTestFixture.CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "one.json"), ProfileTestFixture.ValidValidationJson);
            File.WriteAllText(Path.Combine(directory, "two.json"), ProfileTestFixture.ValidValidationJson);
            var provider = new RepositoryValidationPolicyProvider(directory);

            var exception = Assert.Throws<ToolOperationException>(provider.GetAll);

            Assert.Equal("ValidationPolicyInvalid", exception.Code);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void GetRequired_WhenJsonFormattingAndPropertyOrderDiffer_ReturnsSameDigestWithoutRepositoryPath()
    {
        var first = ProfileTestFixture.ResolvePolicyFromJson(ProfileTestFixture.ValidValidationJson);
        var reordered = """
            { "catastrophicDecreaseRelativeChange": 0.80, "comparativeWarningRelativeChange": 0.25, "minimumTotalManagedBytes": 1048576, "minimumMethodDefinitionCount": 1, "minimumTypeDefinitionCount": 1, "minimumManagedAssemblyCount": 1, "requiredAssemblyIdentities": ["Assembly-CSharp"], "policyVersion": 1, "policyId": "managed-assemblies-v1", "schemaVersion": 1 }
            """;
        var second = ProfileTestFixture.ResolvePolicyFromJson(reordered);

        Assert.Equal(first.PolicyDigest, second.PolicyDigest);
    }
}
