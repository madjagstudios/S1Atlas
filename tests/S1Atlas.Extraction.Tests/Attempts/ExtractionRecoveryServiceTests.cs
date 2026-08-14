using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Extraction.Tests.Attempts;

public sealed class ExtractionRecoveryServiceTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(), $"s1atlas-recovery-{Guid.NewGuid():N}");
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-12T13:00:00Z");

    [Theory]
    [InlineData(ExtractionAttemptStatus.Created)]
    [InlineData(ExtractionAttemptStatus.Preparing)]
    [InlineData(ExtractionAttemptStatus.Running)]
    [InlineData(ExtractionAttemptStatus.Validating)]
    public async Task RecoverAsync_ProvablyDeadNonterminalAttempt_BecomesAbandoned(
        ExtractionAttemptStatus status)
    {
        Directory.CreateDirectory(_dataRoot);
        var paths = CreatePaths();
        var attempt = CreateAttempt(paths, status) with { ProcessId = 303 };
        var repository = new RecordingRepository(attempt);
        var store = new AttemptDocumentStore();
        await store.WriteAsync(
            paths, attempt, AttemptDocumentStoreTests.CreateFacts(),
            TestContext.Current.CancellationToken);
        var service = CreateService(repository, store, alive: _ => false);

        await service.RecoverAsync(TestContext.Current.CancellationToken);

        var abandoned = Assert.Single(repository.Transitions);
        Assert.Equal(ExtractionAttemptStatus.Abandoned, abandoned.Status);
        Assert.Equal(status, repository.ExpectedStatuses.Single());
        Assert.Equal(_now, abandoned.CompletedAtUtc);
        Assert.Equal(ExtractionFailureStage.Recovery, abandoned.FailureStage);
        Assert.Equal(ExtractionFailureCode.InterruptedProcess, abandoned.FailureCode);
        Assert.Equal("The extraction attempt was interrupted because its recorded processes are no longer running.", abandoned.FailureMessage);
    }

    [Theory]
    [InlineData(ExtractionAttemptStatus.ProcessCompleted)]
    [InlineData(ExtractionAttemptStatus.Failed)]
    [InlineData(ExtractionAttemptStatus.Canceled)]
    [InlineData(ExtractionAttemptStatus.Abandoned)]
    [InlineData(ExtractionAttemptStatus.Succeeded)]
    public async Task RecoverAsync_TerminalOrProcessCompletedAttempt_IsNeverMutated(
        ExtractionAttemptStatus status)
    {
        Directory.CreateDirectory(_dataRoot);
        var paths = CreatePaths();
        var repository = new RecordingRepository(CreateAttempt(paths, status));

        await CreateService(repository, new AttemptDocumentStore(), _ => false)
            .RecoverAsync(TestContext.Current.CancellationToken);

        Assert.Empty(repository.Transitions);
    }

    [Fact]
    public async Task RecoverAsync_LiveLockOwner_FailsWithoutMutation()
    {
        Directory.CreateDirectory(_dataRoot);
        var paths = CreatePaths();
        var attempt = CreateAttempt(paths, ExtractionAttemptStatus.Running);
        var repository = new RecordingRepository(attempt);
        var manager = new ExtractionLock(
            _dataRoot, 101, id => id == 101, new FixedTimeProvider(_now));
        var lease = await manager.AcquireAsync(
            attempt.AttemptId, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ExtractionAlreadyActiveException>(() =>
            CreateService(repository, new AttemptDocumentStore(), id => id == 101)
                .RecoverAsync(TestContext.Current.CancellationToken));

        Assert.Empty(repository.Transitions);
        await lease.ReleaseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RecoverAsync_MalformedLock_PreservesEvidenceAndFailsClosed()
    {
        Directory.CreateDirectory(_dataRoot);
        var repository = new RecordingRepository(
            CreateAttempt(CreatePaths(), ExtractionAttemptStatus.Created));
        var path = Path.Combine(_dataRoot, "extraction.lock");
        await File.WriteAllTextAsync(
            path, "{malformed", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateService(repository, new AttemptDocumentStore(), _ => false)
                .RecoverAsync(TestContext.Current.CancellationToken));

        Assert.Empty(repository.Transitions);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RecoverAsync_LiveRecordedChild_PreservesAttemptAndFailsClosed()
    {
        Directory.CreateDirectory(_dataRoot);
        var paths = CreatePaths();
        var attempt = CreateAttempt(paths, ExtractionAttemptStatus.Running) with { ProcessId = 303 };
        var repository = new RecordingRepository(attempt);

        await Assert.ThrowsAsync<ExtractionAlreadyActiveException>(() =>
            CreateService(repository, new AttemptDocumentStore(), id => id == 303)
                .RecoverAsync(TestContext.Current.CancellationToken));

        Assert.Empty(repository.Transitions);
    }

    [Fact]
    public async Task RecoverAsync_MissingDocument_IsRecreatedFromDatabaseAttempt()
    {
        Directory.CreateDirectory(_dataRoot);
        var paths = CreatePaths();
        var attempt = CreateAttempt(paths, ExtractionAttemptStatus.Created);
        var repository = new RecordingRepository(attempt);
        var store = new AttemptDocumentStore();

        await CreateService(repository, store, _ => false)
            .RecoverAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(paths.AttemptDocumentPath));
        Assert.NotNull(await store.TryReadAsync(
            paths, repository.Transitions.Single(), TestContext.Current.CancellationToken));
        var json = await File.ReadAllTextAsync(
            paths.AttemptDocumentPath, TestContext.Current.CancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(
            System.Text.Json.JsonValueKind.Null,
            document.RootElement.GetProperty("arguments").ValueKind);
        Assert.Equal(
            System.Text.Json.JsonValueKind.Null,
            document.RootElement.GetProperty("environmentOverrides").ValueKind);
        Assert.Equal(
            System.Text.Json.JsonValueKind.Null,
            document.RootElement.GetProperty("timeoutSeconds").ValueKind);
    }

    [Fact]
    public async Task RecoverAsync_LaggingDocument_PreservesExecutionFactsAndUsesDatabaseState()
    {
        Directory.CreateDirectory(_dataRoot);
        var paths = CreatePaths();
        var databaseAttempt = CreateAttempt(paths, ExtractionAttemptStatus.Running) with
        {
            ProcessId = 303
        };
        var lagging = databaseAttempt with
        {
            Status = ExtractionAttemptStatus.Created,
            ProcessId = null
        };
        var facts = AttemptDocumentStoreTests.CreateFacts();
        var store = new AttemptDocumentStore();
        await store.WriteAsync(
            paths, lagging, facts, TestContext.Current.CancellationToken);
        var repository = new RecordingRepository(databaseAttempt);

        await CreateService(repository, store, _ => false)
            .RecoverAsync(TestContext.Current.CancellationToken);

        var abandoned = repository.Transitions.Single();
        var recovered = await store.TryReadAsync(
            paths, abandoned, TestContext.Current.CancellationToken);
        Assert.Equal(ExtractionAttemptStatus.Abandoned, recovered!.Attempt.Status);
        Assert.Equal(facts.Arguments, recovered.ExecutionFacts!.Arguments);
        Assert.Equal(1357, recovered.ExecutionFacts.OwnerProcessId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverAsync_OwnedStaging_AppliesRequestedRetentionOnly(bool keep)
    {
        Directory.CreateDirectory(_dataRoot);
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.OutputRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(paths.OutputRoot, "partial.dll"), new byte[17],
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(paths.StagingLogsRoot);
        await File.WriteAllTextAsync(
            Path.Combine(paths.StagingLogsRoot, "stdout.log"), "bounded",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(paths.StagingLogsRoot, "stderr.log"), "error",
            TestContext.Current.CancellationToken);
        var attempt = CreateAttempt(paths, ExtractionAttemptStatus.Running) with
        {
            KeepFailedArtifacts = keep,
            ProcessId = 303
        };
        var repository = new RecordingRepository(attempt);

        await CreateService(repository, new AttemptDocumentStore(), _ => false)
            .RecoverAsync(TestContext.Current.CancellationToken);

        var abandoned = repository.Transitions.Single();
        Assert.Equal(keep ? 0 : 1, abandoned.DiscardedFileCount);
        Assert.Equal(keep ? 0 : 17, abandoned.DiscardedByteCount);
        Assert.False(Directory.Exists(paths.StagingRoot));
        Assert.Equal(keep, Directory.Exists(paths.RetainedOutputRoot));
        Assert.True(File.Exists(Path.Combine(paths.FinalLogsRoot, "stdout.log")));
        Assert.True(File.Exists(Path.Combine(paths.FinalLogsRoot, "stderr.log")));
        Assert.True(File.Exists(paths.AttemptDocumentPath));
    }

    [Fact]
    public async Task RecoverAsync_UnassociatedStaging_IsPreservedAsAmbiguousEvidence()
    {
        Directory.CreateDirectory(_dataRoot);
        var paths = CreatePaths();
        var ambiguous = Path.Combine(
            Path.GetDirectoryName(paths.StagingRoot)!,
            "ffffffffffffffffffffffffffffffff");
        Directory.CreateDirectory(ambiguous);
        await File.WriteAllTextAsync(
            Path.Combine(ambiguous, "evidence.bin"), "keep",
            TestContext.Current.CancellationToken);
        var repository = new RecordingRepository(
            CreateAttempt(paths, ExtractionAttemptStatus.Created));

        await CreateService(repository, new AttemptDocumentStore(), _ => false)
            .RecoverAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(ambiguous, "evidence.bin")));
    }

    [Fact]
    public async Task RecoverAsync_ConflictingLogEvidence_FailsBeforeDatabaseMutation()
    {
        Directory.CreateDirectory(_dataRoot);
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.StagingLogsRoot);
        Directory.CreateDirectory(paths.FinalLogsRoot);
        await File.WriteAllTextAsync(
            Path.Combine(paths.StagingLogsRoot, "stdout.log"), "staging",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(paths.FinalLogsRoot, "stdout.log"), "final",
            TestContext.Current.CancellationToken);
        var repository = new RecordingRepository(
            CreateAttempt(paths, ExtractionAttemptStatus.Running));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateService(repository, new AttemptDocumentStore(), _ => false)
                .RecoverAsync(TestContext.Current.CancellationToken));

        Assert.Empty(repository.Transitions);
        Assert.True(File.Exists(Path.Combine(paths.StagingLogsRoot, "stdout.log")));
        Assert.True(File.Exists(Path.Combine(paths.FinalLogsRoot, "stdout.log")));
    }

    [Theory]
    [InlineData(ExtractionAttemptStatus.Created)]
    [InlineData(ExtractionAttemptStatus.Preparing)]
    [InlineData(ExtractionAttemptStatus.Running)]
    public async Task RecoverAsync_RealSqliteRepository_PersistsAbandonedRecoveryMetadata(
        ExtractionAttemptStatus initialStatus)
    {
        Directory.CreateDirectory(_dataRoot);
        var repository = new SqliteAtlasRepository(
            Path.Combine(_dataRoot, "atlas.db"),
            Path.Combine(_dataRoot, "backups"));
        var cancellationToken = TestContext.Current.CancellationToken;
        await repository.InitializeAsync(cancellationToken);
        await SeedBuildAsync(repository, cancellationToken);
        var paths = CreatePaths();
        var attempt = CreateAttempt(paths, ExtractionAttemptStatus.Created) with
        {
            RecipeId = null,
            ToolInstanceId = null
        };
        await repository.CreateAttemptAsync(attempt, cancellationToken);
        if (initialStatus is ExtractionAttemptStatus.Preparing or ExtractionAttemptStatus.Running)
        {
            attempt = attempt with { Status = ExtractionAttemptStatus.Preparing };
            await repository.TransitionAttemptAsync(
                attempt, ExtractionAttemptStatus.Created, cancellationToken);
        }

        if (initialStatus == ExtractionAttemptStatus.Running)
        {
            attempt = attempt with { Status = ExtractionAttemptStatus.Running };
            await repository.TransitionAttemptAsync(
                attempt, ExtractionAttemptStatus.Preparing, cancellationToken);
        }

        await CreateService(repository, new AttemptDocumentStore(), _ => false)
            .RecoverAsync(cancellationToken);

        var stored = await repository.GetAttemptAsync(attempt.AttemptId, cancellationToken);
        Assert.Equal(ExtractionAttemptStatus.Abandoned, stored!.Status);
        Assert.Equal(ExtractionFailureStage.Recovery, stored.FailureStage);
        Assert.Equal(ExtractionFailureCode.InterruptedProcess, stored.FailureCode);
        Assert.Equal(ExtractionRecoveryService.InterruptedMessage, stored.FailureMessage);
        Assert.Equal(_now, stored.CompletedAtUtc);
    }

    private OwnedAttemptPaths CreatePaths() => OwnedAttemptPaths.Create(
        _dataRoot, "build-a", "0123456789abcdef0123456789abcdef");

    private static Task SeedBuildAsync(
        SqliteAtlasRepository repository,
        CancellationToken cancellationToken)
    {
        var observedAt = DateTimeOffset.Parse("2026-08-12T12:00:00Z");
        var gameRoot = Path.GetFullPath(Path.Combine(@"C:\games", "build-a"));
        return repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                IdentityVersion: 2,
                Build: new GameBuild(
                    "build-a",
                    "assembly-build-a",
                    "metadata-build-a",
                    observedAt,
                    IsValid: true),
                Installation: new InstallationObservation(
                    "2022.3",
                    "3164500",
                    "123",
                    gameRoot,
                    Path.Combine(gameRoot, "GameAssembly.dll"),
                    Path.Combine(gameRoot, "global-metadata.dat")),
                Dependencies: [],
                AtlasVersion: "0.3.0-test",
                CapturedAtUtc: observedAt),
            cancellationToken);
    }

    private ExtractionRecoveryService CreateService(
        IExtractionRepository repository,
        AttemptDocumentStore store,
        Func<int, bool> alive) => new(
            _dataRoot, repository, store, alive, new FixedTimeProvider(_now));

    private static ExtractionAttempt CreateAttempt(
        OwnedAttemptPaths paths,
        ExtractionAttemptStatus status) => new(
        paths.AttemptId, new string('1', 64), "build-a", "tool-1", "profile", 1,
        new string('2', 64), "policy", 1, new string('3', 64), 1, 1,
        null, null, status, DateTimeOffset.Parse("2026-08-12T12:00:00Z"),
        null, null, null, null, paths.WorkingRoot,
        Path.Combine(paths.FinalLogsRoot, "stdout.log"),
        Path.Combine(paths.FinalLogsRoot, "stderr.log"), false, false, 0, 0,
        null, null, null, null, null, false, 0, 0,
        status == ExtractionAttemptStatus.ProcessCompleted ? paths.CandidateOutputRoot : null,
        null);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingRepository(params ExtractionAttempt[] attempts)
        : IExtractionRepository
    {
        public List<ExtractionAttempt> Transitions { get; } = [];
        public List<ExtractionAttemptStatus> ExpectedStatuses { get; } = [];

        public Task<IReadOnlyList<ExtractionAttempt>> ListNonTerminalAttemptsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtractionAttempt>>(attempts);

        public Task TransitionAttemptAsync(
            ExtractionAttempt attempt,
            ExtractionAttemptStatus expectedStatus,
            CancellationToken cancellationToken)
        {
            Transitions.Add(attempt);
            ExpectedStatuses.Add(expectedStatus);
            return Task.CompletedTask;
        }

        public Task<ExtractionAttempt?> GetAttemptAsync(string attemptId, CancellationToken cancellationToken) =>
            Task.FromResult(attempts.FirstOrDefault(item => item.AttemptId == attemptId));
        public Task<GameBuild?> GetBuildAsync(string buildId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<InstallationObservationRecord>> ListInstallationObservationsAsync(string buildId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CreateAttemptAsync(ExtractionAttempt attempt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveInputSnapshotAsync(InputSnapshot snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<InputSnapshot>> ListReplayVerifiedInputSnapshotsAsync(string buildId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InputSnapshot?> GetInputSnapshotAsync(string inputSnapshotId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkInputSnapshotReplayVerifiedAsync(string inputSnapshotId, string expectedBuildId, string expectedManifestDigest, DateTimeOffset verifiedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
