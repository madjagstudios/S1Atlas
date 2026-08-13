using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Cleanup;
using Xunit;

namespace S1Atlas.Extraction.Tests.Cleanup;

public sealed class ExtractionCleanupServiceTests
{
    private const string DataRoot = "C:\\atlas";
    private static readonly string BuildId = new('a', 64);
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Old = Now - TimeSpan.FromDays(40);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string AttemptId(int index) => index.ToString("x32");

    private static string AttemptRoot(string id) =>
        $"{DataRoot}\\builds\\{BuildId}\\attempts\\{id}";

    private static string StagingRoot(string id) =>
        $"{DataRoot}\\builds\\{BuildId}\\extractions\\.staging\\{id}";

    [Fact]
    public async Task Preview_InitializesRecoversAndNeverDeletes()
    {
        var harness = new Harness();
        harness.FileSystem.Directory(AttemptRoot(AttemptId(1)), Old);
        harness.FileSystem.File(AttemptRoot(AttemptId(1)) + "\\logs\\stdout.log", 5, Old);
        harness.AddAttempt(Attempt(AttemptId(1), ExtractionAttemptStatus.Failed, Old));

        var plan = await harness.Service.PreviewAsync(TimeSpan.FromDays(30), Ct);

        Assert.True(harness.Initialized);
        Assert.True(harness.Recovered);
        Assert.Empty(harness.DeletedPaths);
        Assert.Single(plan.EligibleItems);
    }

    [Fact]
    public async Task Apply_DeletesEligibleTerminalAttemptFilesBeforeRepositoryRow()
    {
        var harness = new Harness();
        var id = AttemptId(1);
        harness.FileSystem.Directory(AttemptRoot(id), Old);
        harness.FileSystem.File(AttemptRoot(id) + "\\logs\\stdout.log", 5, Old);
        harness.AddAttempt(Attempt(id, ExtractionAttemptStatus.Failed, Old));

        var result = await harness.Service.ApplyAsync(TimeSpan.FromDays(30), Ct);

        var deleted = Assert.Single(result.DeletedItems);
        Assert.Equal(id, deleted.Id);
        Assert.False(result.HasOperationalProblems);
        // The attempt root and staging root were both deleted before the row.
        Assert.Contains(AttemptRoot(id), harness.DeletedPaths);
        Assert.Contains(StagingRoot(id), harness.DeletedPaths);
        var fsIndex = harness.Events.FindIndex(e => e.StartsWith("fs:", StringComparison.Ordinal));
        var dbIndex = harness.Events.IndexOf($"db:{id}");
        Assert.True(fsIndex >= 0 && dbIndex >= 0 && fsIndex < dbIndex);
        Assert.DoesNotContain(id, harness.Repository.AttemptIds);
    }

    [Fact]
    public async Task Apply_ReObservesAndPreservesChangedCandidate()
    {
        var harness = new Harness();
        var id = AttemptId(1);
        harness.FileSystem.Directory(AttemptRoot(id), Old);
        harness.FileSystem.File(AttemptRoot(id) + "\\logs\\stdout.log", 5, Old);
        harness.AddAttempt(Attempt(id, ExtractionAttemptStatus.Failed, Old));
        // Mutate the tree between planning and deletion via the re-observation hook.
        harness.OnReObserve = () =>
            harness.FileSystem.File(AttemptRoot(id) + "\\logs\\extra.log", 9, Old);

        var result = await harness.Service.ApplyAsync(TimeSpan.FromDays(30), Ct);

        Assert.Empty(result.DeletedItems);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("CleanupEvidenceChanged", failure.Code);
        Assert.True(result.HasOperationalProblems);
        Assert.Empty(harness.DeletedPaths);
    }

    [Fact]
    public async Task PreviewAndApply_BlockWhenExtractionActive()
    {
        var harness = new Harness { ExtractionActive = true };
        harness.AddAttempt(Attempt(AttemptId(1), ExtractionAttemptStatus.Failed, Old));

        await Assert.ThrowsAsync<ExtractionCleanupActiveException>(() =>
            harness.Service.PreviewAsync(TimeSpan.FromDays(30), Ct));
        await Assert.ThrowsAsync<ExtractionCleanupActiveException>(() =>
            harness.Service.ApplyAsync(TimeSpan.FromDays(30), Ct));
        Assert.Empty(harness.DeletedPaths);
    }

    [Fact]
    public async Task Apply_DeletesSafeItemsEvenWhenABlockedItemRemains()
    {
        var harness = new Harness();
        var id = AttemptId(1);
        harness.FileSystem.Directory(AttemptRoot(id), Old);
        harness.AddAttempt(Attempt(id, ExtractionAttemptStatus.Failed, Old));
        // An unknown tool-staging entry blocks but must not prevent the safe deletion.
        harness.FileSystem.Directory($"{DataRoot}\\tools\\.staging\\not-owned", Old);

        var result = await harness.Service.ApplyAsync(TimeSpan.FromDays(30), Ct);

        Assert.Single(result.DeletedItems);
        Assert.NotEmpty(result.Plan.BlockedItems);
        Assert.True(result.HasOperationalProblems);
    }

    [Fact]
    public async Task Apply_DatabaseDeleteFailure_IsTruthfulAndIdempotentlyRetryable()
    {
        var harness = new Harness();
        var id = AttemptId(1);
        harness.FileSystem.Directory(AttemptRoot(id), Old);
        harness.FileSystem.File(AttemptRoot(id) + "\\logs\\stdout.log", 5, Old);
        harness.AddAttempt(Attempt(id, ExtractionAttemptStatus.Failed, Old));
        harness.Repository.FailNextDelete = true;

        var first = await harness.Service.ApplyAsync(TimeSpan.FromDays(30), Ct);

        var failure = Assert.Single(first.Failures);
        Assert.Equal("CleanupDatabaseDeleteFailed", failure.Code);
        // Files are gone but the row is retained.
        Assert.Contains(id, harness.Repository.AttemptIds);

        var second = await harness.Service.ApplyAsync(TimeSpan.FromDays(30), Ct);

        Assert.Single(second.DeletedItems);
        Assert.False(second.HasOperationalProblems);
        Assert.DoesNotContain(id, harness.Repository.AttemptIds);
    }

    [Fact]
    public async Task Apply_SecondRunAfterSuccess_IsZeroItemNoOp()
    {
        var harness = new Harness();
        var id = AttemptId(1);
        harness.FileSystem.Directory(AttemptRoot(id), Old);
        harness.AddAttempt(Attempt(id, ExtractionAttemptStatus.Failed, Old));

        var first = await harness.Service.ApplyAsync(TimeSpan.FromDays(30), Ct);
        var second = await harness.Service.ApplyAsync(TimeSpan.FromDays(30), Ct);

        Assert.Single(first.DeletedItems);
        Assert.Empty(second.DeletedItems);
        Assert.Empty(second.Plan.EligibleItems);
        Assert.False(second.HasOperationalProblems);
    }

    private static ExtractionAttempt Attempt(
        string attemptId,
        ExtractionAttemptStatus status,
        DateTimeOffset? completedAtUtc) =>
        new(
            AttemptId: attemptId,
            RecipeId: "recipe-1",
            BuildId: BuildId,
            ToolInstanceId: null,
            ProfileId: "default",
            ProfileVersion: 1,
            ProfileDigest: new string('a', 64),
            ValidationPolicyId: "default",
            ValidationPolicyVersion: 1,
            ValidationPolicyDigest: new string('b', 64),
            AdapterVersion: 1,
            ExtractionSchemaVersion: 1,
            InputSource: null,
            InputSnapshotId: null,
            Status: status,
            CreatedAtUtc: Old,
            StartedAtUtc: null,
            CompletedAtUtc: completedAtUtc,
            PreInputManifestDigest: null,
            PostInputManifestDigest: null,
            WorkingPath: "C:\\attempts\\work",
            StandardOutputPath: "C:\\attempts\\logs\\stdout.log",
            StandardErrorPath: "C:\\attempts\\logs\\stderr.log",
            StandardOutputTruncated: false,
            StandardErrorTruncated: false,
            StandardOutputDiscardedBytes: 0,
            StandardErrorDiscardedBytes: 0,
            ProcessId: null,
            ProcessExitCode: null,
            FailureStage: null,
            FailureCode: null,
            FailureMessage: null,
            KeepFailedArtifacts: false,
            DiscardedFileCount: 0,
            DiscardedByteCount: 0,
            CandidateOutputPath: null,
            ResultExtractionId: null);

    private sealed class Harness
    {
        public InMemoryCleanupFileSystem FileSystem { get; } = new(DataRoot);
        public MutableAttemptRepository Repository { get; } = new();
        public bool Initialized { get; private set; }
        public bool Recovered { get; private set; }
        public bool ExtractionActive { get; init; }
        public List<string> DeletedPaths { get; } = [];
        public List<string> Events { get; } = [];
        public Action? OnReObserve { get; set; }

        public ExtractionCleanupService Service { get; }

        public Harness()
        {
            Repository.Events = Events;
            var planner = new ExtractionCleanupPlanner(
                DataRoot,
                Repository,
                new FixedTimeProvider(Now),
                new CleanupTreeInspector(FileSystem),
                FileSystem);
            var inspector = new CleanupTreeInspector(new ReObserveHookFileSystem(this));
            Service = new ExtractionCleanupService(
                ct => { Initialized = true; return Task.CompletedTask; },
                ct => { Recovered = true; return Task.CompletedTask; },
                ct => Task.FromResult(ExtractionActive),
                planner,
                Repository,
                inspector,
                DeleteAsync);
        }

        public void AddAttempt(ExtractionAttempt attempt) => Repository.Attempts.Add(attempt);

        private Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            Events.Add($"fs:{path}");
            DeletedPaths.Add(path);
            FileSystem.Remove(path);
            return Task.CompletedTask;
        }

        // Wraps the filesystem so the re-observation hook fires exactly once, letting a
        // test mutate the tree between planning and deletion.
        private sealed class ReObserveHookFileSystem(Harness harness) : ICleanupFileSystem
        {
            private bool _fired;

            public FileAttributes GetAttributes(string path)
            {
                MaybeFire();
                return harness.FileSystem.GetAttributes(path);
            }

            public IEnumerable<string> EnumerateEntries(string path) =>
                harness.FileSystem.EnumerateEntries(path);

            public long GetFileLength(string path) => harness.FileSystem.GetFileLength(path);

            public DateTimeOffset GetLastWriteUtc(string path) =>
                harness.FileSystem.GetLastWriteUtc(path);

            private void MaybeFire()
            {
                if (_fired || harness.OnReObserve is null)
                {
                    return;
                }

                _fired = true;
                harness.OnReObserve();
            }
        }
    }

    private sealed class MutableAttemptRepository : IValidatedExtractionRepository
    {
        public List<ExtractionAttempt> Attempts { get; } = [];
        public bool FailNextDelete { get; set; }
        public List<string> Events { get; set; } = [];

        public IEnumerable<string> AttemptIds => Attempts.Select(a => a.AttemptId);

        public Task<IReadOnlyList<ExtractionAttempt>> ListAttemptsAsync(
            string? buildId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtractionAttempt>>(Attempts.ToArray());

        public Task DeleteCleanupEligibleAttemptAsync(
            string attemptId,
            ExtractionAttemptStatus expectedStatus,
            DateTimeOffset expectedCompletedAtUtc,
            CancellationToken cancellationToken)
        {
            if (FailNextDelete)
            {
                FailNextDelete = false;
                throw new InvalidOperationException("Injected database delete failure.");
            }

            Events.Add($"db:{attemptId}");
            Attempts.RemoveAll(attempt =>
                string.Equals(attempt.AttemptId, attemptId, StringComparison.Ordinal));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ExtractionAttempt>> ListProcessCompletedAttemptsAsync(
            string recipeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ValidatedExtraction?> GetValidatedExtractionAsync(
            string extractionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ArtifactManifestEntry>> GetExtractionArtifactsAsync(
            string extractionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsAsync(
            string? buildId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ValidatedExtraction>> ListValidatedExtractionsByRecipeAsync(
            string recipeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredValidationResult?> GetLatestValidationResultAsync(
            string extractionId, string policyDigest, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PreferredExtraction?> GetPreferredExtractionAsync(
            string buildId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveValidationFailureAsync(
            ValidationPersistence validation, ExtractionAttemptStatus expectedStatus,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CommitValidatedExtractionAsync(
            ValidatedExtractionPromotion promotion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LinkAttemptToValidatedExtractionAsync(
            ValidationPersistence validation, ValidatedExtraction extraction,
            ExtractionAttemptStatus expectedStatus, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveRevalidationAsync(
            ValidationPersistence validation, ExtractionAttemptStatus expectedStatus,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetPreferredExtractionAsync(
            PreferredExtraction preference, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearPreferredExtractionAsync(
            string buildId, string expectedExtractionId, ExtractionPreferenceReason reason,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
