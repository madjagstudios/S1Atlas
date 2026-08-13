namespace S1Atlas.Core.Extraction;

public sealed record ValidationMetricComparison(
    string Metric,
    string? AssemblyName,
    long BaselineValue,
    long CandidateValue,
    double? RelativeChange,
    string Classification);

public enum ValidationSubjectKind
{
    CandidateOutput,
    ValidatedExtraction
}

/// <summary>
/// The complete strict validation document. <see cref="Issues"/> is the single
/// in-memory source of validation issues; persistence layers do not carry a second
/// independent issues list.
/// </summary>
public sealed record ValidationReport(
    int SchemaVersion,
    string AttemptId,
    ValidationSubjectKind SubjectKind,
    string? SubjectExtractionId,
    string BuildId,
    string RecipeId,
    string PolicyId,
    int PolicyVersion,
    string PolicyDigest,
    ValidationOutcome Outcome,
    bool InputIntegrityPassed,
    bool ProcessIntegrityPassed,
    bool OutputContainmentPassed,
    string ArtifactManifestDigest,
    ExtractionStatistics Statistics,
    string? BaselineExtractionId,
    IReadOnlyList<ValidationMetricComparison> Comparisons,
    IReadOnlyList<ValidationIssue> Issues,
    bool PreferenceEligible,
    DateTimeOffset ValidatedAtUtc);
