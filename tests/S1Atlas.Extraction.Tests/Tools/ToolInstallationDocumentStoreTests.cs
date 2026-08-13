using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Tools;
using Xunit;

namespace S1Atlas.Extraction.Tests.Tools;

public sealed class ToolInstallationDocumentStoreTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory =
        ToolTestFixture.CreateTemporaryDirectory();

    [Fact]
    public async Task WriteAsync_ThenTryReadAsync_RoundTripsNormalizedDocuments()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var definition = CreateDefinition();
        var installRoot = Path.Combine(_temporaryDirectory, "install");
        Directory.CreateDirectory(installRoot);
        var installation = CreateInstallation(definition, installRoot);
        var store = new ToolInstallationDocumentStore();

        await store.WriteAsync(
            installRoot,
            definition,
            installation,
            cancellationToken);
        var result = await store.TryReadAsync(installRoot, cancellationToken);

        Assert.NotNull(result);
        Assert.Equal(definition.DefinitionDigest, result.Value.Definition.DefinitionDigest);
        Assert.Equal(definition.Definition, result.Value.Definition.Definition);
        Assert.Equal(installation.ToolId, result.Value.Installation.ToolId);
        Assert.Equal(installation.DisplayName, result.Value.Installation.DisplayName);
        Assert.Equal(installation.Version, result.Value.Installation.Version);
        Assert.Equal(installation.Platform, result.Value.Installation.Platform);
        Assert.Equal(installation.DefinitionDigest, result.Value.Installation.DefinitionDigest);
        Assert.Equal(installation.ExecutableSha256, result.Value.Installation.ExecutableSha256);
        Assert.Equal(installation.RootPath, result.Value.Installation.RootPath);
        Assert.Equal(installation.InstalledAtUtc, result.Value.Installation.InstalledAtUtc);
        Assert.Equal(
            installation.LastVerifiedAtUtc,
            result.Value.Installation.LastVerifiedAtUtc);
        Assert.Single(result.Value.Installation.ProbeResults);
        Assert.True(result.Value.Installation.ProbeResults[0].Succeeded);
        Assert.True(File.Exists(Path.Combine(installRoot, "tool-manifest.json")));
        Assert.True(File.Exists(Path.Combine(installRoot, "installation.json")));
    }

    [Fact]
    public async Task TryReadAsync_WhenDocumentIsMalformed_ReturnsNull()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var installRoot = Path.Combine(_temporaryDirectory, "malformed");
        Directory.CreateDirectory(installRoot);
        await File.WriteAllTextAsync(
            Path.Combine(installRoot, "tool-manifest.json"),
            "{ not-json",
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(installRoot, "installation.json"),
            "{}",
            cancellationToken);
        var store = new ToolInstallationDocumentStore();

        var result = await store.TryReadAsync(installRoot, cancellationToken);

        Assert.Null(result);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ResolvedToolDefinition CreateDefinition(
        byte[]? executableBytes = null,
        string displayName = "Cpp2IL")
    {
        executableBytes ??= [1, 2, 3, 4];
        var sha256 = Convert
            .ToHexString(System.Security.Cryptography.SHA256.HashData(executableBytes))
            .ToLowerInvariant();
        var definition = new ToolDefinition(
            SchemaVersion: 1,
            ToolId: "cpp2il",
            DisplayName: displayName,
            Version: "test-version",
            Platform: "win-x64",
            Package: new ToolPackageDefinition(
                ToolPackageKind.SingleFile,
                ArchiveFormat: null,
                SourceUri: new Uri("https://example.test/tool.exe"),
                ReleaseUri: new Uri("https://example.test/releases/tool"),
                AssetName: "tool.exe",
                ExpectedSize: executableBytes.Length,
                Sha256: sha256,
                ExecutableRelativePath: "Cpp2IL.exe",
                Limits: new ToolSafetyLimits(
                    executableBytes.Length,
                    executableBytes.Length,
                    1)),
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
        return new ResolvedToolDefinition(
            definition,
            ToolDefinitionFingerprint.Create(definition));
    }

    internal static ManagedToolInstallation CreateInstallation(
        ResolvedToolDefinition definition,
        string installRoot,
        string? executableSha256 = null,
        DateTimeOffset? installedAtUtc = null,
        DateTimeOffset? lastVerifiedAtUtc = null) =>
        new(
            SchemaVersion: 1,
            ToolId: definition.Definition.ToolId,
            DisplayName: definition.Definition.DisplayName,
            Version: definition.Definition.Version,
            Platform: definition.Definition.Platform,
            DefinitionDigest: definition.DefinitionDigest,
            PackageSha256: definition.Definition.Package.Sha256,
            ExecutableSha256:
                executableSha256 ?? definition.Definition.Package.Sha256,
            RootPath: Path.GetFullPath(installRoot),
            Status: ToolInstallationStatus.Verified,
            InstalledAtUtc:
                installedAtUtc ?? DateTimeOffset.Parse("2026-08-13T02:10:00Z"),
            LastVerifiedAtUtc:
                lastVerifiedAtUtc ?? DateTimeOffset.Parse("2026-08-13T02:15:00Z"),
            ProbeResults:
            [
                new ToolProbeResult(
                    "help",
                    Succeeded: true,
                    ExitCode: 0,
                    TimedOut: false,
                    StandardOutputTruncated: false,
                    StandardErrorTruncated: false,
                    FailureCode: null,
                    FailureMessage: null)
            ],
            ReplacedInstallationPath: null);
}
