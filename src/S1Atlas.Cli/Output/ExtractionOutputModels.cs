namespace S1Atlas.Cli.Output;

internal sealed record ExtractionOutput(
    string? AttemptId,
    string BuildId,
    string RecipeId,
    string ExtractionId,
    string ExtractionRoot,
    string ToolTrustLevel,
    string ValidationOutcome,
    bool ProcessWasRun,
    bool ValidationWasRun,
    bool ReusedExistingExtraction,
    bool Preferred,
    bool Authoritative);
