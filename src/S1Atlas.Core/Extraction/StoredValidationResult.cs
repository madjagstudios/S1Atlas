namespace S1Atlas.Core.Extraction;

/// <summary>
/// The SQLite summary of a validation result. A caller treats this as reusable only
/// after reading and verifying <see cref="ReportPath"/> against the strict
/// <see cref="ValidationReport"/> it summarizes.
/// </summary>
public sealed record StoredValidationResult(
    string AttemptId,
    string? SubjectExtractionId,
    string ArtifactManifestDigest,
    string PolicyId,
    int PolicyVersion,
    string PolicyDigest,
    ValidationOutcome Outcome,
    string ReportPath,
    string? BaselineExtractionId,
    bool PreferenceEligible,
    DateTimeOffset ValidatedAtUtc,
    ExtractionStatistics Statistics);
