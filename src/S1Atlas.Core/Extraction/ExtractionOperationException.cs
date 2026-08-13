namespace S1Atlas.Core.Extraction;

public sealed class ExtractionOperationException : Exception
{
    public ExtractionOperationException(
        ExtractionFailureStage stage,
        ExtractionFailureCode code,
        string message,
        string? attemptId = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Stage = stage;
        Code = code;
        AttemptId = attemptId;
    }

    public ExtractionFailureStage Stage { get; }

    public ExtractionFailureCode Code { get; }

    public string? AttemptId { get; }
}
