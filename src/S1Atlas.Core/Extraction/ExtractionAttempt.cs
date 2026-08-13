namespace S1Atlas.Core.Extraction;

public sealed record ExtractionAttempt(
    string AttemptId,
    string? RecipeId,
    string BuildId,
    string? ToolInstanceId,
    string ProfileId,
    int ProfileVersion,
    string ProfileDigest,
    string ValidationPolicyId,
    int ValidationPolicyVersion,
    string ValidationPolicyDigest,
    int AdapterVersion,
    int ExtractionSchemaVersion,
    ExtractionInputSource? InputSource,
    string? InputSnapshotId,
    ExtractionAttemptStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? PreInputManifestDigest,
    string? PostInputManifestDigest,
    string WorkingPath,
    string StandardOutputPath,
    string StandardErrorPath,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated,
    long StandardOutputDiscardedBytes,
    long StandardErrorDiscardedBytes,
    int? ProcessId,
    int? ProcessExitCode,
    ExtractionFailureStage? FailureStage,
    ExtractionFailureCode? FailureCode,
    string? FailureMessage,
    bool KeepFailedArtifacts,
    int DiscardedFileCount,
    long DiscardedByteCount,
    string? CandidateOutputPath,
    string? ResultExtractionId,
    // Phase 4: the plan places ValidationSourceExtractionId immediately before
    // ResultExtractionId. It is declared here instead, as a trailing optional
    // parameter, so the existing Phase 3 positional constructor call in
    // AttemptDocumentStore.cs (owned by Task 2) keeps compiling unmodified.
    // The field name, type, and semantics are unchanged from the plan.
    string? ValidationSourceExtractionId = null);
