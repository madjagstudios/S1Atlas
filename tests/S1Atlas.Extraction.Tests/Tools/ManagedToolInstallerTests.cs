using System.Net;
using System.Security.Cryptography;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Tools;
using Xunit;

namespace S1Atlas.Extraction.Tests.Tools;

public sealed class ManagedToolInstallerTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory =
        ToolTestFixture.CreateTemporaryDirectory();
    private readonly string _toolsRoot;
    private readonly string _stagingRoot;
    private readonly string _quarantineRoot;
    private readonly byte[] _packageBytes;
    private readonly ResolvedToolDefinition _definition;
    private readonly List<HttpClient> _httpClients = [];
    private readonly DateTimeOffset _now =
        DateTimeOffset.Parse("2026-08-13T04:00:00Z");

    public ManagedToolInstallerTests()
    {
        _toolsRoot = Path.Combine(_temporaryDirectory, "tools");
        _stagingRoot = Path.Combine(_toolsRoot, ".staging");
        _quarantineRoot = Path.Combine(_toolsRoot, "quarantine");
        _packageBytes = File.ReadAllBytes(GetCommandProcessorPath());
        _definition = CreateDefinition(_packageBytes);
    }

    [Fact]
    public async Task InstallAsync_WhenNotInstalled_DownloadsVerifiesProbesAndPromotes()
    {
        var handler = new RecordingHandler(_packageBytes);
        var installer = CreateInstaller(handler);

        var outcome = await installer.InstallAsync(
            _definition,
            repair: false,
            TestContext.Current.CancellationToken);

        var finalRoot = FinalRoot();
        Assert.Equal(1, handler.RequestCount);
        Assert.False(outcome.WasAlreadyVerified);
        Assert.False(outcome.Repaired);
        Assert.Equal(ToolInstallationStatus.Verified, outcome.Installation.Status);
        Assert.Equal(_definition.DefinitionDigest, outcome.Installation.DefinitionDigest);
        Assert.Equal(_definition.Definition.Package.Sha256, outcome.Installation.PackageSha256);
        Assert.Equal(_definition.Definition.Package.Sha256, outcome.Installation.ExecutableSha256);
        Assert.Equal(finalRoot, outcome.Installation.RootPath);
        Assert.Equal(_packageBytes, await File.ReadAllBytesAsync(
            Path.Combine(finalRoot, "Cpp2IL.exe"),
            TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(finalRoot, "tool-manifest.json")));
        Assert.True(File.Exists(Path.Combine(finalRoot, "installation.json")));
        Assert.False(Directory.Exists(_stagingRoot) &&
            Directory.EnumerateFileSystemEntries(_stagingRoot).Any());
    }

    [Fact]
    public async Task InstallAsync_WhenAlreadyVerified_IsNoOpWithoutHttp()
    {
        var initialHandler = new RecordingHandler(_packageBytes);
        await CreateInstaller(initialHandler).InstallAsync(
            _definition,
            repair: false,
            TestContext.Current.CancellationToken);
        var noRequestHandler = new RecordingHandler(_ =>
            throw new InvalidOperationException("HTTP must not be used for a verified install."));

        var outcome = await CreateInstaller(noRequestHandler).InstallAsync(
            _definition,
            repair: false,
            TestContext.Current.CancellationToken);

        Assert.True(outcome.WasAlreadyVerified);
        Assert.False(outcome.Repaired);
        Assert.Equal(0, noRequestHandler.RequestCount);
    }

    [Fact]
    public async Task InstallAsync_WhenExistingInstallationIsInvalidWithoutRepair_RequiresRepairWithoutHttp()
    {
        Directory.CreateDirectory(FinalRoot());
        await File.WriteAllTextAsync(
            Path.Combine(FinalRoot(), "existing.txt"),
            "preserve",
            TestContext.Current.CancellationToken);
        var handler = new RecordingHandler(_ =>
            throw new InvalidOperationException("HTTP must not be used before repair is approved."));

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            CreateInstaller(handler).InstallAsync(
                _definition,
                repair: false,
                TestContext.Current.CancellationToken));

        Assert.Equal("ToolRepairRequired", exception.Code);
        Assert.Equal(0, handler.RequestCount);
        Assert.True(File.Exists(Path.Combine(FinalRoot(), "existing.txt")));
    }

    [Fact]
    public async Task InstallAsync_WithRepair_StagesBeforeMovingExistingRootAndQuarantinesOldRoot()
    {
        Directory.CreateDirectory(FinalRoot());
        await File.WriteAllTextAsync(
            Path.Combine(FinalRoot(), "existing.txt"),
            "preserve",
            TestContext.Current.CancellationToken);
        var handler = new RecordingHandler(_packageBytes);

        var outcome = await CreateInstaller(handler).InstallAsync(
            _definition,
            repair: true,
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Repaired);
        Assert.False(outcome.WasAlreadyVerified);
        Assert.NotNull(outcome.QuarantinePath);
        Assert.True(File.Exists(Path.Combine(outcome.QuarantinePath, "existing.txt")));
        Assert.True(File.Exists(Path.Combine(FinalRoot(), "Cpp2IL.exe")));
        Assert.False(File.Exists(Path.Combine(FinalRoot(), "existing.txt")));
    }

    [Fact]
    public async Task InstallAsync_WhenRepairDownloadFails_LeavesExistingRootAtOriginalPath()
    {
        Directory.CreateDirectory(FinalRoot());
        var existingPath = Path.Combine(FinalRoot(), "existing.txt");
        await File.WriteAllTextAsync(
            existingPath,
            "preserve",
            TestContext.Current.CancellationToken);
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadGateway));

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            CreateInstaller(handler).InstallAsync(
                _definition,
                repair: true,
                TestContext.Current.CancellationToken));

        Assert.Equal("ToolDownloadFailed", exception.Code);
        Assert.True(File.Exists(existingPath));
        Assert.False(Directory.Exists(_quarantineRoot) &&
            Directory.EnumerateFileSystemEntries(_quarantineRoot).Any());
    }

    [Fact]
    public async Task InstallAsync_WhenPromotionFails_RestoresQuarantinedRootBestEffort()
    {
        Directory.CreateDirectory(FinalRoot());
        var existingPath = Path.Combine(FinalRoot(), "existing.txt");
        await File.WriteAllTextAsync(
            existingPath,
            "preserve",
            TestContext.Current.CancellationToken);
        var handler = new RecordingHandler(_packageBytes);
        var promotionFailed = false;

        void MovePath(string source, string destination)
        {
            if (!promotionFailed &&
                source.StartsWith(_stagingRoot, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(destination, FinalRoot(), StringComparison.OrdinalIgnoreCase))
            {
                promotionFailed = true;
                throw new IOException("Injected promotion failure.");
            }

            MoveExistingPath(source, destination);
        }

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            CreateInstaller(handler, MovePath).InstallAsync(
                _definition,
                repair: true,
                TestContext.Current.CancellationToken));

        Assert.Equal("ToolInstallationFailed", exception.Code);
        Assert.True(promotionFailed);
        Assert.True(File.Exists(existingPath));
        Assert.Equal("preserve", await File.ReadAllTextAsync(
            existingPath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallAsync_WhenCanceled_RemovesOnlyOwnedStagingPath()
    {
        var unownedPath = Path.Combine(_stagingRoot, "keep-this-directory");
        Directory.CreateDirectory(unownedPath);
        var sentinel = Path.Combine(unownedPath, "sentinel.txt");
        await File.WriteAllTextAsync(
            sentinel,
            "preserve",
            TestContext.Current.CancellationToken);
        var handler = new BlockingHandler();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateInstaller(handler).InstallAsync(
                _definition,
                repair: false,
                cancellation.Token));

        Assert.True(File.Exists(sentinel));
        Assert.Equal([unownedPath], Directory.GetDirectories(_stagingRoot));
    }

    public ValueTask DisposeAsync()
    {
        foreach (var client in _httpClients)
        {
            client.Dispose();
        }

        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private ManagedToolInstaller CreateInstaller(
        HttpMessageHandler handler,
        Action<string, string>? movePath = null)
    {
        var httpClient = new HttpClient(handler, disposeHandler: true);
        _httpClients.Add(httpClient);
        var fileHasher = new Sha256FileHasher();
        var documentStore = new ToolInstallationDocumentStore();
        var probeRunner = new ToolProbeRunner();
        var validator = new ManagedToolInstallationValidator(
            _toolsRoot,
            documentStore,
            probeRunner,
            fileHasher,
            new FixedTimeProvider(_now));
        return new ManagedToolInstaller(
            _toolsRoot,
            _stagingRoot,
            _quarantineRoot,
            validator,
            new ToolDownloadClient(httpClient),
            new ToolPackageVerifier(fileHasher),
            new SafeToolPackageInstaller(),
            documentStore,
            probeRunner,
            fileHasher,
            new FixedTimeProvider(_now),
            movePath);
    }

    private string FinalRoot() => ToolPathPolicy.GetManagedInstallRoot(
        _toolsRoot,
        _definition.Definition);

    private static ResolvedToolDefinition CreateDefinition(byte[] bytes)
    {
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var definition = new ToolDefinition(
            SchemaVersion: 1,
            ToolId: "cpp2il",
            DisplayName: "Cpp2IL",
            Version: "test-version",
            Platform: "win-x64",
            Package: new ToolPackageDefinition(
                ToolPackageKind.SingleFile,
                ArchiveFormat: null,
                new Uri("https://example.test/tool.exe"),
                new Uri("https://example.test/releases/tool"),
                AssetName: "tool.exe",
                ExpectedSize: bytes.Length,
                Sha256: sha256,
                ExecutableRelativePath: "Cpp2IL.exe",
                new ToolSafetyLimits(bytes.Length, bytes.Length, 1)),
            License: new ToolLicenseDefinition(
                "MIT",
                new Uri("https://example.test/LICENSE")),
            Probes:
            [
                new ToolProbeDefinition(
                    "help",
                    ["/d", "/c", "echo help"],
                    [0],
                    TimeSpan.FromSeconds(10),
                    ["help"]),
                new ToolProbeDefinition(
                    "output-formats",
                    ["/d", "/c", "echo dll_il_recovery"],
                    [0],
                    TimeSpan.FromSeconds(10),
                    ["dll_il_recovery"])
            ]);
        return new ResolvedToolDefinition(
            definition,
            ToolDefinitionFingerprint.Create(definition));
    }

    private static string GetCommandProcessorPath() =>
        Environment.GetEnvironmentVariable("ComSpec") ??
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");

    private static void MoveExistingPath(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(byte[] bytes)
            : this(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            })
        {
        }

        public RecordingHandler(
            Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            var response = _responseFactory(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking request completed unexpectedly.");
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
