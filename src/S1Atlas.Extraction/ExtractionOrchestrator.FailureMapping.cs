using S1Atlas.Core.Builds;
using S1Atlas.Core.Extraction;
using S1Atlas.Core.Storage;
using S1Atlas.Core.Tools;
using S1Atlas.Extraction.Attempts;
using S1Atlas.Extraction.Cpp2Il;
using S1Atlas.Extraction.Inputs;
using S1Atlas.Extraction.Tools;

namespace S1Atlas.Extraction;

public sealed partial class ExtractionOrchestrator
{
    private static ExtractionOperationException MapFailure(
        Exception exception,
        ExtractionFailureStage currentStage,
        string? attemptId)
    {
        if (exception is ExtractionOperationException extractionFailure)
        {
            return extractionFailure.AttemptId is null && attemptId is not null
                ? new ExtractionOperationException(
                    extractionFailure.Stage,
                    extractionFailure.Code,
                    extractionFailure.Message,
                    attemptId,
                    extractionFailure)
                : extractionFailure;
        }

        if (exception is ToolOperationException toolFailure)
        {
            return MapToolFailure(toolFailure, attemptId);
        }

        if (exception is OperationCanceledException)
        {
            return new ExtractionOperationException(
                currentStage,
                ExtractionFailureCode.OperationCanceled,
                "The extraction operation was canceled.",
                attemptId,
                exception);
        }

        var code = currentStage switch
        {
            ExtractionFailureStage.ProcessStart => ExtractionFailureCode.ProcessStartFailed,
            ExtractionFailureStage.PostRunInputVerification =>
                ExtractionFailureCode.InputChangedDuringExtraction,
            ExtractionFailureStage.InputSnapshotCreation =>
                ExtractionFailureCode.FilesystemPromotionFailed,
            ExtractionFailureStage.FilesystemPromotion =>
                ExtractionFailureCode.FilesystemPromotionFailed,
            _ => ExtractionFailureCode.IntegrityMismatch
        };
        return new ExtractionOperationException(
            currentStage,
            code,
            "The extraction operation failed.",
            attemptId,
            exception);
    }

    private static ExtractionOperationException MapToolFailure(
        ToolOperationException exception,
        string? attemptId)
    {
        var code = Enum.TryParse<ExtractionFailureCode>(
            exception.Code,
            ignoreCase: false,
            out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : ExtractionFailureCode.ToolDefinitionInvalid;
        return new ExtractionOperationException(
            ExtractionFailureStage.ToolResolution,
            code,
            exception.Message,
            attemptId,
            exception);
    }

    private static ExtractionOperationException PersistenceFailure(
        ExtractionFailureCode code,
        string message,
        string attemptId,
        Exception exception) => new(
            ExtractionFailureStage.AttemptPersistence,
            code,
            message,
            attemptId,
            exception);

    private static ExtractionOperationException FilesystemFailure(
        string attemptId,
        string message,
        Exception? innerException = null) => new(
            ExtractionFailureStage.FilesystemPromotion,
            ExtractionFailureCode.FilesystemPromotionFailed,
            message,
            attemptId,
            innerException);

    private readonly record struct OutputFacts(int FileCount, long ByteCount);

    private sealed record TerminalFailureResult(
        ExtractionAttempt Attempt,
        ExtractionOperationException Failure);
}
