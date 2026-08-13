using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Inputs;
using S1Atlas.Extraction.Manifests;

namespace S1Atlas.Extraction.Promotion;

/// <summary>
/// A minimal recovery hook the generic Phase 3 <see cref="ExtractionRecoveryService"/>
/// runs before its own attempt recovery, so a <c>Validating</c> attempt's promotion
/// evidence is examined before generic recovery could abandon it.
/// </summary>
internal interface IValidatingAttemptRecovery
{
    Task RecoverAsync(CancellationToken cancellationToken);
}

internal enum ValidatedPromotionRecoveryAction
{
    RegisteredCompleteFinal,
    RenamedStagingAndRegistered,
    RemovedStaleJournal,
    QuarantinedIncompleteStaging,
    ClearedInvalidatedPointer,
    ConvergedAlreadyRegistered
}

internal sealed record ValidatedPromotionRecoveryEntry(
    string BuildId,
    string AttemptId,
    string ExtractionId,
    ValidatedPromotionRecoveryAction Action,
    string? Detail);

internal sealed record ValidatedPromotionRecoveryOutcome(
    IReadOnlyList<ValidatedPromotionRecoveryEntry> Entries);

/// <summary>
/// Recovers interrupted validated-extraction promotions, fail-closed. It is driven
/// entirely by sibling promotion journals: for every journal it finds under an owned
/// <c>.staging</c> directory it examines the database row, the final output, and the
/// staged output, then converges on the single safe action for that evidence:
/// register a complete unregistered final output, finish and register provable
/// staging, remove a stale journal after a fully-registered promotion, quarantine and
/// abandon incomplete owned staging, or clear only a proven-invalid selected
/// preference pointer (<see cref="ExtractionPreferenceReason.IntegrityInvalidated"/>).
/// It never invents a manifest, trusts no-marker output, rewrites final files, deletes
/// ambiguous evidence, or auto-selects a custom output; on any inconsistent, tampered,
/// or unowned evidence it throws and preserves the evidence untouched.
/// </summary>
internal sealed class ValidatedExtractionRecoveryService : IValidatingAttemptRecovery
{
    private const string PromotionJournalSuffix = ".promotion.json";

    private readonly string _dataRoot;
    private readonly IExtractionRepository _attemptRepository;
    private readonly IValidatedExtractionRepository _validatedRepository;
    private readonly ValidatedExtractionDocumentStore _documentStore;
    private readonly ValidatedExtractionIntegrityVerifier _integrityVerifier;
    private readonly PromotionJournalStore _journalStore;
    private readonly TimeProvider _timeProvider;

    public ValidatedExtractionRecoveryService(
        string dataRoot,
        IExtractionRepository attemptRepository,
        IValidatedExtractionRepository validatedRepository,
        ValidatedExtractionDocumentStore documentStore,
        ValidatedExtractionIntegrityVerifier integrityVerifier,
        PromotionJournalStore journalStore,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        _attemptRepository = attemptRepository ?? throw new ArgumentNullException(nameof(attemptRepository));
        _validatedRepository = validatedRepository ?? throw new ArgumentNullException(nameof(validatedRepository));
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _integrityVerifier = integrityVerifier ?? throw new ArgumentNullException(nameof(integrityVerifier));
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    async Task IValidatingAttemptRecovery.RecoverAsync(CancellationToken cancellationToken) =>
        await RecoverAsync(cancellationToken);

    public async Task<ValidatedPromotionRecoveryOutcome> RecoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PathSafety.IsNormalDirectory(_dataRoot))
        {
            throw new InvalidOperationException("The Atlas data root is unsafe.");
        }

        var entries = new List<ValidatedPromotionRecoveryEntry>();
        foreach (var located in EnumerateJournals())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = await RecoverJournalAsync(located, cancellationToken);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return new ValidatedPromotionRecoveryOutcome(entries);
    }

    private async Task<ValidatedPromotionRecoveryEntry?> RecoverJournalAsync(
        LocatedJournal located, CancellationToken cancellationToken)
    {
        var journal = await _journalStore.TryReadAsync(located.JournalPath, cancellationToken);
        if (journal is null)
        {
            return null;
        }

        // Fail closed on any inconsistent or unowned evidence before touching anything.
        if (!string.Equals(journal.AttemptId, located.AttemptId, StringComparison.Ordinal) ||
            !string.Equals(journal.BuildId, located.BuildId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The promotion journal '{located.JournalPath}' does not describe its own attempt.");
        }

        var attemptPaths = OwnedValidatedExtractionPaths.ForAttempt(
            _dataRoot, journal.BuildId, journal.AttemptId);
        var ownedAttempt = OwnedAttemptPaths.Create(_dataRoot, journal.BuildId, journal.AttemptId);

        string recomputedExtractionId;
        try
        {
            recomputedExtractionId = ExtractionId.Create(journal.RecipeId, journal.ArtifactManifestDigest);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"The promotion journal '{located.JournalPath}' has an invalid recipe or digest.",
                exception);
        }

        OwnedValidatedExtractionPaths finalPaths;
        try
        {
            finalPaths = OwnedValidatedExtractionPaths.ForExtraction(
                _dataRoot, journal.BuildId, journal.PlannedExtractionId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException(
                $"The promotion journal '{located.JournalPath}' has an unsafe planned extraction ID.",
                exception);
        }

        var finalRoot = finalPaths.FinalExtractionRoot!;
        var stagingRoot = attemptPaths.PromotionStagingRoot!;

        if (!string.Equals(recomputedExtractionId, journal.PlannedExtractionId, StringComparison.Ordinal) ||
            !SamePath(journal.StagingPath, stagingRoot) ||
            !SamePath(journal.FinalExtractionPath, finalRoot) ||
            !SamePath(journal.SourceCandidatePath, ownedAttempt.CandidateOutputRoot))
        {
            throw new InvalidDataException(
                $"The promotion journal '{located.JournalPath}' does not match its owned Atlas paths.");
        }

        var journalPath = attemptPaths.PromotionJournalPath!;
        var extractionId = journal.PlannedExtractionId;
        var dbExtraction = await _validatedRepository.GetValidatedExtractionAsync(
            extractionId, cancellationToken);
        var attempt = await _attemptRepository.GetAttemptAsync(journal.AttemptId, cancellationToken);

        if (dbExtraction is not null)
        {
            return await RecoverWithExistingRowAsync(
                journal, journalPath, extractionId, cancellationToken);
        }

        return await RecoverWithoutRowAsync(
            journal, journalPath, attempt, finalPaths, finalRoot, stagingRoot, extractionId,
            cancellationToken);
    }

    private async Task<ValidatedPromotionRecoveryEntry> RecoverWithExistingRowAsync(
        PromotionJournalContent journal,
        string journalPath,
        string extractionId,
        CancellationToken cancellationToken)
    {
        var integrity = await _integrityVerifier.VerifyAsync(
            _dataRoot, journal.BuildId, extractionId, cancellationToken);
        if (integrity.Status == ValidatedExtractionIntegrityStatus.Valid)
        {
            // A fully registered promotion whose best-effort journal delete was lost.
            _journalStore.DeleteBestEffort(journalPath);
            return Entry(journal, ValidatedPromotionRecoveryAction.RemovedStaleJournal, "final output verified");
        }

        // A registered extraction whose final output is now invalid or missing: clear
        // only the selected pointer (never delete historical extraction data), report.
        var preferred = await _validatedRepository.GetPreferredExtractionAsync(
            journal.BuildId, cancellationToken);
        if (preferred is not null &&
            string.Equals(preferred.ExtractionId, extractionId, StringComparison.Ordinal))
        {
            await _validatedRepository.ClearPreferredExtractionAsync(
                journal.BuildId,
                extractionId,
                ExtractionPreferenceReason.IntegrityInvalidated,
                _timeProvider.GetUtcNow().ToUniversalTime(),
                cancellationToken);
        }

        _journalStore.DeleteBestEffort(journalPath);
        return Entry(
            journal,
            ValidatedPromotionRecoveryAction.ClearedInvalidatedPointer,
            integrity.Message);
    }

    private async Task<ValidatedPromotionRecoveryEntry> RecoverWithoutRowAsync(
        PromotionJournalContent journal,
        string journalPath,
        ExtractionAttempt? attempt,
        OwnedValidatedExtractionPaths finalPaths,
        string finalRoot,
        string stagingRoot,
        string extractionId,
        CancellationToken cancellationToken)
    {
        var finalBundle = await _documentStore.TryReadFinalDocumentsAsync(finalRoot, cancellationToken);
        if (finalBundle is not null)
        {
            var expected = BuildExpected(finalBundle, finalRoot);
            var integrity = await _integrityVerifier.VerifyAsync(
                _dataRoot, finalRoot, expected, finalBundle.ArtifactManifest.Manifest.Entries,
                cancellationToken);
            if (integrity.Status != ValidatedExtractionIntegrityStatus.Valid)
            {
                throw new InvalidDataException(
                    $"Final extraction output for '{extractionId}' exists but is not verifiable and is " +
                    $"not registered; preserving it and failing closed: {integrity.Message}");
            }

            var action = await RegisterAsync(
                journal, journalPath, attempt, expected, finalBundle, extractionId,
                ValidatedPromotionRecoveryAction.RegisteredCompleteFinal, cancellationToken);
            return Entry(journal, action, "registered complete final output");
        }

        var stagingBundle = await _documentStore.TryReadFinalDocumentsAsync(stagingRoot, cancellationToken);
        if (stagingBundle is not null)
        {
            var expected = BuildExpected(stagingBundle, finalRoot);
            var integrity = await _integrityVerifier.VerifyAsync(
                _dataRoot, stagingRoot, expected, stagingBundle.ArtifactManifest.Manifest.Entries,
                cancellationToken);
            if (integrity.Status == ValidatedExtractionIntegrityStatus.Valid)
            {
                RenameStagingToFinal(stagingRoot, finalRoot);
                var action = await RegisterAsync(
                    journal, journalPath, attempt, expected, stagingBundle, extractionId,
                    ValidatedPromotionRecoveryAction.RenamedStagingAndRegistered, cancellationToken);
                return Entry(journal, action, "verified staging, renamed and registered");
            }

            // Staging exists but is not verifiable: quarantine as incomplete evidence.
            return await QuarantineAsync(
                journal, journalPath, attempt, finalPaths, stagingRoot, cancellationToken);
        }

        if (Directory.Exists(stagingRoot))
        {
            return await QuarantineAsync(
                journal, journalPath, attempt, finalPaths, stagingRoot, cancellationToken);
        }

        // No database row, no final output, no staged output: the promotion barely
        // began. The candidate is left intact for generic attempt recovery; only the
        // now-orphaned journal is removed.
        _journalStore.DeleteBestEffort(journalPath);
        return Entry(
            journal, ValidatedPromotionRecoveryAction.RemovedStaleJournal, "orphaned journal, nothing staged");
    }

    private async Task<ValidatedPromotionRecoveryAction> RegisterAsync(
        PromotionJournalContent journal,
        string journalPath,
        ExtractionAttempt? attempt,
        ValidatedExtraction expected,
        ValidatedExtractionDocumentBundle bundle,
        string extractionId,
        ValidatedPromotionRecoveryAction successAction,
        CancellationToken cancellationToken)
    {
        if (attempt is null || attempt.Status != ExtractionAttemptStatus.Validating)
        {
            // No row, complete output, but the attempt is not in the state a pending
            // promotion requires. Check for a concurrent commit before failing closed.
            var converged = await _validatedRepository.GetValidatedExtractionAsync(
                extractionId, cancellationToken);
            if (converged is not null)
            {
                _journalStore.DeleteBestEffort(journalPath);
                return ValidatedPromotionRecoveryAction.ConvergedAlreadyRegistered;
            }

            throw new InvalidDataException(
                $"Complete output for '{extractionId}' exists with no database row, but its source " +
                "attempt is not Validating; preserving evidence and failing closed.");
        }

        if (!string.Equals(bundle.ValidationReport.AttemptId, attempt.AttemptId, StringComparison.Ordinal) ||
            !string.Equals(expected.SourceAttemptId, attempt.AttemptId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The documents for '{extractionId}' do not describe attempt '{attempt.AttemptId}'.");
        }

        var succeeded = attempt with
        {
            Status = ExtractionAttemptStatus.Succeeded,
            CompletedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime(),
            ResultExtractionId = extractionId
        };
        _ = ExtractionAttemptLifecycle.Transition(attempt, succeeded);

        var promotion = new ValidatedExtractionPromotion(
            succeeded,
            expected,
            bundle.ArtifactManifest.Manifest,
            bundle.ValidationReport,
            AutomaticPreferenceReason: null);
        try
        {
            await _validatedRepository.CommitValidatedExtractionAsync(promotion, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            var converged = await _validatedRepository.GetValidatedExtractionAsync(
                extractionId, cancellationToken);
            if (converged is null)
            {
                throw;
            }

            _journalStore.DeleteBestEffort(journalPath);
            return ValidatedPromotionRecoveryAction.ConvergedAlreadyRegistered;
        }

        _journalStore.DeleteBestEffort(journalPath);
        return successAction;
    }

    private async Task<ValidatedPromotionRecoveryEntry> QuarantineAsync(
        PromotionJournalContent journal,
        string journalPath,
        ExtractionAttempt? attempt,
        OwnedValidatedExtractionPaths finalPaths,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var quarantineRoot = finalPaths.QuarantineRoot;
        Directory.CreateDirectory(quarantineRoot);
        OwnedAttemptPaths.EnsureSafeExistingPath(_dataRoot, quarantineRoot);

        if (Directory.Exists(stagingRoot))
        {
            if (!PathSafety.IsNormalDirectory(stagingRoot))
            {
                throw new InvalidDataException(
                    $"The staging root '{stagingRoot}' is unsafe; preserving and failing closed.");
            }

            var target = Path.Combine(quarantineRoot, journal.AttemptId);
            if (Directory.Exists(target) || File.Exists(target))
            {
                target = Path.Combine(quarantineRoot, $"{journal.AttemptId}-{Guid.NewGuid():N}");
            }

            OwnedAttemptPaths.EnsureSafeExistingPath(_dataRoot, target);
            Directory.Move(stagingRoot, target);
        }

        if (attempt is { Status: ExtractionAttemptStatus.Validating })
        {
            var abandoned = attempt with
            {
                Status = ExtractionAttemptStatus.Abandoned,
                CompletedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime(),
                FailureStage = ExtractionFailureStage.Recovery,
                FailureCode = ExtractionFailureCode.InterruptedProcess,
                FailureMessage =
                    "The interrupted extraction promotion left incomplete staging output, which was quarantined."
            };
            _ = ExtractionAttemptLifecycle.Transition(attempt, abandoned);
            await _attemptRepository.TransitionAttemptAsync(
                abandoned, ExtractionAttemptStatus.Validating, cancellationToken);
        }

        _journalStore.DeleteBestEffort(journalPath);
        return Entry(
            journal,
            ValidatedPromotionRecoveryAction.QuarantinedIncompleteStaging,
            "incomplete owned staging quarantined");
    }

    private void RenameStagingToFinal(string stagingRoot, string finalRoot)
    {
        if (Directory.Exists(finalRoot))
        {
            // Another converging process already produced the final output.
            return;
        }

        var finalParent = Path.GetDirectoryName(finalRoot)!;
        Directory.CreateDirectory(finalParent);
        OwnedAttemptPaths.EnsureSafeExistingPath(_dataRoot, finalParent);
        Directory.Move(stagingRoot, finalRoot);
    }

    private static ValidatedExtraction BuildExpected(
        ValidatedExtractionDocumentBundle bundle, string rootPath)
    {
        var document = bundle.Extraction;
        return new ValidatedExtraction(
            document.ExtractionId,
            document.RecipeId,
            document.BuildId,
            document.ToolInstanceId,
            document.SourceAttemptId,
            document.ProfileId,
            document.ProfileVersion,
            document.ProfileDigest,
            document.AdapterVersion,
            document.ExtractionSchemaVersion,
            document.ArtifactManifestDigest,
            rootPath,
            document.CreatedAtUtc,
            document.TrustLevel,
            document.InitialValidationOutcome,
            document.Statistics);
    }

    private IEnumerable<LocatedJournal> EnumerateJournals()
    {
        var buildsRoot = Path.Combine(_dataRoot, "builds");
        if (!Directory.Exists(buildsRoot) || !PathSafety.IsNormalDirectory(buildsRoot))
        {
            yield break;
        }

        var located = new List<LocatedJournal>();
        foreach (var buildDirectory in Directory.GetDirectories(buildsRoot).OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!IsNormalDirectory(buildDirectory))
            {
                continue;
            }

            var buildId = Path.GetFileName(buildDirectory);
            var stagingRoot = Path.Combine(buildDirectory, "extractions", ".staging");
            if (!Directory.Exists(stagingRoot) || !IsNormalDirectory(stagingRoot))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(stagingRoot, "*" + PromotionJournalSuffix, SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(file);
                if (!fileName.EndsWith(PromotionJournalSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                var attemptId = fileName[..^PromotionJournalSuffix.Length];
                if (!OwnedAttemptPaths.IsLowerGuidN(attemptId))
                {
                    throw new InvalidDataException(
                        $"The promotion journal '{file}' does not name a valid attempt.");
                }

                located.Add(new LocatedJournal(file, buildId, attemptId));
            }
        }

        foreach (var entry in located
            .OrderBy(item => item.BuildId, StringComparer.Ordinal)
            .ThenBy(item => item.AttemptId, StringComparer.Ordinal))
        {
            yield return entry;
        }
    }

    private static bool IsNormalDirectory(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SamePath(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static ValidatedPromotionRecoveryEntry Entry(
        PromotionJournalContent journal, ValidatedPromotionRecoveryAction action, string? detail) =>
        new(journal.BuildId, journal.AttemptId, journal.PlannedExtractionId, action, detail);

    private readonly record struct LocatedJournal(string JournalPath, string BuildId, string AttemptId);
}
