using System.Security.Cryptography;
using S1Atlas.Core.Hashing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Tools;
using Xunit;

namespace S1Atlas.Extraction.Tests.Tools;

public sealed class ManagedToolServiceTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory =
        ToolTestFixture.CreateTemporaryDirectory();
    private readonly string _toolsRoot;
    private readonly ToolInstallationDocumentStore _documentStore = new();
    private readonly IFileHasher _fileHasher = new Sha256FileHasher();
    private readonly DateTimeOffset _now =
        DateTimeOffset.Parse("2026-08-13T05:00:00Z");

    public ManagedToolServiceTests()
    {
        _toolsRoot = Path.Combine(_temporaryDirectory, "tools");
    }

    [Fact]
    public async Task GetStatusesAsync_WithoutToolId_ReturnsCurrentPlatformDefinitionsInDeterministicOrder()
    {
        var definitions = new[]
        {
            CreateDefinition("zeta", "win-x64", [1]),
            CreateDefinition("alpha", "win-x64", [2]),
            CreateDefinition("ignored", "linux-x64", [3])
        };
        var repository = new RecordingRepository();
        var service = CreateService(
            new StubDefinitionProvider(definitions),
            new StubInstaller(),
            repository);

        var statuses = await service.GetStatusesAsync(
            toolId: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(["alpha", "zeta"], statuses
            .Select(status => status.Definition.Definition.ToolId)
            .ToArray());
        Assert.All(statuses, status =>
            Assert.Equal(ToolInstallationStatus.NotInstalled, status.Status));
        Assert.Empty(repository.Saves);
    }

    [Fact]
    public async Task GetStatusAsync_WhenVerified_UpsertsInstallationAndToolInstance()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var definition = CreateDefinition("cpp2il", "win-x64", bytes);
        var installRoot = ToolPathPolicy.GetManagedInstallRoot(
            _toolsRoot,
            definition.Definition);
        Directory.CreateDirectory(installRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(installRoot, "Cpp2IL.exe"),
            bytes,
            TestContext.Current.CancellationToken);
        var installation = CreateInstallation(definition, installRoot, _now.AddHours(-1));
        await _documentStore.WriteAsync(
            installRoot,
            definition,
            installation,
            TestContext.Current.CancellationToken);
        var repository = new RecordingRepository();
        var service = CreateService(
            new StubDefinitionProvider([definition]),
            new StubInstaller(),
            repository);

        var statuses = await service.GetStatusesAsync(
            "cpp2il",
            TestContext.Current.CancellationToken);

        var status = Assert.Single(statuses);
        Assert.Equal(ToolInstallationStatus.Verified, status.Status);
        var saved = Assert.Single(repository.Saves);
        Assert.Equal(status.Installation, saved.Installation);
        Assert.Equal(ToolTrustLevel.ManagedPinned, saved.ToolInstance.TrustLevel);
        Assert.Equal("cpp2il", saved.ToolInstance.ToolName);
        Assert.Equal(definition.DefinitionDigest, saved.ToolInstance.DefinitionDigest);
        Assert.Equal(Path.Combine(installRoot, "Cpp2IL.exe"), saved.ToolInstance.ObservedPath);
        Assert.Equal(
            ToolInstanceId.Create(
                "cpp2il",
                definition.Definition.Package.Sha256,
                "win-x64",
                ToolTrustLevel.ManagedPinned),
            saved.ToolInstance.ToolInstanceId);
    }

    [Fact]
    public async Task InstallAsync_WhenFilesystemSucceeds_RegistersVerifiedProvenance()
    {
        var definition = CreateDefinition("cpp2il", "win-x64", [1, 2, 3, 4]);
        var installation = CreateInstallation(
            definition,
            ToolPathPolicy.GetManagedInstallRoot(_toolsRoot, definition.Definition),
            _now);
        var installer = new StubInstaller(new ManagedToolInstallOutcome(
            installation,
            WasAlreadyVerified: false,
            Repaired: false,
            QuarantinePath: null));
        var repository = new RecordingRepository();
        var service = CreateService(
            new StubDefinitionProvider([definition]),
            installer,
            repository);

        var result = await service.InstallAsync(
            "cpp2il",
            repair: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, installer.CallCount);
        Assert.Equal(installation, result.Installation);
        Assert.Equal(ToolTrustLevel.ManagedPinned, result.ToolInstance.TrustLevel);
        Assert.Equal(installation.InstalledAtUtc, result.ToolInstance.FirstObservedAtUtc);
        Assert.Single(repository.Saves);
        Assert.Equal(result.ToolInstance, repository.Saves[0].ToolInstance);
    }

    [Fact]
    public async Task InstallAsync_WhenRepositorySaveFails_LeavesVerifiedFilesystemForLaterStatusRecovery()
    {
        var definition = CreateDefinition("cpp2il", "win-x64", [1, 2, 3, 4]);
        var installRoot = ToolPathPolicy.GetManagedInstallRoot(
            _toolsRoot,
            definition.Definition);
        Directory.CreateDirectory(installRoot);
        var executablePath = Path.Combine(installRoot, "Cpp2IL.exe");
        await File.WriteAllBytesAsync(
            executablePath,
            [1, 2, 3, 4],
            TestContext.Current.CancellationToken);
        var installation = CreateInstallation(definition, installRoot, _now);
        var installer = new StubInstaller(new ManagedToolInstallOutcome(
            installation,
            WasAlreadyVerified: false,
            Repaired: false,
            QuarantinePath: null));
        var repository = new RecordingRepository
        {
            SaveException = new IOException("Injected database failure.")
        };
        var service = CreateService(
            new StubDefinitionProvider([definition]),
            installer,
            repository);

        await Assert.ThrowsAsync<IOException>(() => service.InstallAsync(
            "cpp2il",
            repair: false,
            TestContext.Current.CancellationToken));

        Assert.True(File.Exists(executablePath));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(
            executablePath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallAsync_UnknownTool_FailsBeforeHttpOrFilesystemWork()
    {
        var installer = new StubInstaller();
        var service = CreateService(
            new StubDefinitionProvider([]),
            installer,
            new RecordingRepository());

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            service.InstallAsync(
                "unknown",
                repair: true,
                TestContext.Current.CancellationToken));

        Assert.Equal("UnknownTool", exception.Code);
        Assert.Equal(0, installer.CallCount);
        Assert.False(Directory.Exists(_toolsRoot));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private ManagedToolService CreateService(
        IToolDefinitionProvider definitionProvider,
        IToolInstaller installer,
        IToolRepository repository)
    {
        var validator = new ManagedToolInstallationValidator(
            _toolsRoot,
            _documentStore,
            SuccessfulProbe,
            _fileHasher,
            new FixedTimeProvider(_now));
        return new ManagedToolService(
            definitionProvider,
            validator,
            installer,
            repository,
            "win-x64",
            new FixedTimeProvider(_now));
    }

    private static ResolvedToolDefinition CreateDefinition(
        string toolId,
        string platform,
        byte[] bytes)
    {
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var definition = new ToolDefinition(
            1,
            toolId,
            toolId.ToUpperInvariant(),
            "test-version",
            platform,
            new ToolPackageDefinition(
                ToolPackageKind.SingleFile,
                null,
                new Uri($"https://example.test/{toolId}.exe"),
                new Uri($"https://example.test/releases/{toolId}"),
                $"{toolId}.exe",
                bytes.Length,
                sha256,
                "Cpp2IL.exe",
                new ToolSafetyLimits(bytes.Length, bytes.Length, 1)),
            new ToolLicenseDefinition(
                "MIT",
                new Uri("https://example.test/LICENSE")),
            [new ToolProbeDefinition(
                "help",
                ["--help"],
                [0],
                TimeSpan.FromSeconds(10),
                [])]);
        return new ResolvedToolDefinition(
            definition,
            ToolDefinitionFingerprint.Create(definition));
    }

    private static ManagedToolInstallation CreateInstallation(
        ResolvedToolDefinition definition,
        string rootPath,
        DateTimeOffset installedAtUtc) =>
        new(
            1,
            definition.Definition.ToolId,
            definition.Definition.DisplayName,
            definition.Definition.Version,
            definition.Definition.Platform,
            definition.DefinitionDigest,
            definition.Definition.Package.Sha256,
            definition.Definition.Package.Sha256,
            Path.GetFullPath(rootPath),
            ToolInstallationStatus.Verified,
            installedAtUtc,
            installedAtUtc,
            [new ToolProbeResult(
                "help",
                true,
                0,
                false,
                false,
                false,
                null,
                null)],
            null);

    private static Task<ToolProbeResult> SuccessfulProbe(
        string executablePath,
        string workingDirectory,
        ToolProbeDefinition probe,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ToolProbeResult(
            probe.ProbeId,
            true,
            0,
            false,
            false,
            false,
            null,
            null));
    }

    private sealed class StubDefinitionProvider(
        IReadOnlyList<ResolvedToolDefinition> definitions)
        : IToolDefinitionProvider
    {
        public IReadOnlyList<ResolvedToolDefinition> GetAll() => definitions;

        public ResolvedToolDefinition GetRequired(string toolId, string platform) =>
            definitions.FirstOrDefault(definition =>
                string.Equals(
                    definition.Definition.ToolId,
                    toolId,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    definition.Definition.Platform,
                    platform,
                    StringComparison.OrdinalIgnoreCase)) ??
            throw new ToolOperationException(
                "UnknownTool",
                $"Tool '{toolId}' is not defined for platform '{platform}'.");
    }

    private sealed class StubInstaller(
        ManagedToolInstallOutcome? outcome = null) : IToolInstaller
    {
        public int CallCount { get; private set; }

        public Task<ManagedToolInstallOutcome> InstallAsync(
            ResolvedToolDefinition definition,
            bool repair,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(outcome ??
                throw new InvalidOperationException("Installer should not be called."));
        }
    }

    private sealed class RecordingRepository : IToolRepository
    {
        public List<(ManagedToolInstallation Installation, ToolInstance ToolInstance)>
            Saves { get; } = [];

        public Exception? SaveException { get; init; }

        public Task SaveVerifiedManagedToolAsync(
            ManagedToolInstallation installation,
            ToolInstance toolInstance,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SaveException is not null)
            {
                return Task.FromException(SaveException);
            }

            Saves.Add((installation, toolInstance));
            return Task.CompletedTask;
        }

        public Task<ManagedToolInstallation?> GetManagedToolAsync(
            string toolId,
            string version,
            string platform,
            CancellationToken cancellationToken) =>
            Task.FromResult<ManagedToolInstallation?>(null);

        public Task<ToolInstance?> GetToolInstanceAsync(
            string toolInstanceId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ToolInstance?>(null);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
