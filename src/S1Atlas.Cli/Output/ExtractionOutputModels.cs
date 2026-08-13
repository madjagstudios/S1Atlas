namespace S1Atlas.Cli.Output;

internal sealed record ExtractionOutput(
    string AttemptId,
    string Status,
    string BuildId,
    string RecipeId,
    string ToolInstanceId,
    string ToolTrustLevel,
    string InputSource,
    string? InputSnapshotId,
    string CandidateOutputPath,
    string StandardOutputPath,
    string StandardErrorPath,
    bool ProcessWasRun,
    bool Authoritative,
    string? ValidationOutcome);
