# Evidence Policy and Seam Composition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement AT-33 and AT-32 so S1Atlas applies a behavior-ownership gate and exposes one deterministic `investigate_seam` result through both CLI and MCP.

**Architecture:** AT-33 defines the policy and regression contract in the canonical skill. AT-32 adds shared query models and a service in `S1Atlas.Indexing.Query`; the CLI and MCP only adapt that result to their existing output/envelope conventions. The first implementation uses current managed/indexed evidence and reports native evidence as unavailable until the separate AT-35 plan enriches the same result.

**Tech Stack:** .NET 8, C#, `System.CommandLine`, ModelContextProtocol 2.2.0, SQLite repository fixtures, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-29-successor-epic-design.md`

## Global Constraints

- Querying is read-only and offline; no MCP operation launches the game, mutates Atlas state, downloads inputs, or applies a patch.
- A completed, matching index is the only authoritative indexed evidence.
- Evidence is classified as `FACT`, `DERIVED`, or `UNKNOWN`; interpretations remain separate.
- Missing or partial callers are not equivalent to zero callers.
- Owner candidates are deterministic and score-free: shortest path, distinct evidence-family count, canonical key ordinal, then symbol ID ordinal; limit applies after ordering.
- The committed operation name is `investigate_seam` for both CLI and MCP.
- The resolved packet contains behavioral question, pinned provenance, candidate symbol/role, body/callability coverage, authority/entity attribution, alternate/generic callers and exclusivity, lifecycle position/before-after state, public API check, UNKNOWN dimensions, and bounded next actions.
- Proprietary binaries, raw disassembly, extracted method bodies, reference-mod code/assets, and local paths remain outside the repository.

## File Map

- Modify `skills/s1atlas/SKILL.md` for the mandatory gate, negative-seam template, runtime-proof template, and OC examples.
- Modify `tests/S1Atlas.Docs.Tests/AgentUsageContractTests.cs` for the skill regression contract.
- Create `src/S1Atlas.Indexing/Query/SeamInvestigationQueryModels.cs` for shared request/result records and enums.
- Create `src/S1Atlas.Indexing/Query/SeamInvestigationService.cs` for authority-pinned evidence composition and deterministic owner traversal.
- Create `src/S1Atlas.Cli/Commands/InvestigateSeamCommand.cs` and `src/S1Atlas.Cli/Output/SeamInvestigationOutputModels.cs` for the CLI adapter.
- Modify `src/S1Atlas.Cli/CliApplication.cs` to register the command with existing query services.
- Create `src/S1Atlas.Mcp/Tools/SeamTools.cs` for the MCP adapter.
- Modify `src/S1Atlas.Mcp/McpServerComposition.cs`, `src/S1Atlas.Mcp/Program.cs`, and `src/S1Atlas.Mcp/Mapping/EnvelopeMapper.cs` when dependency wiring or envelope mapping is required.
- Modify `docs/USAGE.md` and `docs/REFERENCE.md` for command/tool/provenance documentation.
- Create `tests/S1Atlas.Indexing.Tests/Query/SeamInvestigationServiceTests.cs`, `tests/S1Atlas.IntegrationTests/SeamInvestigationCliTests.cs`, and `tests/S1Atlas.Mcp.Tests/SeamToolTests.cs`.
- Modify `tests/S1Atlas.Mcp.Tests/McpTestAtlas.cs` only to add repository-owned OC-32 and OC-2-shaped fixtures.

### Task 1: Lock the AT-33 policy contract

**Files:**
- Modify: `skills/s1atlas/SKILL.md`
- Modify: `tests/S1Atlas.Docs.Tests/AgentUsageContractTests.cs`

**Interfaces:**
- Produces the exact field checklist consumed by `SeamInvestigationResult`: behavioral question; pinned provenance; candidate symbol and role; body/callability coverage; authority/entity attribution; alternate/generic callers and exclusivity; lifecycle position and before/after state; API-before-patch result; remaining UNKNOWNs; bounded next action.

- [ ] **Step 1: Write failing skill assertions**

Add tests that require the skill to contain these concepts and reject unsupported recommendations:

```csharp
Assert.Contains("behavioral question", normalizedSkill, StringComparison.OrdinalIgnoreCase);
Assert.Contains("alternate/generic callers", normalizedSkill, StringComparison.OrdinalIgnoreCase);
Assert.Contains("event names are not lifecycle proof", normalizedSkill, StringComparison.OrdinalIgnoreCase);
Assert.Contains("missing or incomplete callers must not be reported as no callers", normalizedSkill, StringComparison.OrdinalIgnoreCase);
Assert.Contains("Negative-seam result", skill, StringComparison.Ordinal);
Assert.Contains("Runtime-proof plan", skill, StringComparison.Ordinal);
```

Add a negative fixture string containing a symbol name, friendly event name, callback order, and visible result; assert the test documentation says that fixture is insufficient for an ownership recommendation.

- [ ] **Step 2: Run the focused test and verify it fails**

Run:

```powershell
dotnet test tests/S1Atlas.Docs.Tests/S1Atlas.Docs.Tests.csproj --filter FullyQualifiedName~AgentUsageContractTests --no-restore
```

Expected: FAIL because the new gate and both templates are not yet present.

- [ ] **Step 3: Update the canonical skill**

Add a mandatory gate with the eleven fields above. Add explicit rules for OC-29, OC-32, OC-30, and OC-2. Define the negative-seam result as a completed evidence result that records why a candidate is unsuitable. Define the runtime-proof plan as hypotheses, observables, controls, duration, cleanup, and PASS/INCONCLUSIVE/STOP outcomes. State that unavailable body/caller coverage routes to named escalation rather than speculation.

- [ ] **Step 4: Run the focused test and verify it passes**

Run the same `dotnet test` command. Expected: PASS.

- [ ] **Step 5: Commit the policy contract**

```powershell
git add skills/s1atlas/SKILL.md tests/S1Atlas.Docs.Tests/AgentUsageContractTests.cs
git commit -m "docs: add behavior ownership gate"
```

### Task 2: Define the shared seam packet

**Files:**
- Create: `src/S1Atlas.Indexing/Query/SeamInvestigationQueryModels.cs`
- Create: `tests/S1Atlas.Indexing.Tests/Query/SeamInvestigationServiceTests.cs`

**Interfaces:**

Define these public query contracts in `S1Atlas.Indexing.Query`:

```csharp
public sealed record SeamInvestigationRequest(
    string BehavioralQuestion,
    string Selector,
    IndexQueryOptions Options,
    int RelationshipLimit = 50,
    int OwnerCandidateLimit = 10,
    int SourceContext = 5,
    bool IncludeDetails = false);

public enum SeamConclusion
{
    SupportableSeam,
    NoSupportableSeam,
    InsufficientCoverage
}

public enum EvidenceCoverage
{
    Complete,
    Bounded,
    Incomplete,
    Unavailable,
    NotApplicable
}

public sealed record SeamEvidenceClaim(
    string Dimension,
    string Classification,
    string Statement,
    IReadOnlyList<string> EvidenceIds);

public sealed record SeamEvidencePath(
    IReadOnlyList<string> RelationshipIds,
    int PathLength,
    int SupportingRelationshipFamilyCount);

public sealed record SeamOwnerCandidate(
    SymbolQueryResult Symbol,
    string Role,
    SeamEvidencePath Path,
    IReadOnlyList<string> EvidenceIds);

public sealed record SeamEvidenceSection(
    string Family,
    EvidenceCoverage Coverage,
    int TotalCount,
    int ReturnedCount,
    IReadOnlyList<string> EvidenceIds,
    string? Notice);

public sealed record SeamNextAction(
    string Kind,
    string Reason,
    string Scope,
    bool RequiresRuntimeProof);

public sealed record SeamInvestigationResult(
    string BehavioralQuestion,
    SeamConclusion Conclusion,
    SymbolResolutionResult Resolution,
    SymbolQueryResult? Candidate,
    string CandidateRole,
    BodyRecoveryStatus? BodyRecoveryStatus,
    EvidenceCoverage BodyCoverage,
    EvidenceCoverage CallableCoverage,
    IReadOnlyList<SeamEvidenceClaim> Claims,
    IReadOnlyList<SeamEvidenceSection> EvidenceSections,
    IReadOnlyList<SeamOwnerCandidate> OwnerCandidates,
    IReadOnlyList<string> CoverageWarnings,
    IReadOnlyList<string> UnknownDimensions,
    IReadOnlyList<SeamNextAction> NextActions);
```

- [ ] **Step 1: Write model invariants as failing tests**

Test that a resolved packet can represent `NoSupportableSeam`, that owner paths expose relationship IDs and derived family counts, and that `EvidenceCoverage.Incomplete` is distinct from a complete zero-count section.

- [ ] **Step 2: Run the focused test and verify it fails**

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter FullyQualifiedName~SeamInvestigationServiceTests --no-restore
```

Expected: FAIL because the contracts do not exist.

- [ ] **Step 3: Implement the records and validation**

Validate nonblank behavioral question/selector, positive limits, owner limit between 1 and 50, source context nonnegative, and `RelationshipLimit` between 1 and 50. Keep query contracts in `S1Atlas.Indexing.Query`; do not add a second seam-packet namespace under Core.

- [ ] **Step 4: Run the focused test and verify it passes**

Run the same test command. Expected: PASS.

- [ ] **Step 5: Commit the packet contract**

```powershell
git add src/S1Atlas.Indexing/Query/SeamInvestigationQueryModels.cs tests/S1Atlas.Indexing.Tests/Query/SeamInvestigationServiceTests.cs
git commit -m "feat: define seam investigation packet"
```

### Task 3: Compose evidence and deterministic owner candidates

**Files:**
- Create: `src/S1Atlas.Indexing/Query/SeamInvestigationService.cs`
- Modify: `tests/S1Atlas.Indexing.Tests/Query/SeamInvestigationServiceTests.cs`

**Interfaces:**

```csharp
public sealed class SeamInvestigationService
{
    public SeamInvestigationService(
        IndexQueryService gameQuery,
        FederatedIndexQueryService federatedQuery,
        ReferenceModQueryService referenceQuery);

    public Task<SeamInvestigationResult> InvestigateAsync(
        SeamInvestigationRequest request,
        CancellationToken cancellationToken);
}
```

The service uses the request's pinned `IndexQueryOptions`, resolves the target before relationship traversal, and calls existing query services for source, callable surface, callers, callees, references, call sites, and field references.

- [ ] **Step 1: Write the OC-32-shaped failing test**

Seed a completed fixture with a request boundary, a generic clearing method, a player-less UI settlement method, and a generic release method. Assert:

```csharp
Assert.Equal(SeamConclusion.NoSupportableSeam, result.Conclusion);
Assert.Contains(result.UnknownDimensions, value => value.Contains("lifecycle", StringComparison.OrdinalIgnoreCase));
Assert.Contains(result.CoverageWarnings, value => value.Contains("caller", StringComparison.OrdinalIgnoreCase));
Assert.DoesNotContain(result.OwnerCandidates, candidate => candidate.Symbol.QualifiedName == "Game.Free_Server");
```

Also assert the generic methods remain evidence that weakens exclusivity rather than being hidden.

- [ ] **Step 2: Write the deterministic-ordering failing test**

Create candidates with equal path lengths and different relationship-family counts. Assert order is path length ascending, family count descending, canonical key ordinal ascending, symbol ID ordinal ascending, and truncation occurs after sorting.

- [ ] **Step 3: Run focused tests and verify they fail**

```powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter FullyQualifiedName~SeamInvestigationServiceTests --no-restore
```

Expected: FAIL because the service does not exist.

- [ ] **Step 4: Implement exact resolution and evidence sections**

Return the original resolution status for ambiguous, not-found, and no-completed-index cases. For a resolved symbol, compose only bounded results. Build coverage from returned totals, returned counts, relationship notices, body status, and callable status; never convert an incomplete zero into a negative claim.

- [ ] **Step 5: Implement role and dimension claims**

Use explicit role values: `request`, `rpc-ingress`, `host-logic`, `state-writer`, `event-emission`, `presentation`, `persistence`, `cleanup`, and `unknown`. Emit claims as FACT or DERIVED strings backed by evidence IDs. Emit UNKNOWN dimensions for authority, canonical identity, lifecycle, exclusivity, native substrate, or API coverage whenever evidence is unavailable.

- [ ] **Step 6: Implement bounded reverse traversal**

Traverse relationship edges in ascending `RelationshipId` order. Retain the shortest path for each candidate. Compute distinct relationship-family count from the retained path. Sort by path length ascending, family count descending, canonical key ordinal ascending, and symbol ID ordinal ascending; apply `OwnerCandidateLimit` only after sorting. Do not calculate or emit a confidence score.

- [ ] **Step 7: Add deterministic next actions**

Use only these action kinds: `api-lookup`, `targeted-native-recovery`, `runtime-proof`, and `qualify-symbol`. Select actions from unknown dimensions and coverage state. A stubbed/unavailable body must produce `targeted-native-recovery` or `runtime-proof`, never an implementation recommendation.

- [ ] **Step 8: Run focused tests and verify they pass**

Run the same test command. Expected: PASS, including repeated invocation equality for the same fixture and request.

- [ ] **Step 9: Commit the shared service**

```powershell
git add src/S1Atlas.Indexing/Query/SeamInvestigationService.cs tests/S1Atlas.Indexing.Tests/Query/SeamInvestigationServiceTests.cs
git commit -m "feat: compose deterministic seam evidence"
```

### Task 4: Add the CLI adapter

**Files:**
- Create: `src/S1Atlas.Cli/Commands/InvestigateSeamCommand.cs`
- Create: `src/S1Atlas.Cli/Output/SeamInvestigationOutputModels.cs`
- Modify: `src/S1Atlas.Cli/CliApplication.cs`
- Create: `tests/S1Atlas.IntegrationTests/SeamInvestigationCliTests.cs`

**Interfaces:**

Register this command shape:

```text
s1atlas investigate_seam <selector> --question <text> [--codebase schedule-i] [--channel installed] [--build <id>] [--scope game|reference|all] [--collection <id>] [--relationship-limit <1-50>] [--owner-limit <1-50>] [--context <n>] [--details] [--json]
```

JSON is the serialized `SeamInvestigationResult` plus the existing command envelope. Human output prints conclusion, candidate, coverage warnings, owner paths, unknowns, and next actions before optional details.

- [ ] **Step 1: Write failing CLI integration tests**

Test the OC-32 fixture, ambiguous selector, no completed index, invalid limit, deterministic JSON rerun, and the valid `NoSupportableSeam` result with exit code 0.

- [ ] **Step 2: Run focused integration tests and verify they fail**

```powershell
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter FullyQualifiedName~SeamInvestigationCliTests --no-restore
```

Expected: FAIL because the command is not registered.

- [ ] **Step 3: Implement command validation and authority selection**

Follow `SourceCommand` and `IndexQueryCommandFactory` patterns. Reject blank questions/selectors, invalid limits, collection/scope mismatches, and build IDs used with non-installed Schedule I authority. Preserve existing authority resolver and federated selection behavior.

- [ ] **Step 4: Implement output mapping**

Map the shared result without changing its fields or ordering. Use `CommandOutput.Success` for resolved packets, including `NoSupportableSeam`; use existing failure semantics for ambiguous, unavailable, and invalid authority/resolution cases.

- [ ] **Step 5: Register and test the command**

Add `root.Subcommands.Add(InvestigateSeamCommand.Create(...))` in `CliApplication`. Run the focused test command and expect PASS.

- [ ] **Step 6: Commit the CLI adapter**

```powershell
git add src/S1Atlas.Cli/Commands/InvestigateSeamCommand.cs src/S1Atlas.Cli/Output/SeamInvestigationOutputModels.cs src/S1Atlas.Cli/CliApplication.cs tests/S1Atlas.IntegrationTests/SeamInvestigationCliTests.cs
git commit -m "feat: expose investigate seam through cli"
```

### Task 5: Add the MCP adapter and trust-boundary coverage

**Files:**
- Create: `src/S1Atlas.Mcp/Tools/SeamTools.cs`
- Modify: `src/S1Atlas.Mcp/McpServerComposition.cs`
- Modify: `src/S1Atlas.Mcp/Program.cs`
- Modify: `src/S1Atlas.Mcp/Mapping/EnvelopeMapper.cs`
- Create: `tests/S1Atlas.Mcp.Tests/SeamToolTests.cs`

**Interfaces:**

```csharp
[McpServerTool(Name = "investigate_seam")]
public Task<ToolEnvelope<SeamInvestigationResult>> InvestigateSeamAsync(
    string behavioralQuestion,
    string selector,
    string? buildId = null,
    string? scope = null,
    string? collection = null,
    int relationshipLimit = 50,
    int ownerLimit = 10,
    int context = 5,
    bool details = false,
    CancellationToken ct = default);
```

- [ ] **Step 1: Write failing MCP tests**

Cover resolved supportable and no-supportable packets, ambiguity, invalid limits, absent build authority, deterministic serialized data, and absence of mutation verbs. Assert provenance entries remain FACT or DERIVED and a partial call graph carries a coverage warning.

- [ ] **Step 2: Run focused MCP tests and verify they fail**

```powershell
dotnet test tests/S1Atlas.Mcp.Tests/S1Atlas.Mcp.Tests.csproj --filter FullyQualifiedName~SeamToolTests --no-restore
```

Expected: FAIL because the tool is not registered.

- [ ] **Step 3: Implement the tool using shared composition**

Resolve Schedule I/reference authority through the same path as `CodeSymbolTools`, construct `SeamInvestigationRequest`, and delegate to `SeamInvestigationService`. Do not access SQLite from the tool and do not duplicate relationship traversal.

- [ ] **Step 4: Map result statuses and provenance**

Add a mapper that preserves ambiguous candidates, unavailable authority, and the resolved packet's evidence boundaries. The valid `NoSupportableSeam` conclusion remains `ToolStatus.Resolved` because research completed successfully.

- [ ] **Step 5: Register the service and run tests**

Wire the singleton in `McpServerComposition` and `Program` only if current assembly tool activation requires it. Run the focused test command and expect PASS.

- [ ] **Step 6: Commit the MCP adapter**

```powershell
git add src/S1Atlas.Mcp/Tools/SeamTools.cs src/S1Atlas.Mcp/McpServerComposition.cs src/S1Atlas.Mcp/Program.cs src/S1Atlas.Mcp/Mapping/EnvelopeMapper.cs tests/S1Atlas.Mcp.Tests/SeamToolTests.cs
git commit -m "feat: expose investigate seam through mcp"
```

### Task 6: Document and verify CLI/MCP parity

**Files:**
- Modify: `docs/USAGE.md`
- Modify: `docs/REFERENCE.md`
- Modify: `tests/S1Atlas.Docs.Tests/AgentUsageContractTests.cs`
- Modify: `tests/S1Atlas.IntegrationTests/SeamInvestigationCliTests.cs`
- Modify: `tests/S1Atlas.Mcp.Tests/SeamToolTests.cs`

- [ ] **Step 1: Add documentation assertions**

Assert documentation names `investigate_seam`, both invocation surfaces, deterministic candidate order, no-confidence rule, FACT/DERIVED/UNKNOWN distinction, and read-only behavior.

- [ ] **Step 2: Update user documentation**

Document command/tool parameters, valid result states, coverage semantics, owner-candidate ordering, provenance, and examples of `NoSupportableSeam`. State that native recovery and runtime proof are next actions only, not automatic execution.

- [ ] **Step 3: Add parity assertions**

Run the same seeded request through CLI JSON and the MCP method. Deserialize both payloads and compare conclusion, candidate symbol ID, owner candidate symbol IDs/order, coverage warnings, unknown dimensions, next-action kinds, and provenance identifiers.

- [ ] **Step 4: Run complete track verification**

```powershell
dotnet build S1Atlas.sln --configuration Release
dotnet test S1Atlas.sln --configuration Release --no-build
powershell -ExecutionPolicy Bypass -File scripts/verify-repository-hygiene.ps1
```

Expected: build, tests, and hygiene pass; no proprietary or generated evidence is present.

- [ ] **Step 5: Commit documentation and parity verification**

```powershell
git add docs/USAGE.md docs/REFERENCE.md tests/S1Atlas.Docs.Tests/AgentUsageContractTests.cs tests/S1Atlas.IntegrationTests/SeamInvestigationCliTests.cs tests/S1Atlas.Mcp.Tests/SeamToolTests.cs
git commit -m "docs: document seam investigation parity"
```



