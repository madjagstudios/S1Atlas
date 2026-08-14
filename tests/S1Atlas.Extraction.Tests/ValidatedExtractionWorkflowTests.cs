using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Extraction.Promotion;
using S1Atlas.Extraction.Tests.Promotion;
using S1Atlas.Extraction.Tools;
using S1Atlas.Extraction.Validation;
using S1Atlas.ManagedAssemblyFixture;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Extraction.Tests;

public sealed class ValidatedExtractionWorkflowTests
{
    private static readonly string ProfileDigest = new('a', 64);
    private static readonly string PolicyDigest = new('b', 64);
    private static readonly string OtherPolicyDigest = new('9', 64);

    // --- Task 8 Step 1: preparation resolves without touching the game ---

    [Fact]
    public async Task PrepareAsync_ResolvesBuildProfilePolicyToolAndRecipe_WithoutGameProcess()
    {
        var buildId = new string('0', 64);
        var toolInstanceId = new string('c', 64);
        var build = new GameBuild(buildId, "assembly", "metadata", DateTimeOffset.UtcNow, IsValid: true);
        var profile = ResolvedProfile();
        var policy = ResolvedPolicy(PolicyDigest);
        var tool = ResolvedTool(toolInstanceId, ToolTrustLevel.ManagedPinned);
        var buildSelections = 0;
        var toolResolutions = 0;

        var service = new ExtractionPreparationService(
            (_, _) => { buildSelections++; return Task.FromResult(build); },
            _ => profile,
            _ => policy,
            (_, _) => { toolResolutions++; return Task.FromResult(tool); });

        var context = await service.PrepareAsync(
            Options(), TestContext.Current.CancellationToken);

        Assert.Equal(1, buildSelections);
        Assert.Equal(1, toolResolutions);
        Assert.Same(build, context.Build);
        Assert.Same(profile, context.Profile);
        Assert.Same(policy, context.Policy);
        Assert.Same(tool, context.Tool);
        Assert.Equal(
            ExtractionRecipeId.Create(new ExtractionRecipe(
                buildId, toolInstanceId, ProfileDigest, profile.Profile.AdapterVersion,
                profile.Profile.ExtractionSchemaVersion)),
            context.RecipeId);
    }

    [Fact]
    public async Task PrepareAsync_RecipeExcludesPolicyAndGamePath()
    {
        var buildId = new string('0', 64);
        var toolInstanceId = new string('c', 64);
        var build = new GameBuild(buildId, "assembly", "metadata", DateTimeOffset.UtcNow, IsValid: true);
        var profile = ResolvedProfile();
        var tool = ResolvedTool(toolInstanceId, ToolTrustLevel.ManagedPinned);

        ExtractionPreparationService Service(ResolvedValidationPolicy policy) =>
            new((_, _) => Task.FromResult(build), _ => profile, _ => policy, (_, _) => Task.FromResult(tool));

        var withPolicyA = await Service(ResolvedPolicy(PolicyDigest)).PrepareAsync(
            Options(gamePath: "C:/game-one"), TestContext.Current.CancellationToken);
        var withPolicyB = await Service(ResolvedPolicy(OtherPolicyDigest)).PrepareAsync(
            Options(gamePath: "C:/some-other-game"), TestContext.Current.CancellationToken);

        Assert.Equal(withPolicyA.RecipeId, withPolicyB.RecipeId);
        Assert.NotEqual(withPolicyA.Policy.PolicyDigest, withPolicyB.Policy.PolicyDigest);
    }

    // --- Task 8 Step 1: decision-order workflow ---

    [Fact]
    public async Task RunAsync_OneCurrentPolicyOutput_ReturnsIntegrityVerifiedNoOp()
    {
        using var fixture = await WorkflowFixture.CreateAsync(TestContext.Current.CancellationToken);
        var extractionId = await fixture.SeedValidatedExtractionAsync(
            new string('1', 32), [1, 2, 3, 4],
            PolicyDigest, autoPrefer: true, storeSubjectAndReport: true,
            TestContext.Current.CancellationToken);

        var result = await fixture.CreateWorkflow().RunAsync(
            fixture.Options, TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.ProcessInvocations);
        Assert.True(result.ReusedExistingExtraction);
        Assert.False(result.ProcessWasRun);
        Assert.False(result.ValidationWasRun);
        Assert.True(result.IsAuthoritative);
        Assert.Equal(extractionId, result.ExtractionId);
        Assert.Null(result.AttemptId);
    }

    [Fact]
    public async Task RunAsync_MultipleRecipeOutputsExactlyOnePreferred_PreferredDisambiguatesReuse()
    {
        using var fixture = await WorkflowFixture.CreateAsync(TestContext.Current.CancellationToken);
        var preferredId = await fixture.SeedValidatedExtractionAsync(
            new string('1', 32), [1, 1, 1, 1],
            OtherPolicyDigest, autoPrefer: true, storeSubjectAndReport: false,
            TestContext.Current.CancellationToken);
        await fixture.SeedValidatedExtractionAsync(
            new string('2', 32), [2, 2, 2, 2, 2],
            OtherPolicyDigest, autoPrefer: false, storeSubjectAndReport: false,
            TestContext.Current.CancellationToken);

        var result = await fixture.CreateWorkflow().RunAsync(
            fixture.Options, TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.ProcessInvocations);
        Assert.Equal(preferredId, result.ExtractionId);
        Assert.False(result.ProcessWasRun);
        Assert.True(result.ValidationWasRun);
    }

    [Fact]
    public async Task RunAsync_MultipleRecipeOutputsNoSinglePreferred_FailsRecipeOutputAmbiguous()
    {
        using var fixture = await WorkflowFixture.CreateAsync(TestContext.Current.CancellationToken);
        await fixture.SeedValidatedExtractionAsync(
            new string('1', 32), [1, 1, 1, 1],
            OtherPolicyDigest, autoPrefer: false, storeSubjectAndReport: false,
            TestContext.Current.CancellationToken);
        await fixture.SeedValidatedExtractionAsync(
            new string('2', 32), [2, 2, 2, 2, 2],
            OtherPolicyDigest, autoPrefer: false, storeSubjectAndReport: false,
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            fixture.CreateWorkflow().RunAsync(fixture.Options, TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.RecipeOutputAmbiguous, exception.Code);
        Assert.Equal(0, fixture.ProcessInvocations);
    }

    [Fact]
    public async Task RunAsync_PolicyChanged_PerformsValidationOnlyRevalidationWithoutCpp2Il()
    {
        using var fixture = await WorkflowFixture.CreateAsync(TestContext.Current.CancellationToken);
        var extractionId = await fixture.SeedValidatedExtractionAsync(
            new string('1', 32), [1, 2, 3, 4],
            OtherPolicyDigest, autoPrefer: true, storeSubjectAndReport: true,
            TestContext.Current.CancellationToken);

        var result = await fixture.CreateWorkflow().RunAsync(
            fixture.Options, TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.ProcessInvocations);
        Assert.Equal(extractionId, result.ExtractionId);
        Assert.False(result.ProcessWasRun);
        Assert.True(result.ValidationWasRun);
        Assert.False(result.ReusedExistingExtraction);
        Assert.True(result.IsAuthoritative);

        var attempts = await fixture.Repository.ListAttemptsAsync(
            WorkflowFixture.BuildId, TestContext.Current.CancellationToken);
        var revalidation = Assert.Single(
            attempts, attempt => attempt.ValidationSourceExtractionId == extractionId);
        Assert.Equal(ExtractionAttemptStatus.Succeeded, revalidation.Status);
        Assert.Null(revalidation.CandidateOutputPath);
        Assert.Equal(extractionId, revalidation.ResultExtractionId);
    }

    [Fact]
    public async Task RunAsync_ProcessCompletedCandidateWithoutValidatedOutput_ValidatesBeforeProcess()
    {
        using var fixture = await WorkflowFixture.CreateAsync(TestContext.Current.CancellationToken);
        var candidate = await fixture.CreateProcessCompletedCandidateAsync(
            new string('a', 32), PromotionTestData.BaseTime, TestContext.Current.CancellationToken);

        var result = await fixture.CreateWorkflow().RunAsync(
            fixture.Options, TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.ProcessInvocations);
        Assert.True(result.ValidationWasRun);
        Assert.False(result.ProcessWasRun);
        Assert.True(result.IsAuthoritative);
        Assert.Equal(candidate.AttemptId, result.AttemptId);
        Assert.NotEqual(string.Empty, result.ExtractionId);
    }

    [Fact]
    public async Task RunAsync_NoCandidate_RunsPhase3ProcessThenValidation()
    {
        using var fixture = await WorkflowFixture.CreateAsync(TestContext.Current.CancellationToken);

        var result = await fixture.CreateWorkflow().RunAsync(
            fixture.Options, TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.ProcessInvocations);
        Assert.True(result.ProcessWasRun);
        Assert.True(result.ValidationWasRun);
        Assert.True(result.IsAuthoritative);
        Assert.NotEqual(string.Empty, result.ExtractionId);
    }

    [Fact]
    public async Task RunAsync_ArchivedProcess_AuthoritativeResult_CertifiesSnapshot()
    {
        using var fixture = await WorkflowFixture.CreateAsync(TestContext.Current.CancellationToken);
        fixture.ProcessInputSource = ExtractionInputSource.ArchivedSnapshot;
        fixture.ProcessInputSnapshotId = new string('a', 64);

        var result = await fixture.CreateWorkflow().RunAsync(
            fixture.Options with { Retry = true }, TestContext.Current.CancellationToken);

        Assert.True(result.IsAuthoritative);
        Assert.Equal(ExtractionInputSource.ArchivedSnapshot, result.InputSource);
        Assert.Equal(new string('a', 64), result.InputSnapshotId);
        Assert.True(result.InputSnapshotReplayVerified);
        var certification = Assert.Single(fixture.ReplayCertifications);
        Assert.Equal(new string('a', 64), certification.SnapshotId);
        Assert.Equal(fixture.Context.Build.BuildId, certification.BuildId);
        // Certification uses the process attempt's pre-input manifest digest.
        Assert.Equal(new string('7', 64), certification.ManifestDigest);
    }

    [Fact]
    public async Task RunAsync_LiveProcess_DoesNotCertifyButReportsSnapshotId()
    {
        using var fixture = await WorkflowFixture.CreateAsync(TestContext.Current.CancellationToken);
        // A live retry that also archived its inputs reports the snapshot but never certifies.
        fixture.ProcessInputSource = ExtractionInputSource.Live;
        fixture.ProcessInputSnapshotId = new string('b', 64);

        var result = await fixture.CreateWorkflow().RunAsync(
            fixture.Options with { Retry = true }, TestContext.Current.CancellationToken);

        Assert.True(result.IsAuthoritative);
        Assert.Equal(ExtractionInputSource.Live, result.InputSource);
        Assert.Equal(new string('b', 64), result.InputSnapshotId);
        Assert.False(result.InputSnapshotReplayVerified);
        Assert.Empty(fixture.ReplayCertifications);
    }

    [Fact]
    public async Task RunAsync_CertificationDbFailure_PreservesExtractionAndReportsFailure()
    {
        using var fixture = await WorkflowFixture.CreateAsync(TestContext.Current.CancellationToken);
        fixture.ProcessInputSource = ExtractionInputSource.ArchivedSnapshot;
        fixture.ProcessInputSnapshotId = new string('c', 64);
        fixture.ReplayCertificationFailure =
            new InvalidOperationException("Injected certification failure.");

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            fixture.CreateWorkflow().RunAsync(
                fixture.Options with { Retry = true }, TestContext.Current.CancellationToken));

        Assert.Equal(ExtractionFailureCode.DatabasePromotionFailed, exception.Code);
        // The authoritative extraction was committed before certification and is preserved.
        var outputs = await fixture.Repository.ListValidatedExtractionsByRecipeAsync(
            PromotionTestData.RecipeId, TestContext.Current.CancellationToken);
        Assert.Single(outputs);
    }

    [Fact]
    public async Task RunAsync_Retry_RunsNewProcessEvenWhenReusableOutputExists()
    {
        using var fixture = await WorkflowFixture.CreateAsync(TestContext.Current.CancellationToken);
        var reusableBytes = await File.ReadAllBytesAsync(
            typeof(FixtureRoot).Assembly.Location, TestContext.Current.CancellationToken);
        var extractionId = await fixture.SeedValidatedExtractionAsync(
            new string('1', 32), reusableBytes, PolicyDigest,
            autoPrefer: true, storeSubjectAndReport: true, TestContext.Current.CancellationToken);

        var result = await fixture.CreateWorkflow().RunAsync(
            fixture.Options with { Retry = true }, TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.ProcessInvocations);
        Assert.True(result.ProcessWasRun);
        Assert.True(result.ValidationWasRun);
        Assert.Equal(extractionId, result.ExtractionId);
    }

    [Fact]
    public async Task RunAsync_MultipleCandidates_ConsumesNewestByCompletedThenAttemptIdDescending()
    {
        using var fixture = await WorkflowFixture.CreateAsync(TestContext.Current.CancellationToken);
        var older = PromotionTestData.BaseTime;
        var newer = PromotionTestData.BaseTime.AddMinutes(5);
        await fixture.CreateProcessCompletedCandidateAsync(
            new string('c', 32), older, TestContext.Current.CancellationToken);
        var expected = await fixture.CreateProcessCompletedCandidateAsync(
            new string('b', 32), newer, TestContext.Current.CancellationToken);
        await fixture.CreateProcessCompletedCandidateAsync(
            new string('a', 32), newer, TestContext.Current.CancellationToken);

        var result = await fixture.CreateWorkflow().RunAsync(
            fixture.Options, TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.ProcessInvocations);
        Assert.Equal(expected.AttemptId, result.AttemptId);
    }

    private static ExtractionOptions Options(string? gamePath = null) => new(
        BuildId: null,
        GamePath: gamePath,
        CustomCpp2IlPath: null,
        ProfileId: "default-profile",
        Retry: false,
        SnapshotInputs: false,
        KeepFailedArtifacts: false);

    private static ResolvedExtractionProfile ResolvedProfile() => new(
        new ExtractionProfile(
            SchemaVersion: 1,
            ProfileId: "default-profile",
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

    private static ResolvedValidationPolicy ResolvedPolicy(string policyDigest) => new(
        new ValidationPolicy(
            SchemaVersion: 1,
            PolicyId: "managed-assemblies-v1",
            PolicyVersion: 1,
            RequiredAssemblyIdentities: ["Assembly-CSharp"],
            MinimumManagedAssemblyCount: 1,
            MinimumTypeDefinitionCount: 1,
            MinimumMethodDefinitionCount: 1,
            MinimumTotalManagedBytes: 1,
            ComparativeWarningRelativeChange: 0.25,
            CatastrophicDecreaseRelativeChange: 0.80),
        policyDigest);

    private static ResolvedExtractionTool ResolvedTool(string toolInstanceId, ToolTrustLevel trust) => new(
        Definition: null!,
        new ToolInstance(
            toolInstanceId,
            "cpp2il",
            "test-version",
            "win-x64",
            trust,
            new string('d', 64),
            new string('e', 64),
            new string('f', 64),
            "C:/tools/Cpp2IL.exe",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            ToolInstallationStatus.Verified),
        "C:/tools/Cpp2IL.exe",
        []);

    private sealed class WorkflowFixture : IDisposable
    {
        public const string BuildId = PromotionTestData.BuildId;

        private readonly Sha256FileHasher _hasher = new();
        private readonly ValidatedExtractionDocumentStore _documentStore = new();
        private readonly PromotionJournalStore _journalStore = new();
        private readonly AttemptDocumentStore _attemptDocumentStore = new();

        private WorkflowFixture(string atlasRoot, SqliteAtlasRepository repository)
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
            Context = new PreparedExtractionContext(
                new GameBuild(BuildId, "assembly-a", "metadata-a", PromotionTestData.BaseTime, IsValid: true),
                ResolvedProfile(),
                ResolvedPolicy(PolicyDigest),
                ResolvedTool(PromotionTestData.ToolInstanceId, ToolTrustLevel.ManagedPinned),
                PromotionTestData.RecipeId);
        }

        public string AtlasRoot { get; }
        public SqliteAtlasRepository Repository { get; }
        public FixedTimeProvider Clock { get; }
        public ValidatedExtractionIntegrityVerifier IntegrityVerifier { get; }
        public CandidateOutputInspector CandidateInspector { get; }
        public ExtractionValidationService ValidationService { get; }
        public PreparedExtractionContext Context { get; }
        public int ProcessInvocations { get; private set; }

        public ExtractionOptions Options => new(
            BuildId,
            GamePath: null,
            CustomCpp2IlPath: null,
            ProfileId: "default-profile",
            Retry: false,
            SnapshotInputs: false,
            KeepFailedArtifacts: false);

        public static async Task<WorkflowFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var atlasRoot = Path.Combine(
                Path.GetTempPath(), $"s1atlas-workflow-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(atlasRoot);
            var repository = await PromotionTestData.CreateRepositoryAsync(atlasRoot, cancellationToken);
            return new WorkflowFixture(atlasRoot, repository);
        }

        public ExtractionInputSource ProcessInputSource { get; set; } =
            ExtractionInputSource.Live;

        public string? ProcessInputSnapshotId { get; set; }

        public List<(string SnapshotId, string BuildId, string ManifestDigest)>
            ReplayCertifications
        { get; } = [];

        public Exception? ReplayCertificationFailure { get; set; }

        public ValidatedExtractionWorkflow CreateWorkflow() => new(
            AtlasRoot,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            (_, _) => Task.FromResult(Context),
            Repository,
            IntegrityVerifier,
            _documentStore,
            ValidationService,
            RunPreparedProcessAsync,
            MarkReplayVerifiedAsync,
            Clock);

        private Task MarkReplayVerifiedAsync(
            string snapshotId,
            string buildId,
            string manifestDigest,
            DateTimeOffset verifiedAtUtc,
            CancellationToken cancellationToken)
        {
            if (ReplayCertificationFailure is not null)
            {
                throw ReplayCertificationFailure;
            }

            ReplayCertifications.Add((snapshotId, buildId, manifestDigest));
            return Task.CompletedTask;
        }

        public async Task<string> SeedValidatedExtractionAsync(
            string attemptId,
            byte[] candidateBytes,
            string policyDigest,
            bool autoPrefer,
            bool storeSubjectAndReport,
            CancellationToken cancellationToken)
        {
            var candidate = await PromotionTestData.CreateValidatingCandidateAsync(
                Repository, AtlasRoot, attemptId, PromotionTestData.RecipeId, candidateBytes, cancellationToken);
            var report = PromotionTestData.BuildReport(
                candidate.Attempt,
                candidate.Digest,
                candidate.Statistics,
                ValidationOutcome.Valid,
                [],
                preferenceEligible: true,
                validatedAtUtc: PromotionTestData.BaseTime,
                subjectExtractionId: storeSubjectAndReport ? candidate.ExtractionId : null) with
            {
                PolicyDigest = policyDigest
            };

            var promoter = new ValidatedExtractionPromoter(
                AtlasRoot, Repository, _documentStore, IntegrityVerifier, _journalStore,
                CandidateInspector, Clock);
            await promoter.PromoteAsync(
                new ValidatedExtractionPromotionRequest(
                    candidate.Attempt,
                    report,
                    candidate.Manifest,
                    ToolTrustLevel.ManagedPinned,
                    DeduplicationTarget: null,
                    autoPrefer ? ExtractionPreferenceReason.ManagedAutomatic : null,
                    KeepInvalidOutput: false),
                cancellationToken);

            if (storeSubjectAndReport)
            {
                var attemptPaths = OwnedValidatedExtractionPaths.ForAttempt(AtlasRoot, BuildId, attemptId);
                await _documentStore.WriteAttemptValidationReportAsync(attemptPaths, report, cancellationToken);
            }

            return candidate.ExtractionId;
        }

        public async Task<ExtractionAttempt> CreateProcessCompletedCandidateAsync(
            string attemptId,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken)
        {
            var ownedCandidateRoot = OwnedValidatedExtractionPaths
                .ForAttempt(AtlasRoot, BuildId, attemptId).AttemptCandidatePath!;
            Directory.CreateDirectory(ownedCandidateRoot);
            File.Copy(
                typeof(FixtureRoot).Assembly.Location,
                Path.Combine(ownedCandidateRoot, "Assembly-CSharp.dll"),
                overwrite: true);

            var created = BuildAttempt(attemptId, ownedCandidateRoot);
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
                CompletedAtUtc = completedAtUtc,
                CandidateOutputPath = ownedCandidateRoot,
                PreInputManifestDigest = new string('7', 64),
                PostInputManifestDigest = new string('7', 64)
            };
            await Repository.TransitionAttemptAsync(
                processCompleted, ExtractionAttemptStatus.Running, cancellationToken);
            return processCompleted;
        }

        private Task<ExtractionOperationResult> RunPreparedProcessAsync(
            PreparedExtractionContext context,
            ExtractionOptions options,
            CancellationToken cancellationToken)
        {
            ProcessInvocations++;
            return RunProcessCoreAsync(context, cancellationToken);
        }

        private async Task<ExtractionOperationResult> RunProcessCoreAsync(
            PreparedExtractionContext context,
            CancellationToken cancellationToken)
        {
            var candidate = await CreateProcessCompletedCandidateAsync(
                Guid.NewGuid().ToString("N"), PromotionTestData.BaseTime, cancellationToken);
            return new ExtractionOperationResult(
                candidate,
                context.Tool.Instance,
                ProcessInputSource,
                ProcessInputSnapshotId,
                ProcessWasRun: true,
                IsAuthoritative: false);
        }

        private static ExtractionAttempt BuildAttempt(string attemptId, string candidateRoot)
        {
            var attemptRoot = Path.GetDirectoryName(candidateRoot)!;
            return new ExtractionAttempt(
                AttemptId: attemptId,
                RecipeId: PromotionTestData.RecipeId,
                BuildId: BuildId,
                ToolInstanceId: PromotionTestData.ToolInstanceId,
                ProfileId: "default-profile",
                ProfileVersion: 1,
                ProfileDigest: ProfileDigest,
                ValidationPolicyId: "managed-assemblies-v1",
                ValidationPolicyVersion: 1,
                ValidationPolicyDigest: PolicyDigest,
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
