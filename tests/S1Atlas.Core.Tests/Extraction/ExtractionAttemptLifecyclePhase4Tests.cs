using S1Atlas.Core.Extraction;
using Xunit;

namespace S1Atlas.Core.Tests.Extraction;

public sealed class ExtractionAttemptLifecyclePhase4Tests
{
    [Fact]
    public void Transition_ProcessCompletedToValidating_WithCandidateOutputPath_ReturnsNextAttempt()
    {
        var current = CreateAttempt(ExtractionAttemptStatus.ProcessCompleted);
        var next = CreateAttempt(ExtractionAttemptStatus.Validating) with
        {
            AttemptId = current.AttemptId,
            CandidateOutputPath = "candidate-output",
            ValidationSourceExtractionId = null
        };

        Assert.Equal(next, ExtractionAttemptLifecycle.Transition(current, next));
    }

    [Fact]
    public void Transition_CreatedToValidating_WithValidationSourceExtractionId_ReturnsNextAttempt()
    {
        var current = CreateAttempt(ExtractionAttemptStatus.Created);
        var next = CreateAttempt(ExtractionAttemptStatus.Validating) with
        {
            AttemptId = current.AttemptId,
            CandidateOutputPath = null,
            ValidationSourceExtractionId = new string('a', 64)
        };

        Assert.Equal(next, ExtractionAttemptLifecycle.Transition(current, next));
    }

    [Fact]
    public void Transition_ValidatingWithBothCandidateAndValidationSource_RejectsAttempt()
    {
        var current = CreateAttempt(ExtractionAttemptStatus.Created);
        var next = CreateAttempt(ExtractionAttemptStatus.Validating) with
        {
            AttemptId = current.AttemptId,
            CandidateOutputPath = "candidate-output",
            ValidationSourceExtractionId = new string('a', 64)
        };

        Assert.Throws<InvalidOperationException>(() => ExtractionAttemptLifecycle.Transition(current, next));
    }

    [Fact]
    public void Transition_ValidatingWithNeitherCandidateNorValidationSource_RejectsAttempt()
    {
        var current = CreateAttempt(ExtractionAttemptStatus.Created);
        var next = CreateAttempt(ExtractionAttemptStatus.Validating) with
        {
            AttemptId = current.AttemptId,
            CandidateOutputPath = null,
            ValidationSourceExtractionId = null
        };

        Assert.Throws<InvalidOperationException>(() => ExtractionAttemptLifecycle.Transition(current, next));
    }

    [Fact]
    public void Transition_SucceededWithoutResultExtractionId_RejectsAttempt()
    {
        var current = CreateAttempt(ExtractionAttemptStatus.Validating);
        var next = CreateAttempt(ExtractionAttemptStatus.Succeeded) with
        {
            AttemptId = current.AttemptId,
            ResultExtractionId = null
        };

        Assert.Throws<InvalidOperationException>(() => ExtractionAttemptLifecycle.Transition(current, next));
    }

    [Fact]
    public void Transition_SucceededWithResultExtractionId_ReturnsNextAttempt()
    {
        var current = CreateAttempt(ExtractionAttemptStatus.Validating);
        var next = CreateAttempt(ExtractionAttemptStatus.Succeeded) with
        {
            AttemptId = current.AttemptId,
            ResultExtractionId = new string('f', 64)
        };

        Assert.Equal(next, ExtractionAttemptLifecycle.Transition(current, next));
    }

    [Fact]
    public void Transition_FailedRetainsValidationSourceExtractionIdWithoutResultExtractionId_ReturnsNextAttempt()
    {
        var current = CreateAttempt(ExtractionAttemptStatus.Validating) with
        {
            CandidateOutputPath = null,
            ValidationSourceExtractionId = new string('a', 64)
        };
        var next = CreateAttempt(ExtractionAttemptStatus.Failed) with
        {
            AttemptId = current.AttemptId,
            ValidationSourceExtractionId = new string('a', 64),
            FailureStage = ExtractionFailureStage.SanityValidation,
            FailureCode = ExtractionFailureCode.ValidationPolicyInvalid,
            FailureMessage = "The validation policy is invalid.",
            ResultExtractionId = null
        };

        Assert.Equal(next, ExtractionAttemptLifecycle.Transition(current, next));
    }

    [Fact]
    public void Transition_FailedCannotClaimResultExtractionId_RejectsAttempt()
    {
        var current = CreateAttempt(ExtractionAttemptStatus.Validating) with
        {
            CandidateOutputPath = null,
            ValidationSourceExtractionId = new string('a', 64)
        };
        var next = CreateAttempt(ExtractionAttemptStatus.Failed) with
        {
            AttemptId = current.AttemptId,
            ValidationSourceExtractionId = new string('a', 64),
            FailureStage = ExtractionFailureStage.SanityValidation,
            FailureCode = ExtractionFailureCode.ValidationReportInvalid,
            FailureMessage = "The validation report is invalid.",
            ResultExtractionId = new string('f', 64)
        };

        Assert.Throws<InvalidOperationException>(() => ExtractionAttemptLifecycle.Transition(current, next));
    }

    private static ExtractionAttempt CreateAttempt(ExtractionAttemptStatus status) => new(
        Guid.NewGuid().ToString("N"), new string('a', 64), new string('b', 64), new string('c', 64),
        "profile", 1, new string('d', 64), "policy", 1, new string('e', 64), 1, 1,
        null, null, status, DateTimeOffset.UtcNow, null, null, null, null,
        "work", "stdout", "stderr", false, false, 0, 0, null, null,
        status == ExtractionAttemptStatus.Failed ? ExtractionFailureStage.ProcessExecution : null,
        status == ExtractionAttemptStatus.Failed ? ExtractionFailureCode.ProcessExitNonZero : null,
        status == ExtractionAttemptStatus.Failed ? "failed" : null,
        false, 0, 0,
        status is ExtractionAttemptStatus.ProcessCompleted or ExtractionAttemptStatus.Validating
            ? "candidate-output"
            : null,
        status == ExtractionAttemptStatus.Succeeded ? new string('f', 64) : null);
}
