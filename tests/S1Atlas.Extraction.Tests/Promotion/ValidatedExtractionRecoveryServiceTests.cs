using Microsoft.Data.Sqlite;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Hashing;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Extraction.Promotion;
using S1Atlas.Extraction.Validation;
using S1Atlas.Storage.Sqlite;
using Xunit;

namespace S1Atlas.Extraction.Tests.Promotion;

public sealed class ValidatedExtractionRecoveryServiceTests : IAsyncDisposable
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(), $"s1atlas-promotion-recovery-{Guid.NewGuid():N}");
    private readonly Sha256FileHasher _hasher = new();
    private readonly ValidatedExtractionDocumentStore _documentStore = new();
    private readonly PromotionJournalStore _journalStore = new();
    private readonly FixedTimeProvider _clock = new(PromotionTestData.BaseTime.AddMinutes(30));

    public ValidatedExtractionRecoveryServiceTests() => Directory.CreateDirectory(_dataRoot);

    [Fact]
    public async Task RecoverAsync_CompleteFinalOutputAndJournalNoDatabaseRow_RegistersAndSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var candidate = await PromotionTestData.ProduceUnregisteredFinalAsync(
            repository, _dataRoot, "00000000000000000000000000000301", PromotionTestData.RecipeId,
            [1, 2, 3, 4], _hasher, _documentStore, PromotionTestData.BaseTime.AddMinutes(5), ct);
        Assert.Null(await repository.GetValidatedExtractionAsync(candidate.ExtractionId, ct));

        var outcome = await CreateService(repository).RecoverAsync(ct);

        var entry = Assert.Single(outcome.Entries);
        Assert.Equal(ValidatedPromotionRecoveryAction.RegisteredCompleteFinal, entry.Action);
        Assert.NotNull(await repository.GetValidatedExtractionAsync(candidate.ExtractionId, ct));
        var reloaded = await repository.GetAttemptAsync(candidate.Attempt.AttemptId, ct);
        Assert.Equal(ExtractionAttemptStatus.Succeeded, reloaded!.Status);
        Assert.Equal(candidate.ExtractionId, reloaded.ResultExtractionId);
        Assert.False(File.Exists(JournalPath(candidate.Attempt.AttemptId)));
    }

    [Fact]
    public async Task RecoverAsync_CompleteStagingAndJournal_VerifiesRenamesAndRegisters()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var candidate = await PromotionTestData.CreateValidatingCandidateAsync(
            repository, _dataRoot, "00000000000000000000000000000302", PromotionTestData.RecipeId, [5, 6, 7], ct);
        await StageCompletelyAsync(candidate, PromotionTestData.BaseTime.AddMinutes(5), writeDocuments: true, ct);

        var outcome = await CreateService(repository).RecoverAsync(ct);

        var entry = Assert.Single(outcome.Entries);
        Assert.Equal(ValidatedPromotionRecoveryAction.RenamedStagingAndRegistered, entry.Action);
        var finalRoot = FinalRoot(candidate.ExtractionId);
        Assert.True(File.Exists(Path.Combine(finalRoot, "complete.marker")));
        Assert.False(Directory.Exists(StagingRoot(candidate.Attempt.AttemptId)));
        Assert.NotNull(await repository.GetValidatedExtractionAsync(candidate.ExtractionId, ct));
        Assert.Equal(
            ExtractionAttemptStatus.Succeeded,
            (await repository.GetAttemptAsync(candidate.Attempt.AttemptId, ct))!.Status);
        Assert.False(File.Exists(JournalPath(candidate.Attempt.AttemptId)));
    }

    [Fact]
    public async Task RecoverAsync_DatabaseRowAndValidFinal_NoOpAndRemovesStaleJournal()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var candidate = await PromoteNormallyAsync(
            repository, "00000000000000000000000000000303", PromotionTestData.RecipeId, [8, 9, 10],
            ExtractionPreferenceReason.ManagedAutomatic, ct);
        await WriteStaleJournalAsync(candidate, ct);

        var outcome = await CreateService(repository).RecoverAsync(ct);

        var entry = Assert.Single(outcome.Entries);
        Assert.Equal(ValidatedPromotionRecoveryAction.RemovedStaleJournal, entry.Action);
        Assert.False(File.Exists(JournalPath(candidate.Attempt.AttemptId)));
        Assert.NotNull(await repository.GetValidatedExtractionAsync(candidate.ExtractionId, ct));
        var preferred = await repository.GetPreferredExtractionAsync(PromotionTestData.BuildId, ct);
        Assert.Equal(candidate.ExtractionId, preferred!.ExtractionId);
    }

    [Fact]
    public async Task RecoverAsync_DatabaseRowAndInvalidFinal_ClearsSelectedPointerAndPreservesHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var candidate = await PromoteNormallyAsync(
            repository, "00000000000000000000000000000304", PromotionTestData.RecipeId, [11, 12, 13],
            ExtractionPreferenceReason.ManagedAutomatic, ct);
        // Corrupt the registered final output so its integrity no longer verifies.
        File.Delete(Path.Combine(FinalRoot(candidate.ExtractionId), "reconstructed", "Assembly-CSharp.dll"));
        await WriteStaleJournalAsync(candidate, ct);

        var outcome = await CreateService(repository).RecoverAsync(ct);

        var entry = Assert.Single(outcome.Entries);
        Assert.Equal(ValidatedPromotionRecoveryAction.ClearedInvalidatedPointer, entry.Action);
        Assert.Null(await repository.GetPreferredExtractionAsync(PromotionTestData.BuildId, ct));
        // Historical extraction data is never deleted, only the selected pointer.
        Assert.NotNull(await repository.GetValidatedExtractionAsync(candidate.ExtractionId, ct));
        Assert.False(File.Exists(JournalPath(candidate.Attempt.AttemptId)));
    }

    [Fact]
    public async Task RecoverAsync_IncompleteOwnedStagingAndJournal_QuarantinesAndAbandons()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var candidate = await PromotionTestData.CreateValidatingCandidateAsync(
            repository, _dataRoot, "00000000000000000000000000000305", PromotionTestData.RecipeId, [3, 3, 3], ct);
        await StageCompletelyAsync(candidate, PromotionTestData.BaseTime.AddMinutes(5), writeDocuments: false, ct);

        var outcome = await CreateService(repository).RecoverAsync(ct);

        var entry = Assert.Single(outcome.Entries);
        Assert.Equal(ValidatedPromotionRecoveryAction.QuarantinedIncompleteStaging, entry.Action);
        var quarantined = Path.Combine(
            _dataRoot, "builds", PromotionTestData.BuildId, "extractions", "quarantine",
            candidate.Attempt.AttemptId);
        Assert.True(File.Exists(Path.Combine(quarantined, "reconstructed", "Assembly-CSharp.dll")));
        Assert.False(Directory.Exists(StagingRoot(candidate.Attempt.AttemptId)));
        var reloaded = await repository.GetAttemptAsync(candidate.Attempt.AttemptId, ct);
        Assert.Equal(ExtractionAttemptStatus.Abandoned, reloaded!.Status);
        Assert.Equal(ExtractionFailureCode.InterruptedProcess, reloaded.FailureCode);
        Assert.Null(await repository.GetValidatedExtractionAsync(candidate.ExtractionId, ct));
        Assert.False(File.Exists(JournalPath(candidate.Attempt.AttemptId)));
    }

    [Fact]
    public async Task RecoverAsync_TamperedJournal_PreservesEvidenceAndFailsClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var candidate = await PromotionTestData.CreateValidatingCandidateAsync(
            repository, _dataRoot, "00000000000000000000000000000306", PromotionTestData.RecipeId, [4, 5, 6], ct);
        var attemptPaths = OwnedValidatedExtractionPaths.ForAttempt(
            _dataRoot, PromotionTestData.BuildId, candidate.Attempt.AttemptId);
        // A planned extraction ID that does not derive from the journal's recipe + digest.
        var tampered = new PromotionJournalContent(
            1, candidate.Attempt.AttemptId, PromotionTestData.BuildId, PromotionTestData.RecipeId,
            new string('e', 64), candidate.Digest, candidate.OwnedCandidateRoot,
            attemptPaths.PromotionStagingRoot!,
            FinalRoot(new string('e', 64)), PromotionTestData.BaseTime.AddMinutes(5));
        await _journalStore.WriteAsync(attemptPaths, tampered, ct);

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateService(repository).RecoverAsync(ct));

        Assert.True(File.Exists(JournalPath(candidate.Attempt.AttemptId)));
    }

    [Fact]
    public async Task RecoverAsync_RunTwice_ConvergesIdempotently()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var candidate = await PromotionTestData.ProduceUnregisteredFinalAsync(
            repository, _dataRoot, "00000000000000000000000000000307", PromotionTestData.RecipeId,
            [2, 4, 6, 8], _hasher, _documentStore, PromotionTestData.BaseTime.AddMinutes(5), ct);

        var first = await CreateService(repository).RecoverAsync(ct);
        var second = await CreateService(repository).RecoverAsync(ct);

        Assert.Single(first.Entries);
        Assert.Empty(second.Entries);
        Assert.Equal(1L, await CountAsync("validated_extractions", ct));
        Assert.Equal(
            ExtractionAttemptStatus.Succeeded,
            (await repository.GetAttemptAsync(candidate.Attempt.AttemptId, ct))!.Status);
    }

    [Fact]
    public async Task RecoverAsync_UnrelatedRecovery_PreservesManualPreference()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var pinned = await PromoteNormallyAsync(
            repository, "00000000000000000000000000000308", PromotionTestData.RecipeId2, [1, 1, 1, 1],
            reason: null, ct);
        await repository.SetPreferredExtractionAsync(
            new PreferredExtraction(
                PromotionTestData.BuildId, pinned.ExtractionId, PromotionTestData.BaseTime.AddMinutes(6),
                ExtractionPreferenceReason.ManualPromotion),
            ct);
        var recovered = await PromotionTestData.ProduceUnregisteredFinalAsync(
            repository, _dataRoot, "00000000000000000000000000000309", PromotionTestData.RecipeId,
            [9, 9, 9], _hasher, _documentStore, PromotionTestData.BaseTime.AddMinutes(7), ct);

        await CreateService(repository).RecoverAsync(ct);

        Assert.NotNull(await repository.GetValidatedExtractionAsync(recovered.ExtractionId, ct));
        var preferred = await repository.GetPreferredExtractionAsync(PromotionTestData.BuildId, ct);
        Assert.Equal(pinned.ExtractionId, preferred!.ExtractionId);
        Assert.Equal(ExtractionPreferenceReason.ManualPromotion, preferred.SelectionReason);
    }

    [Fact]
    public async Task GenericRecovery_RunsPhase4First_AndDoesNotAbandonValidatingWithPromotionEvidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = await PromotionTestData.CreateRepositoryAsync(_dataRoot, ct);
        var candidate = await PromotionTestData.ProduceUnregisteredFinalAsync(
            repository, _dataRoot, "00000000000000000000000000000310", PromotionTestData.RecipeId,
            [7, 7, 7, 7], _hasher, _documentStore, PromotionTestData.BaseTime.AddMinutes(5), ct);

        var generic = new ExtractionRecoveryService(
            _dataRoot, repository, new AttemptDocumentStore(), _ => false, _clock, CreateService(repository));
        await generic.RecoverAsync(ct);

        var reloaded = await repository.GetAttemptAsync(candidate.Attempt.AttemptId, ct);
        Assert.Equal(ExtractionAttemptStatus.Succeeded, reloaded!.Status);
        Assert.NotNull(await repository.GetValidatedExtractionAsync(candidate.ExtractionId, ct));
    }

    private ValidatedExtractionRecoveryService CreateService(SqliteAtlasRepository repository) =>
        new(_dataRoot, repository, repository, _documentStore,
            new ValidatedExtractionIntegrityVerifier(_documentStore, _hasher, repository),
            _journalStore, _clock);

    private async Task<PromotionCandidate> PromoteNormallyAsync(
        SqliteAtlasRepository repository,
        string attemptId,
        string recipeId,
        byte[] bytes,
        ExtractionPreferenceReason? reason,
        CancellationToken cancellationToken)
    {
        var candidate = await PromotionTestData.CreateValidatingCandidateAsync(
            repository, _dataRoot, attemptId, recipeId, bytes, cancellationToken);
        var report = PromotionTestData.BuildReport(
            candidate.Attempt, candidate.Digest, candidate.Statistics, ValidationOutcome.Valid, [],
            preferenceEligible: true, PromotionTestData.BaseTime.AddMinutes(5));
        var promoter = new ValidatedExtractionPromoter(
            _dataRoot, repository, _documentStore,
            new ValidatedExtractionIntegrityVerifier(_documentStore, _hasher, repository),
            _journalStore, new CandidateOutputInspector(_hasher),
            new FixedTimeProvider(PromotionTestData.BaseTime.AddMinutes(5)));
        await promoter.PromoteAsync(
            new ValidatedExtractionPromotionRequest(
                candidate.Attempt, report, candidate.Manifest, ToolTrustLevel.ManagedPinned,
                DeduplicationTarget: null, AutomaticPreferenceReason: reason, KeepInvalidOutput: false),
            cancellationToken);
        return candidate;
    }

    /// <summary>Reproduces the promoter's on-disk staging state (optionally sealed with the immutable documents) without renaming to final or committing.</summary>
    private async Task StageCompletelyAsync(
        PromotionCandidate candidate,
        DateTimeOffset validatedAtUtc,
        bool writeDocuments,
        CancellationToken cancellationToken)
    {
        var attemptPaths = OwnedValidatedExtractionPaths.ForAttempt(
            _dataRoot, PromotionTestData.BuildId, candidate.Attempt.AttemptId);
        var finalRoot = FinalRoot(candidate.ExtractionId);
        var stagingRoot = attemptPaths.PromotionStagingRoot!;
        Directory.CreateDirectory(stagingRoot);

        var journal = new PromotionJournalContent(
            1, candidate.Attempt.AttemptId, PromotionTestData.BuildId, candidate.Attempt.RecipeId!,
            candidate.ExtractionId, candidate.Digest, candidate.OwnedCandidateRoot, stagingRoot, finalRoot,
            validatedAtUtc);
        await _journalStore.WriteAsync(attemptPaths, journal, cancellationToken);

        Directory.Move(candidate.OwnedCandidateRoot, attemptPaths.StagedReconstructedRoot!);

        if (!writeDocuments)
        {
            return;
        }

        var extraction = BuildExtraction(candidate, validatedAtUtc, finalRoot);
        var report = PromotionTestData.BuildReport(
            candidate.Attempt, candidate.Digest, candidate.Statistics, ValidationOutcome.Valid, [],
            preferenceEligible: true, validatedAtUtc);
        await _documentStore.WriteFinalDocumentsAsync(
            _dataRoot, stagingRoot, extraction, candidate.Manifest, report, cancellationToken);
    }

    private async Task WriteStaleJournalAsync(PromotionCandidate candidate, CancellationToken cancellationToken)
    {
        var attemptPaths = OwnedValidatedExtractionPaths.ForAttempt(
            _dataRoot, PromotionTestData.BuildId, candidate.Attempt.AttemptId);
        var journal = new PromotionJournalContent(
            1, candidate.Attempt.AttemptId, PromotionTestData.BuildId, candidate.Attempt.RecipeId!,
            candidate.ExtractionId, candidate.Digest, candidate.OwnedCandidateRoot,
            attemptPaths.PromotionStagingRoot!, FinalRoot(candidate.ExtractionId),
            PromotionTestData.BaseTime.AddMinutes(5));
        await _journalStore.WriteAsync(attemptPaths, journal, cancellationToken);
    }

    private static ValidatedExtraction BuildExtraction(
        PromotionCandidate candidate, DateTimeOffset createdAtUtc, string finalRoot) => new(
        candidate.ExtractionId,
        candidate.Attempt.RecipeId!,
        PromotionTestData.BuildId,
        candidate.Attempt.ToolInstanceId!,
        candidate.Attempt.AttemptId,
        candidate.Attempt.ProfileId,
        candidate.Attempt.ProfileVersion,
        candidate.Attempt.ProfileDigest,
        candidate.Attempt.AdapterVersion,
        candidate.Attempt.ExtractionSchemaVersion,
        candidate.Digest,
        finalRoot,
        createdAtUtc,
        ToolTrustLevel.ManagedPinned,
        ValidationOutcome.Valid,
        candidate.Statistics);

    private string FinalRoot(string extractionId) => OwnedValidatedExtractionPaths
        .ForExtraction(_dataRoot, PromotionTestData.BuildId, extractionId).FinalExtractionRoot!;

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
        try
        {
            if (Directory.Exists(_dataRoot))
            {
                Directory.Delete(_dataRoot, recursive: true);
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
