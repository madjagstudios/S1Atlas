# API Parity and Native Evidence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement AT-34's S1API/S1MAPI MCP parity surface and AT-35's bounded, provenance-preserving native evidence path, then attach both to the shared seam packet.

**Architecture:** API parity is a read-only extension over the existing completed API indexes and ApiIndexingWorkflow. Native recovery is a separate provider with hash-keyed records and explicit status; it enriches normal query results only when a request is bounded and inputs match. AT-34's standalone MCP surface ships before its investigate_seam integration; AT-35 starts only after the AT-32 packet contract is stable.

**Tech Stack:** .NET 8, C#, SQLite migrations/repositories, existing ILSpy/managed-index models, ModelContextProtocol 2.2.0, xUnit.

**Spec:** docs/superpowers/specs/2026-08-29-successor-epic-design.md

## Global Constraints

- Recovery is read-only and local.
- A completed, matching index is authoritative; stale, missing, ambiguous, or failed input remains explicit.
- Native recovery is bounded to user-selected methods or traversal budgets.
- Every native record preserves build, GameAssembly.dll hash, S1Atlas index identity, recovery tool name/version/hash, pointer mapping, wrapper/native relationship, recovered direct edges and field accesses, completeness, output hashes, and timestamps.
- Recovery failure never silently falls back to an interop stub.
- Indirect/cross-thread/runtime dispatch remains UNKNOWN unless directly supported.
- No game binary, proprietary extracted body, raw disassembly, reference-mod code/assets, or local path is committed.
- API MCP calls do not build, download, or mutate indexes.

## File Map

- Create src/S1Atlas.Indexing/Query/ApiIndexQueryModels.cs and src/S1Atlas.Indexing/Query/ApiIndexQueryService.cs for API catalog/selection/query contracts.
- Create src/S1Atlas.Mcp/Tools/ApiIndexTools.cs; modify src/S1Atlas.Mcp/McpServerComposition.cs, src/S1Atlas.Mcp/Program.cs, and src/S1Atlas.Mcp/Mapping/EnvelopeMapper.cs for MCP wiring.
- Modify src/S1Atlas.Indexing/Query/IndexQueryService.cs only where an API-specific overload is needed to preserve existing CLI semantics.
- Modify src/S1Atlas.Core/Storage/IIndexRepository.cs and src/S1Atlas.Storage/Sqlite/ReadOnlySqliteAtlasRepository.cs only if catalog metadata cannot be composed from existing completed-index lookups.
- Create tests/S1Atlas.Mcp.Tests/ApiIndexToolTests.cs, tests/S1Atlas.Indexing.Tests/Query/ApiIndexQueryServiceTests.cs, and add API fixture cases to tests/S1Atlas.IntegrationTests/CliQueryParityTests.cs.
- Create native contracts in src/S1Atlas.Indexing/Query/NativeEvidenceQueryModels.cs and the provider/workflow under src/S1Atlas.Indexing/NativeRecovery/.
- Modify src/S1Atlas.Core/Storage/IIndexRepository.cs, src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs, src/S1Atlas.Storage/Sqlite/ReadOnlySqliteAtlasRepository.cs, and src/S1Atlas.Storage/Migrations/SqliteMigrations.cs for hash-keyed native records.
- Create tests/S1Atlas.Indexing.Tests/NativeRecovery/NativeRecoveryWorkflowTests.cs, tests/S1Atlas.Storage.Tests/Sqlite/NativeEvidenceRepositoryTests.cs, and tests/S1Atlas.Storage.Tests/Migrations/NativeEvidenceMigrationTests.cs.
- Modify the AT-32 seam service/tests and docs/USAGE.md/docs/REFERENCE.md for API/native integration.

### Task 1: Verify and model API index selection

**Files:**
- Create: src/S1Atlas.Indexing/Query/ApiIndexQueryModels.cs
- Create: src/S1Atlas.Indexing/Query/ApiIndexQueryService.cs
- Create: tests/S1Atlas.Indexing.Tests/Query/ApiIndexQueryServiceTests.cs

**Interfaces:**

~~~csharp
public enum ApiIndexAvailability
{
    Current,
    Stale,
    Unavailable,
    Ambiguous
}

public sealed record ApiIndexSelection(
    CodebaseKind Codebase,
    CodeChannel Channel,
    ApiIndexAvailability Availability,
    string? IndexId,
    string? SnapshotId,
    string? SourceIdentity,
    string? EnvironmentSnapshotId,
    string? Message);

public sealed record ApiIndexCatalogResult(
    IReadOnlyList<ApiIndexSelection> Selections,
    string? RequestedBuildId,
    string? ResolvedBuildId);

public sealed class ApiIndexQueryService
{
    public ApiIndexQueryService(IIndexRepository repository, IndexQueryService indexQueryService);

    public Task<ApiIndexCatalogResult> ListAsync(
        string? buildId,
        CancellationToken cancellationToken);

    public Task<SymbolSearchResult> SearchAsync(
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int limit,
        CancellationToken cancellationToken);

    public Task<SourceSnippetResolutionResult> SourceAsync(
        CodebaseKind codebase,
        CodeChannel channel,
        string selector,
        int context,
        int relatedLimit,
        CancellationToken cancellationToken);
}
~~~

- [ ] **Step 1: Write failing selection tests**

Seed completed installed S1API, completed release S1MAPI, an absent preview index, and an installed API bound to another environment snapshot. Assert catalog entries distinguish Current, Stale, and Unavailable; no absent index is reported as empty success.

- [ ] **Step 2: Run focused tests and verify they fail**

~~~powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter FullyQualifiedName~ApiIndexQueryServiceTests --no-restore
~~~

Expected: FAIL because the service and models do not exist.

- [ ] **Step 3: Implement catalog selection**

Use the existing authority resolver for an optional Schedule I build. For each fixed pair (S1Api, Installed), (S1Api, Release), (S1Api, Preview), (S1MApi, Installed), (S1MApi, Release), and (S1MApi, Preview), resolve the latest completed index and snapshot. Mark installed API Current only when its environment snapshot matches the selected build; mark a completed installed API for another build Stale; mark missing or non-completed indexes Unavailable. Release/preview indexes are recorded as completed API sources without inventing a Schedule I build binding.

- [ ] **Step 4: Implement API query delegation**

Call existing IndexQueryService overloads with explicit codebase/channel. Preserve symbol ambiguity, source/body status, relationship totals, and source commit/binary identity from the selected snapshot. Do not add a second SQLite query implementation in MCP.

- [ ] **Step 5: Run focused tests and verify they pass**

Run the same test command. Expected: PASS.

- [ ] **Step 6: Commit the API query service**

~~~powershell
git add src/S1Atlas.Indexing/Query/ApiIndexQueryModels.cs src/S1Atlas.Indexing/Query/ApiIndexQueryService.cs tests/S1Atlas.Indexing.Tests/Query/ApiIndexQueryServiceTests.cs
git commit -m "feat: model completed api index selection"
~~~

### Task 2: Expose API indexes through MCP

**Files:**
- Create: src/S1Atlas.Mcp/Tools/ApiIndexTools.cs
- Modify: src/S1Atlas.Mcp/McpServerComposition.cs
- Modify: src/S1Atlas.Mcp/Program.cs
- Modify: src/S1Atlas.Mcp/Mapping/EnvelopeMapper.cs
- Create: tests/S1Atlas.Mcp.Tests/ApiIndexToolTests.cs

**Interfaces:**

~~~csharp
[McpServerTool(Name = "list_api_indexes")]
public Task<ToolEnvelope<ApiIndexCatalogResult>> ListApiIndexesAsync(
    string? buildId = null,
    CancellationToken ct = default);

[McpServerTool(Name = "search_api_symbols")]
public Task<ToolEnvelope<SymbolSearchResult>> SearchApiSymbolsAsync(
    string codebase,
    string channel,
    string query,
    int limit = 50,
    CancellationToken ct = default);

[McpServerTool(Name = "get_api_source")]
public Task<ToolEnvelope<SourceSnippetQueryResult>> GetApiSourceAsync(
    string codebase,
    string channel,
    string selector,
    int context = 5,
    int relatedLimit = 10,
    CancellationToken ct = default);
~~~

- [ ] **Step 1: Write failing MCP tests**

Cover exact S1API lookup, exact S1MAPI lookup, ambiguous symbol, missing index, stale installed index, source/body status, and a read-only invocation that leaves Atlas data-root hashes unchanged. Assert every result exposes codebase, channel, source identity, and index identity.

- [ ] **Step 2: Run focused MCP tests and verify they fail**

~~~powershell
dotnet test tests/S1Atlas.Mcp.Tests/S1Atlas.Mcp.Tests.csproj --filter FullyQualifiedName~ApiIndexToolTests --no-restore
~~~

Expected: FAIL because the tools are not registered.

- [ ] **Step 3: Implement argument parsing and envelope mapping**

Accept only s1api and s1mapi codebases and installed, release, or preview channels. Return Invalid for unsupported values, NotFound for no completed index, Ambiguous for symbol ambiguity, and Unavailable for stale/unusable authority. Include API-specific provenance entries using selected codebase/channel and index ID; do not force a Schedule I build context onto release/preview results.

- [ ] **Step 4: Register tools and preserve the mutation boundary**

Wire ApiIndexQueryService through shared read-only composition. Add registration tests that verify the new names contain no mutation verbs and that McpProject_DoesNotReferenceCliOrExtractionProjects remains true.

- [ ] **Step 5: Run focused and trust tests**

~~~powershell
dotnet test tests/S1Atlas.Mcp.Tests/S1Atlas.Mcp.Tests.csproj --filter "FullyQualifiedName~ApiIndexToolTests|FullyQualifiedName~HostCompositionTests|FullyQualifiedName~McpTrustBoundaryTests" --no-restore
~~~

Expected: PASS.

- [ ] **Step 6: Commit the API MCP surface**

~~~powershell
git add src/S1Atlas.Mcp/Tools/ApiIndexTools.cs src/S1Atlas.Mcp/McpServerComposition.cs src/S1Atlas.Mcp/Program.cs src/S1Atlas.Mcp/Mapping/EnvelopeMapper.cs tests/S1Atlas.Mcp.Tests/ApiIndexToolTests.cs
git commit -m "feat: expose api indexes through mcp"
~~~

### Task 3: Create the bounded native evidence contract and feasibility record

**Files:**
- Create: src/S1Atlas.Indexing/Query/NativeEvidenceQueryModels.cs
- Create: src/S1Atlas.Indexing/NativeRecovery/INativeBodyRecoveryProvider.cs
- Create: src/S1Atlas.Indexing/NativeRecovery/NativeRecoveryWorkflow.cs
- Create: tests/S1Atlas.Indexing.Tests/NativeRecovery/NativeRecoveryWorkflowTests.cs
- Create: docs/design/2026-08-29-native-recovery-provenance.md

**Interfaces:**

~~~csharp
public enum NativeRecoveryStatus
{
    Recovered,
    NoBody,
    AmbiguousMapping,
    InputChanged,
    Failed,
    Unsupported
}

public sealed record NativeRecoveryRequest(
    string BuildId,
    string IndexId,
    string GameAssemblySha256,
    IReadOnlyList<string> SymbolIds,
    int MaxTraversalEdges);

public sealed record NativeEvidenceEdge(
    string EdgeId,
    string SourceMethodPointer,
    string? TargetMethodPointer,
    string? TargetText,
    string Kind,
    string Evidence,
    bool IsComplete);

public sealed record NativeRecoveryRecord(
    string RecoveryId,
    NativeRecoveryRequest Request,
    string ToolName,
    string ToolVersion,
    string ToolSha256,
    NativeRecoveryStatus Status,
    IReadOnlyList<string> MappingEvidence,
    IReadOnlyList<NativeEvidenceEdge> Edges,
    IReadOnlyList<string> FieldAccesses,
    bool IsComplete,
    string OutputSha256,
    DateTimeOffset CreatedAtUtc,
    string? FailureMessage);

public interface INativeBodyRecoveryProvider
{
    Task<NativeRecoveryRecord> RecoverAsync(
        NativeRecoveryRequest request,
        CancellationToken cancellationToken);
}
~~~

- [ ] **Step 1: Inventory the local provider boundary**

Record in docs/design/2026-08-29-native-recovery-provenance.md the selected local recovery executable/tool identity, version, SHA-256, accepted inputs, output hashes, licensing/distribution boundary, and exact reason a method is marked Unsupported when no provider is configured. Do not copy a game binary, raw body, or disassembly into the repository.

- [ ] **Step 2: Write failing workflow tests**

Use a fake provider with repository-owned data to cover recovered mapping, no body, ambiguous managed-to-native mapping, changed build hash, bounded edge count, provider failure, and reproducibility for identical inputs.

- [ ] **Step 3: Run focused tests and verify they fail**

~~~powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter FullyQualifiedName~NativeRecoveryWorkflowTests --no-restore
~~~

Expected: FAIL because the contracts/workflow do not exist.

- [ ] **Step 4: Implement request validation and provenance capture**

Reject blank IDs, non-64-character SHA-256 values, empty symbol selections, traversal budgets outside 1–500, and build/index mismatches. Capture tool name/version/hash and output hash in every result, including failure. Use InputChanged when current GameAssembly.dll or index identity differs from the request.

- [ ] **Step 5: Implement explicit failure behavior**

Return NoBody, AmbiguousMapping, Failed, or Unsupported directly. Never substitute interop wrapper source or claim native calls/field accesses when the provider did not return them.

- [ ] **Step 6: Run focused tests and verify they pass**

Run the same test command. Expected: PASS.

- [ ] **Step 7: Commit the feasibility contract**

~~~powershell
git add src/S1Atlas.Indexing/Query/NativeEvidenceQueryModels.cs src/S1Atlas.Indexing/NativeRecovery/INativeBodyRecoveryProvider.cs src/S1Atlas.Indexing/NativeRecovery/NativeRecoveryWorkflow.cs tests/S1Atlas.Indexing.Tests/NativeRecovery/NativeRecoveryWorkflowTests.cs docs/design/2026-08-29-native-recovery-provenance.md
git commit -m "feat: define bounded native evidence contract"
~~~

### Task 4: Persist and query native evidence

**Files:**
- Modify: src/S1Atlas.Core/Storage/IIndexRepository.cs
- Modify: src/S1Atlas.Storage/Migrations/SqliteMigrations.cs
- Modify: src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs
- Modify: src/S1Atlas.Storage/Sqlite/ReadOnlySqliteAtlasRepository.cs
- Create: tests/S1Atlas.Storage.Tests/Sqlite/NativeEvidenceRepositoryTests.cs
- Create: tests/S1Atlas.Storage.Tests/Migrations/NativeEvidenceMigrationTests.cs

**Interfaces:**

~~~csharp
Task SaveNativeRecoveryAsync(NativeRecoveryRecord record, CancellationToken cancellationToken);
Task<NativeRecoveryRecord?> GetNativeRecoveryAsync(string recoveryId, CancellationToken cancellationToken);
Task<IReadOnlyList<NativeRecoveryRecord>> GetNativeRecoveriesAsync(
    string indexId,
    IReadOnlyList<string> symbolIds,
    CancellationToken cancellationToken);
~~~

- [ ] **Step 1: Write failing schema/repository tests**

Assert records are keyed by recovery ID and input tuple, edges preserve native evidence kind, read-only repository queries do not create/migrate state, and a changed build/index cannot resolve a previous record as current.

- [ ] **Step 2: Run focused storage tests and verify they fail**

~~~powershell
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj --filter "FullyQualifiedName~NativeEvidenceRepositoryTests|FullyQualifiedName~NativeEvidenceMigrationTests" --no-restore
~~~

Expected: FAIL because tables and repository methods do not exist.

- [ ] **Step 3: Add the migration**

Create native_recovery_runs with recovery ID, build ID, index ID, GameAssembly hash, tool identity, status, completeness, output hash, timestamps, and failure message. Create native_recovery_edges and native_recovery_fields keyed to the recovery run. Add indexes for (index_id, build_id, game_assembly_sha256) and (recovery_id, edge_id). Store no raw body/disassembly column.

- [ ] **Step 4: Implement read/write repository methods**

Write completed or explicit failure records from the workflow. Read-only methods return records only when stored build/index/hash tuple matches the requested tuple. Map native evidence separately from managed RecoveredIL edges.

- [ ] **Step 5: Run focused storage tests and verify they pass**

Run the same storage test command. Expected: PASS.

- [ ] **Step 6: Commit native persistence**

~~~powershell
git add src/S1Atlas.Core/Storage/IIndexRepository.cs src/S1Atlas.Storage/Migrations/SqliteMigrations.cs src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs src/S1Atlas.Storage/Sqlite/ReadOnlySqliteAtlasRepository.cs tests/S1Atlas.Storage.Tests/Sqlite/NativeEvidenceRepositoryTests.cs tests/S1Atlas.Storage.Tests/Migrations/NativeEvidenceMigrationTests.cs
git commit -m "feat: persist native evidence provenance"
~~~

### Task 5: Enrich normal queries and investigate_seam

**Files:**
- Modify: src/S1Atlas.Indexing/Query/IndexQueryService.cs
- Modify: src/S1Atlas.Indexing/Query/SeamInvestigationService.cs
- Modify: src/S1Atlas.Indexing/Query/SeamInvestigationQueryModels.cs
- Modify: src/S1Atlas.Mcp/Tools/SeamTools.cs
- Modify: tests/S1Atlas.Indexing.Tests/Query/SeamInvestigationServiceTests.cs
- Modify: tests/S1Atlas.Mcp.Tests/SeamToolTests.cs

**Interfaces:**

Add an optional native evidence section:

~~~csharp
public sealed record NativeEvidenceSummary(
    NativeRecoveryStatus Status,
    bool IsComplete,
    IReadOnlyList<string> MappingEvidence,
    IReadOnlyList<NativeEvidenceEdge> DirectEdges,
    IReadOnlyList<string> FieldAccesses,
    string ToolProvenance,
    string OutputSha256);
~~~

Extend SeamInvestigationRequest with IReadOnlyList<string>? NativeSymbolIds and int NativeTraversalBudget = 0; zero means no recovery request, not an implicit attempt.

- [ ] **Step 1: Write failing enrichment tests**

Assert a recovered native edge is distinguishable from a managed relationship, provider failure remains visible, a no-body result creates a targeted native-recovery next action, and no recovery occurs when native IDs are omitted.

- [ ] **Step 2: Run focused indexing/MCP tests and verify they fail**

~~~powershell
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter FullyQualifiedName~SeamInvestigationServiceTests --no-restore
dotnet test tests/S1Atlas.Mcp.Tests/S1Atlas.Mcp.Tests.csproj --filter FullyQualifiedName~SeamToolTests --no-restore
~~~

Expected: FAIL because the packet has no native evidence section.

- [ ] **Step 3: Add opt-in native enrichment**

Resolve and validate selected build/index/hash, query the repository for matching native records, and attach only matching records. If no matching record exists, preserve UNKNOWN and the bounded next action. Never mark a wrapper as native logic solely because it is callable.

- [ ] **Step 4: Run focused tests and verify they pass**

Run both commands from Step 2. Expected: PASS.

- [ ] **Step 5: Commit seam/native integration**

~~~powershell
git add src/S1Atlas.Indexing/Query/IndexQueryService.cs src/S1Atlas.Indexing/Query/SeamInvestigationService.cs src/S1Atlas.Indexing/Query/SeamInvestigationQueryModels.cs src/S1Atlas.Mcp/Tools/SeamTools.cs tests/S1Atlas.Indexing.Tests/Query/SeamInvestigationServiceTests.cs tests/S1Atlas.Mcp.Tests/SeamToolTests.cs
git commit -m "feat: enrich seam evidence with native provenance"
~~~

### Task 6: Verify API/native boundaries and documentation

**Files:**
- Modify: docs/USAGE.md
- Modify: docs/REFERENCE.md
- Modify: tests/S1Atlas.IntegrationTests/CliQueryParityTests.cs
- Modify: tests/S1Atlas.Mcp.Tests/McpTrustBoundaryTests.cs

- [ ] **Step 1: Document API and native evidence**

Document API codebase/channel selection, source commit or installed binary identity, stale semantics, native recovery bounds, hashes, failure statuses, and the prohibition on storing proprietary artifacts.

- [ ] **Step 2: Add CLI/API parity tests**

For an equivalent S1API query, compare existing CLI JSON with shared API service results for codebase, channel, index ID, symbol identity, source/body status, ambiguity, and completeness fields.

- [ ] **Step 3: Extend MCP trust tests**

Exercise all API tools and a matching native evidence lookup, snapshot Atlas files before/after, and assert no file changes, network client, extraction process, game launch, or mutation tool registration.

- [ ] **Step 4: Run complete verification**

~~~powershell
dotnet build S1Atlas.sln --configuration Release
dotnet test S1Atlas.sln --configuration Release --no-build
powershell -ExecutionPolicy Bypass -File scripts/verify-repository-hygiene.ps1
~~~

Expected: PASS with no proprietary/native artifacts in Git.

- [ ] **Step 5: Commit documentation and boundary verification**

~~~powershell
git add docs/USAGE.md docs/REFERENCE.md tests/S1Atlas.IntegrationTests/CliQueryParityTests.cs tests/S1Atlas.Mcp.Tests/McpTrustBoundaryTests.cs
git commit -m "docs: document api and native evidence boundaries"
~~~



