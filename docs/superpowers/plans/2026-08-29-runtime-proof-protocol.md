# Runtime-Proof Protocol Implementation Plan

**Status:** Shipped in `dd3f04456569a1a7009fe9905d2fbf5e27d60608`

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement AT-36 as a pure planner that converts static UNKNOWNs into one bounded diagnostic protocol while preserving execution-role and native-compatibility boundaries.

**Architecture:** RuntimeProofPlanner is a side-effect-free component in S1Atlas.Indexing.Query. It consumes the shared investigate_seam packet plus an explicit inventory of observables available in the selected build and emits a concise plan. The skill documents how agents use the plan; investigate_seam details can include it. No standalone runtime executor or game-launch capability is added.

**Tech Stack:** .NET 8, C#, existing query models, CLI JSON/human output, MCP structured results, xUnit.

**Spec:** docs/superpowers/specs/2026-08-29-successor-epic-design.md

## Global Constraints

- The planner is read-only and never launches a game or mutates runtime state.
- It never invents telemetry unavailable in the selected build.
- It requests one decisive bounded run when the required observables are available.
- PASS, INCONCLUSIVE, and STOP are first-class outcomes.
- Each plan is scoped to exactly one execution boundary: single-player, listen-host, dedicated server, or client.
- Authority, identity, lifecycle, and observability assumptions must not transfer between execution boundaries.
- Visible movement/functionality is not equivalent to native workflow compatibility.
- Runtime plans preserve cleanup, artifact hashes, lifecycle checks, and the no-speculative-fix rule.

## File Map

- Create src/S1Atlas.Indexing/Query/RuntimeProofPlanModels.cs and src/S1Atlas.Indexing/Query/RuntimeProofPlanner.cs.
- Modify skills/s1atlas/SKILL.md to explain how agents consume a generated plan.
- Modify src/S1Atlas.Indexing/Query/SeamInvestigationQueryModels.cs and src/S1Atlas.Indexing/Query/SeamInvestigationService.cs to attach an optional plan.
- Modify src/S1Atlas.Cli/Commands/InvestigateSeamCommand.cs and src/S1Atlas.Cli/Output/SeamInvestigationOutputModels.cs to expose runtime-plan detail output.
- Modify src/S1Atlas.Mcp/Tools/SeamTools.cs to expose an optional runtimePlan argument while preserving the same structured result.
- Create tests/S1Atlas.Indexing.Tests/Query/RuntimeProofPlannerTests.cs, tests/S1Atlas.IntegrationTests/RuntimeProofPlanCliTests.cs, and tests/S1Atlas.Mcp.Tests/RuntimeProofPlanTests.cs.
- Modify docs/USAGE.md and docs/REFERENCE.md for generated-plan semantics.

### Task 1: Define runtime-proof plan models

**Files:**
- Create: src/S1Atlas.Indexing/Query/RuntimeProofPlanModels.cs
- Create: tests/S1Atlas.Indexing.Tests/Query/RuntimeProofPlannerTests.cs

**Interfaces:**

~~~csharp
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

public sealed record RuntimeObservable(
    string Name,
    string Source,
    bool Available,
    string AvailabilityReason);

public sealed record RuntimeHypothesis(
    string Id,
    string Statement,
    IReadOnlyList<string> DistinguishingObservableNames);

public sealed record RuntimeControl(
    string Name,
    string Purpose,
    bool IsNegativeControl);

public sealed record RuntimeDecisionRule(
    RuntimeProofDecision Outcome,
    string Condition,
    string RequiredEvidence);

public sealed record RuntimeProofPlan(
    RuntimeExecutionBoundary Boundary,
    string Question,
    IReadOnlyList<RuntimeHypothesis> Hypotheses,
    IReadOnlyList<string> EstablishedFacts,
    IReadOnlyList<string> StaticUnknowns,
    IReadOnlyList<RuntimeObservable> Observables,
    IReadOnlyList<RuntimeControl> Controls,
    TimeSpan Duration,
    TimeSpan SampleInterval,
    IReadOnlyList<string> LifecycleChecks,
    IReadOnlyList<string> CleanupSteps,
    IReadOnlyList<string> ArtifactRequirements,
    IReadOnlyList<RuntimeDecisionRule> DecisionRules,
    bool DiagnosticOnly,
    string CompatibilityBoundary);
~~~

- [ ] **Step 1: Write failing model tests**

Assert a plan requires one execution boundary, has at least one hypothesis and one observable, contains PASS/INCONCLUSIVE/STOP rules, and marks DiagnosticOnly true. Assert unavailable observables remain listed with their reason.

- [ ] **Step 2: Run focused test and verify it fails**

~~~powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter FullyQualifiedName~RuntimeProofPlannerTests --no-restore
~~~

Expected: FAIL because the models do not exist.

- [ ] **Step 3: Implement validated records**

Reject blank names/statements, nonpositive duration or sample intervals, a sample interval longer than the duration, an empty boundary, and decision tables missing any of the three outcomes. Keep records in S1Atlas.Indexing.Query with the other public query contracts.

- [ ] **Step 4: Run focused test and verify it passes**

Run the same command. Expected: PASS.

- [ ] **Step 5: Commit the plan models**

~~~powershell
git add src/S1Atlas.Indexing/Query/RuntimeProofPlanModels.cs tests/S1Atlas.Indexing.Tests/Query/RuntimeProofPlannerTests.cs
git commit -m "feat: define runtime proof plan"
~~~

### Task 2: Implement the planner from static UNKNOWNs

**Files:**
- Create: src/S1Atlas.Indexing/Query/RuntimeProofPlanner.cs
- Modify: tests/S1Atlas.Indexing.Tests/Query/RuntimeProofPlannerTests.cs

**Interfaces:**

~~~csharp
public sealed record RuntimeProofPlannerInput(
    SeamInvestigationResult Seam,
    RuntimeExecutionBoundary Boundary,
    IReadOnlyList<RuntimeObservable> AvailableObservables,
    TimeSpan Duration,
    TimeSpan SampleInterval);

public sealed class RuntimeProofPlanner
{
    public RuntimeProofPlan Build(RuntimeProofPlannerInput input);
}
~~~

- [ ] **Step 1: Write the OC-29 failing test**

Provide an event candidate whose name suggests completion, a runtime state field that is available, and occurrence/receipt timestamps. Assert the plan creates competing hypotheses, asks for before/after state at occurrence time, includes a negative control, and does not call the event authoritative from its name.

- [ ] **Step 2: Write the OC-30 failing test**

Provide a host-identity UNKNOWN with available save-folder identity and sleep/load/restart observables. Assert the plan checks readiness before use, restart stability, sleep/load persistence, and distinguishes runtime-readable value from canonical identity.

- [ ] **Step 3: Write the OC-2 failing compatibility test**

Provide visible custom movement, absent native reachability evidence, and available destination/NavMesh/dispatch observables. Assert the plan records native navigation as a compatibility risk and requests reachability/dispatch evidence rather than treating visible movement as proof.

- [ ] **Step 4: Run focused tests and verify they fail**

~~~powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter FullyQualifiedName~RuntimeProofPlannerTests --no-restore
~~~

Expected: FAIL because the planner does not exist.

- [ ] **Step 5: Implement fact/unknown extraction**

Copy only FACT and DERIVED claims from the seam packet into EstablishedFacts. Copy unresolved dimensions and incomplete coverage into StaticUnknowns. If an observable is not marked available in the input, do not emit it as a required measurement; add it as unavailable with an availability reason.

- [ ] **Step 6: Implement hypothesis and control generation**

For lifecycle/authority questions, generate competing “candidate owns transition” and “candidate observes or presents transition” hypotheses. For identity questions, generate “runtime value is canonical” and “runtime value is transient/placeholder” hypotheses. For compatibility questions, generate “native substrate preserved” and “replacement bypasses native consumer” hypotheses. Add a negative control that would falsify the candidate explanation.

- [ ] **Step 7: Implement boundary-aware lifecycle and compatibility sections**

Set CompatibilityBoundary to the exact boundary name. Add only relevant checks for single-player, listen-host, dedicated server, or client. Never use dedicated-server authority assumptions in a client plan. Add native workflow owner, downstream consumers, state flags, surfaces, caches, network authority, interception/suppression/replacement, restore/teardown, and smallest native-preserving alternative when the seam packet contains compatibility UNKNOWNs.

- [ ] **Step 8: Implement decision rules and cleanup**

Emit a PASS rule requiring all decisive observables and controls, an INCONCLUSIVE rule for missing/contradictory evidence, and a STOP rule for mutation, authority-boundary violation, unsafe cleanup, or unavailable required telemetry. Add bounded duration/sample interval, artifact hashes, and cleanup instructions. Set DiagnosticOnly to true.

- [ ] **Step 9: Run focused tests and verify they pass**

Run the same test command. Expected: PASS.

- [ ] **Step 10: Commit the planner**

~~~powershell
git add src/S1Atlas.Indexing/Query/RuntimeProofPlanner.cs tests/S1Atlas.Indexing.Tests/Query/RuntimeProofPlannerTests.cs
git commit -m "feat: generate bounded runtime proof plans"
~~~

### Task 3: Integrate the planner into investigate_seam

**Files:**
- Modify: src/S1Atlas.Indexing/Query/SeamInvestigationQueryModels.cs
- Modify: src/S1Atlas.Indexing/Query/SeamInvestigationService.cs
- Modify: src/S1Atlas.Cli/Commands/InvestigateSeamCommand.cs
- Modify: src/S1Atlas.Cli/Output/SeamInvestigationOutputModels.cs
- Modify: src/S1Atlas.Mcp/Tools/SeamTools.cs
- Create: tests/S1Atlas.IntegrationTests/RuntimeProofPlanCliTests.cs
- Create: tests/S1Atlas.Mcp.Tests/RuntimeProofPlanTests.cs

**Interfaces:**

Extend the request/result without changing existing defaults:

~~~csharp
public sealed record SeamInvestigationRequest(
    string BehavioralQuestion,
    string Selector,
    IndexQueryOptions Options,
    int RelationshipLimit = 50,
    int OwnerCandidateLimit = 10,
    int SourceContext = 5,
    bool IncludeDetails = false,
    RuntimeExecutionBoundary? RuntimeBoundary = null,
    IReadOnlyList<RuntimeObservable>? RuntimeObservables = null,
    TimeSpan? RuntimeDuration = null,
    TimeSpan? RuntimeSampleInterval = null);
~~~

Add RuntimeProofPlan? RuntimePlan to SeamInvestigationResult. The plan is null unless a boundary and observable inventory are explicitly supplied.

- [ ] **Step 1: Write failing integration tests**

Assert default investigate_seam output has no runtime plan, while --runtime-plan --boundary listen-host includes one. Assert MCP receives the same plan fields as CLI JSON for the same fixture.

- [ ] **Step 2: Run focused integration/MCP tests and verify they fail**

~~~powershell
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter FullyQualifiedName~RuntimeProofPlanCliTests --no-restore
dotnet test tests/S1Atlas.Mcp.Tests/S1Atlas.Mcp.Tests.csproj --filter FullyQualifiedName~RuntimeProofPlanTests --no-restore
~~~

Expected: FAIL because optional planner input/output is not wired.

- [ ] **Step 3: Wire explicit planner inputs**

Add CLI options for --runtime-plan, --boundary, --duration, --sample-interval, and repeated --observable name=source:available|unavailable. Add equivalent MCP arguments. Reject a runtime plan request without a boundary or with an unavailable decisive observable; do not infer a boundary from the build.

- [ ] **Step 4: Attach the plan after seam composition**

Build the seam result first, then pass it to RuntimeProofPlanner only when explicitly requested. Preserve all seam coverage/unknowns and do not let the plan upgrade an UNKNOWN to FACT.

- [ ] **Step 5: Run focused tests and verify they pass**

Run both commands from Step 2. Expected: PASS.

- [ ] **Step 6: Commit integration**

~~~powershell
git add src/S1Atlas.Indexing/Query/SeamInvestigationQueryModels.cs src/S1Atlas.Indexing/Query/SeamInvestigationService.cs src/S1Atlas.Cli/Commands/InvestigateSeamCommand.cs src/S1Atlas.Cli/Output/SeamInvestigationOutputModels.cs src/S1Atlas.Mcp/Tools/SeamTools.cs tests/S1Atlas.IntegrationTests/RuntimeProofPlanCliTests.cs tests/S1Atlas.Mcp.Tests/RuntimeProofPlanTests.cs
git commit -m "feat: attach runtime plans to seam investigations"
~~~

### Task 4: Update the skill and documentation

**Files:**
- Modify: skills/s1atlas/SKILL.md
- Modify: docs/USAGE.md
- Modify: docs/REFERENCE.md
- Modify: tests/S1Atlas.Docs.Tests/AgentUsageContractTests.cs

- [ ] **Step 1: Add skill usage rules**

Explain generated plans are diagnostic protocols only, require an explicit execution boundary, preserve FACT/DERIVED/UNKNOWN, and must not justify a speculative fix. State that PASS confirms only the plan's named evidence conditions, while INCONCLUSIVE and STOP require escalation.

- [ ] **Step 2: Document compatibility matrix fields**

Document native workflow owner, downstream consumers, native agents/destinations/state flags/surfaces/caches/network authority, interception/suppression/replacement, restore/teardown, precedent/licensing boundary, and smallest native-preserving alternative.

- [ ] **Step 3: Add documentation assertions**

Assert the skill and docs mention all four execution boundaries, occurrence-time versus receipt-time state, positive/negative controls, bounded duration/sample rate, cleanup/artifact hashes, and PASS/INCONCLUSIVE/STOP.

- [ ] **Step 4: Run complete verification**

~~~powershell
dotnet build S1Atlas.sln --configuration Release
dotnet test S1Atlas.sln --configuration Release --no-build
powershell -ExecutionPolicy Bypass -File scripts/verify-repository-hygiene.ps1
~~~

Expected: PASS; no runtime executor, game-launch code, mutation capability, or proprietary evidence is introduced.

- [ ] **Step 5: Commit documentation**

~~~powershell
git add skills/s1atlas/SKILL.md docs/USAGE.md docs/REFERENCE.md tests/S1Atlas.Docs.Tests/AgentUsageContractTests.cs
git commit -m "docs: define runtime proof protocol usage"
~~~


