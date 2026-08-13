using S1Atlas.Core.Tools;
using Xunit;

namespace S1Atlas.Core.Tests.Tools;

public sealed class ToolDefinitionFingerprintTests
{
    [Fact]
    public void Create_WithEquivalentDefinition_ReturnsSameDigest()
    {
        var first = CreateDefinition();
        var second = CreateDefinition();

        var firstDigest = ToolDefinitionFingerprint.Create(first);
        var secondDigest = ToolDefinitionFingerprint.Create(second);

        Assert.Equal(firstDigest, secondDigest);
        Assert.Matches("^[0-9a-f]{64}$", firstDigest);
    }

    [Fact]
    public void Create_WhenProbeRequirementChanges_ReturnsDifferentDigest()
    {
        var baseline = CreateDefinition();
        var changedProbe = baseline.Probes[1] with
        {
            RequiredOutputSubstrings = ["different-output-format"]
        };
        var changed = baseline with
        {
            Probes = [baseline.Probes[0], changedProbe]
        };

        Assert.NotEqual(
            ToolDefinitionFingerprint.Create(baseline),
            ToolDefinitionFingerprint.Create(changed));
    }

    [Fact]
    public void Create_WhenLicenseOrSafetyLimitChanges_ReturnsDifferentDigest()
    {
        var baseline = CreateDefinition();
        var changedLicense = baseline with
        {
            License = baseline.License with
            {
                SpdxIdentifier = "Apache-2.0"
            }
        };
        var changedLimits = baseline with
        {
            Package = baseline.Package with
            {
                Limits = baseline.Package.Limits with
                {
                    MaximumDownloadBytes =
                        baseline.Package.Limits.MaximumDownloadBytes + 1
                }
            }
        };
        var baselineDigest = ToolDefinitionFingerprint.Create(baseline);

        Assert.NotEqual(
            baselineDigest,
            ToolDefinitionFingerprint.Create(changedLicense));
        Assert.NotEqual(
            baselineDigest,
            ToolDefinitionFingerprint.Create(changedLimits));
    }

    private static ToolDefinition CreateDefinition() =>
        new(
            SchemaVersion: 1,
            ToolId: "cpp2il",
            DisplayName: "Cpp2IL",
            Version: "2022.1.0-pre-release.21",
            Platform: "win-x64",
            Package: new ToolPackageDefinition(
                Kind: ToolPackageKind.SingleFile,
                ArchiveFormat: null,
                SourceUri: new Uri("https://example.test/Cpp2IL.exe"),
                ReleaseUri: new Uri("https://example.test/releases/cpp2il"),
                AssetName: "Cpp2IL.exe",
                ExpectedSize: 128,
                Sha256: new string('a', 64),
                ExecutableRelativePath: "Cpp2IL.exe",
                Limits: new ToolSafetyLimits(
                    MaximumDownloadBytes: 128,
                    MaximumExpandedBytes: 128,
                    MaximumFileCount: 1)),
            License: new ToolLicenseDefinition(
                SpdxIdentifier: "MIT",
                SourceUri: new Uri("https://example.test/LICENSE")),
            Probes:
            [
                new ToolProbeDefinition(
                    ProbeId: "help",
                    Arguments: ["--help"],
                    AcceptedExitCodes: [0],
                    Timeout: TimeSpan.FromSeconds(30),
                    RequiredOutputSubstrings: []),
                new ToolProbeDefinition(
                    ProbeId: "output-formats",
                    Arguments: ["--list-output-formats"],
                    AcceptedExitCodes: [0],
                    Timeout: TimeSpan.FromSeconds(30),
                    RequiredOutputSubstrings: ["dll_il_recovery"])
            ]);
}
