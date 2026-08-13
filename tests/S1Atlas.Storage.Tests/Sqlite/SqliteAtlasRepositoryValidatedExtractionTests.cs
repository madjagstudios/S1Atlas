using Microsoft.Data.Sqlite;
using S1Atlas.Core.Builds;
using S1Atlas.Core.Environment;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Storage.Tests.Sqlite;

/// <summary>
/// Phase 4 validated-extraction persistence: atomic commit/link/failure/revalidation
/// commands, preference pointer/event bookkeeping, and strict row mapping.
/// </summary>
public sealed class SqliteAtlasRepositoryValidatedExtractionTests : IAsyncDisposable
{
    private static readonly DateTimeOffset BaseTime =
        DateTimeOffset.Parse("2026-08-13T05:00:00Z");
    private const string BuildId = "build-a";
    private const string ToolInstanceId = "tool-instance-1";
    private static readonly string RecipeId1 = new('1', 64);
    private static readonly string RecipeId2 = new('2', 64);

    private readonly string _temporaryDirectory;
    private readonly string _databasePath;
    private readonly string _backupDirectory;

    public SqliteAtlasRepositoryValidatedExtractionTests()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"s1atlas-validated-extraction-tests-{Guid.NewGuid():N}");
        _databasePath = Path.Combine(_temporaryDirectory, "atlas.db");
        _backupDirectory = Path.Combine(_temporaryDirectory, "backups");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task CommitValidatedExtractionAsync_InsertsExtractionArtifactsValidationAndIssuesAtomically()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(ct);
        var manifest = CreateManifest(
            ManagedEntry("reconstructed/z-other/Extra.dll", 40, new string('7', 64), "Extra", 2, 2),
            ManagedEntry("reconstructed/Assembly-CSharp.dll", 100, new string('1', 64), "Assembly-CSharp", 10, 20),
            NativeEntry("reconstructed/native/GameAssembly.dll", 50, new string('2', 64)));
        var digest = ArtifactManifestFingerprint.Create(manifest);
        var extractionId = ExtractionId.Create(RecipeId1, digest);
        var validating = await AdvanceToValidatingWithCandidateAsync(
            repository, "00000000000000000000000000000101", RecipeId1, "C:\\attempts\\candidate-1", ct);
        var issues = new[]
        {
            new ValidationIssue(
                ValidationIssueSeverity.Warning, "DuplicateAssemblyIdentity",
                "informational duplicate", "reconstructed/z-other/Extra.dll", PreferenceBlocking: false)
        };
        var report = BuildReport(
            validating.AttemptId, ValidationSubjectKind.CandidateOutput, subjectExtractionId: null,
            digest, ValidationOutcome.ValidWithWarnings, Statistics(3, 2, 2, 12, 22, 0, 0, 0, 190, 140),
            baselineExtractionId: null, issues, preferenceEligible: true, BaseTime.AddMinutes(10));
        var extraction = BuildValidatedExtraction(
            extractionId, RecipeId1, validating.AttemptId, digest,
            Statistics(3, 2, 2, 12, 22, 0, 0, 0, 190, 140));
        var succeeded = validating with
        {
            Status = ExtractionAttemptStatus.Succeeded,
            CompletedAtUtc = BaseTime.AddMinutes(10),
            ResultExtractionId = extractionId
        };
        var promotion = new ValidatedExtractionPromotion(
            succeeded, extraction, manifest, report, ExtractionPreferenceReason.ManagedAutomatic);

        await repository.CommitValidatedExtractionAsync(promotion, ct);

        Assert.Equal(1L, await CountAsync("validated_extractions", ct));
        Assert.Equal(3L, await CountAsync("extraction_artifacts", ct));
        Assert.Equal(1L, await CountAsync("extraction_validation_results", ct));
        Assert.Equal(1L, await CountAsync("extraction_validation_issues", ct));
        Assert.Equal(
            ExtractionAttemptStatus.Succeeded,
            (await repository.GetAttemptAsync(validating.AttemptId, ct))!.Status);
        Assert.Equal(
            extractionId,
            (await repository.GetAttemptAsync(validating.AttemptId, ct))!.ResultExtractionId);
    }

    [Fact]
    public async Task GetExtractionArtifactsAsync_ReturnsDeterministicPathOrderRegardlessOfManifestOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(ct);
        var (extractionId, _, _) = await CommitHappyPathExtractionAsync(
            repository, "00000000000000000000000000000102", RecipeId1, ct);

        var artifacts = await repository.GetExtractionArtifactsAsync(extractionId, ct);

        Assert.Equal(
            artifacts.Select(entry => entry.RelativePath).OrderBy(path => path, StringComparer.Ordinal),
            artifacts.Select(entry => entry.RelativePath));
        Assert.Equal(3, artifacts.Count);
    }

    [Fact]
    public async Task GetValidatedExtractionAsync_ReconstructsPerAssemblyStatisticsFromArtifacts()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(ct);
        var (extractionId, _, _) = await CommitHappyPathExtractionAsync(
            repository, "00000000000000000000000000000103", RecipeId1, ct);

        var extraction = await repository.GetValidatedExtractionAsync(extractionId, ct);

        Assert.NotNull(extraction);
        var assembly = Assert.Single(extraction.Statistics.Assemblies);
        Assert.Equal("Assembly-CSharp", assembly.AssemblyName);
        Assert.Equal(1, assembly.FileCount);
        Assert.Equal(100L, assembly.ManagedBytes);
        Assert.Equal(10, assembly.TypeDefinitionCount);
        Assert.Equal(20, assembly.MethodDefinitionCount);
    }

    [Fact]
    public async Task CommitValidatedExtractionAsync_FailedArtifactInsertRollsBackExtractionAttemptAndPreference()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(ct);
        await CreateFailingArtifactTriggerAsync(ct);
        var manifest = CreateManifest(
            ManagedEntry("reconstructed/Assembly-CSharp.dll", 100, new string('1', 64), "Assembly-CSharp", 10, 20),
            NativeEntry("reconstructed/bad.dll", 50, new string('2', 64)));
        var digest = ArtifactManifestFingerprint.Create(manifest);
        var extractionId = ExtractionId.Create(RecipeId1, digest);
        var validating = await AdvanceToValidatingWithCandidateAsync(
            repository, "00000000000000000000000000000104", RecipeId1, "C:\\attempts\\candidate-1", ct);
        var report = BuildReport(
            validating.AttemptId, ValidationSubjectKind.CandidateOutput, subjectExtractionId: null,
            digest, ValidationOutcome.Valid, Statistics(2, 2, 1, 10, 20, 0, 0, 0, 150, 100),
            baselineExtractionId: null, [], preferenceEligible: true, BaseTime.AddMinutes(10));
        var extraction = BuildValidatedExtraction(
            extractionId, RecipeId1, validating.AttemptId, digest,
            Statistics(2, 2, 1, 10, 20, 0, 0, 0, 150, 100));
        var succeeded = validating with
        {
            Status = ExtractionAttemptStatus.Succeeded,
            CompletedAtUtc = BaseTime.AddMinutes(10),
            ResultExtractionId = extractionId
        };
        var promotion = new ValidatedExtractionPromotion(
            succeeded, extraction, manifest, report, ExtractionPreferenceReason.ManagedAutomatic);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CommitValidatedExtractionAsync(promotion, ct));

        Assert.Equal(0L, await CountAsync("validated_extractions", ct));
        Assert.Equal(0L, await CountAsync("extraction_artifacts", ct));
        Assert.Equal(0L, await CountAsync("extraction_validation_results", ct));
        Assert.Equal(0L, await CountAsync("preferred_extractions", ct));
        Assert.Equal(
            ExtractionAttemptStatus.Validating,
            (await repository.GetAttemptAsync(validating.AttemptId, ct))!.Status);
    }

    [Fact]
    public async Task CommitValidatedExtractionAsync_SameRecipeAndManifestCannotDuplicateBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(ct);
        var (extractionId, manifest, digest) = await CommitHappyPathExtractionAsync(
            repository, "00000000000000000000000000000105", RecipeId1, ct);
        var validating = await AdvanceToValidatingWithCandidateAsync(
            repository, "00000000000000000000000000000106", RecipeId1, "C:\\attempts\\candidate-2", ct);
        var report = BuildReport(
            validating.AttemptId, ValidationSubjectKind.CandidateOutput, subjectExtractionId: null,
            digest, ValidationOutcome.Valid, Statistics(3, 2, 2, 12, 22, 0, 0, 0, 190, 140),
            baselineExtractionId: null, [], preferenceEligible: true, BaseTime.AddMinutes(20));
        var duplicateExtraction = BuildValidatedExtraction(
            extractionId, RecipeId1, validating.AttemptId, digest,
            Statistics(3, 2, 2, 12, 22, 0, 0, 0, 190, 140));
        var succeeded = validating with
        {
            Status = ExtractionAttemptStatus.Succeeded,
            CompletedAtUtc = BaseTime.AddMinutes(20),
            ResultExtractionId = extractionId
        };
        var promotion = new ValidatedExtractionPromotion(
            succeeded, duplicateExtraction, manifest, report, ExtractionPreferenceReason.ManagedAutomatic);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CommitValidatedExtractionAsync(promotion, ct));

        Assert.Equal(1L, await CountAsync("validated_extractions", ct));
    }

    [Fact]
    public async Task LinkAttemptToValidatedExtractionAsync_StoresValidationAndSucceedsWithoutANewExtractionRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(ct);
        var (extractionId, _, digest) = await CommitHappyPathExtractionAsync(
            repository, "00000000000000000000000000000107", RecipeId1, ct);
        var validating = await AdvanceToValidatingWithCandidateAsync(
            repository, "00000000000000000000000000000108", RecipeId1, "C:\\attempts\\candidate-3", ct);
        var existingExtraction = await repository.GetValidatedExtractionAsync(extractionId, ct);
        var report = BuildReport(
            validating.AttemptId, ValidationSubjectKind.CandidateOutput, subjectExtractionId: null,
            digest, ValidationOutcome.Valid, Statistics(3, 2, 2, 12, 22, 0, 0, 0, 190, 140),
            baselineExtractionId: extractionId, [], preferenceEligible: true, BaseTime.AddMinutes(30));
        var succeeded = validating with
        {
            Status = ExtractionAttemptStatus.Succeeded,
            CompletedAtUtc = BaseTime.AddMinutes(30),
            ResultExtractionId = extractionId
        };

        await repository.LinkAttemptToValidatedExtractionAsync(
            new ValidationPersistence(succeeded, report), existingExtraction!,
            ExtractionAttemptStatus.Validating, ct);

        Assert.Equal(1L, await CountAsync("validated_extractions", ct));
        Assert.Equal(2L, await CountAsync("extraction_validation_results", ct));
        var reloaded = await repository.GetAttemptAsync(validating.AttemptId, ct);
        Assert.Equal(ExtractionAttemptStatus.Succeeded, reloaded!.Status);
        Assert.Equal(extractionId, reloaded.ResultExtractionId);
    }

    [Fact]
    public async Task SaveValidationFailureAsync_StoresSummaryAndIssuesAndTransitionsFailedWithNoExtractionRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(ct);
        var validating = await AdvanceToValidatingWithCandidateAsync(
            repository, "00000000000000000000000000000109", RecipeId1, "C:\\attempts\\candidate-4", ct);
        var issues = new[]
        {
            new ValidationIssue(
                ValidationIssueSeverity.Error, "NoManagedAssembliesProduced",
                "no managed assembly", null, PreferenceBlocking: true),
            new ValidationIssue(
                ValidationIssueSeverity.Warning, "EmptyArtifact",
                "artifact was empty", "reconstructed/empty.dll", PreferenceBlocking: false)
        };
        var report = BuildReport(
            validating.AttemptId, ValidationSubjectKind.CandidateOutput, subjectExtractionId: null,
            new string('9', 64), ValidationOutcome.Invalid, Statistics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            baselineExtractionId: null, issues, preferenceEligible: false, BaseTime.AddMinutes(5));
        var failed = validating with
        {
            Status = ExtractionAttemptStatus.Failed,
            CompletedAtUtc = BaseTime.AddMinutes(5),
            FailureStage = ExtractionFailureStage.AssemblyValidation,
            FailureCode = ExtractionFailureCode.NoManagedAssembliesProduced,
            FailureMessage = "No managed assemblies were produced."
        };

        await repository.SaveValidationFailureAsync(
            new ValidationPersistence(failed, report), ExtractionAttemptStatus.Validating, ct);

        Assert.Equal(0L, await CountAsync("validated_extractions", ct));
        Assert.Equal(1L, await CountAsync("extraction_validation_results", ct));
        Assert.Equal(2L, await CountAsync("extraction_validation_issues", ct));
        var reloaded = await repository.GetAttemptAsync(validating.AttemptId, ct);
        Assert.Equal(ExtractionAttemptStatus.Failed, reloaded!.Status);
        Assert.Null(reloaded.ResultExtractionId);
    }

    [Fact]
    public async Task SaveRevalidationAsync_StoresPolicySummaryWithoutChangingExtractionRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(ct);
        var (extractionId, _, digest) = await CommitHappyPathExtractionAsync(
            repository, "00000000000000000000000000000110", RecipeId1, ct);
        var before = await repository.GetValidatedExtractionAsync(extractionId, ct);
        var revalidating = await CreateValidatingRevalidationAttemptAsync(
            repository, "00000000000000000000000000000111", extractionId, ct);
        var report = BuildReport(
            revalidating.AttemptId, ValidationSubjectKind.ValidatedExtraction, extractionId,
            digest, ValidationOutcome.Valid, Statistics(3, 2, 2, 12, 22, 0, 0, 0, 190, 140),
            baselineExtractionId: null, [], preferenceEligible: true, BaseTime.AddMinutes(40),
            policyDigest: new string('f', 64));
        var succeeded = revalidating with
        {
            Status = ExtractionAttemptStatus.Succeeded,
            CompletedAtUtc = BaseTime.AddMinutes(40),
            ResultExtractionId = extractionId
        };

        await repository.SaveRevalidationAsync(
            new ValidationPersistence(succeeded, report), ExtractionAttemptStatus.Validating, ct);

        Assert.Equal(1L, await CountAsync("validated_extractions", ct));
        var after = await repository.GetValidatedExtractionAsync(extractionId, ct);
        Assert.NotNull(before);
        Assert.NotNull(after);
        // Compare scalar fields via record equality, and the assembly breakdown
        // separately: xUnit's default comparer prefers the record's generated
        // IEquatable<T>.Equals, which compares IReadOnlyList<T> fields by reference,
        // so two structurally-identical-but-distinct List<T> instances would
        // otherwise report as unequal even though nothing changed.
        Assert.Equal(
            before with { Statistics = before.Statistics with { Assemblies = [] } },
            after with { Statistics = after.Statistics with { Assemblies = [] } });
        Assert.Equal(before.Statistics.Assemblies, after.Statistics.Assemblies);
        var reloaded = await repository.GetAttemptAsync(revalidating.AttemptId, ct);
        Assert.Equal(ExtractionAttemptStatus.Succeeded, reloaded!.Status);
    }

    [Fact]
    public async Task GetLatestValidationResultAsync_ReturnsTheLatestPolicyResultDeterministically()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(ct);
        var (extractionId, _, digest) = await CommitHappyPathExtractionAsync(
            repository, "00000000000000000000000000000112", RecipeId1, ct);
        var policyDigest = new string('e', 64);
        await RevalidateAsync(
            repository, "00000000000000000000000000000113", extractionId, digest, policyDigest,
            BaseTime.AddMinutes(50), ct);
        await RevalidateAsync(
            repository, "00000000000000000000000000000114", extractionId, digest, policyDigest,
            BaseTime.AddMinutes(60), ct);

        var latest = await repository.GetLatestValidationResultAsync(extractionId, policyDigest, ct);

        Assert.NotNull(latest);
        Assert.Equal("00000000000000000000000000000114", latest.AttemptId);
        Assert.Equal(BaseTime.AddMinutes(60), latest.ValidatedAtUtc);
    }

    [Fact]
    public async Task SetPreferredExtractionAsync_WritesPointerAndEventAtomically()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(ct);
        var (extraction1Id, _, _) = await CommitHappyPathExtractionAsync(
            repository, "00000000000000000000000000000115", RecipeId1, ct);
        var (extraction2Id, _, _) = await CommitHappyPathExtractionAsync(
            repository, "00000000000000000000000000000116", RecipeId2, ct,
            preferenceReason: null);

        await repository.SetPreferredExtractionAsync(
            new PreferredExtraction(BuildId, extraction2Id, BaseTime.AddMinutes(70), ExtractionPreferenceReason.ManualPromotion),
            ct);

        var preferred = await repository.GetPreferredExtractionAsync(BuildId, ct);
        Assert.NotNull(preferred);
        Assert.Equal(extraction2Id, preferred.ExtractionId);
        Assert.Equal(ExtractionPreferenceReason.ManualPromotion, preferred.SelectionReason);
        Assert.Equal(2L, await CountAsync("extraction_preference_events", ct));
        Assert.Equal(
            extraction1Id,
            await ScalarStringAsync(
                "SELECT previous_extraction_id FROM extraction_preference_events " +
                "ORDER BY selected_at_utc DESC LIMIT 1;",
                ct));
    }

    [Fact]
    public async Task SetPreferredExtractionAsync_ExtractionFromDifferentBuild_ThrowsWithoutMutation()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(ct);
        var (extractionId, _, _) = await CommitHappyPathExtractionAsync(
            repository, "00000000000000000000000000000117", RecipeId1, ct, preferenceReason: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SetPreferredExtractionAsync(
                new PreferredExtraction(
                    "other-build", extractionId, BaseTime.AddMinutes(71),
                    ExtractionPreferenceReason.ManualPromotion),
                ct));

        Assert.Equal(0L, await CountAsync("preferred_extractions", ct));
        Assert.Equal(0L, await CountAsync("extraction_preference_events", ct));
    }

    [Fact]
    public async Task ClearPreferredExtractionAsync_RequiresExpectedPointerAndWritesNullTargetEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(ct);
        var (extractionId, _, _) = await CommitHappyPathExtractionAsync(
            repository, "00000000000000000000000000000118", RecipeId1, ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.ClearPreferredExtractionAsync(
                BuildId, "stale-extraction-id", ExtractionPreferenceReason.IntegrityInvalidated,
                BaseTime.AddMinutes(80), ct));
        Assert.NotNull(await repository.GetPreferredExtractionAsync(BuildId, ct));

        await repository.ClearPreferredExtractionAsync(
            BuildId, extractionId, ExtractionPreferenceReason.IntegrityInvalidated,
            BaseTime.AddMinutes(81), ct);

        Assert.Null(await repository.GetPreferredExtractionAsync(BuildId, ct));
        Assert.Equal(
            2L,
            await CountAsync("extraction_preference_events", ct));
        Assert.Null(
            await ScalarStringAsync(
                "SELECT new_extraction_id FROM extraction_preference_events " +
                "ORDER BY selected_at_utc DESC LIMIT 1;",
                ct));
    }

    [Fact]
    public async Task GetValidatedExtractionAsync_CorruptTrustLevelRow_FailsClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(ct);
        var (extractionId, _, _) = await CommitHappyPathExtractionAsync(
            repository, "00000000000000000000000000000119", RecipeId1, ct);
        await ExecuteAsync(
            "UPDATE validated_extractions SET trust_level = 'managedpinned' WHERE extraction_id = $id;",
            ("$id", extractionId), ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.GetValidatedExtractionAsync(extractionId, ct));
    }

    [Fact]
    public async Task GetLatestValidationResultAsync_CorruptOutcomeRow_FailsClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await CreateInitializedRepositoryAsync(ct);
        var (extractionId, _, digest) = await CommitHappyPathExtractionAsync(
            repository, "00000000000000000000000000000120", RecipeId1, ct);
        var policyDigest = new string('d', 64);
        await RevalidateAsync(
            repository, "00000000000000000000000000000121", extractionId, digest, policyDigest,
            BaseTime.AddMinutes(90), ct);
        await ExecuteAsync(
            "UPDATE extraction_validation_results SET outcome = 'valid' " +
            "WHERE attempt_id = '00000000000000000000000000000121';",
            ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.GetLatestValidationResultAsync(extractionId, policyDigest, ct));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private async Task<(string ExtractionId, ArtifactManifest Manifest, string ManifestDigest)>
        CommitHappyPathExtractionAsync(
            SqliteAtlasRepository repository,
            string attemptId,
            string recipeId,
            CancellationToken ct,
            ExtractionPreferenceReason? preferenceReason = ExtractionPreferenceReason.ManagedAutomatic)
    {
        var manifest = CreateManifest(
            ManagedEntry(
                "reconstructed/Assembly-CSharp.dll", 100, new string('1', 64),
                "Assembly-CSharp", 10, 20),
            NativeEntry("reconstructed/native/GameAssembly.dll", 50, new string('2', 64)),
            NativeEntry("reconstructed/z-other/Extra.dll", 40, new string('3', 64)));
        var digest = ArtifactManifestFingerprint.Create(manifest);
        var extractionId = ExtractionId.Create(recipeId, digest);
        var validating = await AdvanceToValidatingWithCandidateAsync(
            repository, attemptId, recipeId, $"C:\\attempts\\{attemptId}\\candidate-output", ct);
        var statistics = Statistics(3, 3, 1, 10, 20, 0, 0, 0, 190, 100);
        var report = BuildReport(
            validating.AttemptId, ValidationSubjectKind.CandidateOutput, subjectExtractionId: null,
            digest, ValidationOutcome.Valid, statistics, baselineExtractionId: null, [],
            preferenceEligible: true, BaseTime.AddMinutes(1));
        var extraction = BuildValidatedExtraction(
            extractionId, recipeId, validating.AttemptId, digest, statistics);
        var succeeded = validating with
        {
            Status = ExtractionAttemptStatus.Succeeded,
            CompletedAtUtc = BaseTime.AddMinutes(1),
            ResultExtractionId = extractionId
        };
        var promotion = new ValidatedExtractionPromotion(
            succeeded, extraction, manifest, report, preferenceReason);

        await repository.CommitValidatedExtractionAsync(promotion, ct);

        return (extractionId, manifest, digest);
    }

    private async Task RevalidateAsync(
        SqliteAtlasRepository repository,
        string attemptId,
        string extractionId,
        string manifestDigest,
        string policyDigest,
        DateTimeOffset validatedAtUtc,
        CancellationToken ct)
    {
        var revalidating = await CreateValidatingRevalidationAttemptAsync(
            repository, attemptId, extractionId, ct);
        var report = BuildReport(
            revalidating.AttemptId, ValidationSubjectKind.ValidatedExtraction, extractionId,
            manifestDigest, ValidationOutcome.Valid, Statistics(3, 3, 1, 10, 20, 0, 0, 0, 190, 100),
            baselineExtractionId: null, [], preferenceEligible: true, validatedAtUtc, policyDigest);
        var succeeded = revalidating with
        {
            Status = ExtractionAttemptStatus.Succeeded,
            CompletedAtUtc = validatedAtUtc,
            ResultExtractionId = extractionId
        };

        await repository.SaveRevalidationAsync(
            new ValidationPersistence(succeeded, report), ExtractionAttemptStatus.Validating, ct);
    }

    private async Task<ExtractionAttempt> AdvanceToValidatingWithCandidateAsync(
        SqliteAtlasRepository repository,
        string attemptId,
        string recipeId,
        string candidateOutputPath,
        CancellationToken ct)
    {
        var created = CreateAttempt(attemptId, recipeId);
        await repository.CreateAttemptAsync(created, ct);
        var preparing = created with
        {
            Status = ExtractionAttemptStatus.Preparing,
            StartedAtUtc = BaseTime
        };
        await repository.TransitionAttemptAsync(preparing, ExtractionAttemptStatus.Created, ct);
        var running = preparing with { Status = ExtractionAttemptStatus.Running, ProcessId = 100 };
        await repository.TransitionAttemptAsync(running, ExtractionAttemptStatus.Preparing, ct);
        var processCompleted = running with
        {
            Status = ExtractionAttemptStatus.ProcessCompleted,
            ProcessExitCode = 0,
            CandidateOutputPath = candidateOutputPath
        };
        await repository.TransitionAttemptAsync(processCompleted, ExtractionAttemptStatus.Running, ct);
        var validating = processCompleted with { Status = ExtractionAttemptStatus.Validating };
        await repository.TransitionAttemptAsync(
            validating, ExtractionAttemptStatus.ProcessCompleted, ct);
        return validating;
    }

    private async Task<ExtractionAttempt> CreateValidatingRevalidationAttemptAsync(
        SqliteAtlasRepository repository,
        string attemptId,
        string validationSourceExtractionId,
        CancellationToken ct)
    {
        var created = CreateAttempt(attemptId, recipeId: null);
        await repository.CreateAttemptAsync(created, ct);
        var validating = created with
        {
            Status = ExtractionAttemptStatus.Validating,
            ValidationSourceExtractionId = validationSourceExtractionId
        };
        await repository.TransitionAttemptAsync(validating, ExtractionAttemptStatus.Created, ct);
        return validating;
    }

    private static ExtractionAttempt CreateAttempt(string attemptId, string? recipeId) => new(
        AttemptId: attemptId,
        RecipeId: recipeId,
        BuildId: BuildId,
        ToolInstanceId: ToolInstanceId,
        ProfileId: "default-profile",
        ProfileVersion: 1,
        ProfileDigest: new string('a', 64),
        ValidationPolicyId: "managed-assemblies-v1",
        ValidationPolicyVersion: 1,
        ValidationPolicyDigest: new string('b', 64),
        AdapterVersion: 1,
        ExtractionSchemaVersion: 1,
        InputSource: ExtractionInputSource.Live,
        InputSnapshotId: null,
        Status: ExtractionAttemptStatus.Created,
        CreatedAtUtc: BaseTime,
        StartedAtUtc: null,
        CompletedAtUtc: null,
        PreInputManifestDigest: null,
        PostInputManifestDigest: null,
        WorkingPath: $"C:\\attempts\\{attemptId}\\work",
        StandardOutputPath: $"C:\\attempts\\{attemptId}\\logs\\stdout.log",
        StandardErrorPath: $"C:\\attempts\\{attemptId}\\logs\\stderr.log",
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

    private static ArtifactManifest CreateManifest(params ArtifactManifestEntry[] entries) =>
        new(SchemaVersion: 1, Entries: entries);

    private static ArtifactManifestEntry ManagedEntry(
        string path, long size, string sha256, string assemblyName, int typeCount, int methodCount) =>
        new(path, ArtifactKind.ManagedAssembly, size, sha256, assemblyName, assemblyName,
            typeCount, methodCount, 0, 0, 0);

    private static ArtifactManifestEntry NativeEntry(string path, long size, string sha256) =>
        new(path, ArtifactKind.NativeLibrary, size, sha256, null, null, null, null, null, null, null);

    private static ExtractionStatistics Statistics(
        int artifactCount, int libraryCount, int managedAssemblyCount, int typeCount, int methodCount,
        int fieldCount, int propertyCount, int eventCount, long totalOutputBytes, long totalManagedBytes) =>
        new(
            artifactCount, libraryCount, managedAssemblyCount, typeCount, methodCount, fieldCount,
            propertyCount, eventCount, totalOutputBytes, totalManagedBytes, []);

    private static ValidatedExtraction BuildValidatedExtraction(
        string extractionId,
        string recipeId,
        string sourceAttemptId,
        string manifestDigest,
        ExtractionStatistics statistics) =>
        new(
            extractionId, recipeId, BuildId, ToolInstanceId,
            sourceAttemptId, "default-profile", 1, new string('a', 64), 1, 1, manifestDigest,
            $"C:\\builds\\{BuildId}\\extractions\\{extractionId}", BaseTime, ToolTrustLevel.ManagedPinned,
            ValidationOutcome.Valid, statistics);

    private static ValidationReport BuildReport(
        string attemptId,
        ValidationSubjectKind subjectKind,
        string? subjectExtractionId,
        string manifestDigest,
        ValidationOutcome outcome,
        ExtractionStatistics statistics,
        string? baselineExtractionId,
        IReadOnlyList<ValidationIssue> issues,
        bool preferenceEligible,
        DateTimeOffset validatedAtUtc,
        string? policyDigest = null) =>
        new(
            1, attemptId, subjectKind, subjectExtractionId, BuildId, RecipeId1,
            "managed-assemblies-v1", 1, policyDigest ?? new string('b', 64), outcome,
            InputIntegrityPassed: true, ProcessIntegrityPassed: true, OutputContainmentPassed: true,
            manifestDigest, statistics, baselineExtractionId, [], issues, preferenceEligible,
            validatedAtUtc);

    private async Task<SqliteAtlasRepository> CreateInitializedRepositoryAsync(
        CancellationToken cancellationToken)
    {
        var repository = new SqliteAtlasRepository(_databasePath, _backupDirectory);
        await repository.InitializeAsync(cancellationToken);
        await SeedBuildAsync(repository, cancellationToken);
        await SeedToolInstanceAsync(cancellationToken);
        return repository;
    }

    private static async Task SeedBuildAsync(
        SqliteAtlasRepository repository,
        CancellationToken cancellationToken) =>
        await repository.SaveSnapshotAsync(
            new EnvironmentSnapshot(
                IdentityVersion: 2,
                Build: new GameBuild(BuildId, "assembly-a", "metadata-a", BaseTime, IsValid: true),
                Installation: new InstallationObservation(
                    "2022.3", "3164500", "123", "C:\\game", "C:\\game\\GameAssembly.dll",
                    "C:\\game\\global-metadata.dat"),
                Dependencies: [],
                AtlasVersion: "0.2.0-test",
                CapturedAtUtc: BaseTime),
            cancellationToken);

    private async Task SeedToolInstanceAsync(CancellationToken cancellationToken) =>
        await ExecuteAsync(
            $"""
            INSERT INTO tool_instances (
                tool_instance_id, tool_name, version_label, platform, trust_level,
                definition_digest, package_sha256, executable_sha256, observed_path,
                first_observed_at_utc, last_verified_at_utc, status)
            VALUES (
                '{ToolInstanceId}', 'cpp2il', 'test-version', 'win-x64', 'ManagedPinned',
                'definition', 'package', 'executable', 'C:\tools\Cpp2IL.exe',
                '2026-08-13T04:00:00.0000000+00:00',
                '2026-08-13T04:30:00.0000000+00:00', 'Verified');
            """,
            cancellationToken);

    private async Task CreateFailingArtifactTriggerAsync(CancellationToken cancellationToken) =>
        await ExecuteAsync(
            """
            CREATE TRIGGER fail_specific_artifact
            BEFORE INSERT ON extraction_artifacts
            WHEN NEW.relative_path = 'reconstructed/bad.dll'
            BEGIN
                SELECT RAISE(ABORT, 'forced artifact failure');
            END;
            """,
            cancellationToken);

    private async Task<long> CountAsync(string tableName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(SqliteOpenMode.ReadOnly, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<string?> ScalarStringAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(SqliteOpenMode.ReadOnly, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(
            value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(SqliteOpenMode.ReadWrite, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExecuteAsync(
        string sql, (string Name, object Value) parameter, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(SqliteOpenMode.ReadWrite, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(
        SqliteOpenMode mode, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = mode,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
