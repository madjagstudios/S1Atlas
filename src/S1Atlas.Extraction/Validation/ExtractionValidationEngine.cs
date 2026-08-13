using S1Atlas.Core.Extraction;

namespace S1Atlas.Extraction.Validation;

/// <summary>
/// Everything <see cref="ExtractionValidationEngine.Evaluate"/> needs to produce
/// a <see cref="ValidationReport"/> for one attempt. <see cref="ArtifactBuild"/>
/// is <see langword="null"/> exactly when <see cref="CandidateInspection"/> did
/// not pass containment, since <see cref="ArtifactManifestBuilder"/> has nothing
/// safe to annotate in that case. <see cref="BaselineStatistics"/>,
/// <see cref="BaselineExtractionId"/>, and <see cref="SameRecipeExtractions"/>
/// are accepted here so Task 5 can complete comparative and reproducibility
/// checks without another engine signature change; Task 4 does not evaluate them.
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
    DateTimeOffset ValidatedAtUtc);

/// <summary>
/// Produces the single strict <see cref="ValidationReport"/> for an attempt from
/// its candidate containment result, its already-annotated artifact manifest, the
/// source attempt's own input/process integrity facts, and the committed
/// validation policy.
/// </summary>
/// <remarks>
/// <para>
/// Task 4 evaluates output containment and absolute policy sanity only.
/// Comparative preferred-baseline checks and same-recipe reproducibility checks
/// are Task 5's concern: this engine already accepts their inputs (see
/// <see cref="ExtractionValidationRequest"/>) but returns no comparisons and no
/// baseline linkage yet, so Task 5 can complete them without another signature
/// change.
/// </para>
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

    public static ValidationReport Evaluate(ExtractionValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Attempt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Attempt.RecipeId);
        ArgumentNullException.ThrowIfNull(request.CandidateInspection);
        ArgumentNullException.ThrowIfNull(request.Policy);

        if (!request.CandidateInspection.ContainmentPassed || request.CandidateInspection.Inventory is null)
        {
            return new ValidationReport(
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
        }

        if (request.ArtifactBuild is null)
        {
            throw new ArgumentException(
                "An artifact build is required once candidate containment has passed.",
                nameof(request));
        }

        var artifactBuild = request.ArtifactBuild;
        var issues = AbsoluteSanityValidator.Validate(artifactBuild, request.Policy);

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

        return new ValidationReport(
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
            BaselineExtractionId: null,
            Comparisons: [],
            issues,
            preferenceEligible,
            request.ValidatedAtUtc);
    }
}
