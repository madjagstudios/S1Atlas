using System.ComponentModel;
using ModelContextProtocol.Server;
using S1Atlas.Application.Envelope;
using S1Atlas.Indexing.Query;

namespace S1Atlas.Mcp.Tools;

[McpServerToolType]
public sealed class RuntimeProofTools
{
    [McpServerTool(Name = "plan_runtime_proof"), Description("Generate a bounded, read-only runtime diagnostic plan without launching a game or inventing telemetry.")]
    public Task<ToolEnvelope<RuntimeProofPlan>> PlanRuntimeProofAsync(
        [Description("The behavioral question the diagnostic plan must resolve.")] string behavioralQuestion,
        [Description("Execution boundary: singlePlayer, listenHost, dedicatedServer, or client.")] string executionBoundary,
        [Description("Canonical identity to pin during the experiment.")] string canonicalIdentity,
        [Description("Authority or host role that owns the selected behavior.")] string authority,
        [Description("Static facts already established for this selected build.")] string[]? knownStaticFacts = null,
        [Description("Runtime observables available for this build and execution boundary.")] string[]? availableObservables = null,
        [Description("Runtime observables unavailable for this build and execution boundary.")] string[]? unavailableObservables = null,
        [Description("Whether the behavior-ownership policy gate is satisfied.")] bool policyGateSatisfied = false)
    {
        if (!Enum.TryParse<RuntimeExecutionBoundary>(executionBoundary, ignoreCase: true, out var boundary))
        {
            return Task.FromResult(
                ToolEnvelope<RuntimeProofPlan>.Invalid(
                    new ToolError("InvalidExecutionBoundary", "Execution boundary must be singlePlayer, listenHost, dedicatedServer, or client.")));
        }

        try
        {
            var plan = RuntimeProofPlanner.Create(new RuntimeProofRequest(
                behavioralQuestion,
                boundary,
                canonicalIdentity,
                authority,
                knownStaticFacts ?? [],
                availableObservables ?? [],
                unavailableObservables ?? [],
                policyGateSatisfied));
            return Task.FromResult(
                ToolEnvelope<RuntimeProofPlan>.Resolved(
                    null,
                    plan,
                    new ProvenanceEntry(
                        ProvenanceClassification.Derived,
                        "runtime-proof-planner",
                        null,
                        null,
                        null)));
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(
                ToolEnvelope<RuntimeProofPlan>.Invalid(
                    new ToolError("InvalidRuntimeProofRequest", exception.Message)));
        }
    }
}
