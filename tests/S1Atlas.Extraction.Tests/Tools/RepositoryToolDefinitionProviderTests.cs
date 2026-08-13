using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Tools;
using Xunit;

namespace S1Atlas.Extraction.Tests.Tools;

public sealed class RepositoryToolDefinitionProviderTests
{
    [Fact]
    public void GetRequired_Cpp2IlWindowsX64_ReturnsApprovedPin()
    {
        var definitionDirectory = Path.Combine(
            ToolTestFixture.RepositoryRoot,
            "config",
            "tools");
        var provider = new RepositoryToolDefinitionProvider(definitionDirectory);

        var resolved = provider.GetRequired("cpp2il", "win-x64");
        var definition = resolved.Definition;

        Assert.Equal(1, definition.SchemaVersion);
        Assert.Equal("cpp2il", definition.ToolId);
        Assert.Equal("Cpp2IL", definition.DisplayName);
        Assert.Equal("2022.1.0-pre-release.21", definition.Version);
        Assert.Equal("win-x64", definition.Platform);
        Assert.Equal(ToolPackageKind.SingleFile, definition.Package.Kind);
        Assert.Null(definition.Package.ArchiveFormat);
        Assert.Equal(
            new Uri("https://github.com/SamboyCoding/Cpp2IL/releases/download/2022.1.0-pre-release.21/Cpp2IL-2022.1.0-pre-release.21-Windows.exe"),
            definition.Package.SourceUri);
        Assert.Equal(
            new Uri("https://github.com/SamboyCoding/Cpp2IL/releases/tag/2022.1.0-pre-release.21"),
            definition.Package.ReleaseUri);
        Assert.Equal(
            "Cpp2IL-2022.1.0-pre-release.21-Windows.exe",
            definition.Package.AssetName);
        Assert.Equal(15_137_811, definition.Package.ExpectedSize);
        Assert.Equal(
            "663fb432433b4371fd1ee0ebc321a8fff2a9aac5ac4230c843f9e03ddee4e04c",
            definition.Package.Sha256);
        Assert.Equal("Cpp2IL.exe", definition.Package.ExecutableRelativePath);
        Assert.Equal(15_137_811, definition.Package.Limits.MaximumDownloadBytes);
        Assert.Equal(15_137_811, definition.Package.Limits.MaximumExpandedBytes);
        Assert.Equal(1, definition.Package.Limits.MaximumFileCount);
        Assert.Equal("MIT", definition.License.SpdxIdentifier);
        Assert.Equal(2, definition.Probes.Count);
        Assert.Equal("help", definition.Probes[0].ProbeId);
        Assert.Equal(new[] { "--help" }, definition.Probes[0].Arguments);
        Assert.Equal("output-formats", definition.Probes[1].ProbeId);
        Assert.Equal(
            new[] { "--list-output-formats" },
            definition.Probes[1].Arguments);
        Assert.Equal(
            new[] { "dll_il_recovery" },
            definition.Probes[1].RequiredOutputSubstrings);
        Assert.Matches("^[0-9a-f]{64}$", resolved.DefinitionDigest);
    }

    [Fact]
    public void GetAll_WhenToolPlatformPairRepeats_Rejects()
    {
        var directory = ToolTestFixture.CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "first.json"),
                ToolTestFixture.ValidDefinitionJson);
            File.WriteAllText(
                Path.Combine(directory, "second.json"),
                ToolTestFixture.ValidDefinitionJson.Replace(
                    "\"displayName\": \"Cpp2IL\"",
                    "\"displayName\": \"Duplicate Cpp2IL\"",
                    StringComparison.Ordinal));
            var provider = new RepositoryToolDefinitionProvider(directory);

            var exception = Assert.Throws<ToolOperationException>(provider.GetAll);

            Assert.Equal("ToolDefinitionInvalid", exception.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
