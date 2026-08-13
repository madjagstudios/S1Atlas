namespace S1Atlas.Core.Extraction;

public enum ValidationIssueSeverity
{
    Information,
    Warning,
    Error
}

public sealed record ValidationIssue(
    ValidationIssueSeverity Severity,
    string Code,
    string Message,
    string? ArtifactRelativePath,
    bool PreferenceBlocking);
