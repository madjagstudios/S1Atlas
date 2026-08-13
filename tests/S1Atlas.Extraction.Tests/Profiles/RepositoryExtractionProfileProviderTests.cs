using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Profiles;
using Xunit;

namespace S1Atlas.Extraction.Tests.Profiles;

public sealed class RepositoryExtractionProfileProviderTests
{
    [Fact]
    public void GetRequired_LoadsExactProductionProfile()
    {
        var profilePath = Path.Combine(
            ProfileTestFixture.RepositoryRoot, "config", "extraction",
            "cpp2il-reconstructed-assemblies-v1.json");
        var provider = new RepositoryExtractionProfileProvider(Path.GetDirectoryName(profilePath)!);

        var profile = provider.GetRequired("cpp2il-reconstructed-assemblies-v1");

        Assert.Equal(1, profile.Profile.SchemaVersion);
        Assert.Equal(1, profile.Profile.ProfileVersion);
        Assert.Equal(1, profile.Profile.AdapterVersion);
        Assert.Equal("Schedule I", profile.Profile.ExecutableName);
        Assert.Equal("dll_il_recovery", profile.Profile.OutputFormat);
        Assert.Equal(TimeSpan.FromMinutes(30), profile.Profile.Timeout);
        Assert.Equal(64L * 1024 * 1024, profile.Profile.MaximumRetainedStandardOutputBytes);
        Assert.Equal(64L * 1024 * 1024, profile.Profile.MaximumRetainedStandardErrorBytes);
        Assert.Equal([0], profile.Profile.AcceptedExitCodes);
        Assert.Equal(["Assembly-CSharp"], profile.Profile.RequiredAssemblyIdentities);
        Assert.Equal(
            [
                "GameAssembly.dll",
                "Schedule I_Data/il2cpp_data/Metadata/global-metadata.dat",
                "Schedule I.exe"
            ],
            profile.Profile.SnapshotInputs.Select(input => input.RelativePath));
        Assert.Equal(
            ["gameAssembly", "globalMetadata", "executableSupport"],
            profile.Profile.SnapshotInputs.Select(input => input.Role));
        Assert.Equal(
            ["Schedule I_Data/globalgamemanagers", "Schedule I_Data/data.unity3d"],
            profile.Profile.UnityVersionSources);
        Assert.DoesNotContain("arguments", File.ReadAllText(profilePath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetRequired_IsCaseSensitive()
    {
        var provider = new RepositoryExtractionProfileProvider(ProfileTestFixture.ExtractionDirectory);

        var exception = Assert.Throws<ToolOperationException>(
            () => provider.GetRequired("CPP2IL-RECONSTRUCTED-ASSEMBLIES-V1"));

        Assert.Equal("UnknownExtractionProfile", exception.Code);
    }

    [Theory]
    [InlineData("\"unexpected\": true,")]
    [InlineData("")]
    public void GetRequired_WhenDocumentIsMalformed_Rejects(string extraProperty)
    {
        var json = ProfileTestFixture.ValidExtractionJson.Replace(
            "\"profileId\":", extraProperty + "\n  \"profileId\":",
            StringComparison.Ordinal);
        if (string.IsNullOrEmpty(extraProperty))
        {
            json = json.Replace("\"outputFormat\": \"dll_il_recovery\", ", string.Empty, StringComparison.Ordinal);
        }

        ProfileTestFixture.AssertExtractionRejected(json);
    }

    [Theory]
    [InlineData("../GameAssembly.dll")]
    [InlineData("C:/GameAssembly.dll")]
    [InlineData("Schedule I_Data//data.unity3d")]
    public void GetRequired_WhenSnapshotPathIsInvalid_Rejects(string path)
    {
        ProfileTestFixture.AssertExtractionRejected(
            ProfileTestFixture.ValidExtractionJson.Replace(
                "GameAssembly.dll", path, StringComparison.Ordinal));
    }

    [Fact]
    public void GetRequired_WhenTimeoutIsNonIntegral_Rejects()
    {
        ProfileTestFixture.AssertExtractionRejected(
            ProfileTestFixture.ValidExtractionJson.Replace(
                "\"timeoutSeconds\": 1800", "\"timeoutSeconds\": 1800.5", StringComparison.Ordinal));
    }

    [Fact]
    public void GetAll_WhenProfileIdRepeats_Rejects()
    {
        var directory = ProfileTestFixture.CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "one.json"), ProfileTestFixture.ValidExtractionJson);
            File.WriteAllText(Path.Combine(directory, "two.json"), ProfileTestFixture.ValidExtractionJson);
            var provider = new RepositoryExtractionProfileProvider(directory);

            var exception = Assert.Throws<ToolOperationException>(provider.GetAll);

            Assert.Equal("ExtractionProfileInvalid", exception.Code);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void GetRequired_WhenJsonFormattingAndPropertyOrderDiffer_ReturnsSameDigestWithoutRepositoryPath()
    {
        var first = ProfileTestFixture.ResolveExtractionFromJson(ProfileTestFixture.ValidExtractionJson);
        var reordered = """
            {
              "unityVersionSources": ["Schedule I_Data/globalgamemanagers"],
              "snapshotInputs": [{ "role": "gameAssembly", "relativePath": "GameAssembly.dll" }],
              "requiredAssemblyIdentities": ["Assembly-CSharp"],
              "acceptedExitCodes": [0],
              "maximumRetainedStandardErrorBytes": 67108864,
              "maximumRetainedStandardOutputBytes": 67108864,
              "timeoutSeconds": 1800,
              "outputFormat": "dll_il_recovery",
              "executableName": "Schedule I",
              "extractionSchemaVersion": 1,
              "adapterVersion": 1,
              "profileVersion": 1,
              "profileId": "cpp2il-reconstructed-assemblies-v1",
              "schemaVersion": 1
            }
            """;
        var second = ProfileTestFixture.ResolveExtractionFromJson(reordered);

        Assert.Equal(first.ProfileDigest, second.ProfileDigest);
    }
}
