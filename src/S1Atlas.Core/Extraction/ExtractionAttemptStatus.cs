namespace S1Atlas.Core.Extraction;

public enum ExtractionAttemptStatus
{
    Created,
    Preparing,
    Running,
    Validating,
    ProcessCompleted,
    Succeeded,
    Failed,
    Canceled,
    Abandoned
}
