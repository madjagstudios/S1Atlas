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
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SelectBuildAsync_WithoutBuildId_UsesCurrentBuild()
    {
        using var fixture = InputTestFixture.Create();
        var repository = new FakeRepository { CurrentBuild = fixture.Build };
        var resolver = CreateResolver(repository, []);

        var selected = await resolver.SelectBuildAsync(requestedBuildId: null, Ct);

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

        var selected = await resolver.SelectBuildAsync(historicalBuild.BuildId, Ct);

        Assert.Equal(historicalBuild, selected);
        Assert.Equal(current.Build, repository.CurrentBuild);
        Assert.Equal(0, repository.SaveSnapshotCallCount);
    }

    [Fact]
    public async Task SelectBuildAsync_WhenRequestedBuildIsUnknown_ReportsBuildNotFound()
    {
        var resolver = CreateResolver(new FakeRepository(), []);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => resolver.SelectBuildAsync("unknown-build", Ct));

        Assert.Equal(ExtractionFailureCode.BuildNotFound, exception.Code);
    }

    [Fact]
    public async Task SelectBuildAsync_WhenNoCurrentSnapshot_ReportsBuildNotFound()
    {
        var resolver = CreateResolver(new FakeRepository(), []);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => resolver.SelectBuildAsync(requestedBuildId: null, Ct));

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
        await CreateSnapshotAsync(repository, archivedInput, replayVerified: true);
        var resolver = CreateResolver(repository, [steamInput.RootPath]);

        var result = await resolver.ResolveAsync(
            explicitInput.Build,
            explicitInput.RootPath,
            explicitInputSnapshotId: null,
            InputTestFixture.Profile,
            Ct);

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
                storedInput.Build, missing, null, InputTestFixture.Profile, Ct));

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
                storedInput.Build, explicitInput.RootPath, null, InputTestFixture.Profile, Ct));

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
            newer.Build, null, null, InputTestFixture.Profile, Ct);

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
            steam.Build, null, null, InputTestFixture.Profile, Ct);

        Assert.Equal(Path.GetFullPath(steam.RootPath), result.GameRoot);
    }

    [Fact]
    public async Task ResolveAsync_WhenOnlyArchiveMatches_UsesNewestReplayVerifiedSnapshot()
    {
        using var older = InputTestFixture.Create();
        using var newer = InputTestFixture.Create();
        var repository = new FakeRepository();
        await CreateSnapshotAsync(repository, older, replayVerified: true, hour: 1);
        var newerSnapshot = await CreateSnapshotAsync(
            repository, newer, replayVerified: true, hour: 2);
        var resolver = CreateResolver(repository, []);

        var result = await resolver.ResolveAsync(
            newer.Build, null, null, InputTestFixture.Profile, Ct);

        Assert.Equal(ExtractionInputSource.ArchivedSnapshot, result.Source);
        Assert.Equal(newerSnapshot.InputSnapshotId, result.InputSnapshotId);
        Assert.Equal(
            InputSnapshotDocumentStore.GetGameRoot(newerSnapshot.RootPath),
            result.GameRoot);
    }

    [Fact]
    public async Task ResolveAsync_ArchivedGameAssemblyLivesBelowGameRoot_NotSnapshotRoot()
    {
        using var archived = InputTestFixture.Create();
        var repository = new FakeRepository();
        var snapshot = await CreateSnapshotAsync(repository, archived, replayVerified: true);
        var resolver = CreateResolver(repository, []);

        var result = await resolver.ResolveAsync(
            archived.Build, null, null, InputTestFixture.Profile, Ct);

        var gameRoot = InputSnapshotDocumentStore.GetGameRoot(snapshot.RootPath);
        Assert.Equal(gameRoot, result.GameRoot);
        Assert.Equal(Path.Combine(gameRoot, "GameAssembly.dll"), result.GameAssemblyPath);
        Assert.True(File.Exists(result.GameAssemblyPath));
        // The assembly is never directly below the snapshot document root.
        Assert.False(File.Exists(Path.Combine(snapshot.RootPath, "GameAssembly.dll")));
    }

    [Fact]
    public async Task ResolveAsync_WhenReplayVerifiedArchiveBytesTampered_RejectsArchive()
    {
        using var archived = InputTestFixture.Create();
        var repository = new FakeRepository();
        var snapshot = await CreateSnapshotAsync(repository, archived, replayVerified: true);
        // Tamper the archived bytes under game-root after certification.
        var tampered = Path.Combine(
            InputSnapshotDocumentStore.GetGameRoot(snapshot.RootPath), "GameAssembly.dll");
        await File.WriteAllBytesAsync(tampered, [0x00, 0x00], Ct);
        var resolver = CreateResolver(repository, []);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => resolver.ResolveAsync(archived.Build, null, null, InputTestFixture.Profile, Ct));

        Assert.Equal(ExtractionFailureCode.ArchivedInputInvalid, exception.Code);
    }

    [Fact]
    public async Task ResolveAsync_ImplicitResolution_IgnoresUnverifiedSnapshots()
    {
        using var archived = InputTestFixture.Create();
        var repository = new FakeRepository();
        // Only an unverified snapshot exists and no live input.
        await CreateSnapshotAsync(repository, archived, replayVerified: false);
        var resolver = CreateResolver(repository, []);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => resolver.ResolveAsync(archived.Build, null, null, InputTestFixture.Profile, Ct));

        Assert.Equal(ExtractionFailureCode.LiveInputNotFound, exception.Code);
    }

    [Fact]
    public async Task ResolveAsync_ExplicitSnapshot_UnknownId_ReportsArchivedInvalidWithoutLiveFallback()
    {
        using var steam = InputTestFixture.Create();
        var repository = new FakeRepository();
        // A perfectly good live candidate exists but must never be used.
        var resolver = CreateResolver(repository, [steam.RootPath]);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => resolver.ResolveAsync(
                steam.Build, null, new string('a', 64), InputTestFixture.Profile, Ct));

        Assert.Equal(ExtractionFailureCode.ArchivedInputInvalid, exception.Code);
    }

    [Fact]
    public async Task ResolveAsync_ExplicitSnapshot_DifferentBuild_Rejects()
    {
        using var archived = InputTestFixture.Create();
        var repository = new FakeRepository();
        var snapshot = await CreateSnapshotAsync(repository, archived, replayVerified: false);
        var otherBuild = archived.Build with { BuildId = "other-build" };
        var resolver = CreateResolver(repository, []);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => resolver.ResolveAsync(
                otherBuild, null, snapshot.InputSnapshotId, InputTestFixture.Profile, Ct));

        Assert.Equal(ExtractionFailureCode.ArchivedInputInvalid, exception.Code);
    }

    [Fact]
    public async Task ResolveAsync_ExplicitSnapshot_UnverifiedIntact_IsAllowedForCertification()
    {
        using var archived = InputTestFixture.Create();
        var repository = new FakeRepository();
        var snapshot = await CreateSnapshotAsync(repository, archived, replayVerified: false);
        var resolver = CreateResolver(repository, []);

        var result = await resolver.ResolveAsync(
            archived.Build, null, snapshot.InputSnapshotId, InputTestFixture.Profile, Ct);

        Assert.Equal(ExtractionInputSource.ArchivedSnapshot, result.Source);
        Assert.Equal(snapshot.InputSnapshotId, result.InputSnapshotId);
        Assert.Equal(
            InputSnapshotDocumentStore.GetGameRoot(snapshot.RootPath),
            result.GameRoot);
    }

    [Fact]
    public async Task ResolveAsync_WhenNoInputMatches_RequestsConciseRescan()
    {
        using var input = InputTestFixture.Create();
        var resolver = CreateResolver(new FakeRepository(), []);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(
            () => resolver.ResolveAsync(input.Build, null, null, InputTestFixture.Profile, Ct));

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
        var effectiveHasher = hasher ?? new Sha256FileHasher();
        var verifier = new LiveInputVerifier(effectiveHasher);
        return new ExtractionInputResolver(
            repository, repository, locator, verifier, effectiveHasher);
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

    /// <summary>
    /// Builds a real on-disk input snapshot (manifest, marker, and a contained
    /// <c>game-root</c> tree) backed by <paramref name="repository"/>, then optionally
    /// certifies its stored record replay-verified.
    /// </summary>
    private static async Task<InputSnapshot> CreateSnapshotAsync(
        FakeRepository repository,
        InputTestFixture fixture,
        bool replayVerified,
        int hour = 1)
    {
        var inputsRoot = Path.Combine(
            Path.GetTempPath(),
            "s1atlas-resolver-snapshots",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inputsRoot);
        var service = new InputSnapshotService(
            inputsRoot,
            Path.Combine(inputsRoot, ".staging"),
            new Sha256FileHasher(),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 12, hour, 0, 0, TimeSpan.Zero)),
            repository);
        var input = new ResolvedExtractionInput(
            ExtractionInputSource.Live,
            fixture.RootPath,
            fixture.GameAssemblyPath,
            fixture.MetadataPath,
            fixture.ExecutablePath,
            fixture.UnityVersionPath,
            InputSnapshotId: null);

        var snapshot = await service.CreateAsync(input, fixture.Build, InputTestFixture.Profile, Ct);
        if (replayVerified)
        {
            snapshot = snapshot with
            {
                ReplayVerified = true,
                ReplayVerifiedAtUtc = snapshot.CreatedAtUtc.AddMinutes(1)
            };
            repository.Replace(snapshot);
        }

        return snapshot;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeWindowsCandidateSource(IReadOnlyList<string> candidates)
        : IWindowsScheduleOneCandidateSource
    {
        public IReadOnlyList<string> GetCandidatePaths() => candidates;
    }

    private sealed class FakeRepository : IAtlasRepository, IExtractionRepository
    {
        public GameBuild? CurrentBuild { get; set; }
        public Dictionary<string, GameBuild> Builds { get; } = [];
        public List<InstallationObservationRecord> Observations { get; } = [];
        public List<InputSnapshot> Snapshots { get; } = [];
        public int SaveSnapshotCallCount { get; private set; }

        public void Replace(InputSnapshot snapshot)
        {
            Snapshots.RemoveAll(item => string.Equals(
                item.InputSnapshotId, snapshot.InputSnapshotId, StringComparison.Ordinal));
            Snapshots.Add(snapshot);
        }

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
            CancellationToken cancellationToken)
        {
            Replace(snapshot);
            return Task.CompletedTask;
        }

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
