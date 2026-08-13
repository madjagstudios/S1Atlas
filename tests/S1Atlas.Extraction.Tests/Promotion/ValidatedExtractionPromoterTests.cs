using Microsoft.Data.Sqlite;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Extraction.Promotion;
using S1Atlas.Extraction.Validation;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Extraction.Tests.Promotion;

public sealed class ValidatedExtractionPromoterTests : IAsyncDisposable
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(), $"s1atlas-promoter-{Guid.NewGuid():N}");
    private readonly Sha256FileHasher _hasher = new();
    private readonly ValidatedExtractionDocumentStore _documentStore = new();
    private readonly FixedTimeProvider _clock = new(PromotionTestData.BaseTime.AddMinutes(10));

    public ValidatedExtractionPromoterTests() => Directory.CreateDirectory(_dataRoot);

    [Fact]
    public async Task PromoteAsync_NewValidOutput_MovesCandidateFinalizesAndCommitsAtomically()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var candidate = await PromotionTestData.CreateValidatingCandidateAsync(
            repository, _dataRoot, "00000000000000000000000000000201", PromotionTestData.RecipeId, [1, 2, 3, 4, 5], ct);
        var promoter = CreatePromoter(repository);
        var report = PromotionTestData.BuildReport(
            candidate.Attempt, candidate.Digest, candidate.Statistics, ValidationOutcome.Valid, [],
            preferenceEligible: true, PromotionTestData.BaseTime.AddMinutes(5));

        var result = await promoter.PromoteAsync(
            Request(candidate, report, ExtractionPreferenceReason.ManagedAutomatic), ct);

        var finalRoot = OwnedValidatedExtractionPaths
            .ForExtraction(_dataRoot, PromotionTestData.BuildId, candidate.ExtractionId).FinalExtractionRoot!;
        Assert.Equal(PromotionDisposition.NewExtraction, result.Disposition);
        Assert.Equal(candidate.ExtractionId, result.ExtractionId);
        Assert.Equal(finalRoot, result.ExtractionRoot);
        Assert.True(File.Exists(Path.Combine(finalRoot, "complete.marker")));
        Assert.True(File.Exists(Path.Combine(finalRoot, "reconstructed", "Assembly-CSharp.dll")));
        Assert.False(Directory.Exists(candidate.OwnedCandidateRoot));
        Assert.False(Directory.Exists(StagingRoot(candidate.Attempt.AttemptId)));
        Assert.False(File.Exists(JournalPath(candidate.Attempt.AttemptId)));

        var stored = await repository.GetValidatedExtractionAsync(candidate.ExtractionId, ct);
        Assert.NotNull(stored);
        var reloaded = await repository.GetAttemptAsync(candidate.Attempt.AttemptId, ct);
        Assert.Equal(ExtractionAttemptStatus.Succeeded, reloaded!.Status);
        Assert.Equal(candidate.ExtractionId, reloaded.ResultExtractionId);
        var integrity = await CreateVerifier(repository).VerifyAsync(
            _dataRoot, PromotionTestData.BuildId, candidate.ExtractionId, ct);
        Assert.Equal(ValidatedExtractionIntegrityStatus.Valid, integrity.Status);
    }

    [Fact]
    public async Task PromoteAsync_ManagedValidOutput_AutoPrefersWhenAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var candidate = await PromotionTestData.CreateValidatingCandidateAsync(
            repository, _dataRoot, "00000000000000000000000000000202", PromotionTestData.RecipeId, [10, 11, 12], ct);
        var promoter = CreatePromoter(repository);
        var report = PromotionTestData.BuildReport(
            candidate.Attempt, candidate.Digest, candidate.Statistics, ValidationOutcome.Valid, [],
            preferenceEligible: true, PromotionTestData.BaseTime.AddMinutes(5));

        await promoter.PromoteAsync(
            Request(candidate, report, ExtractionPreferenceReason.ManagedAutomatic), ct);

        var preferred = await repository.GetPreferredExtractionAsync(PromotionTestData.BuildId, ct);
        Assert.NotNull(preferred);
        Assert.Equal(candidate.ExtractionId, preferred.ExtractionId);
        Assert.Equal(ExtractionPreferenceReason.ManagedAutomatic, preferred.SelectionReason);
    }

    [Fact]
    public async Task PromoteAsync_CustomValidOutput_DoesNotAutoPrefer()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var candidate = await PromotionTestData.CreateValidatingCandidateAsync(
            repository, _dataRoot, "00000000000000000000000000000203", PromotionTestData.RecipeId, [7, 7, 7, 7], ct);
        var promoter = CreatePromoter(repository);
        var report = PromotionTestData.BuildReport(
            candidate.Attempt, candidate.Digest, candidate.Statistics, ValidationOutcome.ValidWithWarnings, [],
            preferenceEligible: false, PromotionTestData.BaseTime.AddMinutes(5));

        var result = await promoter.PromoteAsync(
            new ValidatedExtractionPromotionRequest(
                candidate.Attempt, report, candidate.Manifest, ToolTrustLevel.CustomOverride,
                DeduplicationTarget: null, AutomaticPreferenceReason: null, KeepInvalidOutput: false),
            ct);

        Assert.Equal(PromotionDisposition.NewExtraction, result.Disposition);
        Assert.False(result.IsPreferred);
        Assert.Null(await repository.GetPreferredExtractionAsync(PromotionTestData.BuildId, ct));
    }

    [Fact]
    public async Task PromoteAsync_ManagedValidOutput_DoesNotOverwriteManualPreference()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var first = await PromotionTestData.CreateValidatingCandidateAsync(
            repository, _dataRoot, "00000000000000000000000000000204", PromotionTestData.RecipeId, [1, 1, 1], ct);
        var promoter = CreatePromoter(repository);
        await promoter.PromoteAsync(
            Request(first, PromotionTestData.BuildReport(
                first.Attempt, first.Digest, first.Statistics, ValidationOutcome.Valid, [],
                true, PromotionTestData.BaseTime.AddMinutes(5)), ExtractionPreferenceReason.ManagedAutomatic),
            ct);
        await repository.SetPreferredExtractionAsync(
            new PreferredExtraction(
                PromotionTestData.BuildId, first.ExtractionId, PromotionTestData.BaseTime.AddMinutes(6),
                ExtractionPreferenceReason.ManualPromotion),
            ct);

        var second = await PromotionTestData.CreateValidatingCandidateAsync(
            repository, _dataRoot, "00000000000000000000000000000205", PromotionTestData.RecipeId2, [2, 2, 2, 2], ct);
        // Automatic selection never overwrites a current ManualPromotion: the policy
        // decides no reason, so the promoter is asked to change nothing.
        await promoter.PromoteAsync(
            new ValidatedExtractionPromotionRequest(
                second.Attempt,
                PromotionTestData.BuildReport(
                    second.Attempt, second.Digest, second.Statistics, ValidationOutcome.Valid, [],
                    true, PromotionTestData.BaseTime.AddMinutes(7)),
                second.Manifest, ToolTrustLevel.ManagedPinned, DeduplicationTarget: null,
                AutomaticPreferenceReason: null, KeepInvalidOutput: false),
            ct);

        var preferred = await repository.GetPreferredExtractionAsync(PromotionTestData.BuildId, ct);
        Assert.NotNull(preferred);
        Assert.Equal(first.ExtractionId, preferred.ExtractionId);
        Assert.Equal(ExtractionPreferenceReason.ManualPromotion, preferred.SelectionReason);
    }

    [Fact]
    public async Task PromoteAsync_SameDigestExistingExtraction_DeletesDuplicateCandidateAndLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var promoter = CreatePromoter(repository);
        var first = await PromotionTestData.CreateValidatingCandidateAsync(
            repository, _dataRoot, "00000000000000000000000000000206", PromotionTestData.RecipeId, [9, 8, 7, 6], ct);
        await promoter.PromoteAsync(
            Request(first, PromotionTestData.BuildReport(
                first.Attempt, first.Digest, first.Statistics, ValidationOutcome.Valid, [],
                true, PromotionTestData.BaseTime.AddMinutes(5)), null),
            ct);
        var existing = await repository.GetValidatedExtractionAsync(first.ExtractionId, ct);

        var second = await PromotionTestData.CreateValidatingCandidateAsync(
            repository, _dataRoot, "00000000000000000000000000000207", PromotionTestData.RecipeId, [9, 8, 7, 6], ct);
        var report = PromotionTestData.BuildReport(
            second.Attempt, second.Digest, second.Statistics, ValidationOutcome.Valid, [],
            true, PromotionTestData.BaseTime.AddMinutes(8));

        var result = await promoter.PromoteAsync(
            new ValidatedExtractionPromotionRequest(
                second.Attempt, report, second.Manifest, ToolTrustLevel.ManagedPinned,
                DeduplicationTarget: existing, AutomaticPreferenceReason: null, KeepInvalidOutput: false),
            ct);

        Assert.Equal(PromotionDisposition.LinkedExistingExtraction, result.Disposition);
        Assert.Equal(first.ExtractionId, result.ExtractionId);
        Assert.False(Directory.Exists(second.OwnedCandidateRoot));
        Assert.Equal(1L, await CountAsync("validated_extractions", ct));
        var reloaded = await repository.GetAttemptAsync(second.Attempt.AttemptId, ct);
        Assert.Equal(ExtractionAttemptStatus.Succeeded, reloaded!.Status);
        Assert.Equal(first.ExtractionId, reloaded.ResultExtractionId);
    }

    [Fact]
    public async Task PromoteAsync_InvalidReport_StoresFailureDiscardsCandidateAndCreatesNoExtraction()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var candidate = await PromotionTestData.CreateValidatingCandidateAsync(
            repository, _dataRoot, "00000000000000000000000000000208", PromotionTestData.RecipeId, [4, 4, 4], ct);
        var promoter = CreatePromoter(repository);
        var report = PromotionTestData.BuildReport(
            candidate.Attempt, candidate.Digest, candidate.Statistics, ValidationOutcome.Invalid,
            [new ValidationIssue(
                ValidationIssueSeverity.Error, "NoManagedAssembliesProduced",
                "No managed assemblies were produced.", null, PreferenceBlocking: true)],
            preferenceEligible: false, PromotionTestData.BaseTime.AddMinutes(5));

        var result = await promoter.PromoteAsync(
            new ValidatedExtractionPromotionRequest(
                candidate.Attempt, report, ArtifactManifest: null, ToolTrustLevel.ManagedPinned,
                DeduplicationTarget: null, AutomaticPreferenceReason: null, KeepInvalidOutput: false),
            ct);

        Assert.Equal(PromotionDisposition.ValidationFailed, result.Disposition);
        Assert.Null(result.ExtractionId);
        var reloaded = await repository.GetAttemptAsync(candidate.Attempt.AttemptId, ct);
        Assert.Equal(ExtractionAttemptStatus.Failed, reloaded!.Status);
        Assert.Equal(ExtractionFailureStage.AssemblyValidation, reloaded.FailureStage);
        Assert.Equal(ExtractionFailureCode.NoManagedAssembliesProduced, reloaded.FailureCode);
        Assert.True(reloaded.DiscardedFileCount >= 1);
        Assert.Equal(0L, await CountAsync("validated_extractions", ct));
        Assert.False(Directory.Exists(candidate.OwnedCandidateRoot));
        Assert.False(Directory.Exists(
            OwnedValidatedExtractionPaths.ForExtraction(
                _dataRoot, PromotionTestData.BuildId, candidate.ExtractionId).FinalExtractionRoot!));
    }

    [Fact]
    public async Task PromoteAsync_InvalidReportWithKeepRequested_RetainsCandidateOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var candidate = await PromotionTestData.CreateValidatingCandidateAsync(
            repository, _dataRoot, "00000000000000000000000000000209", PromotionTestData.RecipeId, [5, 5], ct);
        var promoter = CreatePromoter(repository);
        var report = PromotionTestData.BuildReport(
            candidate.Attempt, candidate.Digest, candidate.Statistics, ValidationOutcome.Invalid,
            [new ValidationIssue(
                ValidationIssueSeverity.Error, "EmptyArtifact", "An artifact was empty.",
                "reconstructed/Assembly-CSharp.dll", PreferenceBlocking: true)],
            preferenceEligible: false, PromotionTestData.BaseTime.AddMinutes(5));

        await promoter.PromoteAsync(
            new ValidatedExtractionPromotionRequest(
                candidate.Attempt, report, ArtifactManifest: null, ToolTrustLevel.ManagedPinned,
                DeduplicationTarget: null, AutomaticPreferenceReason: null, KeepInvalidOutput: true),
            ct);

        var retainedRoot = Path.Combine(
            _dataRoot, "builds", PromotionTestData.BuildId, "attempts",
            candidate.Attempt.AttemptId, "retained-output");
        Assert.True(Directory.Exists(retainedRoot));
        Assert.True(File.Exists(Path.Combine(retainedRoot, "Assembly-CSharp.dll")));
        Assert.False(Directory.Exists(candidate.OwnedCandidateRoot));
    }

    [Fact]
    public async Task PromoteAsync_CandidateChangedSinceValidation_FailsClosedWithNoDatabaseRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var candidate = await PromotionTestData.CreateValidatingCandidateAsync(
            repository, _dataRoot, "00000000000000000000000000000210", PromotionTestData.RecipeId, [1, 2, 3], ct);
        // The bytes changed after validation: the manifest no longer matches disk.
        await File.AppendAllTextAsync(
            Path.Combine(candidate.OwnedCandidateRoot, "Assembly-CSharp.dll"), "tampered", ct);
        var promoter = CreatePromoter(repository);
        var report = PromotionTestData.BuildReport(
            candidate.Attempt, candidate.Digest, candidate.Statistics, ValidationOutcome.Valid, [],
            true, PromotionTestData.BaseTime.AddMinutes(5));

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            promoter.PromoteAsync(
                Request(candidate, report, ExtractionPreferenceReason.ManagedAutomatic), ct));

        Assert.Equal(ExtractionFailureCode.FilesystemPromotionFailed, exception.Code);
        Assert.Equal(0L, await CountAsync("validated_extractions", ct));
        Assert.False(Directory.Exists(
            OwnedValidatedExtractionPaths.ForExtraction(
                _dataRoot, PromotionTestData.BuildId, candidate.ExtractionId).FinalExtractionRoot!));
        Assert.Equal(
            ExtractionAttemptStatus.Validating,
            (await repository.GetAttemptAsync(candidate.Attempt.AttemptId, ct))!.Status);
    }

    [Fact]
    public async Task PromoteAsync_DatabaseCommitFails_LeavesCompleteRecoverableFinalDirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var candidate = await PromotionTestData.CreateValidatingCandidateAsync(
            repository, _dataRoot, "00000000000000000000000000000211", PromotionTestData.RecipeId, [3, 1, 4, 1, 5], ct);
        var throwingRepository = new ThrowingCommitRepository(repository);
        var promoter = new ValidatedExtractionPromoter(
            _dataRoot, throwingRepository, _documentStore, CreateVerifier(repository),
            new PromotionJournalStore(), new CandidateOutputInspector(_hasher), _clock);
        var report = PromotionTestData.BuildReport(
            candidate.Attempt, candidate.Digest, candidate.Statistics, ValidationOutcome.Valid, [],
            true, PromotionTestData.BaseTime.AddMinutes(5));

        var exception = await Assert.ThrowsAsync<ExtractionOperationException>(() =>
            promoter.PromoteAsync(
                Request(candidate, report, ExtractionPreferenceReason.ManagedAutomatic), ct));

        Assert.Equal(ExtractionFailureCode.DatabasePromotionFailed, exception.Code);
        var finalRoot = OwnedValidatedExtractionPaths
            .ForExtraction(_dataRoot, PromotionTestData.BuildId, candidate.ExtractionId).FinalExtractionRoot!;
        Assert.True(File.Exists(Path.Combine(finalRoot, "complete.marker")));
        Assert.True(File.Exists(Path.Combine(finalRoot, "reconstructed", "Assembly-CSharp.dll")));
        Assert.True(File.Exists(JournalPath(candidate.Attempt.AttemptId)));
        Assert.Null(await repository.GetValidatedExtractionAsync(candidate.ExtractionId, ct));
    }

    private ValidatedExtractionPromoter CreatePromoter(SqliteAtlasRepository repository) =>
        new(_dataRoot, repository, _documentStore, CreateVerifier(repository),
            new PromotionJournalStore(), new CandidateOutputInspector(_hasher), _clock);

    private ValidatedExtractionIntegrityVerifier CreateVerifier(IValidatedExtractionRepository repository) =>
        new(_documentStore, _hasher, repository);

    private static ValidatedExtractionPromotionRequest Request(
        PromotionCandidate candidate, ValidationReport report, ExtractionPreferenceReason? reason) =>
        new(candidate.Attempt, report, candidate.Manifest, ToolTrustLevel.ManagedPinned,
            DeduplicationTarget: null, AutomaticPreferenceReason: reason, KeepInvalidOutput: false);

    private string StagingRoot(string attemptId) => OwnedValidatedExtractionPaths
        .ForAttempt(_dataRoot, PromotionTestData.BuildId, attemptId).PromotionStagingRoot!;

    private string JournalPath(string attemptId) => OwnedValidatedExtractionPaths
        .ForAttempt(_dataRoot, PromotionTestData.BuildId, attemptId).PromotionJournalPath!;

    private async Task<long> CountAsync(string tableName, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_dataRoot, "atlas.db"),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await Task.Yield();
        TryDelete(_dataRoot);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
