using S1Atlas.Core.Extraction;
using S1Atlas.Core.Hashing;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Extraction.Promotion;
using S1Atlas.Extraction.Validation;

namespace S1Atlas.Extraction;

/// <summary>
/// Owns the Phase 4 validation and promotion of a single subject: either a Phase 3
/// <c>ProcessCompleted</c> candidate (validate then promote a new/linked extraction,
/// or fail it) or an already-validated extraction re-checked against the current
/// policy without rerunning Cpp2IL. It acquires the shared extraction lock for the
/// whole validation/promotion so no concurrent process can race the candidate, runs
/// the source-attempt integrity checks, candidate inspection, and the validation
/// engine, writes the strict attempt-level <c>validation.json</c>, and delegates the
/// safety-critical filesystem/SQLite promotion to <see cref="ValidatedExtractionPromoter"/>.
/// </summary>
internal sealed class ExtractionValidationService
{
    private readonly string _dataRoot;
    private readonly IValidatedExtractionRepository _validatedRepository;
    private readonly IExtractionRepository _attemptRepository;
    private readonly ValidatedExtractionDocumentStore _documentStore;
    private readonly ValidatedExtractionIntegrityVerifier _integrityVerifier;
    private readonly CandidateOutputInspector _candidateInspector;
    private readonly ValidatedExtractionPromoter _promoter;
    private readonly AttemptDocumentStore _attemptDocumentStore;
    private readonly ExtractionLock _extractionLock;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _attemptIdFactory;

    public ExtractionValidationService(
        string dataRoot,
        IValidatedExtractionRepository validatedRepository,
        IExtractionRepository attemptRepository,
        IFileHasher fileHasher,
        ValidatedExtractionDocumentStore documentStore,
        ValidatedExtractionIntegrityVerifier integrityVerifier,
        CandidateOutputInspector candidateInspector,
        PromotionJournalStore journalStore,
        AttemptDocumentStore attemptDocumentStore,
        ExtractionLock extractionLock,
        TimeProvider? timeProvider = null,
        Func<string>? attemptIdFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(fileHasher);
        ArgumentNullException.ThrowIfNull(journalStore);
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        _validatedRepository = validatedRepository ?? throw new ArgumentNullException(nameof(validatedRepository));
        _attemptRepository = attemptRepository ?? throw new ArgumentNullException(nameof(attemptRepository));
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _integrityVerifier = integrityVerifier ?? throw new ArgumentNullException(nameof(integrityVerifier));
        _candidateInspector = candidateInspector ?? throw new ArgumentNullException(nameof(candidateInspector));
        _attemptDocumentStore = attemptDocumentStore ?? throw new ArgumentNullException(nameof(attemptDocumentStore));
        _extractionLock = extractionLock ?? throw new ArgumentNullException(nameof(extractionLock));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _attemptIdFactory = attemptIdFactory ?? (() => Guid.NewGuid().ToString("N"));
        _promoter = new ValidatedExtractionPromoter(
            _dataRoot,
            _validatedRepository,
            _documentStore,
            _integrityVerifier,
            journalStore,
            _candidateInspector,
            _timeProvider);
    }

    /// <summary>
    /// Validates a <c>ProcessCompleted</c> candidate and promotes it to a new validated
    /// extraction, links it to an identical existing extraction, or fails it. Acquires
    /// the extraction lock for the whole operation; if another process took the lock
    /// between the Phase 3 release and here, it fails cleanly and leaves the attempt in
    /// <c>ProcessCompleted</c> so the next invocation consumes it.
    /// </summary>
    public async Task<ExtractionWorkflowResult> ValidateAndPromoteCandidateAsync(
        ExtractionAttempt processCompleted,
        PreparedExtractionContext context,
        bool processWasRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processCompleted);
        ArgumentNullException.ThrowIfNull(context);
        if (processCompleted.Status != ExtractionAttemptStatus.ProcessCompleted)
        {
            throw new ArgumentException(
                "Candidate validation requires a ProcessCompleted source attempt.",
                nameof(processCompleted));
        }

        var lease = await AcquireLockAsync(processCompleted.AttemptId, cancellationToken);
        try
        {
            var ownedAttempt = OwnedAttemptPaths.Create(
                _dataRoot, processCompleted.BuildId, processCompleted.AttemptId);

            var validating = processCompleted with { Status = ExtractionAttemptStatus.Validating };
            _ = ExtractionAttemptLifecycle.Transition(processCompleted, validating);
            await _attemptRepository.TransitionAttemptAsync(
                validating, ExtractionAttemptStatus.ProcessCompleted, cancellationToken);
            await WriteAttemptDocumentBestEffortAsync(ownedAttempt, validating, cancellationToken);

            var inputIntegrityPassed = InputIntegrityPassed(validating);
            var processIntegrityPassed = ProcessIntegrityPassed(validating, context);

            var inspection = await _candidateInspector.InspectAsync(
                validating, _dataRoot, cancellationToken);
            ArtifactBuildResult? artifactBuild =
                inspection.ContainmentPassed && inspection.Inventory is not null
                    ? ArtifactManifestBuilder.Build(inspection.Inventory)
                    : null;

            var candidateExtractionId = artifactBuild is not null
                ? ExtractionId.Create(context.RecipeId, artifactBuild.ManifestDigest)
                : null;

            var baseline = await ResolveBaselineAsync(context, cancellationToken);
            var sameRecipe = await _validatedRepository.ListValidatedExtractionsByRecipeAsync(
                context.RecipeId, cancellationToken);

            var validatedAt = _timeProvider.GetUtcNow().ToUniversalTime();
            var evaluation = ExtractionValidationEngine.Evaluate(new ExtractionValidationRequest(
                validating,
                ValidationSubjectKind.CandidateOutput,
                SubjectExtractionId: null,
                inspection,
                artifactBuild,
                inputIntegrityPassed,
                processIntegrityPassed,
                context.Policy,
                baseline.Statistics,
                baseline.ExtractionId,
                sameRecipe,
                context.Tool.Instance.TrustLevel,
                baseline.Preferred,
                baseline.PreferredToolInstanceId,
                validatedAt));

            var report = evaluation.Report;
            var resolvedExtractionId = evaluation.DeduplicationTarget?.ExtractionId ?? candidateExtractionId;
            if (report.Outcome != ValidationOutcome.Invalid && resolvedExtractionId is not null)
            {
                report = report with { SubjectExtractionId = resolvedExtractionId };
            }

            // A new-output promotion never writes the attempt-level validation.json itself,
            // yet the stored validation summary's derived ReportPath points there, so it
            // must exist for later same-policy reuse to read and verify.
            if (report.Outcome != ValidationOutcome.Invalid && evaluation.DeduplicationTarget is null)
            {
                var attemptPaths = OwnedValidatedExtractionPaths.ForAttempt(
                    _dataRoot, validating.BuildId, validating.AttemptId);
                await _documentStore.WriteAttemptValidationReportAsync(
                    attemptPaths, report, cancellationToken);
            }

            var promoted = await _promoter.PromoteAsync(
                new ValidatedExtractionPromotionRequest(
                    validating,
                    report,
                    artifactBuild?.Manifest,
                    context.Tool.Instance.TrustLevel,
                    evaluation.DeduplicationTarget,
                    evaluation.AutomaticPreferenceReason,
                    KeepInvalidOutput: processCompleted.KeepFailedArtifacts),
                cancellationToken);

            return BuildCandidateResult(context, report, promoted, processWasRun);
        }
        finally
        {
            await ReleaseLockAsync(lease);
        }
    }

    /// <summary>
    /// Re-checks an already-validated extraction against the current policy without
    /// rerunning Cpp2IL. It first proves full extraction integrity, then evaluates the
    /// immutable artifact manifest and statistics under the current policy in a
    /// validation-only attempt. A <c>Valid</c>/<c>ValidWithWarnings</c> outcome records a
    /// revalidation and keeps the same extraction ID; an <c>Invalid</c> outcome retains
    /// the historical extraction, fails the attempt, and clears a selected preference
    /// pointer with <see cref="ExtractionPreferenceReason.PolicyInvalidated"/>.
    /// </summary>
    public async Task<ExtractionWorkflowResult> RevalidateExistingExtractionAsync(
        ValidatedExtraction extraction,
        PreparedExtractionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(context);

        var attemptId = _attemptIdFactory();
        var lease = await AcquireLockAsync(attemptId, cancellationToken);
        try
        {
            var integrity = await _integrityVerifier.VerifyAsync(
                _dataRoot, extraction.BuildId, extraction.ExtractionId, cancellationToken);
            if (integrity.Status != ValidatedExtractionIntegrityStatus.Valid)
            {
                await ClearPreferredIfSelectedAsync(
                    extraction, ExtractionPreferenceReason.IntegrityInvalidated, cancellationToken);
                throw new ExtractionOperationException(
                    ExtractionFailureStage.Recovery,
                    ExtractionFailureCode.IntegrityMismatch,
                    $"The validated extraction '{extraction.ExtractionId}' could not be reused " +
                    $"because it failed integrity verification: {integrity.Message}");
            }

            var ownedAttempt = OwnedAttemptPaths.Create(_dataRoot, extraction.BuildId, attemptId);
            var createdAt = _timeProvider.GetUtcNow().ToUniversalTime();
            var created = CreateRevalidationAttempt(attemptId, extraction, context, ownedAttempt, createdAt);
            await _attemptRepository.CreateAttemptAsync(created, cancellationToken);
            await WriteZeroByteLogsAsync(ownedAttempt);
            await _attemptDocumentStore.WriteAsync(
                ownedAttempt, created, executionFacts: null, cancellationToken);

            var validating = created with
            {
                Status = ExtractionAttemptStatus.Validating,
                StartedAtUtc = createdAt
            };
            _ = ExtractionAttemptLifecycle.Transition(created, validating);
            await _attemptRepository.TransitionAttemptAsync(
                validating, ExtractionAttemptStatus.Created, cancellationToken);
            await _attemptDocumentStore.WriteAsync(
                ownedAttempt, validating, executionFacts: null, cancellationToken);

            var artifacts = await _validatedRepository.GetExtractionArtifactsAsync(
                extraction.ExtractionId, cancellationToken);
            var artifactBuild = new ArtifactBuildResult(
                new ArtifactManifest(1, artifacts),
                extraction.ArtifactManifestDigest,
                extraction.Statistics,
                Issues: []);

            var baseline = await ResolveBaselineAsync(context, cancellationToken);
            var sameRecipe = await _validatedRepository.ListValidatedExtractionsByRecipeAsync(
                context.RecipeId, cancellationToken);

            var validatedAt = _timeProvider.GetUtcNow().ToUniversalTime();
            var evaluation = ExtractionValidationEngine.Evaluate(new ExtractionValidationRequest(
                validating,
                ValidationSubjectKind.ValidatedExtraction,
                extraction.ExtractionId,
                new CandidateInspectionResult(
                    new CandidateInventory(extraction.RootPath, [], 0),
                    ContainmentPassed: true,
                    Issues: []),
                artifactBuild,
                InputIntegrityPassed: true,
                ProcessIntegrityPassed: true,
                context.Policy,
                baseline.Statistics,
                baseline.ExtractionId,
                sameRecipe,
                extraction.TrustLevel,
                baseline.Preferred,
                baseline.PreferredToolInstanceId,
                validatedAt));

            var report = evaluation.Report;
            var attemptPaths = OwnedValidatedExtractionPaths.ForAttempt(
                _dataRoot, extraction.BuildId, attemptId);
            await _documentStore.WriteAttemptValidationReportAsync(attemptPaths, report, cancellationToken);

            if (report.Outcome == ValidationOutcome.Invalid)
            {
                var failed = validating with
                {
                    Status = ExtractionAttemptStatus.Failed,
                    CompletedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime(),
                    FailureStage = ExtractionFailureStage.SanityValidation,
                    FailureCode = ExtractionFailureCode.ValidationPolicyInvalid,
                    FailureMessage = FirstErrorMessage(report)
                };
                _ = ExtractionAttemptLifecycle.Transition(validating, failed);
                await _validatedRepository.SaveValidationFailureAsync(
                    new ValidationPersistence(failed, report),
                    ExtractionAttemptStatus.Validating,
                    cancellationToken);
                await ClearPreferredIfSelectedAsync(
                    extraction, ExtractionPreferenceReason.PolicyInvalidated, cancellationToken);
                await _attemptDocumentStore.WriteAsync(
                    ownedAttempt, failed, executionFacts: null, cancellationToken);

                return new ExtractionWorkflowResult(
                    failed.AttemptId,
                    extraction.BuildId,
                    context.RecipeId,
                    extraction.ExtractionId,
                    extraction.RootPath,
                    extraction.TrustLevel,
                    ValidationOutcome.Invalid,
                    ProcessWasRun: false,
                    ValidationWasRun: true,
                    ReusedExistingExtraction: false,
                    IsPreferred: false,
                    IsAuthoritative: false);
            }

            var succeeded = validating with
            {
                Status = ExtractionAttemptStatus.Succeeded,
                CompletedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime(),
                ResultExtractionId = extraction.ExtractionId
            };
            _ = ExtractionAttemptLifecycle.Transition(validating, succeeded);
            await _validatedRepository.SaveRevalidationAsync(
                new ValidationPersistence(succeeded, report),
                ExtractionAttemptStatus.Validating,
                cancellationToken);
            await _attemptDocumentStore.WriteAsync(
                ownedAttempt, succeeded, executionFacts: null, cancellationToken);

            var isPreferred = await ApplyAutomaticPreferenceAsync(
                extraction, evaluation.AutomaticPreferenceReason, validatedAt, cancellationToken);

            return new ExtractionWorkflowResult(
                succeeded.AttemptId,
                extraction.BuildId,
                context.RecipeId,
                extraction.ExtractionId,
                extraction.RootPath,
                extraction.TrustLevel,
                report.Outcome,
                ProcessWasRun: false,
                ValidationWasRun: true,
                ReusedExistingExtraction: false,
                IsPreferred: isPreferred,
                IsAuthoritative: true);
        }
        finally
        {
            await ReleaseLockAsync(lease);
        }
    }

    private ExtractionWorkflowResult BuildCandidateResult(
        PreparedExtractionContext context,
        ValidationReport report,
        ValidatedExtractionPromotionResult promoted,
        bool processWasRun)
    {
        var authoritative =
            report.Outcome != ValidationOutcome.Invalid &&
            promoted.Disposition != PromotionDisposition.ValidationFailed;
        return new ExtractionWorkflowResult(
            promoted.Attempt.AttemptId,
            context.Build.BuildId,
            context.RecipeId,
            promoted.ExtractionId ?? string.Empty,
            promoted.ExtractionRoot ?? string.Empty,
            context.Tool.Instance.TrustLevel,
            report.Outcome,
            ProcessWasRun: processWasRun,
            ValidationWasRun: true,
            ReusedExistingExtraction: false,
            IsPreferred: promoted.IsPreferred,
            IsAuthoritative: authoritative);
    }

    private async Task<BaselineFacts> ResolveBaselineAsync(
        PreparedExtractionContext context,
        CancellationToken cancellationToken)
    {
        var preferred = await _validatedRepository.GetPreferredExtractionAsync(
            context.Build.BuildId, cancellationToken);
        if (preferred is null)
        {
            return new BaselineFacts(null, null, null, null);
        }

        var preferredExtraction = await _validatedRepository.GetValidatedExtractionAsync(
            preferred.ExtractionId, cancellationToken);
        if (preferredExtraction is null)
        {
            return new BaselineFacts(null, null, preferred, null);
        }

        var baselineStatistics = preferredExtraction.Statistics;
        var stored = await _validatedRepository.GetLatestValidationResultAsync(
            preferred.ExtractionId, context.Policy.PolicyDigest, cancellationToken);
        if (stored is not null && stored.Outcome != ValidationOutcome.Invalid)
        {
            var storedReport = await _documentStore.TryReadValidationReportAsync(
                stored.ReportPath, cancellationToken);
            if (storedReport is not null && ReportAgreesWithSummary(storedReport, stored))
            {
                baselineStatistics = storedReport.Statistics;
            }
        }

        return new BaselineFacts(
            baselineStatistics,
            preferred.ExtractionId,
            preferred,
            preferredExtraction.ToolInstanceId);
    }

    private async Task<bool> ApplyAutomaticPreferenceAsync(
        ValidatedExtraction extraction,
        ExtractionPreferenceReason? reason,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        if (reason is not { } selectionReason)
        {
            return false;
        }

        var preferred = await _validatedRepository.GetPreferredExtractionAsync(
            extraction.BuildId, cancellationToken);
        if (preferred is not null &&
            string.Equals(preferred.ExtractionId, extraction.ExtractionId, StringComparison.Ordinal))
        {
            return true;
        }

        await _validatedRepository.SetPreferredExtractionAsync(
            new PreferredExtraction(
                extraction.BuildId, extraction.ExtractionId, occurredAtUtc, selectionReason),
            cancellationToken);
        return true;
    }

    private async Task ClearPreferredIfSelectedAsync(
        ValidatedExtraction extraction,
        ExtractionPreferenceReason reason,
        CancellationToken cancellationToken)
    {
        var preferred = await _validatedRepository.GetPreferredExtractionAsync(
            extraction.BuildId, cancellationToken);
        if (preferred is not null &&
            string.Equals(preferred.ExtractionId, extraction.ExtractionId, StringComparison.Ordinal))
        {
            await _validatedRepository.ClearPreferredExtractionAsync(
                extraction.BuildId,
                extraction.ExtractionId,
                reason,
                _timeProvider.GetUtcNow().ToUniversalTime(),
                cancellationToken);
        }
    }

    private ExtractionAttempt CreateRevalidationAttempt(
        string attemptId,
        ValidatedExtraction extraction,
        PreparedExtractionContext context,
        OwnedAttemptPaths ownedAttempt,
        DateTimeOffset createdAt) => new(
        AttemptId: attemptId,
        RecipeId: context.RecipeId,
        BuildId: extraction.BuildId,
        ToolInstanceId: context.Tool.Instance.ToolInstanceId,
        ProfileId: context.Profile.Profile.ProfileId,
        ProfileVersion: context.Profile.Profile.ProfileVersion,
        ProfileDigest: context.Profile.ProfileDigest,
        ValidationPolicyId: context.Policy.Policy.PolicyId,
        ValidationPolicyVersion: context.Policy.Policy.PolicyVersion,
        ValidationPolicyDigest: context.Policy.PolicyDigest,
        AdapterVersion: context.Profile.Profile.AdapterVersion,
        ExtractionSchemaVersion: context.Profile.Profile.ExtractionSchemaVersion,
        InputSource: null,
        InputSnapshotId: null,
        Status: ExtractionAttemptStatus.Created,
        CreatedAtUtc: createdAt,
        StartedAtUtc: null,
        CompletedAtUtc: null,
        PreInputManifestDigest: null,
        PostInputManifestDigest: null,
        WorkingPath: ownedAttempt.WorkingRoot,
        StandardOutputPath: Path.Combine(ownedAttempt.FinalLogsRoot, "stdout.log"),
        StandardErrorPath: Path.Combine(ownedAttempt.FinalLogsRoot, "stderr.log"),
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
        ResultExtractionId: null,
        ValidationSourceExtractionId: extraction.ExtractionId);

    private async Task WriteZeroByteLogsAsync(OwnedAttemptPaths ownedAttempt)
    {
        Directory.CreateDirectory(ownedAttempt.FinalLogsRoot);
        OwnedAttemptPaths.EnsureSafeExistingPath(ownedAttempt.DataRoot, ownedAttempt.FinalLogsRoot);
        foreach (var name in new[] { "stdout.log", "stderr.log" })
        {
            var path = Path.Combine(ownedAttempt.FinalLogsRoot, name);
            if (!File.Exists(path))
            {
                await using var stream = new FileStream(
                    path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
        }
    }

    private async Task WriteAttemptDocumentBestEffortAsync(
        OwnedAttemptPaths ownedAttempt,
        ExtractionAttempt attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            await _attemptDocumentStore.WriteAsync(
                ownedAttempt, attempt, executionFacts: null, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The database is authoritative for attempt status; a filesystem mirror
            // failure here must not abort the validation the candidate still requires.
        }
    }

    private static bool InputIntegrityPassed(ExtractionAttempt attempt) =>
        !string.IsNullOrEmpty(attempt.PreInputManifestDigest) &&
        string.Equals(
            attempt.PreInputManifestDigest, attempt.PostInputManifestDigest, StringComparison.Ordinal);

    private static bool ProcessIntegrityPassed(
        ExtractionAttempt attempt, PreparedExtractionContext context) =>
        attempt.ProcessExitCode is int exitCode &&
        context.Profile.Profile.AcceptedExitCodes.Contains(exitCode) &&
        string.Equals(attempt.RecipeId, context.RecipeId, StringComparison.Ordinal) &&
        string.Equals(attempt.ProfileDigest, context.Profile.ProfileDigest, StringComparison.Ordinal) &&
        string.Equals(attempt.ProfileId, context.Profile.Profile.ProfileId, StringComparison.Ordinal) &&
        attempt.ProfileVersion == context.Profile.Profile.ProfileVersion &&
        attempt.AdapterVersion == context.Profile.Profile.AdapterVersion &&
        attempt.ExtractionSchemaVersion == context.Profile.Profile.ExtractionSchemaVersion;

    private static bool ReportAgreesWithSummary(ValidationReport report, StoredValidationResult summary) =>
        string.Equals(report.AttemptId, summary.AttemptId, StringComparison.Ordinal) &&
        string.Equals(report.SubjectExtractionId, summary.SubjectExtractionId, StringComparison.Ordinal) &&
        string.Equals(
            report.ArtifactManifestDigest, summary.ArtifactManifestDigest, StringComparison.Ordinal) &&
        string.Equals(report.PolicyId, summary.PolicyId, StringComparison.Ordinal) &&
        report.PolicyVersion == summary.PolicyVersion &&
        string.Equals(report.PolicyDigest, summary.PolicyDigest, StringComparison.Ordinal) &&
        report.Outcome == summary.Outcome &&
        report.PreferenceEligible == summary.PreferenceEligible &&
        report.ValidatedAtUtc == summary.ValidatedAtUtc;

    private static string FirstErrorMessage(ValidationReport report) =>
        report.Issues
            .FirstOrDefault(issue => issue.Severity == ValidationIssueSeverity.Error)?.Message
        ?? "The extraction failed validation under the current policy.";

    private async Task<ExtractionLockLease> AcquireLockAsync(
        string attemptId, CancellationToken cancellationToken)
    {
        try
        {
            return await _extractionLock.AcquireAsync(attemptId, cancellationToken);
        }
        catch (ExtractionAlreadyActiveException exception)
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.Recovery,
                ExtractionFailureCode.ExtractionAlreadyActive,
                "Another extraction process holds the lock; the candidate is preserved for the " +
                "next invocation to validate.",
                innerException: exception);
        }
    }

    private static async Task ReleaseLockAsync(ExtractionLockLease lease)
    {
        try
        {
            await lease.ReleaseAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Ownership already changed; the lock evidence is preserved by the lease.
        }
    }

    private readonly record struct BaselineFacts(
        ExtractionStatistics? Statistics,
        string? ExtractionId,
        PreferredExtraction? Preferred,
        string? PreferredToolInstanceId);
}
