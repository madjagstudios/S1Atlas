using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.History;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Extraction.Promotion;
using S1Atlas.Extraction.Tests.Promotion;
using S1Atlas.Extraction.Tools;
using S1Atlas.Extraction.Validation;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Extraction.Tests.History;

public sealed class ExtractionHistoryServiceTests
{
    private static readonly string ProfileDigest = new('a', 64);

    [Fact]
    public async Task ListAsync_ReturnsValidatedExtractionsNewestFirst()
    {
        using var fixture = await HistoryFixture.CreateAsync(TestContext.Current.CancellationToken);
        var older = await fixture.SeedValidatedExtractionAsync(
            new string('1', 32), [1, 2, 3, 4], PromotionTestData.RecipeId,
            PromotionTestData.BaseTime, autoPrefer: false, storeReport: true,
            ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, preferenceEligible: true,
            TestContext.Current.CancellationToken);
        var newer = await fixture.SeedValidatedExtractionAsync(
            new string('2', 32), [5, 6, 7, 8, 9], PromotionTestData.RecipeId2,
            PromotionTestData.BaseTime.AddMinutes(10), autoPrefer: false, storeReport: true,
            ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, preferenceEligible: true,
            TestContext.Current.CancellationToken);

        var items = await fixture.Service().ListAsync(
            buildId: null, includeFailed: false, TestContext.Current.CancellationToken);

        Assert.Equal(2, items.Count);
        Assert.Equal(newer, items[0].Id);
        Assert.Equal(older, items[1].Id);
        Assert.All(items, item =>
            Assert.Equal(ExtractionHistoryEntryKind.ValidatedExtraction, item.Kind));
    }

    [Fact]
    public async Task ListAsync_BuildFilter_OnlyReturnsMatchingBuild()
    {
        using var fixture = await HistoryFixture.CreateAsync(TestContext.Current.CancellationToken);
        await fixture.SeedValidatedExtractionAsync(
            new string('1', 32), [1, 2, 3, 4], PromotionTestData.RecipeId,
            PromotionTestData.BaseTime, autoPrefer: false, storeReport: true,
            ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, preferenceEligible: true,
            TestContext.Current.CancellationToken);

        var matching = await fixture.Service().ListAsync(
            PromotionTestData.BuildId, includeFailed: false, TestContext.Current.CancellationToken);
        var other = await fixture.Service().ListAsync(
            new string('e', 64), includeFailed: false, TestContext.Current.CancellationToken);

        Assert.Single(matching);
        Assert.Empty(other);
    }

    [Fact]
    public async Task ListAsync_IncludeFailed_AddsTerminalAndCandidateAttempts()
    {
        using var fixture = await HistoryFixture.CreateAsync(TestContext.Current.CancellationToken);
        await fixture.SeedValidatedExtractionAsync(
            new string('1', 32), [1, 2, 3, 4], PromotionTestData.RecipeId,
            PromotionTestData.BaseTime, autoPrefer: false, storeReport: true,
            ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, preferenceEligible: true,
            TestContext.Current.CancellationToken);
        await fixture.CreateProcessCompletedAttemptAsync(
            new string('c', 32), TestContext.Current.CancellationToken);
        await fixture.CreateFailedAttemptAsync(
            new string('d', 32), TestContext.Current.CancellationToken);

        var withoutFailed = await fixture.Service().ListAsync(
            buildId: null, includeFailed: false, TestContext.Current.CancellationToken);
        var withFailed = await fixture.Service().ListAsync(
            buildId: null, includeFailed: true, TestContext.Current.CancellationToken);

        Assert.Single(withoutFailed);
        Assert.Contains(withFailed, item =>
            item.Kind == ExtractionHistoryEntryKind.Attempt &&
            item.Status == nameof(ExtractionAttemptStatus.ProcessCompleted));
        Assert.Contains(withFailed, item =>
            item.Kind == ExtractionHistoryEntryKind.Attempt &&
            item.Status == nameof(ExtractionAttemptStatus.Failed));
        // The seeded extraction's Succeeded source attempt is never a failed/candidate entry.
        Assert.DoesNotContain(withFailed, item =>
            item.Status == nameof(ExtractionAttemptStatus.Succeeded));
    }

    [Fact]
    public async Task ShowAsync_ValidatedExtraction_PerformsFullIntegrityAndReturnsFacts()
    {
        using var fixture = await HistoryFixture.CreateAsync(TestContext.Current.CancellationToken);
        var extractionId = await fixture.SeedValidatedExtractionAsync(
            new string('1', 32), [1, 2, 3, 4], PromotionTestData.RecipeId,
            PromotionTestData.BaseTime, autoPrefer: true, storeReport: true,
            ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, preferenceEligible: true,
            TestContext.Current.CancellationToken);

        var detail = await fixture.Service().ShowAsync(
            extractionId, TestContext.Current.CancellationToken);

        Assert.NotNull(detail);
        Assert.Equal(ExtractionHistoryEntryKind.ValidatedExtraction, detail!.Kind);
        Assert.True(detail.IntegrityVerified);
        Assert.True(detail.Preferred);
        Assert.Equal(extractionId, detail.Extraction!.ExtractionId);
    }

    [Fact]
    public async Task ShowAsync_MismatchedExtraction_FailsIntegrityWithoutExposingRoot()
    {
        using var fixture = await HistoryFixture.CreateAsync(TestContext.Current.CancellationToken);
        var extractionId = await fixture.SeedValidatedExtractionAsync(
            new string('1', 32), [1, 2, 3, 4], PromotionTestData.RecipeId,
            PromotionTestData.BaseTime, autoPrefer: true, storeReport: true,
            ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, preferenceEligible: true,
            TestContext.Current.CancellationToken);
        await fixture.CorruptFinalArtifactAsync(extractionId, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            fixture.Service().ShowAsync(extractionId, TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.IntegrityMismatch, exception.Code);
    }

    [Fact]
    public async Task ShowAsync_Attempt_ReturnsLifecycleValidationAndResultFacts()
    {
        using var fixture = await HistoryFixture.CreateAsync(TestContext.Current.CancellationToken);
        var attemptId = new string('1', 32);
        await fixture.SeedValidatedExtractionAsync(
            attemptId, [1, 2, 3, 4], PromotionTestData.RecipeId,
            PromotionTestData.BaseTime, autoPrefer: false, storeReport: true,
            ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, preferenceEligible: true,
            TestContext.Current.CancellationToken);

        var detail = await fixture.Service().ShowAsync(
            attemptId, TestContext.Current.CancellationToken);

        Assert.NotNull(detail);
        Assert.Equal(ExtractionHistoryEntryKind.Attempt, detail!.Kind);
        Assert.Equal(ExtractionAttemptStatus.Succeeded, detail.Attempt!.Status);
        Assert.NotNull(detail.Attempt.ResultExtractionId);
        Assert.Equal(ToolTrustLevel.ManagedPinned, detail.AttemptToolTrustLevel);
    }

    [Fact]
    public async Task ShowAsync_UnknownOrInvalidId_ReturnsNull()
    {
        using var fixture = await HistoryFixture.CreateAsync(TestContext.Current.CancellationToken);

        Assert.Null(await fixture.Service().ShowAsync(
            new string('f', 64), TestContext.Current.CancellationToken));
        Assert.Null(await fixture.Service().ShowAsync(
            new string('f', 32), TestContext.Current.CancellationToken));
        Assert.Null(await fixture.Service().ShowAsync(
            "not-a-valid-id", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PromoteAsync_ReusesVerifiedCurrentPolicyResultAndRecordsManualPromotion()
    {
        using var fixture = await HistoryFixture.CreateAsync(TestContext.Current.CancellationToken);
        var extractionId = await fixture.SeedValidatedExtractionAsync(
            new string('1', 32), [1, 2, 3, 4], PromotionTestData.RecipeId,
            PromotionTestData.BaseTime, autoPrefer: false, storeReport: true,
            ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, preferenceEligible: true,
            TestContext.Current.CancellationToken);

        var outcome = await fixture.Service().PromoteAsync(
            extractionId, TestContext.Current.CancellationToken);

        Assert.False(outcome.WasAlreadyPreferred);
        Assert.False(outcome.Revalidated);
        var preferred = await fixture.Repository.GetPreferredExtractionAsync(
            PromotionTestData.BuildId, TestContext.Current.CancellationToken);
        Assert.Equal(extractionId, preferred!.ExtractionId);
        Assert.Equal(ExtractionPreferenceReason.ManualPromotion, preferred.SelectionReason);
    }

    [Fact]
    public async Task PromoteAsync_CustomToolExtraction_ManuallyBecomesPreferred()
    {
        using var fixture = await HistoryFixture.CreateAsync(TestContext.Current.CancellationToken);
        var extractionId = await fixture.SeedValidatedExtractionAsync(
            new string('1', 32), [1, 2, 3, 4], PromotionTestData.RecipeId,
            PromotionTestData.BaseTime, autoPrefer: false, storeReport: true,
            ToolTrustLevel.CustomOverride, ValidationOutcome.Valid, preferenceEligible: true,
            TestContext.Current.CancellationToken);

        var outcome = await fixture.Service().PromoteAsync(
            extractionId, TestContext.Current.CancellationToken);

        Assert.Equal(ToolTrustLevel.CustomOverride, outcome.ToolTrustLevel);
        var preferred = await fixture.Repository.GetPreferredExtractionAsync(
            PromotionTestData.BuildId, TestContext.Current.CancellationToken);
        Assert.Equal(extractionId, preferred!.ExtractionId);
    }

    [Fact]
    public async Task PromoteAsync_PreferenceBlockingWarning_IsExplicitlyPermitted()
    {
        using var fixture = await HistoryFixture.CreateAsync(TestContext.Current.CancellationToken);
        var extractionId = await fixture.SeedValidatedExtractionAsync(
            new string('1', 32), [1, 2, 3, 4], PromotionTestData.RecipeId,
            PromotionTestData.BaseTime, autoPrefer: false, storeReport: true,
            ToolTrustLevel.ManagedPinned, ValidationOutcome.ValidWithWarnings,
            preferenceEligible: false, TestContext.Current.CancellationToken);

        var outcome = await fixture.Service().PromoteAsync(
            extractionId, TestContext.Current.CancellationToken);

        Assert.Equal(ValidationOutcome.ValidWithWarnings, outcome.Outcome);
        var preferred = await fixture.Repository.GetPreferredExtractionAsync(
            PromotionTestData.BuildId, TestContext.Current.CancellationToken);
        Assert.Equal(extractionId, preferred!.ExtractionId);
    }

    [Fact]
    public async Task PromoteAsync_InvalidUnderCurrentPolicy_IsRejectedAndHistoryRetained()
    {
        using var fixture = await HistoryFixture.CreateAsync(TestContext.Current.CancellationToken);
        var extractionId = await fixture.SeedValidatedExtractionAsync(
            new string('1', 32), [1, 2, 3, 4], PromotionTestData.RecipeId,
            PromotionTestData.BaseTime, autoPrefer: false, storeReport: false,
            ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, preferenceEligible: true,
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            fixture.ServiceWithRejectingPolicy().PromoteAsync(
                extractionId, TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.ValidationPolicyInvalid, exception.Code);
        Assert.NotNull(await fixture.Repository.GetValidatedExtractionAsync(
            extractionId, TestContext.Current.CancellationToken));
        Assert.Null(await fixture.Repository.GetPreferredExtractionAsync(
            PromotionTestData.BuildId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PromoteAsync_AlreadyPreferred_IsIdempotentWithoutDuplicateEvent()
    {
        using var fixture = await HistoryFixture.CreateAsync(TestContext.Current.CancellationToken);
        var extractionId = await fixture.SeedValidatedExtractionAsync(
            new string('1', 32), [1, 2, 3, 4], PromotionTestData.RecipeId,
            PromotionTestData.BaseTime, autoPrefer: true, storeReport: true,
            ToolTrustLevel.ManagedPinned, ValidationOutcome.Valid, preferenceEligible: true,
            TestContext.Current.CancellationToken);
        var eventsBefore = await fixture.CountPreferenceEventsAsync(
            TestContext.Current.CancellationToken);

        var outcome = await fixture.Service().PromoteAsync(
            extractionId, TestContext.Current.CancellationToken);

        Assert.True(outcome.WasAlreadyPreferred);
        var eventsAfter = await fixture.CountPreferenceEventsAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(eventsBefore, eventsAfter);
        var preferred = await fixture.Repository.GetPreferredExtractionAsync(
            PromotionTestData.BuildId, TestContext.Current.CancellationToken);
        Assert.Equal(ExtractionPreferenceReason.ManagedAutomatic, preferred!.SelectionReason);
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly Sha256FileHasher _hasher = new();
        private readonly ValidatedExtractionDocumentStore _documentStore = new();
        private readonly PromotionJournalStore _journalStore = new();
        private readonly AttemptDocumentStore _attemptDocumentStore = new();

        private HistoryFixture(string atlasRoot, SqliteAtlasRepository repository)
        {
            AtlasRoot = atlasRoot;
            Repository = repository;
            Clock = new FixedTimeProvider(PromotionTestData.BaseTime);
            IntegrityVerifier = new ValidatedExtractionIntegrityVerifier(_documentStore, _hasher, repository);
            CandidateInspector = new CandidateOutputInspector(_hasher);
            var extractionLock = new ExtractionLock(atlasRoot, System.Environment.ProcessId, _ => false);
            ValidationService = new ExtractionValidationService(
                atlasRoot,
                repository,
                repository,
                _hasher,
                _documentStore,
                IntegrityVerifier,
                CandidateInspector,
                _journalStore,
                _attemptDocumentStore,
                extractionLock,
                Clock);
        }

        public string AtlasRoot { get; }
        public SqliteAtlasRepository Repository { get; }
        public FixedTimeProvider Clock { get; }
        public ValidatedExtractionIntegrityVerifier IntegrityVerifier { get; }
        public CandidateOutputInspector CandidateInspector { get; }
        public ExtractionValidationService ValidationService { get; }

        public static async Task<HistoryFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var atlasRoot = Path.Combine(
                Path.GetTempPath(), $"s1atlas-history-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(atlasRoot);
            var repository = await PromotionTestData.CreateRepositoryAsync(atlasRoot, cancellationToken);
            return new HistoryFixture(atlasRoot, repository);
        }

        public ExtractionHistoryService Service() => CreateService(ResolvedPolicy(
            PromotionTestData.PolicyDigest, minimumManagedAssemblyCount: 1));

        public ExtractionHistoryService ServiceWithRejectingPolicy() => CreateService(ResolvedPolicy(
            new string('9', 64), minimumManagedAssemblyCount: 999));

        private ExtractionHistoryService CreateService(ResolvedValidationPolicy policy) =>
            new(
                AtlasRoot,
                Repository,
                Repository,
                IntegrityVerifier,
                _documentStore,
                ValidationService,
                new FakeProfileProvider(ResolvedProfile()),
                new FakePolicyProvider(policy),
                Clock);

        public async Task<string> SeedValidatedExtractionAsync(
            string attemptId,
            byte[] candidateBytes,
            string recipeId,
            DateTimeOffset moment,
            bool autoPrefer,
            bool storeReport,
            ToolTrustLevel trustLevel,
            ValidationOutcome outcome,
            bool preferenceEligible,
            CancellationToken cancellationToken)
        {
            var candidate = await PromotionTestData.CreateValidatingCandidateAsync(
                Repository, AtlasRoot, attemptId, recipeId, candidateBytes, cancellationToken);
            var issues = outcome == ValidationOutcome.ValidWithWarnings
                ? new[]
                {
                    new ValidationIssue(
                        ValidationIssueSeverity.Warning,
                        "SameRecipeDifferentOutput",
                        "A reproducibility warning blocks automatic preference.",
                        ArtifactRelativePath: null,
                        PreferenceBlocking: true)
                }
                : [];
            var report = PromotionTestData.BuildReport(
                candidate.Attempt, candidate.Digest, candidate.Statistics, outcome, issues,
                preferenceEligible, moment,
                subjectExtractionId: storeReport ? candidate.ExtractionId : null);

            var promoter = new ValidatedExtractionPromoter(
                AtlasRoot, Repository, _documentStore, IntegrityVerifier, _journalStore,
                CandidateInspector, new FixedTimeProvider(moment));
            await promoter.PromoteAsync(
                new ValidatedExtractionPromotionRequest(
                    candidate.Attempt,
                    report,
                    candidate.Manifest,
                    trustLevel,
                    DeduplicationTarget: null,
                    autoPrefer ? ExtractionPreferenceReason.ManagedAutomatic : null,
                    KeepInvalidOutput: false),
                cancellationToken);

            if (storeReport)
            {
                var attemptPaths = OwnedValidatedExtractionPaths.ForAttempt(
                    AtlasRoot, PromotionTestData.BuildId, attemptId);
                await _documentStore.WriteAttemptValidationReportAsync(
                    attemptPaths, report, cancellationToken);
            }

            return candidate.ExtractionId;
        }

        public async Task CreateProcessCompletedAttemptAsync(
            string attemptId, CancellationToken cancellationToken)
        {
            var ownedCandidateRoot = OwnedValidatedExtractionPaths
                .ForAttempt(AtlasRoot, PromotionTestData.BuildId, attemptId).AttemptCandidatePath!;
            Directory.CreateDirectory(ownedCandidateRoot);
            var created = BuildAttempt(attemptId);
            await Repository.CreateAttemptAsync(created, cancellationToken);
            var preparing = created with
            {
                Status = ExtractionAttemptStatus.Preparing,
                StartedAtUtc = PromotionTestData.BaseTime
            };
            await Repository.TransitionAttemptAsync(
                preparing, ExtractionAttemptStatus.Created, cancellationToken);
            var running = preparing with { Status = ExtractionAttemptStatus.Running, ProcessId = 100 };
            await Repository.TransitionAttemptAsync(
                running, ExtractionAttemptStatus.Preparing, cancellationToken);
            var processCompleted = running with
            {
                Status = ExtractionAttemptStatus.ProcessCompleted,
                ProcessExitCode = 0,
                CompletedAtUtc = PromotionTestData.BaseTime,
                CandidateOutputPath = ownedCandidateRoot
            };
            await Repository.TransitionAttemptAsync(
                processCompleted, ExtractionAttemptStatus.Running, cancellationToken);
        }

        public async Task CreateFailedAttemptAsync(string attemptId, CancellationToken cancellationToken)
        {
            var created = BuildAttempt(attemptId);
            await Repository.CreateAttemptAsync(created, cancellationToken);
            var failed = created with
            {
                Status = ExtractionAttemptStatus.Failed,
                CompletedAtUtc = PromotionTestData.BaseTime,
                FailureStage = ExtractionFailureStage.ProcessExecution,
                FailureCode = ExtractionFailureCode.ProcessExitNonZero,
                FailureMessage = "The process exited non-zero."
            };
            await Repository.TransitionAttemptAsync(
                failed, ExtractionAttemptStatus.Created, cancellationToken);
        }

        public async Task CorruptFinalArtifactAsync(string extractionId, CancellationToken cancellationToken)
        {
            var finalRoot = OwnedValidatedExtractionPaths
                .ForExtraction(AtlasRoot, PromotionTestData.BuildId, extractionId).FinalExtractionRoot!;
            var artifact = Path.Combine(finalRoot, "reconstructed", "Assembly-CSharp.dll");
            await File.AppendAllTextAsync(artifact, "corruption", cancellationToken);
        }

        public async Task<long> CountPreferenceEventsAsync(CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(AtlasRoot, "atlas.db"),
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM extraction_preference_events;";
            return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        private static ExtractionAttempt BuildAttempt(string attemptId)
        {
            var attemptRoot = Path.Combine(
                Path.GetTempPath(), "unused-attempt-root", attemptId);
            return new ExtractionAttempt(
                AttemptId: attemptId,
                RecipeId: PromotionTestData.RecipeId,
                BuildId: PromotionTestData.BuildId,
                ToolInstanceId: PromotionTestData.ToolInstanceId,
                ProfileId: PromotionTestData.ProfileId,
                ProfileVersion: 1,
                ProfileDigest: ProfileDigest,
                ValidationPolicyId: "managed-assemblies-v1",
                ValidationPolicyVersion: 1,
                ValidationPolicyDigest: PromotionTestData.PolicyDigest,
                AdapterVersion: 1,
                ExtractionSchemaVersion: 1,
                InputSource: ExtractionInputSource.Live,
                InputSnapshotId: null,
                Status: ExtractionAttemptStatus.Created,
                CreatedAtUtc: PromotionTestData.BaseTime,
                StartedAtUtc: null,
                CompletedAtUtc: null,
                PreInputManifestDigest: null,
                PostInputManifestDigest: null,
                WorkingPath: Path.Combine(attemptRoot, "work"),
                StandardOutputPath: Path.Combine(attemptRoot, "logs", "stdout.log"),
                StandardErrorPath: Path.Combine(attemptRoot, "logs", "stderr.log"),
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
        }

        private static ResolvedExtractionProfile ResolvedProfile() => new(
            new ExtractionProfile(
                SchemaVersion: 1,
                ProfileId: PromotionTestData.ProfileId,
                ProfileVersion: 1,
                AdapterVersion: 1,
                ExtractionSchemaVersion: 1,
                ExecutableName: "Schedule I",
                OutputFormat: "dll_il_recovery",
                Timeout: TimeSpan.FromMinutes(5),
                MaximumRetainedStandardOutputBytes: 1024,
                MaximumRetainedStandardErrorBytes: 1024,
                AcceptedExitCodes: [0],
                RequiredAssemblyIdentities: ["Assembly-CSharp"],
                SnapshotInputs: [],
                UnityVersionSources: ["Schedule I_Data/globalgamemanagers"]),
            ProfileDigest);

        private static ResolvedValidationPolicy ResolvedPolicy(
            string policyDigest, int minimumManagedAssemblyCount) => new(
            new ValidationPolicy(
                SchemaVersion: 1,
                PolicyId: "managed-assemblies-v1",
                PolicyVersion: 1,
                RequiredAssemblyIdentities: ["Assembly-CSharp"],
                MinimumManagedAssemblyCount: minimumManagedAssemblyCount,
                MinimumTypeDefinitionCount: 1,
                MinimumMethodDefinitionCount: 1,
                MinimumTotalManagedBytes: 1,
                ComparativeWarningRelativeChange: 0.25,
                CatastrophicDecreaseRelativeChange: 0.80),
            policyDigest);

        private sealed class FakeProfileProvider(ResolvedExtractionProfile profile)
            : IExtractionProfileProvider
        {
            public ResolvedExtractionProfile GetRequired(string profileId) => profile;
        }

        private sealed class FakePolicyProvider(ResolvedValidationPolicy policy)
            : IValidationPolicyProvider
        {
            public ResolvedValidationPolicy GetRequired(string policyId) => policy;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(AtlasRoot))
            {
                try
                {
                    Directory.Delete(AtlasRoot, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
