using S1Atlas.Indexing.Query;
using System.Text.Json;
using Xunit;

namespace S1Atlas.Indexing.Tests.Query;

public sealed class RuntimeProofPlannerTests
{
    [Fact]
    public void Create_IsBoundedAndKeepsObservationsInsideTheExecutionBoundary()
    {
        var plan = RuntimeProofPlanner.Create(new RuntimeProofRequest(
            "Which authority owns settlement clearing after load?",
            RuntimeExecutionBoundary.ListenHost,
            "Game.Seams.Target.Run",
            "ListenHost.Authority",
            ["save/load lifecycle is relevant"],
            ["host state transition", "receipt timestamp"],
            ["dedicated-server telemetry"],
            PolicyGateSatisfied: true));

        Assert.Equal(RuntimeExecutionBoundary.ListenHost, plan.ExecutionBoundary);
        Assert.Equal(RuntimeProofDecision.Inconclusive, plan.InitialDecision);
        Assert.Equal("10 minutes maximum per control", plan.Duration);
        Assert.Contains(plan.Controls, control => control.Contains("ListenHost", StringComparison.Ordinal));
        Assert.Contains(plan.Observables, observable =>
            observable.Name == "dedicated-server telemetry" && !observable.Available);
        Assert.NotEmpty(plan.LifecycleChecks);
        Assert.Contains(plan.NativeWorkflowChecks, value => value.Contains("downstream consumers", StringComparison.Ordinal));
        Assert.Contains(plan.CompatibilityChecks, value => value.Contains("native substrate", StringComparison.Ordinal));
        Assert.NotEmpty(plan.Alternatives);
        Assert.Contains(plan.UnknownDimensions, value => value.Contains("dedicated-server telemetry", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_StopsWhenPolicyGateIsNotSatisfied()
    {
        var plan = RuntimeProofPlanner.Create(new RuntimeProofRequest(
            "Which seam owns the transition?",
            RuntimeExecutionBoundary.SinglePlayer,
            "Game.Target.Run",
            "Game.Target",
            [],
            [],
            [],
            PolicyGateSatisfied: false));

        Assert.Equal(RuntimeProofDecision.Stop, plan.InitialDecision);
        Assert.Contains(plan.Outcomes, outcome =>
            outcome.Decision == RuntimeProofDecision.Stop &&
            outcome.Condition.Contains("policy gate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Create_IsDeterministicForTheSameRequest()
    {
        var request = new RuntimeProofRequest(
            "Which authority owns the event?",
            RuntimeExecutionBoundary.Client,
            "Game.Target.Event",
            "Client.Observer",
            ["FACT: event is emitted"],
            ["event receipt", "state transition"],
            ["server authority telemetry"],
            PolicyGateSatisfied: true);

        Assert.Equal(
            JsonSerializer.Serialize(RuntimeProofPlanner.Create(request)),
            JsonSerializer.Serialize(RuntimeProofPlanner.Create(request)));
    }

    [Theory]
    [InlineData("OC-29 event ordering", "occurrence-time event authority versus receipt-time observer", RuntimeExecutionBoundary.SinglePlayer)]
    [InlineData("OC-30 host authority", "listen-host authority versus client observer", RuntimeExecutionBoundary.ListenHost)]
    [InlineData("OC-2 native navigation", "native workflow owner versus managed replacement", RuntimeExecutionBoundary.Client)]
    public void Create_CoversTheOrganizedCrimeRuntimeShapes(
        string shape,
        string question,
        RuntimeExecutionBoundary boundary)
    {
        var plan = RuntimeProofPlanner.Create(new RuntimeProofRequest(
            shape + ": " + question,
            boundary,
            "Game.OrganizedCrime.Target",
            "selected authority",
            ["authority and lifecycle are UNKNOWN"],
            ["occurrence-time state", "receipt-time state"],
            ["cross-role telemetry"],
            PolicyGateSatisfied: true));

        Assert.Contains(plan.Hypotheses, value => value.Contains("authority", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.NativeWorkflowChecks, value => value.Contains("downstream consumers", StringComparison.Ordinal));
        Assert.Contains(plan.CompatibilityChecks, value => value.Contains("interception", StringComparison.Ordinal));
        Assert.Contains(plan.Alternatives, value => value.Contains("native-preserving", StringComparison.Ordinal));
        Assert.Contains(plan.Outcomes, value => value.Decision == RuntimeProofDecision.Inconclusive);
    }
}
