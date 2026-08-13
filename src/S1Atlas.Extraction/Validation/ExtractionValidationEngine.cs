using S1Atlas.Core.Extraction;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Promotion;

namespace S1Atlas.Extraction.Validation;

/// <summary>
/// Everything <see cref="ExtractionValidationEngine.Evaluate"/> needs to produce
/// a <see cref="ValidationReport"/> for one attempt. <see cref="ArtifactBuild"/>
/// is <see langword="null"/> exactly when <see cref="CandidateInspection"/> did
/// not pass containment, since <see cref="ArtifactManifestBuilder"/> has nothing
/// safe to annotate in that case. <see cref="BaselineStatistics"/> is the
/// preferred baseline's statistics (a verified successful current-policy report's
/// statistics when one exists, otherwise the preferred extraction's immutable
/// initial statistics) or <see langword="null"/> when no baseline exists yet (the
/// first validated extraction for a recipe); comparative checks run only when it
/// is present. <see cref="SameRecipeExtractions"/> is every already-validated
/// extraction sharing this attempt's recipe ID, for reproducibility comparison.
/// <see cref="ToolTrustLevel"/>, <see cref="CurrentPreferredExtraction"/>, and
/// <see cref="CurrentPreferredToolInstanceId"/> feed the automatic preference
/// decision; none of them are read unless the report ends up preference-eligible.
/// </summary>
internal sealed record ExtractionValidationRequest(
    ExtractionAttempt Attempt,
    ValidationSubjectKind SubjectKind,
    string? SubjectExtractionId,
    CandidateInspectionResult CandidateInspection,
    ArtifactBuildResult? ArtifactBuild,
    bool InputIntegrityPassed,
    bool ProcessIntegrityPassed,
    ResolvedValidationPolicy Policy,
    ExtractionStatistics? BaselineStatistics,
    string? BaselineExtractionId,
    IReadOnlyList<ValidatedExtraction> SameRecipeExtractions,
    ToolTrustLevel ToolTrustLevel,
    PreferredExtraction? CurrentPreferredExtraction,
    string? CurrentPreferredToolInstanceId,
    DateTimeOffset ValidatedAtUtc);

/// <summary>
/// The complete outcome of evaluating one attempt: the strict
/// <see cref="ValidationReport"/> itself, an optional
/// <see cref="DeduplicationTarget"/> naming an already-validated extraction whose
/// bytes this candidate exactly reproduces (a promoter should link to it and
/// discard the duplicate candidate rather than promoting a second copy), and an
/// optional <see cref="AutomaticPreferenceReason"/> naming the reason a promoter
/// should automatically select this extraction as its build's new preference.
/// </summary>
internal sealed record ExtractionValidationResult(
    ValidationReport Report,
    ValidatedExtraction? DeduplicationTarget,
    ExtractionPreferenceReason? AutomaticPreferenceReason);

/// <summary>
/// Produces the single strict <see cref="ValidationReport"/> for an attempt from
/// its candidate containment result, its already-annotated artifact manifest, the
/// source attempt's own input/process integrity facts, the committed validation
/// policy, an optional preferred-baseline comparison, same-recipe reproducibility
/// comparison, and an automatic-preference decision.
/// </summary>
/// <remarks>
/// <para>
/// Failure mapping used by later callers when a validated attempt hard-fails
/// (<see cref="ExtractionFailureStage"/> / <see cref="ExtractionFailureCode"/>),
/// keyed by the <see cref="ValidationIssue.Code"/> values this engine and its
/// collaborators produce:
/// </para>
/// <code>
/// containment issue       -> OutputContainment / OutputOutsideStaging
/// no/empty artifact        -> ArtifactValidation / NoArtifactsProduced or EmptyArtifact
/// no managed assembly      -> AssemblyValidation / NoManagedAssembliesProduced
/// invalid managed DLL      -> AssemblyValidation / InvalidManagedAssembly
/// required missing         -> AssemblyValidation / RequiredAssemblyMissing
/// duplicate identity       -> AssemblyValidation / DuplicateAssemblyIdentity
/// catastrophic deviation   -> SanityValidation / CatastrophicSanityDeviation
/// invalid policy/report    -> SanityValidation / ValidationPolicyInvalid or ValidationReportInvalid
/// </code>
/// </remarks>
internal static class ExtractionValidationEngine
{
    private const int SchemaVersion = 1;

    private static readonly ExtractionStatistics EmptyStatistics = new(
        ArtifactCount: 0,
        LibraryCount: 0,
        ManagedAssemblyCount: 0,
        TypeDefinitionCount: 0,
        MethodDefinitionCount: 0,
        FieldDefinitionCount: 0,
        PropertyDefinitionCount: 0,
        EventDefinitionCount: 0,
        TotalOutputBytes: 0,
        TotalManagedBytes: 0,
        Assemblies: []);

    public static ExtractionValidationResult Evaluate(ExtractionValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Attempt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Attempt.RecipeId);
        ArgumentNullException.ThrowIfNull(request.CandidateInspection);
        ArgumentNullException.ThrowIfNull(request.Policy);

        if (!request.CandidateInspection.ContainmentPassed || request.CandidateInspection.Inventory is null)
        {
            var containmentReport = new ValidationReport(
                SchemaVersion,
                request.Attempt.AttemptId,
                request.SubjectKind,
                request.SubjectExtractionId,
                request.Attempt.BuildId,
                request.Attempt.RecipeId,
                request.Policy.Policy.PolicyId,
                request.Policy.Policy.PolicyVersion,
                request.Policy.PolicyDigest,
                ValidationOutcome.Invalid,
                request.InputIntegrityPassed,
                request.ProcessIntegrityPassed,
                OutputContainmentPassed: false,
                ArtifactManifestDigest: string.Empty,
                Statistics: EmptyStatistics,
                BaselineExtractionId: null,
                Comparisons: [],
                Issues: request.CandidateInspection.Issues,
                PreferenceEligible: false,
                request.ValidatedAtUtc);

            return new ExtractionValidationResult(
                containmentReport, DeduplicationTarget: null, AutomaticPreferenceReason: null);
        }

        if (request.ArtifactBuild is null)
        {
            throw new ArgumentException(
                "An artifact build is required once candidate containment has passed.",
                nameof(request));
        }

        var artifactBuild = request.ArtifactBuild;
        var issues = new List<ValidationIssue>();
        issues.AddRange(AbsoluteSanityValidator.Validate(artifactBuild, request.Policy));

        IReadOnlyList<ValidationMetricComparison> comparisons = [];
        if (request.BaselineStatistics is not null)
        {
            var comparative = ComparativeSanityValidator.Validate(
                request.BaselineStatistics, artifactBuild.Statistics, request.Policy);
            comparisons = comparative.Comparisons;
            issues.AddRange(comparative.Issues);
        }

        var reproducibility = ReproducibilityValidator.Validate(
            artifactBuild.ManifestDigest, request.SameRecipeExtractions);
        issues.AddRange(reproducibility.Issues);

        var hasError = issues.Any(issue => issue.Severity == ValidationIssueSeverity.Error);
        var hasWarning = issues.Any(issue => issue.Severity == ValidationIssueSeverity.Warning);
        var outcome = hasError
            ? ValidationOutcome.Invalid
            : hasWarning
                ? ValidationOutcome.ValidWithWarnings
                : ValidationOutcome.Valid;

        var preferenceEligible =
            outcome != ValidationOutcome.Invalid &&
            request.InputIntegrityPassed &&
            request.ProcessIntegrityPassed &&
            !issues.Any(issue => issue.PreferenceBlocking);

        var report = new ValidationReport(
            SchemaVersion,
            request.Attempt.AttemptId,
            request.SubjectKind,
            request.SubjectExtractionId,
            request.Attempt.BuildId,
            request.Attempt.RecipeId,
            request.Policy.Policy.PolicyId,
            request.Policy.Policy.PolicyVersion,
            request.Policy.PolicyDigest,
            outcome,
            request.InputIntegrityPassed,
            request.ProcessIntegrityPassed,
            OutputContainmentPassed: true,
            artifactBuild.ManifestDigest,
            artifactBuild.Statistics,
            request.BaselineStatistics is not null ? request.BaselineExtractionId : null,
            comparisons,
            issues,
            preferenceEligible,
            request.ValidatedAtUtc);

        var deduplicationTarget = reproducibility.Disposition == ReproducibilityDisposition.ExistingOutput
            ? reproducibility.ExistingExtraction
            : null;

        var candidateExtractionId = deduplicationTarget?.ExtractionId
            ?? (request.SubjectKind == ValidationSubjectKind.ValidatedExtraction
                ? request.SubjectExtractionId
                : null);

        var automaticPreferenceReason = ExtractionPreferencePolicy.Decide(new ExtractionPreferenceDecisionRequest(
            request.ToolTrustLevel,
            request.Attempt.ToolInstanceId,
            candidateExtractionId,
            outcome,
            preferenceEligible,
            request.CurrentPreferredExtraction,
            request.CurrentPreferredToolInstanceId));

        return new ExtractionValidationResult(report, deduplicationTarget, automaticPreferenceReason);
    }
}
