using S1Atlas.Core.Tools;

namespace S1Atlas.Core.Extraction;

/// <summary>
/// Immutable accepted facts for a validated extraction. Validation-policy provenance
/// remains in the source attempt and its validation report; it is not part of this
/// production identity record. Preferred status is mutable and lives separately in
/// <see cref="PreferredExtraction"/>.
/// </summary>
public sealed record ValidatedExtraction(
    string ExtractionId,
    string RecipeId,
    string BuildId,
    string ToolInstanceId,
    string SourceAttemptId,
    string ProfileId,
    int ProfileVersion,
    string ProfileDigest,
    int AdapterVersion,
    int ExtractionSchemaVersion,
    string ArtifactManifestDigest,
    string RootPath,
    DateTimeOffset CreatedAtUtc,
    ToolTrustLevel TrustLevel,
    ValidationOutcome InitialValidationOutcome,
    ExtractionStatistics Statistics);
