using S1Atlas.Core.Extraction;
using S1Atlas.Core.Hashing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Inputs;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Extraction.Validation;

namespace S1Atlas.Extraction.Promotion;

/// <summary>Everything <see cref="ValidatedExtractionPromoter.PromoteAsync"/> needs to finalize one validated attempt.</summary>
/// <param name="Attempt">The <c>Validating</c> source attempt, carrying the owned candidate output path.</param>
/// <param name="Report">The strict validation report; <c>Invalid</c> routes to failure finalization.</param>
/// <param name="ArtifactManifest">The annotated manifest; required unless the report is <c>Invalid</c>.</param>
/// <param name="TrustLevel">The candidate tool's trust level, recorded on the promoted extraction.</param>
/// <param name="DeduplicationTarget">An already-validated extraction whose bytes this candidate exactly reproduces; links instead of promoting a second copy.</param>
/// <param name="AutomaticPreferenceReason">The automatic preference reason a promoter should apply, or <see langword="null"/>.</param>
/// <param name="KeepInvalidOutput">When the report is <c>Invalid</c>, retains the candidate as retained-output instead of counting and discarding it.</param>
internal sealed record ValidatedExtractionPromotionRequest(
    ExtractionAttempt Attempt,
    ValidationReport Report,
    ArtifactManifest? ArtifactManifest,
    ToolTrustLevel TrustLevel,
    ValidatedExtraction? DeduplicationTarget,
    ExtractionPreferenceReason? AutomaticPreferenceReason,
    bool KeepInvalidOutput);

internal enum PromotionDisposition
{
    NewExtraction,
    LinkedExistingExtraction,
    ValidationFailed
}

/// <summary>The outcome of finalizing one validated attempt.</summary>
internal sealed record ValidatedExtractionPromotionResult(
    PromotionDisposition Disposition,
    ExtractionAttempt Attempt,
    string? ExtractionId,
    string? ExtractionRoot,
    bool IsPreferred);

/// <summary>
/// Finalizes a validated attempt through the safety-critical, filesystem-first /
/// SQLite-second promotion order. New output is staged into an Atlas-owned staging
/// root, sealed with immutable documents and a <c>complete.marker</c> written last,
/// integrity-verified against the planned record before any database row exists,
/// atomically renamed into its final content-addressed root, and only then committed
/// to SQLite; the sibling promotion journal is deleted only after a successful
/// commit. A filesystem failure therefore never leaves a database extraction row, and
/// a database failure after the final rename leaves a complete, recoverable final
/// directory that recovery registers later.
/// </summary>
internal sealed class ValidatedExtractionPromoter
{
    private readonly string _dataRoot;
    private readonly IValidatedExtractionRepository _validatedRepository;
    private readonly ValidatedExtractionDocumentStore _documentStore;
    private readonly ValidatedExtractionIntegrityVerifier _integrityVerifier;
    private readonly PromotionJournalStore _journalStore;
    private readonly CandidateOutputInspector _candidateInspector;
    private readonly TimeProvider _timeProvider;

    public ValidatedExtractionPromoter(
        string dataRoot,
        IValidatedExtractionRepository validatedRepository,
        ValidatedExtractionDocumentStore documentStore,
        ValidatedExtractionIntegrityVerifier integrityVerifier,
        PromotionJournalStore journalStore,
        CandidateOutputInspector candidateInspector,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        _validatedRepository = validatedRepository ?? throw new ArgumentNullException(nameof(validatedRepository));
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _integrityVerifier = integrityVerifier ?? throw new ArgumentNullException(nameof(integrityVerifier));
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        _candidateInspector = candidateInspector ?? throw new ArgumentNullException(nameof(candidateInspector));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ValidatedExtractionPromotionResult> PromoteAsync(
        ValidatedExtractionPromotionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Attempt);
        ArgumentNullException.ThrowIfNull(request.Report);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Attempt.Status != ExtractionAttemptStatus.Validating)
        {
            throw new ArgumentException(
                "Promotion requires a Validating source attempt.", nameof(request));
        }

        if (request.Report.Outcome == ValidationOutcome.Invalid)
        {
            return await FinalizeInvalidAsync(request, cancellationToken);
        }

        if (request.DeduplicationTarget is not null)
        {
            return await LinkExistingAsync(request, cancellationToken);
        }

        return await PromoteNewOutputAsync(request, cancellationToken);
    }

    private async Task<ValidatedExtractionPromotionResult> PromoteNewOutputAsync(
        ValidatedExtractionPromotionRequest request,
        CancellationToken cancellationToken)
    {
        var attempt = request.Attempt;
        var report = request.Report;
        var manifest = request.ArtifactManifest
            ?? throw new ArgumentException(
                "Promoting new output requires an annotated artifact manifest.", nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(attempt.RecipeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attempt.ToolInstanceId);

        var attemptPaths = OwnedValidatedExtractionPaths.ForAttempt(
            _dataRoot, attempt.BuildId, attempt.AttemptId);
        var ownedAttempt = OwnedAttemptPaths.Create(_dataRoot, attempt.BuildId, attempt.AttemptId);

        // The candidate must still inventory identically to the manifest that
        // was validated. If the bytes changed since validation, fail closed as a
        // filesystem promotion failure so no database extraction row is ever created.
        var inspection = await _candidateInspector.InspectAsync(attempt, _dataRoot, cancellationToken);
        if (!inspection.ContainmentPassed || inspection.Inventory is null ||
            !InventoryMatchesManifest(inspection.Inventory, manifest))
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.FilesystemPromotion,
                ExtractionFailureCode.FilesystemPromotionFailed,
                "The extraction candidate no longer matches the manifest it was validated against.");
        }

        var digest = ArtifactManifestFingerprint.Create(manifest);
        if (!string.Equals(digest, report.ArtifactManifestDigest, StringComparison.Ordinal))
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.FilesystemPromotion,
                ExtractionFailureCode.FilesystemPromotionFailed,
                "The validation report digest does not match the artifact manifest.");
        }

        var extractionId = ExtractionId.Create(attempt.RecipeId!, digest);
        var finalPaths = OwnedValidatedExtractionPaths.ForExtraction(
            _dataRoot, attempt.BuildId, extractionId);
        var finalRoot = finalPaths.FinalExtractionRoot!;
        var stagingRoot = attemptPaths.PromotionStagingRoot!;

        var extraction = BuildExtraction(attempt, report, digest, extractionId, finalRoot, request.TrustLevel);

        // Create the absent Atlas-owned staging root.
        if (Directory.Exists(stagingRoot))
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.FilesystemPromotion,
                ExtractionFailureCode.FilesystemPromotionFailed,
                "The promotion staging root already exists; a prior promotion must be recovered first.");
        }

        Directory.CreateDirectory(stagingRoot);
        OwnedAttemptPaths.EnsureSafeExistingPath(_dataRoot, stagingRoot);

        // Write the sibling promotion journal before any output is staged.
        var journal = new PromotionJournalContent(
            PromotionJournalStore.SchemaVersion,
            attempt.AttemptId,
            attempt.BuildId,
            attempt.RecipeId!,
            extractionId,
            digest,
            ownedAttempt.CandidateOutputRoot,
            stagingRoot,
            finalRoot,
            report.ValidatedAtUtc);
        await _journalStore.WriteAsync(attemptPaths, journal, cancellationToken);

        // Move the candidate output into staging/reconstructed.
        Directory.Move(ownedAttempt.CandidateOutputRoot, attemptPaths.StagedReconstructedRoot!);

        // Copy the bounded attempt logs into staging/logs.
        CopyBoundedLogs(ownedAttempt, attemptPaths.StagedLogsRoot!);

        // Steps 7 and 8: write the immutable documents, marker last.
        await _documentStore.WriteFinalDocumentsAsync(
            _dataRoot, stagingRoot, extraction, manifest, report, cancellationToken);

        // Verify the staged output against the planned record before any DB row exists.
        var stagedIntegrity = await _integrityVerifier.VerifyAsync(
            _dataRoot, stagingRoot, extraction, manifest.Entries, cancellationToken);
        if (stagedIntegrity.Status != ValidatedExtractionIntegrityStatus.Valid)
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.FilesystemPromotion,
                ExtractionFailureCode.FilesystemPromotionFailed,
                $"The staged extraction failed integrity verification: {stagedIntegrity.Message}");
        }

        // Step 10: atomically rename staging into the final content-addressed root.
        var finalParent = Path.GetDirectoryName(finalRoot)!;
        Directory.CreateDirectory(finalParent);
        if (Directory.Exists(finalRoot) || File.Exists(finalRoot))
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.FilesystemPromotion,
                ExtractionFailureCode.FilesystemPromotionFailed,
                $"The final extraction root '{finalRoot}' already exists.");
        }

        Directory.Move(stagingRoot, finalRoot);

        // Step 11: commit the SQLite promotion. A failure here deliberately leaves the
        // complete final directory AND the journal in place for recovery to register.
        var succeeded = BuildSucceededAttempt(attempt, extractionId);
        try
        {
            await _validatedRepository.CommitValidatedExtractionAsync(
                new ValidatedExtractionPromotion(
                    succeeded, extraction, manifest, report, request.AutomaticPreferenceReason),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.DatabasePromotion,
                ExtractionFailureCode.DatabasePromotionFailed,
                "The validated extraction was staged and finalized on disk but could not be " +
                "registered in the database; recovery will register the complete final directory.",
                innerException: exception);
        }

        // Step 12: only now delete the journal.
        _journalStore.DeleteBestEffort(attemptPaths.PromotionJournalPath!);

        return new ValidatedExtractionPromotionResult(
            PromotionDisposition.NewExtraction,
            succeeded,
            extractionId,
            finalRoot,
            request.AutomaticPreferenceReason is not null);
    }

    private async Task<ValidatedExtractionPromotionResult> LinkExistingAsync(
        ValidatedExtractionPromotionRequest request,
        CancellationToken cancellationToken)
    {
        var attempt = request.Attempt;
        var report = request.Report;
        var target = request.DeduplicationTarget!;
        var attemptPaths = OwnedValidatedExtractionPaths.ForAttempt(
            _dataRoot, attempt.BuildId, attempt.AttemptId);
        var ownedAttempt = OwnedAttemptPaths.Create(_dataRoot, attempt.BuildId, attempt.AttemptId);

        // Write the attempt-level strict report first (its full history is retained even
        // though no new extraction is created).
        await _documentStore.WriteAttemptValidationReportAsync(attemptPaths, report, cancellationToken);

        // Verify the existing final extraction is still authoritative before trusting it
        // as this attempt's result and discarding the reproduced candidate.
        var integrity = await _integrityVerifier.VerifyAsync(
            _dataRoot, target.BuildId, target.ExtractionId, cancellationToken);
        if (integrity.Status != ValidatedExtractionIntegrityStatus.Valid)
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.ReproducibilityValidation,
                ExtractionFailureCode.IntegrityMismatch,
                $"The existing validated extraction '{target.ExtractionId}' failed integrity " +
                $"verification and cannot be reused: {integrity.Message}");
        }

        // Delete the exact duplicate candidate; it reproduces bytes that already exist.
        var (fileCount, byteCount) = DeleteOwnedCandidate(ownedAttempt);

        var succeeded = BuildSucceededAttempt(attempt, target.ExtractionId) with
        {
            DiscardedFileCount = checked(attempt.DiscardedFileCount + fileCount),
            DiscardedByteCount = checked(attempt.DiscardedByteCount + byteCount)
        };

        await _validatedRepository.LinkAttemptToValidatedExtractionAsync(
            new ValidationPersistence(succeeded, report),
            target,
            ExtractionAttemptStatus.Validating,
            cancellationToken);

        var isPreferred = false;
        if (request.AutomaticPreferenceReason is { } reason)
        {
            await _validatedRepository.SetPreferredExtractionAsync(
                new PreferredExtraction(
                    target.BuildId, target.ExtractionId, report.ValidatedAtUtc, reason),
                cancellationToken);
            isPreferred = true;
        }

        return new ValidatedExtractionPromotionResult(
            PromotionDisposition.LinkedExistingExtraction,
            succeeded,
            target.ExtractionId,
            target.RootPath,
            isPreferred);
    }

    private async Task<ValidatedExtractionPromotionResult> FinalizeInvalidAsync(
        ValidatedExtractionPromotionRequest request,
        CancellationToken cancellationToken)
    {
        var attempt = request.Attempt;
        var report = request.Report;
        var attemptPaths = OwnedValidatedExtractionPaths.ForAttempt(
            _dataRoot, attempt.BuildId, attempt.AttemptId);
        var ownedAttempt = OwnedAttemptPaths.Create(_dataRoot, attempt.BuildId, attempt.AttemptId);

        await _documentStore.WriteAttemptValidationReportAsync(attemptPaths, report, cancellationToken);

        var discardedFiles = 0;
        long discardedBytes = 0;
        if (request.KeepInvalidOutput)
        {
            RetainOwnedCandidate(ownedAttempt);
        }
        else
        {
            (discardedFiles, discardedBytes) = DeleteOwnedCandidate(ownedAttempt);
        }

        var (stage, code) = MapFailure(report);
        var message = report.Issues
            .FirstOrDefault(issue => issue.Severity == ValidationIssueSeverity.Error)?.Message
            ?? "The extraction candidate failed validation.";

        var failed = attempt with
        {
            Status = ExtractionAttemptStatus.Failed,
            CompletedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime(),
            FailureStage = stage,
            FailureCode = code,
            FailureMessage = message,
            DiscardedFileCount = checked(attempt.DiscardedFileCount + discardedFiles),
            DiscardedByteCount = checked(attempt.DiscardedByteCount + discardedBytes)
        };
        _ = ExtractionAttemptLifecycle.Transition(attempt, failed);

        await _validatedRepository.SaveValidationFailureAsync(
            new ValidationPersistence(failed, report),
            ExtractionAttemptStatus.Validating,
            cancellationToken);

        return new ValidatedExtractionPromotionResult(
            PromotionDisposition.ValidationFailed, failed, null, null, IsPreferred: false);
    }

    private ValidatedExtraction BuildExtraction(
        ExtractionAttempt attempt,
        ValidationReport report,
        string digest,
        string extractionId,
        string finalRoot,
        ToolTrustLevel trustLevel) => new(
        ExtractionId: extractionId,
        RecipeId: attempt.RecipeId!,
        BuildId: attempt.BuildId,
        ToolInstanceId: attempt.ToolInstanceId!,
        SourceAttemptId: attempt.AttemptId,
        ProfileId: attempt.ProfileId,
        ProfileVersion: attempt.ProfileVersion,
        ProfileDigest: attempt.ProfileDigest,
        AdapterVersion: attempt.AdapterVersion,
        ExtractionSchemaVersion: attempt.ExtractionSchemaVersion,
        ArtifactManifestDigest: digest,
        RootPath: finalRoot,
        CreatedAtUtc: report.ValidatedAtUtc,
        TrustLevel: trustLevel,
        InitialValidationOutcome: report.Outcome,
        Statistics: report.Statistics);

    private ExtractionAttempt BuildSucceededAttempt(ExtractionAttempt attempt, string extractionId)
    {
        var succeeded = attempt with
        {
            Status = ExtractionAttemptStatus.Succeeded,
            CompletedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime(),
            ResultExtractionId = extractionId
        };
        return ExtractionAttemptLifecycle.Transition(attempt, succeeded);
    }

    private static bool InventoryMatchesManifest(CandidateInventory inventory, ArtifactManifest manifest)
    {
        var manifestByPath = new Dictionary<string, (long Size, string Sha256)>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            if (!manifestByPath.TryAdd(entry.RelativePath, (entry.Size, entry.Sha256)))
            {
                return false;
            }
        }

        if (inventory.Artifacts.Count != manifestByPath.Count)
        {
            return false;
        }

        foreach (var artifact in inventory.Artifacts)
        {
            if (!manifestByPath.TryGetValue(artifact.RelativePath, out var expected) ||
                expected.Size != artifact.Size ||
                !string.Equals(expected.Sha256, artifact.Sha256, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void CopyBoundedLogs(OwnedAttemptPaths ownedAttempt, string stagedLogsRoot)
    {
        if (!Directory.Exists(ownedAttempt.FinalLogsRoot) ||
            !PathSafety.IsNormalDirectory(ownedAttempt.FinalLogsRoot))
        {
            return;
        }

        var expectedNames = new[] { "stdout.log", "stderr.log" };

        Directory.CreateDirectory(stagedLogsRoot);
        foreach (var name in expectedNames)
        {
            var source = Path.Combine(ownedAttempt.FinalLogsRoot, name);
            if (!File.Exists(source))
            {
                continue;
            }

            var attributes = File.GetAttributes(source);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                continue;
            }

            File.Copy(source, Path.Combine(stagedLogsRoot, name), overwrite: false);
        }
    }

    private static (int FileCount, long ByteCount) DeleteOwnedCandidate(OwnedAttemptPaths ownedAttempt)
    {
        var facts = CountOwnedTree(ownedAttempt.CandidateOutputRoot);
        if (Directory.Exists(ownedAttempt.CandidateOutputRoot) &&
            PathSafety.IsNormalDirectory(ownedAttempt.CandidateOutputRoot))
        {
            Directory.Delete(ownedAttempt.CandidateOutputRoot, recursive: true);
        }

        return facts;
    }

    private static void RetainOwnedCandidate(OwnedAttemptPaths ownedAttempt)
    {
        if (!Directory.Exists(ownedAttempt.CandidateOutputRoot))
        {
            return;
        }

        Directory.CreateDirectory(ownedAttempt.AttemptRoot);
        OwnedAttemptPaths.EnsureSafeExistingPath(ownedAttempt.DataRoot, ownedAttempt.AttemptRoot);
        if (Directory.Exists(ownedAttempt.RetainedOutputRoot) ||
            File.Exists(ownedAttempt.RetainedOutputRoot))
        {
            throw new InvalidOperationException(
                "Retained output already exists for this attempt.");
        }

        Directory.Move(ownedAttempt.CandidateOutputRoot, ownedAttempt.RetainedOutputRoot);
    }

    private static (int FileCount, long ByteCount) CountOwnedTree(string root)
    {
        if (!Directory.Exists(root) || !PathSafety.IsNormalDirectory(root))
        {
            return (0, 0);
        }

        var fileCount = 0;
        long byteCount = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                fileCount = checked(fileCount + 1);
                byteCount = checked(byteCount + new FileInfo(entry).Length);
            }
        }

        return (fileCount, byteCount);
    }

    private static (ExtractionFailureStage Stage, ExtractionFailureCode Code) MapFailure(
        ValidationReport report)
    {
        var firstError = report.Issues.FirstOrDefault(issue => issue.Severity == ValidationIssueSeverity.Error);
        return firstError?.Code switch
        {
            "OutputOutsideStaging" =>
                (ExtractionFailureStage.OutputContainment, ExtractionFailureCode.OutputOutsideStaging),
            "NoArtifactsProduced" =>
                (ExtractionFailureStage.ArtifactValidation, ExtractionFailureCode.NoArtifactsProduced),
            "EmptyArtifact" =>
                (ExtractionFailureStage.ArtifactValidation, ExtractionFailureCode.EmptyArtifact),
            "NoManagedAssembliesProduced" =>
                (ExtractionFailureStage.AssemblyValidation, ExtractionFailureCode.NoManagedAssembliesProduced),
            "InvalidManagedAssembly" =>
                (ExtractionFailureStage.AssemblyValidation, ExtractionFailureCode.InvalidManagedAssembly),
            "RequiredAssemblyMissing" =>
                (ExtractionFailureStage.AssemblyValidation, ExtractionFailureCode.RequiredAssemblyMissing),
            "DuplicateAssemblyIdentity" =>
                (ExtractionFailureStage.AssemblyValidation, ExtractionFailureCode.DuplicateAssemblyIdentity),
            "CatastrophicSanityDeviation" =>
                (ExtractionFailureStage.SanityValidation, ExtractionFailureCode.CatastrophicSanityDeviation),
            "ValidationPolicyInvalid" =>
                (ExtractionFailureStage.SanityValidation, ExtractionFailureCode.ValidationPolicyInvalid),
            "ValidationReportInvalid" =>
                (ExtractionFailureStage.SanityValidation, ExtractionFailureCode.ValidationReportInvalid),
            _ => (ExtractionFailureStage.SanityValidation, ExtractionFailureCode.ValidationReportInvalid)
        };
    }
}
