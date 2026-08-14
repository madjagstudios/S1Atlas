using S1Atlas.Core.Tools;

namespace S1Atlas.Core.Extraction;

public sealed record ExtractionWorkflowResult(
    string? AttemptId,
    string BuildId,
    string RecipeId,
    string ExtractionId,
    string ExtractionRoot,
    ToolTrustLevel ToolTrustLevel,
    ValidationOutcome ValidationOutcome,
    bool ProcessWasRun,
    bool ValidationWasRun,
    bool ReusedExistingExtraction,
    bool IsPreferred,
    bool IsAuthoritative,
    ExtractionInputSource? InputSource = null,
    string? InputSnapshotId = null,
    bool InputSnapshotReplayVerified = false);

public sealed record ValidationPersistence(
    ExtractionAttempt Attempt,
    ValidationReport Report);

public sealed record ValidatedExtractionPromotion(
    ExtractionAttempt Attempt,
    ValidatedExtraction Extraction,
    ArtifactManifest ArtifactManifest,
    ValidationReport ValidationReport,
    ExtractionPreferenceReason? AutomaticPreferenceReason);
