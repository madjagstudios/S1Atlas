using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Manifests;
using S1Atlas.Extraction.Tools;

namespace S1Atlas.Extraction.History;

internal enum ExtractionHistoryEntryKind
{
    ValidatedExtraction,
    Attempt
}

/// <summary>
/// One row in the <c>extractions list</c> projection: either a validated extraction
/// or (with <c>--include-failed</c>) a terminal/candidate attempt. Trust is nullable
/// because attempt trust resolves through <see cref="IToolRepository"/> and missing
/// provenance fails closed rather than inventing a trust level.
/// </summary>
internal sealed record ExtractionHistoryItem(
    ExtractionHistoryEntryKind Kind,
    string Id,
    string BuildId,
    string? RecipeId,
    DateTimeOffset CreatedAtUtc,
    string Status,
    ToolTrustLevel? ToolTrustLevel,
    ValidationOutcome? ValidationOutcome,
    bool Preferred,
    string? ResultExtractionId);

internal sealed record ExtractionHistoryDetail(
    ExtractionHistoryEntryKind Kind,
    ValidatedExtraction? Extraction,
    bool IntegrityVerified,
    bool Preferred,
    ExtractionAttempt? Attempt,
    ToolTrustLevel? AttemptToolTrustLevel);

internal sealed record ManualPromotionOutcome(
    ValidatedExtraction Extraction,
    ValidationOutcome Outcome,
    ToolTrustLevel ToolTrustLevel,
    bool WasAlreadyPreferred,
    bool Revalidated);

/// <summary>
/// Read and manual-promotion surface behind <c>extractions list/show/promote</c>. It
/// never issues HTTP or runs Cpp2IL. <c>show</c>/<c>promote</c> of an extraction prove
/// full integrity before exposing the root; a mismatch is an operational failure that
/// never returns the root as authoritative. Manual promotion reuses a verified
/// current-policy result or creates a revalidation attempt, requires a
/// <see cref="ValidationOutcome.Valid"/>/<see cref="ValidationOutcome.ValidWithWarnings"/>
/// outcome, explicitly permits preference-blocking warnings, and records a
/// <see cref="ExtractionPreferenceReason.ManualPromotion"/> audit idempotently.
/// </summary>
internal sealed class ExtractionHistoryService
{
    private readonly string _dataRoot;
    private readonly IValidatedExtractionRepository _validatedRepository;
    private readonly IToolRepository _toolRepository;
    private readonly ValidatedExtractionIntegrityVerifier _integrityVerifier;
    private readonly ValidatedExtractionDocumentStore _documentStore;
    private readonly ExtractionValidationService _validationService;
    private readonly IExtractionProfileProvider _profileProvider;
    private readonly IValidationPolicyProvider _validationPolicyProvider;
    private readonly TimeProvider _timeProvider;

    public ExtractionHistoryService(
        string dataRoot,
        IValidatedExtractionRepository validatedRepository,
        IToolRepository toolRepository,
        ValidatedExtractionIntegrityVerifier integrityVerifier,
        ValidatedExtractionDocumentStore documentStore,
        ExtractionValidationService validationService,
        IExtractionProfileProvider profileProvider,
        IValidationPolicyProvider validationPolicyProvider,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        _validatedRepository = validatedRepository ?? throw new ArgumentNullException(nameof(validatedRepository));
        _toolRepository = toolRepository ?? throw new ArgumentNullException(nameof(toolRepository));
        _integrityVerifier = integrityVerifier ?? throw new ArgumentNullException(nameof(integrityVerifier));
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
        _profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        _validationPolicyProvider = validationPolicyProvider
            ?? throw new ArgumentNullException(nameof(validationPolicyProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Lists validated extractions newest first; with <paramref name="includeFailed"/>
    /// it also folds in terminal and candidate attempts (Failed/Canceled/Abandoned/
    /// ProcessCompleted). Ordering is created-time descending, then ID descending.
    /// </summary>
    public async Task<IReadOnlyList<ExtractionHistoryItem>> ListAsync(
        string? buildId,
        bool includeFailed,
        CancellationToken cancellationToken)
    {
        var validated = await _validatedRepository.ListValidatedExtractionsAsync(buildId, cancellationToken);
        var preferredByBuild = new Dictionary<string, string?>(StringComparer.Ordinal);
        var items = new List<ExtractionHistoryItem>(validated.Count);
        foreach (var extraction in validated)
        {
            var preferredId = await ResolvePreferredIdAsync(
                preferredByBuild, extraction.BuildId, cancellationToken);
            items.Add(new ExtractionHistoryItem(
                ExtractionHistoryEntryKind.ValidatedExtraction,
                extraction.ExtractionId,
                extraction.BuildId,
                extraction.RecipeId,
                extraction.CreatedAtUtc,
                Status: "Validated",
                extraction.TrustLevel,
                extraction.InitialValidationOutcome,
                Preferred: string.Equals(preferredId, extraction.ExtractionId, StringComparison.Ordinal),
                ResultExtractionId: null));
        }

        if (includeFailed)
        {
            var attempts = await _validatedRepository.ListAttemptsAsync(buildId, cancellationToken);
            foreach (var attempt in attempts)
            {
                if (attempt.Status is not (
                    ExtractionAttemptStatus.Failed or
                    ExtractionAttemptStatus.Canceled or
                    ExtractionAttemptStatus.Abandoned or
                    ExtractionAttemptStatus.ProcessCompleted))
                {
                    continue;
                }

                items.Add(new ExtractionHistoryItem(
                    ExtractionHistoryEntryKind.Attempt,
                    attempt.AttemptId,
                    attempt.BuildId,
                    attempt.RecipeId,
                    attempt.CreatedAtUtc,
                    attempt.Status.ToString(),
                    await ResolveAttemptTrustAsync(attempt.ToolInstanceId, cancellationToken),
                    ValidationOutcome: null,
                    Preferred: false,
                    attempt.ResultExtractionId));
            }
        }

        return items
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Resolves a single history entry. A 64 lower-hex ID is a validated extraction and
    /// triggers a full integrity verification (a mismatch throws an operational failure);
    /// a 32 lower-hex ID is an attempt and returns lifecycle/validation/result facts.
    /// Returns <see langword="null"/> for an unknown or malformed ID.
    /// </summary>
    public async Task<ExtractionHistoryDetail?> ShowAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (IsLowerHex(id, 64))
        {
            var extraction = await _validatedRepository.GetValidatedExtractionAsync(id, cancellationToken);
            if (extraction is null)
            {
                return null;
            }

            var integrity = await _integrityVerifier.VerifyAsync(
                _dataRoot, extraction.BuildId, extraction.ExtractionId, cancellationToken);
            if (integrity.Status != ValidatedExtractionIntegrityStatus.Valid)
            {
                throw IntegrityFailure(extraction.ExtractionId, integrity);
            }

            var preferred = await _validatedRepository.GetPreferredExtractionAsync(
                extraction.BuildId, cancellationToken);
            return new ExtractionHistoryDetail(
                ExtractionHistoryEntryKind.ValidatedExtraction,
                extraction,
                IntegrityVerified: true,
                Preferred: preferred is not null &&
                    string.Equals(preferred.ExtractionId, extraction.ExtractionId, StringComparison.Ordinal),
                Attempt: null,
                AttemptToolTrustLevel: null);
        }

        if (IsLowerHex(id, 32))
        {
            var attempts = await _validatedRepository.ListAttemptsAsync(null, cancellationToken);
            var attempt = attempts.FirstOrDefault(
                candidate => string.Equals(candidate.AttemptId, id, StringComparison.Ordinal));
            if (attempt is null)
            {
                return null;
            }

            return new ExtractionHistoryDetail(
                ExtractionHistoryEntryKind.Attempt,
                Extraction: null,
                IntegrityVerified: false,
                Preferred: false,
                attempt,
                await ResolveAttemptTrustAsync(attempt.ToolInstanceId, cancellationToken));
        }

        return null;
    }

    /// <summary>
    /// Explicitly and non-interactively promotes a validated extraction to the current
    /// preferred output. It proves full integrity, evaluates the current policy (reusing
    /// a verified current-policy result or creating a revalidation attempt), requires a
    /// non-<see cref="ValidationOutcome.Invalid"/> outcome while explicitly permitting
    /// preference-blocking warnings, and records a
    /// <see cref="ExtractionPreferenceReason.ManualPromotion"/> audit. An extraction that
    /// is already preferred is an idempotent no-op that records no duplicate event.
    /// </summary>
    public async Task<ManualPromotionOutcome> PromoteAsync(
        string extractionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionId);
        if (!IsLowerHex(extractionId, 64))
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.Recovery,
                ExtractionFailureCode.IntegrityMismatch,
                "Manual promotion requires a 64-character validated extraction ID.");
        }

        var extraction = await _validatedRepository.GetValidatedExtractionAsync(extractionId, cancellationToken);
        if (extraction is null)
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.Recovery,
                ExtractionFailureCode.IntegrityMismatch,
                $"No validated extraction exists for ID '{extractionId}'.");
        }

        var context = await BuildRevalidationContextAsync(extraction, cancellationToken);

        var outcome = await ResolveCurrentPolicyOutcomeAsync(extraction, context, cancellationToken);
        if (outcome.Outcome == ValidationOutcome.Invalid)
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.SanityValidation,
                ExtractionFailureCode.ValidationPolicyInvalid,
                $"The validated extraction '{extractionId}' does not satisfy the current " +
                "validation policy and cannot be promoted; its history is retained.");
        }

        var preferred = await _validatedRepository.GetPreferredExtractionAsync(
            extraction.BuildId, cancellationToken);
        var alreadyPreferred = preferred is not null &&
            string.Equals(preferred.ExtractionId, extraction.ExtractionId, StringComparison.Ordinal);
        if (!alreadyPreferred)
        {
            await _validatedRepository.SetPreferredExtractionAsync(
                new PreferredExtraction(
                    extraction.BuildId,
                    extraction.ExtractionId,
                    _timeProvider.GetUtcNow().ToUniversalTime(),
                    ExtractionPreferenceReason.ManualPromotion),
                cancellationToken);
        }

        return new ManualPromotionOutcome(
            extraction,
            outcome.Outcome,
            extraction.TrustLevel,
            WasAlreadyPreferred: alreadyPreferred,
            outcome.Revalidated);
    }

    private async Task<(ValidationOutcome Outcome, bool Revalidated)> ResolveCurrentPolicyOutcomeAsync(
        ValidatedExtraction extraction,
        PreparedExtractionContext context,
        CancellationToken cancellationToken)
    {
        var stored = await _validatedRepository.GetLatestValidationResultAsync(
            extraction.ExtractionId, context.Policy.PolicyDigest, cancellationToken);
        if (stored is not null && stored.Outcome != ValidationOutcome.Invalid)
        {
            var report = await _documentStore.TryReadValidationReportAsync(
                stored.ReportPath, cancellationToken);
            if (report is not null && ReportAgreesWithSummary(report, stored))
            {
                // A verified current-policy result already exists: only reprove integrity
                // before reusing it; do not create another revalidation attempt.
                var integrity = await _integrityVerifier.VerifyAsync(
                    _dataRoot, extraction.BuildId, extraction.ExtractionId, cancellationToken);
                if (integrity.Status != ValidatedExtractionIntegrityStatus.Valid)
                {
                    await ClearPreferredIfSelectedAsync(
                        extraction, ExtractionPreferenceReason.IntegrityInvalidated, cancellationToken);
                    throw IntegrityFailure(extraction.ExtractionId, integrity);
                }

                return (stored.Outcome, Revalidated: false);
            }
        }

        // No reusable current-policy result: revalidate the immutable output against the
        // current policy without rerunning Cpp2IL. RevalidateExistingExtractionAsync proves
        // integrity itself (throwing and clearing the pointer on a mismatch) and clears the
        // pointer with PolicyInvalidated on an Invalid outcome.
        var result = await _validationService.RevalidateExistingExtractionAsync(
            extraction, context, cancellationToken);
        return (result.ValidationOutcome, Revalidated: true);
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

    private async Task<string?> ResolvePreferredIdAsync(
        Dictionary<string, string?> cache,
        string buildId,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(buildId, out var cached))
        {
            return cached;
        }

        var preferred = await _validatedRepository.GetPreferredExtractionAsync(buildId, cancellationToken);
        cache[buildId] = preferred?.ExtractionId;
        return preferred?.ExtractionId;
    }

    private async Task<ToolTrustLevel?> ResolveAttemptTrustAsync(
        string? toolInstanceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(toolInstanceId))
        {
            return null;
        }

        var instance = await _toolRepository.GetToolInstanceAsync(toolInstanceId, cancellationToken);
        return instance?.TrustLevel;
    }

    /// <summary>
    /// Builds the revalidation context from the extraction's own facts and the current
    /// profile/policy — never by resolving or installing a managed tool. The extraction's
    /// recorded tool instance supplies the attempt's provenance (and satisfies its foreign
    /// key); its trust is preserved separately by the validation engine. Missing tool
    /// provenance fails closed rather than inventing trust.
    /// </summary>
    private async Task<PreparedExtractionContext> BuildRevalidationContextAsync(
        ValidatedExtraction extraction,
        CancellationToken cancellationToken)
    {
        var instance = await _toolRepository.GetToolInstanceAsync(
            extraction.ToolInstanceId, cancellationToken);
        if (instance is null)
        {
            throw new ExtractionOperationException(
                ExtractionFailureStage.ToolResolution,
                ExtractionFailureCode.ToolDefinitionInvalid,
                $"The tool provenance for extraction '{extraction.ExtractionId}' is missing; " +
                "it cannot be revalidated.");
        }

        var profile = _profileProvider.GetRequired(extraction.ProfileId);
        var policy = _validationPolicyProvider.GetRequired(ExtractionOrchestrator.ValidationPolicyId);
        var build = new GameBuild(
            extraction.BuildId,
            GameAssemblySha256: string.Empty,
            MetadataSha256: string.Empty,
            extraction.CreatedAtUtc,
            IsValid: true);
        var tool = new ResolvedExtractionTool(
            Definition: null!,
            instance,
            instance.ObservedPath,
            []);
        return new PreparedExtractionContext(build, profile, policy, tool, extraction.RecipeId);
    }

    private static ExtractionOperationException IntegrityFailure(
        string extractionId, ValidatedExtractionIntegrity integrity) =>
        new(
            ExtractionFailureStage.Recovery,
            ExtractionFailureCode.IntegrityMismatch,
            $"The validated extraction '{extractionId}' failed integrity verification and is " +
            $"not authoritative: {integrity.Message ?? integrity.Status.ToString()}");

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

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length &&
        value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
