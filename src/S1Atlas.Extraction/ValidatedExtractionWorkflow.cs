using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Extraction.Manifests;

namespace S1Atlas.Extraction;

/// <summary>
/// The single Phase 4 extraction application workflow. It layers reuse, policy-only
/// revalidation, and existing-candidate consumption on top of Phase 3, and exposes
/// only integrity-verified validated output as authoritative. Its decision order,
/// executed exactly once per invocation, is:
/// <list type="number">
/// <item>initialize the repository;</item>
/// <item>run validated-promotion recovery, then generic attempt recovery;</item>
/// <item>prepare build/profile/policy/tool/recipe (no game input, no Cpp2IL);</item>
/// <item>unless <c>--retry</c>: list the recipe's validated outputs, disambiguate a
/// single one with the current preferred output (or fail <c>RecipeOutputAmbiguous</c>),
/// then either perform an authoritative integrity-verified no-op (a verified
/// current-policy summary whose strict report agrees), or a validation-only
/// revalidation (a selected output lacking a current-policy evaluation), or — when no
/// validated output exists yet — consume the newest existing <c>ProcessCompleted</c>
/// candidate without a new process;</item>
/// <item>only when none of the above satisfies the request (or on <c>--retry</c>), run
/// the Phase 3 prepared Cpp2IL process, then validate and promote the returned
/// candidate.</item>
/// </list>
/// No no-op or revalidation path ever resolves a live <c>--game-path</c> or runs Cpp2IL.
/// </summary>
internal sealed class ValidatedExtractionWorkflow
{
    private readonly string _dataRoot;
    private readonly Func<CancellationToken, Task> _initializeAsync;
    private readonly Func<CancellationToken, Task> _recoverAsync;
    private readonly Func<ExtractionOptions, CancellationToken, Task<PreparedExtractionContext>> _prepareAsync;
    private readonly IValidatedExtractionRepository _validatedRepository;
    private readonly ValidatedExtractionIntegrityVerifier _integrityVerifier;
    private readonly ValidatedExtractionDocumentStore _documentStore;
    private readonly ExtractionValidationService _validationService;
    private readonly Func<PreparedExtractionContext, ExtractionOptions, CancellationToken,
        Task<ExtractionOperationResult>> _runPreparedProcessAsync;
    private readonly Func<string, string, string, DateTimeOffset, CancellationToken, Task>
        _markInputSnapshotReplayVerifiedAsync;
    private readonly TimeProvider _timeProvider;

    public ValidatedExtractionWorkflow(
        string dataRoot,
        Func<CancellationToken, Task> initializeAsync,
        Func<CancellationToken, Task> recoverAsync,
        Func<ExtractionOptions, CancellationToken, Task<PreparedExtractionContext>> prepareAsync,
        IValidatedExtractionRepository validatedRepository,
        ValidatedExtractionIntegrityVerifier integrityVerifier,
        ValidatedExtractionDocumentStore documentStore,
        ExtractionValidationService validationService,
        Func<PreparedExtractionContext, ExtractionOptions, CancellationToken,
            Task<ExtractionOperationResult>> runPreparedProcessAsync,
        Func<string, string, string, DateTimeOffset, CancellationToken, Task>
            markInputSnapshotReplayVerifiedAsync,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        _initializeAsync = initializeAsync ?? throw new ArgumentNullException(nameof(initializeAsync));
        _recoverAsync = recoverAsync ?? throw new ArgumentNullException(nameof(recoverAsync));
        _prepareAsync = prepareAsync ?? throw new ArgumentNullException(nameof(prepareAsync));
        _validatedRepository = validatedRepository ?? throw new ArgumentNullException(nameof(validatedRepository));
        _integrityVerifier = integrityVerifier ?? throw new ArgumentNullException(nameof(integrityVerifier));
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
        _runPreparedProcessAsync = runPreparedProcessAsync
            ?? throw new ArgumentNullException(nameof(runPreparedProcessAsync));
        _markInputSnapshotReplayVerifiedAsync = markInputSnapshotReplayVerifiedAsync
            ?? throw new ArgumentNullException(nameof(markInputSnapshotReplayVerifiedAsync));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ExtractionWorkflowResult> RunAsync(
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProfileId);

        await _initializeAsync(cancellationToken);
        await _recoverAsync(cancellationToken);

        var context = await _prepareAsync(options, cancellationToken);

        if (!options.Retry)
        {
            var recipeOutputs = await _validatedRepository.ListValidatedExtractionsByRecipeAsync(
                context.RecipeId, cancellationToken);
            if (recipeOutputs.Count > 0)
            {
                var selected = await SelectRecipeOutputAsync(recipeOutputs, context, cancellationToken);

                var reuse = await TryReuseSelectedAsync(selected, context, cancellationToken);
                if (reuse is not null)
                {
                    return reuse;
                }

                // The selected output lacks a usable current-policy evaluation (no stored
                // summary, or its strict report is missing or disagrees): re-check it under
                // the current policy without rerunning Cpp2IL.
                return await _validationService.RevalidateExistingExtractionAsync(
                    selected, context, cancellationToken);
            }

            // No validated output exists for this recipe yet: consume the newest existing
            // Phase 3 candidate without launching a new process, if one is available.
            var candidates = await _validatedRepository.ListProcessCompletedAttemptsAsync(
                context.RecipeId, cancellationToken);
            var candidate = SelectNewestCandidate(candidates);
            if (candidate is not null)
            {
                return await _validationService.ValidateAndPromoteCandidateAsync(
                    candidate, context, processWasRun: false, cancellationToken);
            }
        }

        // --retry, or nothing above satisfies the request: run the Phase 3 prepared Cpp2IL
        // process and validate/promote the candidate it returns.
        var processResult = await _runPreparedProcessAsync(context, options, cancellationToken);
        var result = await _validationService.ValidateAndPromoteCandidateAsync(
            processResult.Attempt, context, processWasRun: true, cancellationToken);
        result = result with
        {
            InputSource = processResult.InputSource,
            InputSnapshotId = processResult.InputSnapshotId
        };

        return await CertifyArchivedReplayIfAuthoritativeAsync(
            result, processResult, context, cancellationToken);
    }

    /// <summary>
    /// Certifies the input snapshot replay-verified only after Cpp2IL ran from that exact
    /// archived snapshot and Phase 4 returned an authoritative validated extraction. No
    /// no-op, revalidation, existing-candidate, live, or non-authoritative result reaches
    /// here. Certification uses the process attempt's pre-input manifest digest and is
    /// idempotent; if the certification commit fails the authoritative extraction is
    /// preserved and the failure is surfaced as an operational error.
    /// </summary>
    private async Task<ExtractionWorkflowResult> CertifyArchivedReplayIfAuthoritativeAsync(
        ExtractionWorkflowResult result,
        ExtractionOperationResult processResult,
        PreparedExtractionContext context,
        CancellationToken cancellationToken)
    {
        if (!result.IsAuthoritative ||
            processResult.InputSource != ExtractionInputSource.ArchivedSnapshot ||
            processResult.InputSnapshotId is not { } snapshotId)
        {
            return result;
        }

        var preInputDigest = processResult.Attempt.PreInputManifestDigest
            ?? throw new ExtractionOperationException(
                ExtractionFailureStage.DatabasePromotion,
                ExtractionFailureCode.DatabasePromotionFailed,
                "The archived process attempt has no pre-input manifest digest to certify.",
                result.AttemptId);

        try
        {
            await _markInputSnapshotReplayVerifiedAsync(
                snapshotId,
                context.Build.BuildId,
                preInputDigest,
                _timeProvider.GetUtcNow().ToUniversalTime(),
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
                $"The authoritative archived extraction was preserved but its input " +
                $"snapshot '{snapshotId}' could not be certified replay-verified.",
                result.AttemptId,
                exception);
        }

        return result with { InputSnapshotReplayVerified = true };
    }

    private async Task<ValidatedExtraction> SelectRecipeOutputAsync(
        IReadOnlyList<ValidatedExtraction> recipeOutputs,
        PreparedExtractionContext context,
        CancellationToken cancellationToken)
    {
        if (recipeOutputs.Count == 1)
        {
            return recipeOutputs[0];
        }

        var preferred = await _validatedRepository.GetPreferredExtractionAsync(
            context.Build.BuildId, cancellationToken);
        var matches = recipeOutputs
            .Where(output => preferred is not null &&
                string.Equals(output.ExtractionId, preferred.ExtractionId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 1)
        {
            return matches[0];
        }

        throw new ExtractionOperationException(
            ExtractionFailureStage.ReproducibilityValidation,
            ExtractionFailureCode.RecipeOutputAmbiguous,
            $"The recipe has {recipeOutputs.Count} validated outputs and no single current " +
            "preferred output disambiguates reuse; promote one explicitly.");
    }

    private async Task<ExtractionWorkflowResult?> TryReuseSelectedAsync(
        ValidatedExtraction selected,
        PreparedExtractionContext context,
        CancellationToken cancellationToken)
    {
        var stored = await _validatedRepository.GetLatestValidationResultAsync(
            selected.ExtractionId, context.Policy.PolicyDigest, cancellationToken);
        if (stored is null || stored.Outcome == ValidationOutcome.Invalid)
        {
            return null;
        }

        var report = await _documentStore.TryReadValidationReportAsync(
            stored.ReportPath, cancellationToken);
        if (report is null || !ReportAgreesWithSummary(report, stored))
        {
            // A missing or malformed strict report means revalidation is required, never
            // a silent success.
            return null;
        }

        var integrity = await _integrityVerifier.VerifyAsync(
            _dataRoot, selected.BuildId, selected.ExtractionId, cancellationToken);
        if (integrity.Status != ValidatedExtractionIntegrityStatus.Valid)
        {
            await ClearPreferredIfSelectedAsync(
                selected, ExtractionPreferenceReason.IntegrityInvalidated, cancellationToken);
            throw new ExtractionOperationException(
                ExtractionFailureStage.Recovery,
                ExtractionFailureCode.IntegrityMismatch,
                $"The reusable validated extraction '{selected.ExtractionId}' failed integrity " +
                $"verification and cannot be returned as authoritative: {integrity.Message}");
        }

        var preferred = await _validatedRepository.GetPreferredExtractionAsync(
            selected.BuildId, cancellationToken);
        var isPreferred = preferred is not null &&
            string.Equals(preferred.ExtractionId, selected.ExtractionId, StringComparison.Ordinal);

        return new ExtractionWorkflowResult(
            AttemptId: null,
            selected.BuildId,
            context.RecipeId,
            selected.ExtractionId,
            selected.RootPath,
            selected.TrustLevel,
            report.Outcome,
            ProcessWasRun: false,
            ValidationWasRun: false,
            ReusedExistingExtraction: true,
            IsPreferred: isPreferred,
            IsAuthoritative: true);
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

    private static ExtractionAttempt? SelectNewestCandidate(IReadOnlyList<ExtractionAttempt> candidates) =>
        candidates
            .OrderByDescending(attempt => attempt.CompletedAtUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(attempt => attempt.AttemptId, StringComparer.Ordinal)
            .FirstOrDefault();

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
}
