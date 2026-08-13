namespace S1Atlas.Core.Extraction;

public static class ExtractionAttemptLifecycle
{
    public static ExtractionAttempt Transition(
        ExtractionAttempt current,
        ExtractionAttempt next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);
        ValidateState(current);
        ValidateState(next);

        if (!string.Equals(current.AttemptId, next.AttemptId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Attempt IDs cannot change during a transition.");
        }

        if (!IsLegalEdge(current.Status, next.Status))
        {
            throw new InvalidOperationException(
                $"The transition from {current.Status} to {next.Status} is not legal.");
        }

        return next;
    }

    private static bool IsLegalEdge(
        ExtractionAttemptStatus current,
        ExtractionAttemptStatus next) => current switch
    {
        ExtractionAttemptStatus.Created => next is ExtractionAttemptStatus.Preparing
            or ExtractionAttemptStatus.Failed
            or ExtractionAttemptStatus.Canceled
            or ExtractionAttemptStatus.Abandoned,
        ExtractionAttemptStatus.Preparing => next is ExtractionAttemptStatus.Running
            or ExtractionAttemptStatus.Failed
            or ExtractionAttemptStatus.Canceled
            or ExtractionAttemptStatus.Abandoned,
        ExtractionAttemptStatus.Running => next is ExtractionAttemptStatus.ProcessCompleted
            or ExtractionAttemptStatus.Failed
            or ExtractionAttemptStatus.Canceled
            or ExtractionAttemptStatus.Abandoned,
        ExtractionAttemptStatus.Validating => next is ExtractionAttemptStatus.Succeeded
            or ExtractionAttemptStatus.Failed
            or ExtractionAttemptStatus.Canceled
            or ExtractionAttemptStatus.Abandoned,
        _ => false
    };

    private static void ValidateState(ExtractionAttempt attempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attempt.AttemptId);
        if (attempt.AttemptId.Length != 32 || attempt.AttemptId.Any(character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidOperationException("Attempt IDs must be lower-case GUID N values.");
        }

        if (attempt.Status is not ExtractionAttemptStatus.Validating and not ExtractionAttemptStatus.Succeeded
            && attempt.ResultExtractionId is not null)
        {
            throw new InvalidOperationException(
                "Phase 3 attempts cannot carry a result extraction ID.");
        }

        var hasFailureMetadata = attempt.FailureStage is not null
            || attempt.FailureCode is not null
            || attempt.FailureMessage is not null;
        if (attempt.Status == ExtractionAttemptStatus.Failed)
        {
            if (attempt.FailureStage is null
                || attempt.FailureCode is null
                || string.IsNullOrWhiteSpace(attempt.FailureMessage))
            {
                throw new InvalidOperationException(
                    "Failed attempts require failure stage, code, and message.");
            }
        }
        else if (hasFailureMetadata)
        {
            throw new InvalidOperationException(
                "Only failed attempts can carry failure metadata.");
        }

        if (attempt.Status == ExtractionAttemptStatus.ProcessCompleted
            && string.IsNullOrWhiteSpace(attempt.CandidateOutputPath))
        {
            throw new InvalidOperationException(
                "Process-completed attempts require a candidate output path.");
        }

        if (attempt.StandardOutputDiscardedBytes < 0
            || attempt.StandardErrorDiscardedBytes < 0
            || attempt.DiscardedFileCount < 0
            || attempt.DiscardedByteCount < 0)
        {
            throw new InvalidOperationException("Discarded byte and file counts cannot be negative.");
        }
    }
}
