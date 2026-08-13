using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Profiles;
using Xunit;

namespace S1Atlas.Extraction.Tests.Profiles;

internal static class ProfileTestFixture
{
    public const string ValidExtractionJson = """
        { "schemaVersion": 1, "profileId": "cpp2il-reconstructed-assemblies-v1", "profileVersion": 1, "adapterVersion": 1, "extractionSchemaVersion": 1, "executableName": "Schedule I", "outputFormat": "dll_il_recovery", "timeoutSeconds": 1800, "maximumRetainedStandardOutputBytes": 67108864, "maximumRetainedStandardErrorBytes": 67108864, "acceptedExitCodes": [0], "requiredAssemblyIdentities": ["Assembly-CSharp"], "snapshotInputs": [{ "relativePath": "GameAssembly.dll", "role": "gameAssembly" }], "unityVersionSources": ["Schedule I_Data/globalgamemanagers"] }
        """;

    public const string ValidValidationJson = """
        { "schemaVersion": 1, "policyId": "managed-assemblies-v1", "policyVersion": 1, "requiredAssemblyIdentities": ["Assembly-CSharp"], "minimumManagedAssemblyCount": 1, "minimumTypeDefinitionCount": 1, "minimumMethodDefinitionCount": 1, "minimumTotalManagedBytes": 1048576, "comparativeWarningRelativeChange": 0.25, "catastrophicDecreaseRelativeChange": 0.80 }
        """;

    public static string RepositoryRoot { get; } = FindRepositoryRoot();
    public static string ExtractionDirectory => Path.Combine(RepositoryRoot, "config", "extraction");
    public static string ValidationDirectory => Path.Combine(RepositoryRoot, "config", "validation");

    public static void AssertExtractionRejected(string json)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "profile.json"), json);
            var exception = Assert.Throws<ToolOperationException>(
                () => new RepositoryExtractionProfileProvider(directory).GetAll());
            Assert.Equal("ExtractionProfileInvalid", exception.Code);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    public static void AssertPolicyRejected(string json)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "policy.json"), json);
            var exception = Assert.Throws<ToolOperationException>(
                () => new RepositoryValidationPolicyProvider(directory).GetAll());
            Assert.Equal("ValidationPolicyInvalid", exception.Code);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    public static ResolvedExtractionProfile ResolveExtractionFromJson(string json)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "profile.json"), json);
            return new RepositoryExtractionProfileProvider(directory).GetRequired("cpp2il-reconstructed-assemblies-v1");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    public static ResolvedValidationPolicy ResolvePolicyFromJson(string json)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "policy.json"), json);
            return new RepositoryValidationPolicyProvider(directory).GetRequired("managed-assemblies-v1");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    public static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"s1atlas-profile-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "S1Atlas.sln"))) return current.FullName;
        }
        throw new InvalidOperationException("The S1Atlas repository root could not be located for profile tests.");
    }
}
