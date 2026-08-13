using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Hashing;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Discovery;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Inputs;
using Xunit;

namespace S1Atlas.Extraction.Tests.Inputs;

public sealed class ExtractionInputResolverTests
{
    [Fact]
    public async Task SelectBuildAsync_WithoutBuildId_UsesCurrentBuild()
    {
        using var fixture = InputTestFixture.Create();
        var repository = new FakeRepository { CurrentBuild = fixture.Build };
        var resolver = CreateResolver(repository, []);

        var selected = await resolver.SelectBuildAsync(
            requestedBuildId: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(fixture.Build, selected);
        Assert.Equal(0, repository.SaveSnapshotCallCount);
    }

    [Fact]
    public async Task SelectBuildAsync_WithHistoricalBuildId_ReturnsStoredBuildWithoutPromotion()
    {
        using var current = InputTestFixture.Create();
        using var historical = InputTestFixture.Create();
        var historicalBuild = historical.Build with { BuildId = "historical-build" };
        var repository = new FakeRepository
        {
            CurrentBuild = current.Build,
            Builds = { [historicalBuild.BuildId] = historicalBuild }
        };
        var resolver = CreateResolver(repository, []);

        var selected = await resolver.SelectBuildAsync(
            historicalBuild.BuildId,
            TestContext.Current.CancellationToken);

        Assert.Equal(historicalBuild, selected);
        Assert.Equal(current.Build, repository.CurrentBuild);
        Assert.Equal(0, repository.SaveSnapshotCallCount);
    }

    [Fact]
    public async Task SelectBuildAsync_WhenRequestedBuildIsUnknown_ReportsBuildNotFound()
    {
        var repository = new FakeRepository();
        var resolver = CreateResolver(repository, []);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => resolver.SelectBuildAsync(
                "unknown-build",
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.BuildNotFound, exception.Code);
    }

    [Fact]
    public async Task SelectBuildAsync_WhenNoCurrentSnapshot_ReportsBuildNotFound()
    {
        var repository = new FakeRepository();
        var resolver = CreateResolver(repository, []);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => resolver.SelectBuildAsync(
                requestedBuildId: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.BuildNotFound, exception.Code);
    }

    [Fact]
    public async Task ResolveAsync_ExplicitPathHasPriorityOverEveryFallback()
    {
        using var explicitInput = InputTestFixture.Create();
        using var storedInput = InputTestFixture.Create();
        using var steamInput = InputTestFixture.Create();
        using var archivedInput = InputTestFixture.Create();
        var repository = new FakeRepository();
        repository.Observations.Add(CreateObservation("stored", storedInput, 1));
        repository.Snapshots.Add(await CreateSnapshotAsync("archive", archivedInput));
        var resolver = CreateResolver(repository, [steamInput.RootPath]);

        var result = await resolver.ResolveAsync(
            explicitInput.Build,
            explicitInput.RootPath,
            InputTestFixture.Profile,
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(explicitInput.RootPath), result.GameRoot);
        Assert.Equal(ExtractionInputSource.Live, result.Source);
    }

    [Fact]
    public async Task ResolveAsync_WhenExplicitPathMissing_DoesNotFallBack()
    {
        using var storedInput = InputTestFixture.Create();
        var repository = new FakeRepository();
        repository.Observations.Add(CreateObservation("stored", storedInput, 1));
        var resolver = CreateResolver(repository, []);
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => resolver.ResolveAsync(
                storedInput.Build,
                missing,
                InputTestFixture.Profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.LiveInputNotFound, exception.Code);
    }

    [Fact]
    public async Task ResolveAsync_WhenExplicitPathMismatches_DoesNotFallBack()
    {
        using var explicitInput = InputTestFixture.Create();
        using var storedInput = InputTestFixture.Create();
        File.WriteAllBytes(explicitInput.GameAssemblyPath, [0xff]);
        var repository = new FakeRepository();
        repository.Observations.Add(CreateObservation("stored", storedInput, 1));
        var resolver = CreateResolver(repository, []);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => resolver.ResolveAsync(
                storedInput.Build,
                explicitInput.RootPath,
                InputTestFixture.Profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.BuildInputMismatch, exception.Code);
    }

    [Fact]
    public async Task ResolveAsync_StoredObservationsAreTriedNewestFirst()
    {
        using var older = InputTestFixture.Create();
        using var newer = InputTestFixture.Create();
        var repository = new FakeRepository();
        repository.Observations.Add(CreateObservation("older", older, 1));
        repository.Observations.Add(CreateObservation("newer", newer, 2));
        var resolver = CreateResolver(repository, []);

        var result = await resolver.ResolveAsync(
            newer.Build,
            explicitGamePath: null,
            InputTestFixture.Profile,
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(newer.RootPath), result.GameRoot);
    }

    [Fact]
    public async Task ResolveAsync_SkipsMismatchedStoredObservationAndUsesSteamCandidate()
    {
        using var stored = InputTestFixture.Create();
        using var steam = InputTestFixture.Create();
        File.WriteAllBytes(stored.MetadataPath, [0xff]);
        var repository = new FakeRepository();
        repository.Observations.Add(CreateObservation("stored", stored, 1));
        var resolver = CreateResolver(repository, [steam.RootPath]);

        var result = await resolver.ResolveAsync(
            steam.Build,
            explicitGamePath: null,
            InputTestFixture.Profile,
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(steam.RootPath), result.GameRoot);
    }

    [Fact]
    public async Task ResolveAsync_WhenOnlyArchiveMatches_UsesNewestReplayVerifiedSnapshot()
    {
        using var older = InputTestFixture.Create();
        using var newer = InputTestFixture.Create();
        var repository = new FakeRepository();
        repository.Snapshots.Add(await CreateSnapshotAsync("older", older, 1));
        repository.Snapshots.Add(await CreateSnapshotAsync("newer", newer, 2));
        var resolver = CreateResolver(repository, []);

        var result = await resolver.ResolveAsync(
            newer.Build,
            explicitGamePath: null,
            InputTestFixture.Profile,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExtractionInputSource.ArchivedSnapshot, result.Source);
        Assert.Equal("newer", result.InputSnapshotId);
        Assert.Equal(Path.GetFullPath(newer.RootPath), result.GameRoot);
    }

    [Fact]
    public async Task ResolveAsync_WhenArchiveManifestIsInvalid_RejectsArchive()
    {
        using var archived = InputTestFixture.Create();
        var snapshot = await CreateSnapshotAsync("invalid", archived);
        var repository = new FakeRepository();
        repository.Snapshots.Add(snapshot with { ManifestDigest = new string('0', 64) });
        var resolver = CreateResolver(repository, []);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => resolver.ResolveAsync(
                archived.Build,
                explicitGamePath: null,
                InputTestFixture.Profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.ArchivedInputInvalid, exception.Code);
    }

    [Fact]
    public async Task ResolveAsync_DeduplicatesNormalizedCandidatePathsBeforeHashing()
    {
        using var input = InputTestFixture.Create();
        File.WriteAllBytes(input.GameAssemblyPath, [0xff]);
        var repository = new FakeRepository();
        repository.Observations.Add(CreateObservation("stored", input, 1));
        var hasher = new RecordingHasher();
        var resolver = CreateResolver(
            repository,
            [input.RootPath.ToUpperInvariant()],
            hasher);

        await Assert.ThrowsAsync<ExtractionOperationException>(
            () => resolver.ResolveAsync(
                input.Build,
                explicitGamePath: null,
                InputTestFixture.Profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(1, hasher.Paths.Count(path =>
            path.EndsWith("GameAssembly.dll", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ResolveAsync_WhenNoInputMatches_RequestsConciseRescan()
    {
        using var input = InputTestFixture.Create();
        var resolver = CreateResolver(new FakeRepository(), []);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => resolver.ResolveAsync(
                input.Build,
                explicitGamePath: null,
                InputTestFixture.Profile,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.LiveInputNotFound, exception.Code);
        Assert.Contains("scan", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(input.RootPath, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ExtractionInputResolver CreateResolver(
        FakeRepository repository,
        IReadOnlyList<string> steamCandidates,
        IFileHasher? hasher = null)
    {
        var locator = new WindowsScheduleOneLocator(
            new FakeWindowsCandidateSource(steamCandidates));
        var verifier = new LiveInputVerifier(hasher ?? new Sha256FileHasher());
        return new ExtractionInputResolver(repository, repository, locator, verifier);
    }

    private static InstallationObservationRecord CreateObservation(
        string id,
        InputTestFixture fixture,
        int hour) =>
        new(
            id,
            fixture.Build.BuildId,
            new DateTimeOffset(2026, 8, 12, hour, 0, 0, TimeSpan.Zero),
            fixture.RootPath,
            fixture.GameAssemblyPath,
            fixture.MetadataPath);

    private static async Task<InputSnapshot> CreateSnapshotAsync(
        string id,
        InputTestFixture fixture,
        int hour = 1)
    {
        var verifier = new LiveInputVerifier(new Sha256FileHasher());
        var manifest = await verifier.CaptureAsync(
            new ResolvedExtractionInput(
                ExtractionInputSource.ArchivedSnapshot,
                fixture.RootPath,
                fixture.GameAssemblyPath,
                fixture.MetadataPath,
                fixture.ExecutablePath,
                fixture.UnityVersionPath,
                id),
            fixture.Build,
            InputTestFixture.Profile,
            TestContext.Current.CancellationToken);
        return new InputSnapshot(
            id,
            fixture.Build.BuildId,
            fixture.RootPath,
            InputManifestFingerprint.Create(manifest),
            new DateTimeOffset(2026, 8, 12, hour, 0, 0, TimeSpan.Zero),
            ReplayVerified: true,
            ReplayVerifiedAtUtc: new DateTimeOffset(2026, 8, 12, hour, 1, 0, TimeSpan.Zero),
            manifest);
    }

    private sealed class FakeWindowsCandidateSource(IReadOnlyList<string> candidates)
        : IWindowsScheduleOneCandidateSource
    {
        public IReadOnlyList<string> GetCandidatePaths() => candidates;
    }

    private sealed class RecordingHasher : IFileHasher
    {
        private readonly Sha256FileHasher _inner = new();

        public List<string> Paths { get; } = [];

        public Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return _inner.ComputeSha256Async(path, cancellationToken);
        }
    }

    private sealed class FakeRepository : IAtlasRepository, IExtractionRepository
    {
        public GameBuild? CurrentBuild { get; set; }
        public Dictionary<string, GameBuild> Builds { get; } = [];
        public List<InstallationObservationRecord> Observations { get; } = [];
        public List<InputSnapshot> Snapshots { get; } = [];
        public int SaveSnapshotCallCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SaveSnapshotAsync(
            EnvironmentSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            SaveSnapshotCallCount++;
            CurrentBuild = snapshot.Build;
            return Task.CompletedTask;
        }

        public Task<EnvironmentSnapshot?> GetCurrentSnapshotAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(CurrentBuild is null
                ? null
                : new EnvironmentSnapshot(
                    2,
                    CurrentBuild,
                    InstallationObservation.Unknown,
                    [],
                    "test",
                    CurrentBuild.FirstSeenAtUtc));

        public Task<IReadOnlyList<GameBuild>> ListBuildsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GameBuild>>(Builds.Values.ToArray());

        public Task<GameBuild?> GetBuildAsync(
            string buildId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Builds.GetValueOrDefault(buildId));

        public Task<IReadOnlyList<InstallationObservationRecord>>
            ListInstallationObservationsAsync(
                string buildId,
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstallationObservationRecord>>(
                Observations.Where(item => item.BuildId == buildId).ToArray());

        public Task<IReadOnlyList<InputSnapshot>> ListReplayVerifiedInputSnapshotsAsync(
            string buildId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InputSnapshot>>(
                Snapshots.Where(item => item.BuildId == buildId && item.ReplayVerified).ToArray());

        public Task CreateAttemptAsync(
            ExtractionAttempt attempt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task TransitionAttemptAsync(
            ExtractionAttempt attempt,
            ExtractionAttemptStatus expectedStatus,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExtractionAttempt?> GetAttemptAsync(
            string attemptId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ExtractionAttempt>> ListNonTerminalAttemptsAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveInputSnapshotAsync(
            InputSnapshot snapshot,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InputSnapshot?> GetInputSnapshotAsync(
            string inputSnapshotId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Snapshots.FirstOrDefault(item =>
                string.Equals(
                    item.InputSnapshotId,
                    inputSnapshotId,
                    StringComparison.Ordinal)));

        public Task MarkInputSnapshotReplayVerifiedAsync(
            string inputSnapshotId,
            string expectedBuildId,
            string expectedManifestDigest,
            DateTimeOffset verifiedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
