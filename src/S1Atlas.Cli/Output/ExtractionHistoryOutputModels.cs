namespace S1Atlas.Cli.Output;

internal sealed record ExtractionHistoryListOutput(
    IReadOnlyList<ExtractionHistoryEntryOutput> Entries);

internal sealed record ExtractionHistoryEntryOutput(
    string Kind,
    string Id,
    string BuildId,
    string? RecipeId,
    string CreatedAtUtc,
    string Status,
    string? ToolTrustLevel,
    string? ValidationOutcome,
    bool Preferred,
    string? ResultExtractionId);

internal sealed record ExtractionShowOutput(
    string Kind,
    ExtractionDetailOutput? Extraction,
    AttemptDetailOutput? Attempt);

internal sealed record ExtractionDetailOutput(
    string ExtractionId,
    string RecipeId,
    string BuildId,
    string ToolInstanceId,
    string SourceAttemptId,
    string ProfileId,
    int ProfileVersion,
    string ProfileDigest,
    string ArtifactManifestDigest,
    string RootPath,
    string CreatedAtUtc,
    string ToolTrustLevel,
    string InitialValidationOutcome,
    bool Preferred,
    bool IntegrityVerified,
    ExtractionStatisticsOutput Statistics);

internal sealed record ExtractionStatisticsOutput(
    int ArtifactCount,
    int LibraryCount,
    int ManagedAssemblyCount,
    int TypeDefinitionCount,
    int MethodDefinitionCount,
    int FieldDefinitionCount,
    int PropertyDefinitionCount,
    int EventDefinitionCount,
    long TotalOutputBytes,
    long TotalManagedBytes);

internal sealed record AttemptDetailOutput(
    string AttemptId,
    string BuildId,
    string? RecipeId,
    string Status,
    string? ToolInstanceId,
    string? ToolTrustLevel,
    string CreatedAtUtc,
    string? StartedAtUtc,
    string? CompletedAtUtc,
    string? ValidationSourceExtractionId,
    string? ResultExtractionId,
    string? FailureStage,
    string? FailureCode,
    string? FailureMessage);

internal sealed record ExtractionPromoteOutput(
    string ExtractionId,
    string BuildId,
    string ValidationOutcome,
    string ToolTrustLevel,
    bool Preferred,
    bool AlreadyPreferred,
    bool Revalidated);
