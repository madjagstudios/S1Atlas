using System.Security.Cryptography;
using System.Text.Json;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Hashing;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Inputs;
using Xunit;

namespace S1Atlas.Extraction.Tests.Inputs;

public sealed class InputSnapshotServiceTests : IDisposable
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 12, 18, 30, 0, TimeSpan.Zero);

    private readonly InputTestFixture _input = InputTestFixture.Create();
    private readonly string _atlasRoot = Path.Combine(
        Path.GetTempPath(),
        "s1atlas-input-snapshot-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateAsync_CopiesOnlyRequiredInputAndWritesCanonicalDocuments()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new RecordingRepository();
        var service = CreateService(repository: repository);

        var snapshot = await service.CreateAsync(
            ResolvedInput(),
            _input.Build,
            InputTestFixture.Profile,
            cancellationToken);

        Assert.Equal(
            "82158212b73e772d30fe19b31850b6a93588f9764e2252751fb7fd618c37e5f2",
            snapshot.InputSnapshotId);
        Assert.Equal("test-build", snapshot.BuildId);
        Assert.Equal(
            "a1bbd1fa0a4f01110a19d0f5958daa92053600e0a017a6a9fe456619d7e56d27",
            snapshot.ManifestDigest);
        Assert.Equal(CreatedAt, snapshot.CreatedAtUtc);
        Assert.False(snapshot.ReplayVerified);
        Assert.Null(snapshot.ReplayVerifiedAtUtc);
        Assert.Equal(Path.Combine(InputsRoot, snapshot.InputSnapshotId), snapshot.RootPath);
        Assert.Equal(4, snapshot.Manifest.Entries.Count);

        var expected = new Dictionary<string, (string Role, string Source)>(
            StringComparer.Ordinal)
        {
            ["GameAssembly.dll"] = ("gameAssembly", _input.GameAssemblyPath),
            ["Schedule I.exe"] = ("executableSupport", _input.ExecutablePath),
            ["Schedule I_Data/globalgamemanagers"] =
                ("unityVersionSource", _input.UnityVersionPath),
            ["Schedule I_Data/il2cpp_data/Metadata/global-metadata.dat"] =
                ("globalMetadata", _input.MetadataPath)
        };

        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal),
            snapshot.Manifest.Entries.Select(file => file.RelativePath));
        foreach (var file in snapshot.Manifest.Entries)
        {
            var expectedFile = expected[file.RelativePath];
            var destination = Path.Combine(
                snapshot.RootPath,
                "game-root",
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(expectedFile.Role, file.Role);
            Assert.Equal(new FileInfo(expectedFile.Source).Length, file.Size);
            Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
            Assert.Equal(await File.ReadAllBytesAsync(expectedFile.Source, cancellationToken),
                await File.ReadAllBytesAsync(destination, cancellationToken));
            Assert.Equal(
                Hash(await File.ReadAllBytesAsync(expectedFile.Source, cancellationToken)),
                file.Sha256);
        }

        Assert.Equal(snapshot.ManifestDigest,
            InputManifestFingerprint.Create(snapshot.Manifest));
        Assert.Same(snapshot, Assert.Single(repository.Saved));
        Assert.Empty(Directory.EnumerateFileSystemEntries(StagingRoot));

        using var manifestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(snapshot.RootPath, "input-manifest.json"), cancellationToken));
        Assert.Equal(1, manifestDocument.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("test-build", manifestDocument.RootElement.GetProperty("buildId").GetString());
        Assert.Equal(snapshot.ManifestDigest,
            manifestDocument.RootElement.GetProperty("manifestDigest").GetString());
        Assert.Equal(CreatedAt,
            manifestDocument.RootElement.GetProperty("createdAtUtc").GetDateTimeOffset());
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal),
            manifestDocument.RootElement.GetProperty("files")
                .EnumerateArray()
                .Select(file => file.GetProperty("relativePath").GetString()));

        using var markerDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(snapshot.RootPath, "complete.marker"), cancellationToken));
        Assert.Equal(1, markerDocument.RootElement.GetProperty("markerSchemaVersion").GetInt32());
        Assert.Equal(snapshot.InputSnapshotId,
            markerDocument.RootElement.GetProperty("inputSnapshotId").GetString());
        Assert.Equal(snapshot.ManifestDigest,
            markerDocument.RootElement.GetProperty("manifestDigest").GetString());

        var actualFiles = Directory.EnumerateFiles(
                snapshot.RootPath,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(snapshot.RootPath, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
        [
            "complete.marker",
            "game-root/GameAssembly.dll",
            "game-root/Schedule I.exe",
            "game-root/Schedule I_Data/globalgamemanagers",
            "game-root/Schedule I_Data/il2cpp_data/Metadata/global-metadata.dat",
            "input-manifest.json"
        ], actualFiles);
    }

    [Fact]
    public async Task CreateAsync_IdenticalCertifiedSnapshot_PreservesCertification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new RecordingRepository();
        var service = CreateService(repository: repository);

        var first = await service.CreateAsync(
            ResolvedInput(),
            _input.Build,
            InputTestFixture.Profile,
            cancellationToken);
        Assert.False(first.ReplayVerified);

        // Simulate the snapshot having been replay-certified in the database. Recreating
        // the identical snapshot bytes must return the persisted certified record, never
        // the freshly built unverified candidate.
        var certifiedAt = new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero);
        repository.CertifiedOverride = first with
        {
            ReplayVerified = true,
            ReplayVerifiedAtUtc = certifiedAt
        };

        var second = await service.CreateAsync(
            ResolvedInput(),
            _input.Build,
            InputTestFixture.Profile,
            cancellationToken);

        Assert.Equal(first.InputSnapshotId, second.InputSnapshotId);
        Assert.True(second.ReplayVerified);
        Assert.Equal(certifiedAt, second.ReplayVerifiedAtUtc);
    }

    [Fact]
    public async Task CreateAsync_UsesFirstExistingUnitySourceInProfileOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        File.Delete(_input.UnityVersionPath);
        var fallback = Path.Combine(_input.RootPath, "Schedule I_Data", "data.unity3d");
        await File.WriteAllBytesAsync(fallback, [0xb0, 0xc0], cancellationToken);
        var input = ResolvedInput() with { UnityVersionSourcePath = fallback };

        var snapshot = await CreateService().CreateAsync(
            input,
            _input.Build,
            InputTestFixture.Profile,
            cancellationToken);

        var unity = Assert.Single(snapshot.Manifest.Entries,
            entry => entry.Role == "unityVersionSource");
        Assert.Equal("Schedule I_Data/data.unity3d", unity.RelativePath);
        Assert.False(File.Exists(Path.Combine(
            snapshot.RootPath, "game-root", "Schedule I_Data", "globalgamemanagers")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateAsync_WhenSupportInputIsMissing_RejectsAndCleansOwnedStaging(
        bool executableMissing)
    {
        if (executableMissing)
        {
            File.Delete(_input.ExecutablePath);
        }
        else
        {
            File.Delete(_input.UnityVersionPath);
        }

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            CreateService().CreateAsync(
                ResolvedInput(),
                _input.Build,
                InputTestFixture.Profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureStage.InputSnapshotCreation, exception.Stage);
        Assert.Equal(ExtractionFailureCode.LiveInputNotFound, exception.Code);
        AssertStagingEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenSourceIsReparsePoint_RejectsWithoutFollowingIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = Path.Combine(_input.RootPath, "outside-support.bin");
        await File.WriteAllBytesAsync(target, [0x01, 0x02], cancellationToken);
        File.Delete(_input.ExecutablePath);
        if (!TryCreateFileLink(_input.ExecutablePath, target))
        {
            return;
        }

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            CreateService().CreateAsync(
                ResolvedInput(),
                _input.Build,
                InputTestFixture.Profile,
                cancellationToken));

        Assert.Equal(ExtractionFailureCode.LiveInputNotFound, exception.Code);
        AssertStagingEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenDestinationRootIsReparsePoint_RejectsWithoutWritingTarget()
    {
        Directory.CreateDirectory(InputsRoot);
        var target = Path.Combine(_atlasRoot, "unowned-target");
        Directory.CreateDirectory(target);
        if (!TryCreateDirectoryLink(StagingRoot, target))
        {
            return;
        }

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            CreateService().CreateAsync(
                ResolvedInput(),
                _input.Build,
                InputTestFixture.Profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.FilesystemPromotionFailed, exception.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public async Task CreateAsync_WhenProfilePathTraversesOutsideRoot_RejectsBeforeCopy()
    {
        var profile = InputTestFixture.Profile with
        {
            SnapshotInputs =
            [
                .. InputTestFixture.Profile.SnapshotInputs,
                new("../outside.bin", "unowned")
            ]
        };

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            CreateService().CreateAsync(
                ResolvedInput(),
                _input.Build,
                profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.BuildInputMismatch, exception.Code);
        AssertStagingEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenProfilePathsCollideIgnoringCase_RejectsBeforeCopy()
    {
        var profile = InputTestFixture.Profile with
        {
            SnapshotInputs =
            [
                .. InputTestFixture.Profile.SnapshotInputs,
                new("gameassembly.dll", "collision")
            ]
        };

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            CreateService().CreateAsync(
                ResolvedInput(),
                _input.Build,
                profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.BuildInputMismatch, exception.Code);
        AssertStagingEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenSourceChangesDuringCopy_RejectsTripleHashMismatch()
    {
        var hasher = new MutateAfterFirstHashHasher(_input.GameAssemblyPath);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            CreateService(hasher: hasher).CreateAsync(
                ResolvedInput(),
                _input.Build,
                InputTestFixture.Profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.BuildInputMismatch, exception.Code);
        AssertStagingEmpty();
        Assert.False(Directory.Exists(InputsRoot) && Directory.EnumerateDirectories(InputsRoot)
            .Any(path => !PathSafety.PathsEqual(path, StagingRoot)));
    }

    [Fact]
    public async Task CreateAsync_WhenDestinationHashDiffers_RejectsIntegrityMismatch()
    {
        var hasher = new DestinationMismatchHasher(StagingRoot);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            CreateService(hasher: hasher).CreateAsync(
                ResolvedInput(),
                _input.Build,
                InputTestFixture.Profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.IntegrityMismatch, exception.Code);
        AssertStagingEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task CreateAsync_WhenCanceledAfterCopiedFile_RemovesOnlyOwnedStaging(
        int copiedFileCount)
    {
        Directory.CreateDirectory(StagingRoot);
        var unowned = Path.Combine(StagingRoot, "keep-this-directory");
        Directory.CreateDirectory(unowned);
        using var cancellation = new CancellationTokenSource();
        var hasher = new CancelAfterCopiedFileHasher(cancellation, copiedFileCount);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(hasher: hasher).CreateAsync(
                ResolvedInput(),
                _input.Build,
                InputTestFixture.Profile,
                cancellation.Token));

        Assert.Equal([unowned], Directory.GetDirectories(StagingRoot));
        Assert.False(Directory.EnumerateFiles(StagingRoot).Any());
    }

    [Fact]
    public async Task CreateAsync_WhenDatabaseSaveFails_LeavesFinalSnapshotForRetry()
    {
        var repository = new RecordingRepository { Failure = new IOException("database unavailable") };
        var service = CreateService(repository: repository);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            service.CreateAsync(
                ResolvedInput(),
                _input.Build,
                InputTestFixture.Profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureStage.DatabasePromotion, exception.Stage);
        Assert.Equal(ExtractionFailureCode.DatabasePromotionFailed, exception.Code);
        var finalRoot = Assert.Single(Directory.GetDirectories(InputsRoot),
            path => !PathSafety.PathsEqual(path, StagingRoot));
        Assert.True(File.Exists(Path.Combine(finalRoot, "complete.marker")));
        AssertStagingEmpty();

        repository.Failure = null;
        var recovered = await service.CreateAsync(
            ResolvedInput(),
            _input.Build,
            InputTestFixture.Profile,
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFileName(finalRoot), recovered.InputSnapshotId);
        Assert.Equal(finalRoot, recovered.RootPath);
        Assert.False(recovered.ReplayVerified);
        Assert.Same(recovered, Assert.Single(repository.Saved));
        AssertStagingEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenIdenticalSnapshotExists_IsIdempotent()
    {
        var repository = new RecordingRepository();
        var service = CreateService(repository: repository);

        var first = await service.CreateAsync(
            ResolvedInput(), _input.Build, InputTestFixture.Profile,
            TestContext.Current.CancellationToken);
        var second = await service.CreateAsync(
            ResolvedInput(), _input.Build, InputTestFixture.Profile,
            TestContext.Current.CancellationToken);

        Assert.Equal(first.InputSnapshotId, second.InputSnapshotId);
        Assert.Equal(first.RootPath, second.RootPath);
        Assert.Equal(first.CreatedAtUtc, second.CreatedAtUtc);
        Assert.False(first.ReplayVerified);
        Assert.False(second.ReplayVerified);
        Assert.Equal(2, repository.Saved.Count);
        AssertStagingEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAsync_WhenExistingSnapshotConflicts_FailsClosed(
        bool unknownManifestProperty)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = CreateService();
        var first = await service.CreateAsync(
            ResolvedInput(), _input.Build, InputTestFixture.Profile,
            cancellationToken);
        if (unknownManifestProperty)
        {
            var manifestPath = Path.Combine(first.RootPath, "input-manifest.json");
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            await File.WriteAllTextAsync(manifestPath,
                json.Replace("{", "{\"unknown\":true,", StringComparison.Ordinal),
                cancellationToken);
        }
        else
        {
            await File.WriteAllBytesAsync(
                Path.Combine(first.RootPath, "game-root", "Schedule I.exe"),
                [0xff],
                cancellationToken);
        }

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            service.CreateAsync(
                ResolvedInput(), _input.Build, InputTestFixture.Profile,
                cancellationToken));

        Assert.Equal(ExtractionFailureCode.ArchivedInputInvalid, exception.Code);
        Assert.True(Directory.Exists(first.RootPath));
        AssertStagingEmpty();
    }

    private string InputsRoot => Path.Combine(_atlasRoot, "builds", _input.Build.BuildId, "inputs");

    private string StagingRoot => Path.Combine(InputsRoot, ".staging");

    private InputSnapshotService CreateService(
        IFileHasher? hasher = null,
        RecordingRepository? repository = null) =>
        new(
            InputsRoot,
            StagingRoot,
            hasher ?? new Sha256FileHasher(),
            new FixedTimeProvider(CreatedAt),
            repository ?? new RecordingRepository());

    private ResolvedExtractionInput ResolvedInput() =>
        new(
            ExtractionInputSource.Live,
            _input.RootPath,
            _input.GameAssemblyPath,
            _input.MetadataPath,
            _input.ExecutablePath,
            _input.UnityVersionPath,
            InputSnapshotId: null);

    private void AssertStagingEmpty() =>
        Assert.False(Directory.Exists(StagingRoot) &&
            Directory.EnumerateFileSystemEntries(StagingRoot).Any());

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _input.Dispose();
        if (Directory.Exists(_atlasRoot))
        {
            Directory.Delete(_atlasRoot, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingRepository : IExtractionRepository
    {
        public List<InputSnapshot> Saved { get; } = [];

        public Exception? Failure { get; set; }

        /// <summary>
        /// When set, <see cref="GetInputSnapshotAsync"/> returns this record for a
        /// matching ID, simulating a database row that was already replay-certified
        /// before the identical snapshot was recreated.
        /// </summary>
        public InputSnapshot? CertifiedOverride { get; set; }

        public Task SaveInputSnapshotAsync(
            InputSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            Saved.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task<GameBuild?> GetBuildAsync(string buildId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<InstallationObservationRecord>> ListInstallationObservationsAsync(
            string buildId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateAttemptAsync(ExtractionAttempt attempt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task TransitionAttemptAsync(
            ExtractionAttempt attempt,
            ExtractionAttemptStatus expectedStatus,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ExtractionAttempt?> GetAttemptAsync(
            string attemptId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ExtractionAttempt>> ListNonTerminalAttemptsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<InputSnapshot>> ListReplayVerifiedInputSnapshotsAsync(
            string buildId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InputSnapshot?> GetInputSnapshotAsync(
            string inputSnapshotId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CertifiedOverride is { } certified &&
                string.Equals(
                    certified.InputSnapshotId,
                    inputSnapshotId,
                    StringComparison.Ordinal))
            {
                return Task.FromResult<InputSnapshot?>(certified);
            }

            var snapshot = Saved.LastOrDefault(candidate =>
                string.Equals(
                    candidate.InputSnapshotId,
                    inputSnapshotId,
                    StringComparison.Ordinal));
            return Task.FromResult(snapshot);
        }

        public Task MarkInputSnapshotReplayVerifiedAsync(
            string inputSnapshotId,
            string expectedBuildId,
            string expectedManifestDigest,
            DateTimeOffset verifiedAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MutateAfterFirstHashHasher(string target) : IFileHasher
    {
        private readonly Sha256FileHasher _inner = new();
        private bool _mutated;

        public async Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken)
        {
            var hash = await _inner.ComputeSha256Async(path, cancellationToken);
            if (!_mutated && PathSafety.PathsEqual(path, target))
            {
                _mutated = true;
                await File.WriteAllBytesAsync(target, [0xde, 0xad, 0xbe, 0xef, 0x01],
                    cancellationToken);
            }

            return hash;
        }
    }

    private sealed class DestinationMismatchHasher(string stagingRoot) : IFileHasher
    {
        private readonly Sha256FileHasher _inner = new();

        public async Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken)
        {
            var hash = await _inner.ComputeSha256Async(path, cancellationToken);
            return Path.GetFullPath(path).StartsWith(
                    Path.GetFullPath(stagingRoot) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                ? new string(hash[0] == '0' ? '1' : '0', 64)
                : hash;
        }
    }

    private sealed class CancelAfterCopiedFileHasher(
        CancellationTokenSource cancellation,
        int copiedFileCount) : IFileHasher
    {
        private readonly Sha256FileHasher _inner = new();
        private int _callCount;

        public async Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken)
        {
            var hash = await _inner.ComputeSha256Async(path, cancellationToken);
            _callCount++;
            if (_callCount == copiedFileCount * 3)
            {
                cancellation.Cancel();
            }

            return hash;
        }
    }
}
