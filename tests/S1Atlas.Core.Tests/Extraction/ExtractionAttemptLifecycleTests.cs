using S1Atlas.Core.Extraction;
using Xunit;

namespace S1Atlas.Core.Tests.Extraction;

public sealed class ExtractionAttemptLifecycleTests
{
    [Theory]
    [InlineData(ExtractionAttemptStatus.Created, ExtractionAttemptStatus.Preparing)]
    [InlineData(ExtractionAttemptStatus.Preparing, ExtractionAttemptStatus.Running)]
    [InlineData(ExtractionAttemptStatus.Running, ExtractionAttemptStatus.ProcessCompleted)]
    [InlineData(ExtractionAttemptStatus.Validating, ExtractionAttemptStatus.Succeeded)]
    public void Transition_WhenEdgeIsLegal_ReturnsNextAttempt(
        ExtractionAttemptStatus currentStatus,
        ExtractionAttemptStatus nextStatus)
    {
        var current = CreateAttempt(currentStatus);
        var next = CreateAttempt(nextStatus) with
        {
            AttemptId = current.AttemptId,
            CandidateOutputPath = nextStatus == ExtractionAttemptStatus.ProcessCompleted ? "candidate-output" : null
        };

        Assert.Equal(next, ExtractionAttemptLifecycle.Transition(current, next));
    }

    [Theory]
    [InlineData(ExtractionAttemptStatus.Running, ExtractionAttemptStatus.Succeeded)]
    [InlineData(ExtractionAttemptStatus.ProcessCompleted, ExtractionAttemptStatus.Validating)]
    [InlineData(ExtractionAttemptStatus.ProcessCompleted, ExtractionAttemptStatus.Failed)]
    [InlineData(ExtractionAttemptStatus.Succeeded, ExtractionAttemptStatus.Failed)]
    [InlineData(ExtractionAttemptStatus.Failed, ExtractionAttemptStatus.Preparing)]
    [InlineData(ExtractionAttemptStatus.Canceled, ExtractionAttemptStatus.Preparing)]
    [InlineData(ExtractionAttemptStatus.Abandoned, ExtractionAttemptStatus.Preparing)]
    public void Transition_WhenEdgeIsIllegal_RejectsAttempt(
        ExtractionAttemptStatus currentStatus,
        ExtractionAttemptStatus nextStatus)
    {
        var current = CreateAttempt(currentStatus);
        var next = CreateAttempt(nextStatus) with { AttemptId = current.AttemptId };

        Assert.Throws<InvalidOperationException>(() => ExtractionAttemptLifecycle.Transition(current, next));
    }

    [Fact]
    public void Transition_WhenProcessCompletedHasFailureMetadata_RejectsAttempt()
    {
        var next = CreateAttempt(ExtractionAttemptStatus.ProcessCompleted) with
        {
            CandidateOutputPath = "candidate-output",
            FailureStage = ExtractionFailureStage.ProcessExecution,
            FailureCode = ExtractionFailureCode.ProcessExitNonZero,
            FailureMessage = "unexpected"
        };

        Assert.Throws<InvalidOperationException>(() =>
            ExtractionAttemptLifecycle.Transition(
                CreateAttempt(ExtractionAttemptStatus.Running) with { AttemptId = next.AttemptId }, next));
    }

    [Fact]
    public void Transition_WhenProcessCompletedHasNoCandidateOutput_RejectsAttempt()
    {
        var current = CreateAttempt(ExtractionAttemptStatus.Running);
        var next = CreateAttempt(ExtractionAttemptStatus.ProcessCompleted) with
        {
            AttemptId = current.AttemptId,
            CandidateOutputPath = null
        };

        Assert.Throws<InvalidOperationException>(() => ExtractionAttemptLifecycle.Transition(current, next));
    }

    [Theory]
    [InlineData(ExtractionAttemptStatus.Created, ExtractionAttemptStatus.Preparing)]
    [InlineData(ExtractionAttemptStatus.Preparing, ExtractionAttemptStatus.Running)]
    [InlineData(ExtractionAttemptStatus.Running, ExtractionAttemptStatus.ProcessCompleted)]
    [InlineData(ExtractionAttemptStatus.Running, ExtractionAttemptStatus.Failed)]
    [InlineData(ExtractionAttemptStatus.Running, ExtractionAttemptStatus.Canceled)]
    [InlineData(ExtractionAttemptStatus.Running, ExtractionAttemptStatus.Abandoned)]
    public void Transition_WhenPhase3StateHasResultExtractionId_RejectsAttempt(
        ExtractionAttemptStatus currentStatus,
        ExtractionAttemptStatus nextStatus)
    {
        var next = CreateAttempt(nextStatus) with
        {
            CandidateOutputPath = nextStatus == ExtractionAttemptStatus.ProcessCompleted ? "candidate-output" : null,
            FailureStage = nextStatus == ExtractionAttemptStatus.Failed
                ? ExtractionFailureStage.ProcessExecution
                : null,
            FailureCode = nextStatus == ExtractionAttemptStatus.Failed
                ? ExtractionFailureCode.ProcessExitNonZero
                : null,
            FailureMessage = nextStatus == ExtractionAttemptStatus.Failed ? "failed" : null,
            ResultExtractionId = new string('a', 64)
        };

        Assert.Throws<InvalidOperationException>(() =>
            ExtractionAttemptLifecycle.Transition(
                CreateAttempt(currentStatus) with { AttemptId = next.AttemptId }, next));
    }

    [Fact]
    public void Transition_WhenTerminalFailureLacksRequiredMetadata_RejectsAttempt()
    {
        var next = CreateAttempt(ExtractionAttemptStatus.Failed) with
        {
            FailureStage = null,
            FailureCode = null,
            FailureMessage = null
        };

        Assert.Throws<InvalidOperationException>(() =>
            ExtractionAttemptLifecycle.Transition(
                CreateAttempt(ExtractionAttemptStatus.Running) with { AttemptId = next.AttemptId }, next));
    }

    [Fact]
    public void Transition_WhenAttemptIdChanges_RejectsAttempt()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ExtractionAttemptLifecycle.Transition(
                CreateAttempt(ExtractionAttemptStatus.Created),
                CreateAttempt(ExtractionAttemptStatus.Preparing)));
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
        status == ExtractionAttemptStatus.ProcessCompleted ? "candidate-output" : null,
        null);
}
