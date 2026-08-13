using System.Security.Cryptography;
using S1Atlas.Core.Hashing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Tools;
using Xunit;

namespace S1Atlas.Extraction.Tests.Tools;

public sealed class ExtractionToolResolverTests : IAsyncDisposable
{
    private readonly string _temporaryDirectory =
        ToolTestFixture.CreateTemporaryDirectory();
    private readonly ToolInstallationDocumentStore _documentStore = new();
    private readonly DateTimeOffset _now =
        DateTimeOffset.Parse("2026-08-13T06:00:00Z");

    private string ToolsRoot => Path.Combine(_temporaryDirectory, "atlas-tools");

    [Fact]
    public async Task ResolveAsync_WithoutOverride_FreshlyVerifiesAndPersistsManagedCpp2Il()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var definition = CreateDefinition(bytes);
        var executablePath = await PrepareManagedInstallationAsync(definition, bytes);
        var provider = new RecordingDefinitionProvider(definition);
        var hasher = new RecordingHasher();
        var probes = new RecordingProbeExecutor();
        var repository = new RecordingRepository();
        var resolver = CreateResolver(provider, hasher, probes, repository);

        var resolved = await resolver.ResolveAsync(
            customExecutablePath: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(("cpp2il", "win-x64"), Assert.Single(provider.Requests));
        Assert.Equal(definition, resolved.Definition);
        Assert.Equal(Path.GetFullPath(executablePath), resolved.ExecutablePath);
        Assert.Equal(definition.Definition.Package.Sha256, resolved.Instance.ExecutableSha256);
        Assert.Equal(ToolTrustLevel.ManagedPinned, resolved.Instance.TrustLevel);
        Assert.Equal(definition.DefinitionDigest, resolved.Instance.DefinitionDigest);
        Assert.Equal(definition.Definition.Package.Sha256, resolved.Instance.PackageSha256);
        Assert.Equal(executablePath, Assert.Single(hasher.Paths));
        Assert.Equal(executablePath, Assert.Single(probes.Calls).ExecutablePath);
        Assert.Single(resolved.ProbeResults);
        Assert.True(resolved.ProbeResults[0].Succeeded);
        Assert.Equal(resolved.Instance, Assert.Single(repository.InstanceSaves));
        Assert.Empty(repository.ManagedSaves);
    }

    [Fact]
    public async Task ResolveAsync_WithoutInstalledManagedTool_ReturnsInstallInstructions()
    {
        var definition = CreateDefinition([1, 2, 3, 4]);
        var resolver = CreateResolver(
            new RecordingDefinitionProvider(definition),
            new RecordingHasher(),
            new RecordingProbeExecutor(),
            new RecordingRepository());

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            resolver.ResolveAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal("ToolNotInstalled", exception.Code);
        Assert.Contains(
            "s1atlas tools install cpp2il",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ManagedFailure.Incomplete, "ToolNotInstalled")]
    [InlineData(ManagedFailure.DefinitionMismatch, "ToolDefinitionInvalid")]
    [InlineData(ManagedFailure.Corrupt, "ToolChecksumMismatch")]
    [InlineData(ManagedFailure.ProbeFailed, "ToolProbeFailed")]
    public async Task ResolveAsync_WhenManagedVerificationFails_MapsStableResolutionCode(
        ManagedFailure failure,
        string expectedCode)
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var definition = CreateDefinition(bytes);
        var probes = new RecordingProbeExecutor
        {
            Succeed = failure != ManagedFailure.ProbeFailed
        };
        var executablePath = await PrepareManagedFailureAsync(
            definition,
            bytes,
            failure);
        var repository = new RecordingRepository();
        var resolver = CreateResolver(
            new RecordingDefinitionProvider(definition),
            new RecordingHasher(),
            probes,
            repository);

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            resolver.ResolveAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Empty(repository.InstanceSaves);
        if (failure == ManagedFailure.Corrupt)
        {
            Assert.Empty(probes.Calls);
            Assert.True(File.Exists(executablePath));
        }
    }

    [Fact]
    public async Task ResolveAsync_WhenRegisteredManagedExecutableChanges_RejectsBeforeProbeOrPersistence()
    {
        var original = new byte[] { 1, 2, 3, 4 };
        var definition = CreateDefinition(original);
        var executablePath = await PrepareManagedInstallationAsync(
            definition,
            original);
        await File.WriteAllBytesAsync(
            executablePath,
            [9, 8, 7, 6],
            TestContext.Current.CancellationToken);
        var probes = new RecordingProbeExecutor();
        var repository = new RecordingRepository();
        var resolver = CreateResolver(
            new RecordingDefinitionProvider(definition),
            new RecordingHasher(),
            probes,
            repository);

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            resolver.ResolveAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal("ToolChecksumMismatch", exception.Code);
        Assert.Empty(probes.Calls);
        Assert.Empty(repository.InstanceSaves);
    }

    [Fact]
    public async Task ResolveAsync_WithRegularCustomExecutable_HashesProbesAndPersistsOverride()
    {
        var bytes = new byte[] { 4, 3, 2, 1 };
        var definition = CreateDefinition([1, 2, 3, 4]);
        var customDirectory = Path.Combine(_temporaryDirectory, "custom");
        Directory.CreateDirectory(customDirectory);
        var customPath = Path.Combine(customDirectory, "custom-cpp2il.exe");
        await File.WriteAllBytesAsync(
            customPath,
            bytes,
            TestContext.Current.CancellationToken);
        var repository = new RecordingRepository();
        var hasher = new RecordingHasher();
        var probes = new RecordingProbeExecutor();
        var resolver = CreateResolver(
            new RecordingDefinitionProvider(definition),
            hasher,
            probes,
            repository);

        var resolved = await resolver.ResolveAsync(
            customPath,
            TestContext.Current.CancellationToken);

        var expectedSha256 = Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();
        Assert.Equal(ToolTrustLevel.CustomOverride, resolved.Instance.TrustLevel);
        Assert.Null(resolved.Instance.DefinitionDigest);
        Assert.Null(resolved.Instance.PackageSha256);
        Assert.Null(resolved.Instance.VersionLabel);
        Assert.Equal(expectedSha256, resolved.Instance.ExecutableSha256);
        Assert.Equal(Path.GetFullPath(customPath), resolved.ExecutablePath);
        Assert.Equal(Path.GetFullPath(customPath), resolved.Instance.ObservedPath);
        Assert.Equal(customDirectory, Assert.Single(probes.Calls).WorkingDirectory);
        Assert.Equal(resolved.Instance, Assert.Single(repository.InstanceSaves));
        Assert.Empty(repository.ManagedSaves);
    }

    [Theory]
    [InlineData(CustomPathFailure.Missing)]
    [InlineData(CustomPathFailure.Directory)]
    [InlineData(CustomPathFailure.ManagedRoot)]
    public async Task ResolveAsync_WithInvalidCustomPath_RejectsBeforeHashOrProbe(
        CustomPathFailure failure)
    {
        var definition = CreateDefinition([1, 2, 3, 4]);
        var customPath = failure switch
        {
            CustomPathFailure.Missing => Path.Combine(
                _temporaryDirectory,
                "missing.exe"),
            CustomPathFailure.Directory => Directory.CreateDirectory(
                Path.Combine(_temporaryDirectory, "directory.exe")).FullName,
            CustomPathFailure.ManagedRoot => await CreateFileAsync(
                Path.Combine(ToolsRoot, "cpp2il", "other", "Cpp2IL.exe"),
                [1]),
            _ => throw new InvalidOperationException()
        };
        var hasher = new RecordingHasher();
        var probes = new RecordingProbeExecutor();
        var resolver = CreateResolver(
            new RecordingDefinitionProvider(definition),
            hasher,
            probes,
            new RecordingRepository());

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            resolver.ResolveAsync(customPath, TestContext.Current.CancellationToken));

        Assert.Equal("CustomToolPathInvalid", exception.Code);
        Assert.Empty(hasher.Paths);
        Assert.Empty(probes.Calls);
    }

    [Fact]
    public async Task ResolveAsync_WithReparsePointExecutable_RejectsBeforeHash()
    {
        var definition = CreateDefinition([1, 2, 3, 4]);
        var customPath = await CreateFileAsync(
            Path.Combine(_temporaryDirectory, "custom-link.exe"),
            [1, 2, 3]);
        var hasher = new RecordingHasher();
        var resolver = CreateResolver(
            new RecordingDefinitionProvider(definition),
            hasher,
            new RecordingProbeExecutor(),
            new RecordingRepository(),
            path => SamePath(path, customPath)
                ? File.GetAttributes(path) | FileAttributes.ReparsePoint
                : File.GetAttributes(path));

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            resolver.ResolveAsync(customPath, TestContext.Current.CancellationToken));

        Assert.Equal("CustomToolPathInvalid", exception.Code);
        Assert.Empty(hasher.Paths);
    }

    [Fact]
    public async Task ResolveAsync_WithReparsePointAncestor_RejectsBeforeHash()
    {
        var definition = CreateDefinition([1, 2, 3, 4]);
        var linkDirectory = Path.Combine(_temporaryDirectory, "custom-link");
        var linkedPath = await CreateFileAsync(
            Path.Combine(linkDirectory, "Cpp2IL.exe"),
            [1, 2, 3]);
        var hasher = new RecordingHasher();
        var resolver = CreateResolver(
            new RecordingDefinitionProvider(definition),
            hasher,
            new RecordingProbeExecutor(),
            new RecordingRepository(),
            path => SamePath(path, linkDirectory)
                ? File.GetAttributes(path) | FileAttributes.ReparsePoint
                : File.GetAttributes(path));

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            resolver.ResolveAsync(linkedPath, TestContext.Current.CancellationToken));

        Assert.Equal("CustomToolPathInvalid", exception.Code);
        Assert.Empty(hasher.Paths);
    }

    [Fact]
    public async Task ResolveAsync_WhenCustomHashFails_MapsChecksumFailureWithoutProbing()
    {
        var definition = CreateDefinition([1, 2, 3, 4]);
        var customPath = await CreateFileAsync(
            Path.Combine(_temporaryDirectory, "custom", "Cpp2IL.exe"),
            [4, 3, 2, 1]);
        var hasher = new RecordingHasher
        {
            Exception = new IOException("Injected read failure.")
        };
        var probes = new RecordingProbeExecutor();
        var resolver = CreateResolver(
            new RecordingDefinitionProvider(definition),
            hasher,
            probes,
            new RecordingRepository());

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            resolver.ResolveAsync(customPath, TestContext.Current.CancellationToken));

        Assert.Equal("ToolChecksumMismatch", exception.Code);
        Assert.Empty(probes.Calls);
        Assert.DoesNotContain(" at ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_WhenCustomProbeFails_DoesNotPersistInstance()
    {
        var definition = CreateDefinition([1, 2, 3, 4]);
        var customPath = await CreateFileAsync(
            Path.Combine(_temporaryDirectory, "custom", "Cpp2IL.exe"),
            [4, 3, 2, 1]);
        var repository = new RecordingRepository();
        var resolver = CreateResolver(
            new RecordingDefinitionProvider(definition),
            new RecordingHasher(),
            new RecordingProbeExecutor { Succeed = false },
            repository);

        var exception = await Assert.ThrowsAsync<ToolOperationException>(() =>
            resolver.ResolveAsync(customPath, TestContext.Current.CancellationToken));

        Assert.Equal("ToolProbeFailed", exception.Code);
        Assert.Empty(repository.InstanceSaves);
    }

    [Fact]
    public async Task ResolveAsync_CustomIdentityExcludesPathAndIncludesBytes()
    {
        var definition = CreateDefinition([1, 2, 3, 4]);
        var firstPath = await CreateFileAsync(
            Path.Combine(_temporaryDirectory, "one", "Cpp2IL.exe"),
            [7, 7, 7]);
        var secondPath = await CreateFileAsync(
            Path.Combine(_temporaryDirectory, "two", "renamed.exe"),
            [7, 7, 7]);
        var thirdPath = await CreateFileAsync(
            Path.Combine(_temporaryDirectory, "three", "Cpp2IL.exe"),
            [8, 8, 8]);
        var resolver = CreateResolver(
            new RecordingDefinitionProvider(definition),
            new RecordingHasher(),
            new RecordingProbeExecutor(),
            new RecordingRepository());

        var first = await resolver.ResolveAsync(
            firstPath,
            TestContext.Current.CancellationToken);
        var second = await resolver.ResolveAsync(
            secondPath,
            TestContext.Current.CancellationToken);
        var third = await resolver.ResolveAsync(
            thirdPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(first.Instance.ToolInstanceId, second.Instance.ToolInstanceId);
        Assert.NotEqual(first.Instance.ToolInstanceId, third.Instance.ToolInstanceId);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private ExtractionToolResolver CreateResolver(
        IToolDefinitionProvider definitionProvider,
        IFileHasher fileHasher,
        RecordingProbeExecutor probes,
        IToolRepository repository,
        Func<string, FileAttributes>? getFileAttributes = null)
    {
        var validator = new ManagedToolInstallationValidator(
            ToolsRoot,
            _documentStore,
            probes.RunAsync,
            fileHasher,
            new FixedTimeProvider(_now));
        return new ExtractionToolResolver(
            definitionProvider,
            validator,
            probes.RunAsync,
            fileHasher,
            repository,
            ToolsRoot,
            "win-x64",
            new FixedTimeProvider(_now),
            getFileAttributes);
    }

    private async Task<string> PrepareManagedInstallationAsync(
        ResolvedToolDefinition definition,
        byte[] executableBytes)
    {
        var installRoot = ToolPathPolicy.GetManagedInstallRoot(
            ToolsRoot,
            definition.Definition);
        Directory.CreateDirectory(installRoot);
        var executablePath = Path.Combine(installRoot, "Cpp2IL.exe");
        await File.WriteAllBytesAsync(
            executablePath,
            executableBytes,
            TestContext.Current.CancellationToken);
        await _documentStore.WriteAsync(
            installRoot,
            definition,
            ToolInstallationDocumentStoreTests.CreateInstallation(
                definition,
                installRoot),
            TestContext.Current.CancellationToken);
        return executablePath;
    }

    private async Task<string> PrepareManagedFailureAsync(
        ResolvedToolDefinition definition,
        byte[] executableBytes,
        ManagedFailure failure)
    {
        var installRoot = ToolPathPolicy.GetManagedInstallRoot(
            ToolsRoot,
            definition.Definition);
        if (failure == ManagedFailure.Incomplete)
        {
            Directory.CreateDirectory(installRoot);
            return Path.Combine(installRoot, "Cpp2IL.exe");
        }

        var localDefinition = failure == ManagedFailure.DefinitionMismatch
            ? CreateDefinition(executableBytes, displayName: "Changed Cpp2IL")
            : definition;
        var executablePath = await PrepareManagedInstallationAsync(
            localDefinition,
            executableBytes);
        if (failure == ManagedFailure.Corrupt)
        {
            await File.WriteAllBytesAsync(
                executablePath,
                [9, 9, 9, 9],
                TestContext.Current.CancellationToken);
        }

        return executablePath;
    }

    private static ResolvedToolDefinition CreateDefinition(
        byte[] bytes,
        string displayName = "Cpp2IL")
    {
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var definition = new ToolDefinition(
            1,
            "cpp2il",
            displayName,
            "test-version",
            "win-x64",
            new ToolPackageDefinition(
                ToolPackageKind.SingleFile,
                null,
                new Uri("https://offline.invalid/Cpp2IL.exe"),
                new Uri("https://offline.invalid/releases"),
                "Cpp2IL.exe",
                bytes.Length,
                sha256,
                "Cpp2IL.exe",
                new ToolSafetyLimits(bytes.Length, bytes.Length, 1)),
            new ToolLicenseDefinition(
                "MIT",
                new Uri("https://offline.invalid/LICENSE")),
            [new ToolProbeDefinition(
                "help",
                ["--help"],
                [0],
                TimeSpan.FromSeconds(5),
                [])]);
        return new ResolvedToolDefinition(
            definition,
            ToolDefinitionFingerprint.Create(definition));
    }

    private static async Task<string> CreateFileAsync(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(
            path,
            bytes,
            TestContext.Current.CancellationToken);
        return path;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    public enum ManagedFailure
    {
        Incomplete,
        DefinitionMismatch,
        Corrupt,
        ProbeFailed
    }

    public enum CustomPathFailure
    {
        Missing,
        Directory,
        ManagedRoot
    }

    private sealed class RecordingDefinitionProvider(
        ResolvedToolDefinition definition) : IToolDefinitionProvider
    {
        public List<(string ToolId, string Platform)> Requests { get; } = [];

        public IReadOnlyList<ResolvedToolDefinition> GetAll() => [definition];

        public ResolvedToolDefinition GetRequired(string toolId, string platform)
        {
            Requests.Add((toolId, platform));
            if (!string.Equals(toolId, "cpp2il", StringComparison.Ordinal) ||
                !string.Equals(platform, "win-x64", StringComparison.Ordinal))
            {
                throw new ToolOperationException(
                    "UnknownTool",
                    "The resolver requested the wrong committed tool definition.");
            }

            return definition;
        }
    }

    private sealed class RecordingHasher : IFileHasher
    {
        private readonly Sha256FileHasher _inner = new();

        public List<string> Paths { get; } = [];

        public Exception? Exception { get; init; }

        public Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Exception is null
                ? _inner.ComputeSha256Async(path, cancellationToken)
                : Task.FromException<string>(Exception);
        }
    }

    private sealed class RecordingProbeExecutor
    {
        public List<(string ExecutablePath, string WorkingDirectory)> Calls { get; }
            = [];

        public bool Succeed { get; init; } = true;

        public Task<ToolProbeResult> RunAsync(
            string executablePath,
            string workingDirectory,
            ToolProbeDefinition probe,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((executablePath, workingDirectory));
            return Task.FromResult(new ToolProbeResult(
                probe.ProbeId,
                Succeed,
                Succeed ? 0 : 5,
                TimedOut: false,
                StandardOutputTruncated: false,
                StandardErrorTruncated: false,
                FailureCode: Succeed ? null : "ToolProbeExitCodeRejected",
                FailureMessage: Succeed ? null : "The exit code was rejected."));
        }
    }

    private sealed class RecordingRepository : IToolRepository
    {
        public List<ToolInstance> InstanceSaves { get; } = [];

        public List<(ManagedToolInstallation Installation, ToolInstance Instance)>
            ManagedSaves { get; } = [];

        public Task SaveToolInstanceAsync(
            ToolInstance toolInstance,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstanceSaves.Add(toolInstance);
            return Task.CompletedTask;
        }

        public Task SaveVerifiedManagedToolAsync(
            ManagedToolInstallation installation,
            ToolInstance toolInstance,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManagedSaves.Add((installation, toolInstance));
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
