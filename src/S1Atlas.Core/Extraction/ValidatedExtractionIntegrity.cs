namespace S1Atlas.Core.Extraction;

public enum ValidatedExtractionIntegrityStatus
{
    Valid,
    Missing,
    Incomplete,
    Mismatch
}

public sealed record ValidatedExtractionIntegrity(
    ValidatedExtractionIntegrityStatus Status,
    string? Code,
    string? Message);
