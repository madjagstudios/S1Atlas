using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Tools;
using Xunit;

namespace S1Atlas.Extraction.Tests.Tools;

public sealed class ToolPathPolicyTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory =
        ToolTestFixture.CreateTemporaryDirectory();

    [Fact]
    public void GetManagedInstallRoot_UsesSafeToolAndVersionSegments()
    {
        var toolsRoot = Path.Combine(_temporaryDirectory, "tools");
        var definition = CreateDefinition();

        var result = ToolPathPolicy.GetManagedInstallRoot(toolsRoot, definition);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(toolsRoot, "cpp2il", "test-version")),
            result);
    }

    [Fact]
    public void ResolveContainedRelativePath_WithSafeNestedPath_ReturnsContainedFullPath()
    {
        var root = Path.Combine(_temporaryDirectory, "root");

        var result = ToolPathPolicy.ResolveContainedRelativePath(
            root,
            "nested/Cpp2IL.exe");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(root, "nested", "Cpp2IL.exe")),
            result);
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("nested/../../escape.exe")]
    [InlineData("nested\\..\\..\\escape.exe")]
    [InlineData("C:\\escape.exe")]
    [InlineData("/escape.exe")]
    public void ResolveContainedRelativePath_WhenPathEscapes_Rejects(string relativePath)
    {
        var root = Path.Combine(_temporaryDirectory, "root");

        var exception = Assert.Throws<ToolOperationException>(() =>
            ToolPathPolicy.ResolveContainedRelativePath(root, relativePath));

        Assert.Equal("ToolInstallationFailed", exception.Code);
    }

    [Fact]
    public void CreateStagingAndQuarantinePaths_AreContainedAndCollisionResistant()
    {
        var definition = CreateDefinition();
        var stagingRoot = Path.Combine(_temporaryDirectory, "staging");
        var quarantineRoot = Path.Combine(_temporaryDirectory, "quarantine");
        var timestamp = DateTimeOffset.Parse("2026-08-13T02:00:00Z");

        var firstStaging = ToolPathPolicy.CreateStagingPath(
            stagingRoot,
            definition);
        var secondStaging = ToolPathPolicy.CreateStagingPath(
            stagingRoot,
            definition);
        var quarantine = ToolPathPolicy.CreateQuarantinePath(
            quarantineRoot,
            definition,
            timestamp);

        Assert.NotEqual(firstStaging, secondStaging);
        Assert.StartsWith(
            Path.GetFullPath(stagingRoot) + Path.DirectorySeparatorChar,
            firstStaging,
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            Path.GetFullPath(quarantineRoot) + Path.DirectorySeparatorChar,
            quarantine,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cpp2il", quarantine, StringComparison.Ordinal);
        Assert.Contains("test-version", quarantine, StringComparison.Ordinal);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static ToolDefinition CreateDefinition() =>
        new(
            SchemaVersion: 1,
            ToolId: "cpp2il",
            DisplayName: "Cpp2IL",
            Version: "test-version",
            Platform: "win-x64",
            Package: new ToolPackageDefinition(
                ToolPackageKind.SingleFile,
                ArchiveFormat: null,
                SourceUri: new Uri("https://example.test/tool.exe"),
                ReleaseUri: new Uri("https://example.test/releases/tool"),
                AssetName: "tool.exe",
                ExpectedSize: 1,
                Sha256: new string('a', 64),
                ExecutableRelativePath: "Cpp2IL.exe",
                Limits: new ToolSafetyLimits(1, 1, 1)),
            License: new ToolLicenseDefinition(
                "MIT",
                new Uri("https://example.test/LICENSE")),
            Probes:
            [
                new ToolProbeDefinition(
                    "help",
                    ["--help"],
                    [0],
                    TimeSpan.FromSeconds(30),
                    [])
            ]);
}
