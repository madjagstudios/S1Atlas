using S1Atlas.Core.Hashing;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Tools;
using Xunit;

namespace S1Atlas.Extraction.Tests.Tools;

public sealed class ManagedToolInstallationValidatorTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory =
        ToolTestFixture.CreateTemporaryDirectory();
    private readonly string _toolsRoot;
    private readonly ToolInstallationDocumentStore _documentStore = new();
    private readonly IFileHasher _fileHasher = new Sha256FileHasher();
    private readonly DateTimeOffset _verificationTime =
        DateTimeOffset.Parse("2026-08-13T03:00:00Z");

    public ManagedToolInstallationValidatorTests()
    {
        _toolsRoot = Path.Combine(_temporaryDirectory, "tools");
    }

    [Fact]
    public async Task InspectAsync_WhenRootDoesNotExist_ReturnsNotInstalled()
    {
        var definition = ToolInstallationDocumentStoreTests.CreateDefinition();
        var validator = CreateValidator(SuccessfulProbe);

        var status = await validator.InspectAsync(
            definition,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolInstallationStatus.NotInstalled, status.Status);
        Assert.Null(status.Installation);
        Assert.Equal("ToolNotInstalled", status.DiagnosticCode);
    }

    [Fact]
    public async Task InspectAsync_WhenDocumentsOrExecutableAreMissing_ReturnsIncomplete()
    {
        var definition = ToolInstallationDocumentStoreTests.CreateDefinition();
        var installRoot = ToolPathPolicy.GetManagedInstallRoot(
            _toolsRoot,
            definition.Definition);
        Directory.CreateDirectory(installRoot);
        var validator = CreateValidator(SuccessfulProbe);

        var status = await validator.InspectAsync(
            definition,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolInstallationStatus.Incomplete, status.Status);
        Assert.Equal("ToolInstallationIncomplete", status.DiagnosticCode);
    }

    [Fact]
    public async Task InspectAsync_WhenLocalDefinitionDigestDiffers_ReturnsDefinitionMismatch()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var expected = ToolInstallationDocumentStoreTests.CreateDefinition(bytes);
        var local = ToolInstallationDocumentStoreTests.CreateDefinition(
            bytes,
            displayName: "Changed Cpp2IL");
        var installRoot = ToolPathPolicy.GetManagedInstallRoot(
            _toolsRoot,
            expected.Definition);
        await PrepareAsync(
            installRoot,
            bytes,
            local,
            ToolInstallationDocumentStoreTests.CreateInstallation(local, installRoot));
        var probeCount = 0;
        var validator = CreateValidator((_, _, probe, _) =>
        {
            probeCount++;
            return Task.FromResult(SuccessfulProbe(probe));
        });

        var status = await validator.InspectAsync(
            expected,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolInstallationStatus.DefinitionMismatch, status.Status);
        Assert.Equal("ToolDefinitionMismatch", status.DiagnosticCode);
        Assert.Equal(0, probeCount);
    }

    [Fact]
    public async Task InspectAsync_WhenExecutableHashDiffers_ReturnsCorruptWithoutRunningProbes()
    {
        var expectedBytes = new byte[] { 1, 2, 3, 4 };
        var definition = ToolInstallationDocumentStoreTests.CreateDefinition(
            expectedBytes);
        var installRoot = ToolPathPolicy.GetManagedInstallRoot(
            _toolsRoot,
            definition.Definition);
        await PrepareAsync(
            installRoot,
            expectedBytes,
            definition,
            ToolInstallationDocumentStoreTests.CreateInstallation(
                definition,
                installRoot));
        await File.WriteAllBytesAsync(
            Path.Combine(installRoot, "Cpp2IL.exe"),
            [9, 9, 9, 9],
            TestContext.Current.CancellationToken);
        var probeCount = 0;
        var validator = CreateValidator((_, _, probe, _) =>
        {
            probeCount++;
            return Task.FromResult(SuccessfulProbe(probe));
        });

        var status = await validator.InspectAsync(
            definition,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolInstallationStatus.Corrupt, status.Status);
        Assert.Equal("ToolExecutableChecksumMismatch", status.DiagnosticCode);
        Assert.Equal(0, probeCount);
    }

    [Fact]
    public async Task InspectAsync_WhenProbeFails_ReturnsProbeFailed()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var definition = ToolInstallationDocumentStoreTests.CreateDefinition(bytes);
        var installRoot = ToolPathPolicy.GetManagedInstallRoot(
            _toolsRoot,
            definition.Definition);
        await PrepareAsync(
            installRoot,
            bytes,
            definition,
            ToolInstallationDocumentStoreTests.CreateInstallation(
                definition,
                installRoot));
        var validator = CreateValidator((_, _, probe, _) => Task.FromResult(
            new ToolProbeResult(
                probe.ProbeId,
                Succeeded: false,
                ExitCode: 5,
                TimedOut: false,
                StandardOutputTruncated: false,
                StandardErrorTruncated: false,
                FailureCode: "ToolProbeExitCodeRejected",
                FailureMessage: "The exit code was rejected.")));

        var status = await validator.InspectAsync(
            definition,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolInstallationStatus.ProbeFailed, status.Status);
        Assert.NotNull(status.Installation);
        Assert.Equal(ToolInstallationStatus.ProbeFailed, status.Installation.Status);
        Assert.Equal("ToolProbeFailed", status.DiagnosticCode);
        Assert.Single(status.Installation.ProbeResults);
        Assert.False(status.Installation.ProbeResults[0].Succeeded);
    }

    [Fact]
    public async Task InspectAsync_WhenEverythingMatches_ReturnsVerifiedWithFreshVerificationTime()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var definition = ToolInstallationDocumentStoreTests.CreateDefinition(bytes);
        var installRoot = ToolPathPolicy.GetManagedInstallRoot(
            _toolsRoot,
            definition.Definition);
        var installedAt = DateTimeOffset.Parse("2026-08-13T02:10:00Z");
        var stored = ToolInstallationDocumentStoreTests.CreateInstallation(
            definition,
            installRoot,
            installedAtUtc: installedAt) with
        {
            RootPath = Path.Combine(_temporaryDirectory, "old-atlas-root")
        };
        await PrepareAsync(installRoot, bytes, definition, stored);
        var validator = CreateValidator(SuccessfulProbe);

        var status = await validator.InspectAsync(
            definition,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolInstallationStatus.Verified, status.Status);
        Assert.Null(status.DiagnosticCode);
        Assert.NotNull(status.Installation);
        Assert.Equal(ToolInstallationStatus.Verified, status.Installation.Status);
        Assert.Equal(installedAt, status.Installation.InstalledAtUtc);
        Assert.Equal(_verificationTime, status.Installation.LastVerifiedAtUtc);
        Assert.Equal(Path.GetFullPath(installRoot), status.Installation.RootPath);
        Assert.Equal(
            definition.Definition.Package.Sha256,
            status.Installation.ExecutableSha256);
        Assert.Single(status.Installation.ProbeResults);
        Assert.True(status.Installation.ProbeResults[0].Succeeded);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private ManagedToolInstallationValidator CreateValidator(
        Func<string, string, ToolProbeDefinition, CancellationToken,
            Task<ToolProbeResult>> probeExecutor) =>
        new(
            _toolsRoot,
            _documentStore,
            probeExecutor,
            _fileHasher,
            new FixedTimeProvider(_verificationTime));

    private async Task PrepareAsync(
        string installRoot,
        byte[] executableBytes,
        ResolvedToolDefinition localDefinition,
        ManagedToolInstallation installation)
    {
        Directory.CreateDirectory(installRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(installRoot, "Cpp2IL.exe"),
            executableBytes,
            TestContext.Current.CancellationToken);
        await _documentStore.WriteAsync(
            installRoot,
            localDefinition,
            installation,
            TestContext.Current.CancellationToken);
    }

    private static Task<ToolProbeResult> SuccessfulProbe(
        string executablePath,
        string workingDirectory,
        ToolProbeDefinition probe,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.True(File.Exists(executablePath));
        Assert.True(Directory.Exists(workingDirectory));
        return Task.FromResult(SuccessfulProbe(probe));
    }

    private static ToolProbeResult SuccessfulProbe(ToolProbeDefinition probe) =>
        new(
            probe.ProbeId,
            Succeeded: true,
            ExitCode: 0,
            TimedOut: false,
            StandardOutputTruncated: false,
            StandardErrorTruncated: false,
            FailureCode: null,
            FailureMessage: null);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
