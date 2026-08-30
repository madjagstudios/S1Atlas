using S1Atlas.Core.Indexing;

namespace S1Atlas.Indexing.Query;

public enum RuntimeExecutionBoundary
{
    SinglePlayer,
    ListenHost,
    DedicatedServer,
    Client
}

public enum RuntimeProofDecision
{
    Pass,
    Inconclusive,
    Stop
}

public sealed record RuntimeProofRequest(
    string BehavioralQuestion,
    RuntimeExecutionBoundary ExecutionBoundary,
    string CanonicalIdentity,
    string Authority,
    IReadOnlyList<string> KnownStaticFacts,
    IReadOnlyList<string> AvailableObservables,
    IReadOnlyList<string> UnavailableObservables,
    bool PolicyGateSatisfied);

public sealed record RuntimeProofObservable(
    string Name,
    bool Available,
    string Limitation);

public sealed record RuntimeProofOutcome(
    RuntimeProofDecision Decision,
    string Condition);

public sealed record RuntimeProofPlan(
    string BehavioralQuestion,
    RuntimeExecutionBoundary ExecutionBoundary,
    string CanonicalIdentity,
    string Authority,
    IReadOnlyList<string> Hypotheses,
    IReadOnlyList<RuntimeProofObservable> Observables,
    IReadOnlyList<string> Controls,
    string Duration,
    string SampleRate,
    IReadOnlyList<string> LifecycleChecks,
    IReadOnlyList<string> NativeWorkflowChecks,
    IReadOnlyList<string> CompatibilityChecks,
    IReadOnlyList<string> Alternatives,
    IReadOnlyList<string> Cleanup,
    IReadOnlyList<RuntimeProofOutcome> Outcomes,
    IReadOnlyList<string> UnknownDimensions,
    RuntimeProofDecision InitialDecision);

public static class RuntimeProofPlanner
{
    public static RuntimeProofPlan Create(RuntimeProofRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BehavioralQuestion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CanonicalIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Authority);
        ArgumentNullException.ThrowIfNull(request.KnownStaticFacts);
        ArgumentNullException.ThrowIfNull(request.AvailableObservables);
        ArgumentNullException.ThrowIfNull(request.UnavailableObservables);

        var available = Normalize(request.AvailableObservables);
        var unavailable = Normalize(request.UnavailableObservables);
        var observables = available
            .Select(name => new RuntimeProofObservable(name, true, "Available for the selected execution boundary."))
            .Concat(unavailable.Select(name => new RuntimeProofObservable(
                name,
                false,
                "Unavailable for the selected build and execution boundary.")))
            .OrderBy(observable => observable.Name, StringComparer.Ordinal)
            .ToArray();
        var lifecycleRelevant = request.KnownStaticFacts.Any(IsLifecycleFact) ||
            request.BehavioralQuestion.Contains("save", StringComparison.OrdinalIgnoreCase) ||
            request.BehavioralQuestion.Contains("load", StringComparison.OrdinalIgnoreCase) ||
            request.BehavioralQuestion.Contains("lifecycle", StringComparison.OrdinalIgnoreCase);
        var unknowns = unavailable
            .Select(value => "Unavailable runtime observable: " + value)
            .Concat(request.KnownStaticFacts.Where(value => value.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var initialDecision = !request.PolicyGateSatisfied || string.IsNullOrWhiteSpace(request.Authority)
            ? RuntimeProofDecision.Stop
            : RuntimeProofDecision.Inconclusive;

        return new RuntimeProofPlan(
            request.BehavioralQuestion.Trim(),
            request.ExecutionBoundary,
            request.CanonicalIdentity.Trim(),
            request.Authority.Trim(),
            [
                "H1: the selected authority owns the state transition under the requested execution boundary.",
                "H2: an alternate owner, observer, or replacement preserves or changes the transition outside the selected authority."
            ],
            observables,
            [
                $"Positive control: observe the known-good path in {request.ExecutionBoundary} without the proposed interception.",
                $"Negative control: repeat the same bounded path with the selected interception or replacement disabled in {request.ExecutionBoundary}."
            ],
            "10 minutes maximum per control",
            "One observation per second maximum; sample only declared observables",
            lifecycleRelevant
                ? [
                    "Record state at occurrence time and receipt time separately.",
                    "Check load, save, sleep, and restart boundaries when the selected behavior persists state.",
                    "Compare teardown and downstream-consumer observations before and after the intervention."
                ]
                : ["No lifecycle check is added because the supplied static facts do not identify a lifecycle or persistence dependency."],
            [
                "Identify the native workflow owner and downstream consumers within the selected execution boundary.",
                "Record occurrence-time state separately from receipt-time state for wrapper, RPC ingress, host logic, client, and presentation observations."
            ],
            [
                "Preserve the existing native substrate and distinguish interception from replacement; do not infer compatibility from a matching managed signature.",
                "Record applicable precedent and licensing/distribution boundaries without retaining or redistributing proprietary bodies."
            ],
            [
                "Prefer the smallest native-preserving alternative that answers the behavioral question while retaining teardown and downstream-consumer behavior."
            ],
            [
                "Stop the experiment at the duration limit or first authority/identity mismatch.",
                "Delete temporary runtime logs after extracting bounded observations; do not retain game binaries, raw bodies, or disassembly."
            ],
            [
                new(RuntimeProofDecision.Pass, "All declared observables support one hypothesis, both controls behave as specified, and authority plus canonical identity remain stable."),
                new(RuntimeProofDecision.Inconclusive, "A declared observable is unavailable, controls disagree, lifecycle evidence is missing, or observations conflict."),
                new(RuntimeProofDecision.Stop, "The policy gate is not satisfied or authority/canonical identity changes during the experiment.")
            ],
            unknowns,
            initialDecision);
    }

    private static bool IsLifecycleFact(string value) =>
        value.Contains("lifecycle", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("persistence", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("save", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("load", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Normalize(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
}
