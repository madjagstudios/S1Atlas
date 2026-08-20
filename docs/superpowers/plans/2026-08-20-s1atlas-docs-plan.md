# S1Atlas.Docs Static Human Portal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Build S1Atlas.Docs and the s1atlas docs generate [--build <id>] [--output <dir>] CLI command as a deterministic, offline static HTML portal over Schedule I Installed, S1API, and S1MAPI indexed data.

**Architecture:** Extend the shared read-only query layer with bounded bulk enumeration, portal-shaped build history, and cross-build symbol lookup. Build an immutable portal model through the existing ReadOnlyAtlasComposition, then render deterministic HTML/CSS/assets through pluggable sections; renderers never access SQLite. Add the CLI command as a thin adapter that validates output location, invokes generation, and maps read-only/missing-schema failures to explicit non-migrating errors.

**Tech Stack:** .NET 8, C#, System.CommandLine 2.0.10, existing SQLite repositories, existing IndexQueryService/BuildDiffService, existing InstalledBuildAuthorityResolver, Roslyn Microsoft.CodeAnalysis.CSharp, plain HTML/CSS, minimal vanilla JavaScript, xUnit v3 fixtures.

**Spec:** docs/superpowers/specs/2026-08-20-s1atlas-docs-design.md

## Global Constraints

- Schedule I Installed content uses only InstalledBuildAuthorityResolver with Resolved status; no Phase 3 candidate, retained failure output, failed index, unchecked source, or unverified row is rendered.
- S1API/S1MAPI use the latest completed index per (codebase, channel) independently of --build, with commit SHA and index ID provenance.
- --build <id> pins only Schedule I Installed; APIs remain latest-completed and build-independent.
- V1 generates no scene/GameObject/prefab/component HTML; reserve code/schedule-i/installed/scenes/ and render the CLI/MCP deferral note on the Schedule I build page.
- Standalone pages exist only for types, methods, and constructors; fields, properties, and events render inline on the containing type page with deterministic member anchors.
- All generated links are relative; all text is UTF-8 with LF line endings; no wall-clock generation timestamp, absolute path, random ID, or machine-specific value is emitted.
- Symbol and symbol-history paths use a readable sanitized slug plus the first twelve lowercase SHA-256 hex characters of the exact key, with a two-hex-character shard directory.
- Derived counts use true totals and visible coverage; measured zero, unavailable, not indexed, unresolved, truncated, and integrity-failed states remain distinct.
- Roslyn syntax analysis detects learning concepts from the exact displayed source span; no substring/regex detection and no claims of absence.
- FACT and DERIVED labels are visible on every claim; INTERPRETATION is reserved and emits no V1 content.
- The default output is ./s1atlas-docs/, outside the Atlas data root; --output must also remain outside the Atlas data root.
- Missing or wrong-schema Atlas databases fail read-only generation with an explicit scan/migration-first error; generation never creates or migrates the database.
- Tests use generated/repository-owned fixtures only; no proprietary game bytes and no network.
- Before completion run:
  dotnet format S1Atlas.sln --verify-no-changes --no-restore
  dotnet build S1Atlas.sln --configuration Release
  dotnet test S1Atlas.sln --configuration Release --no-build

---

## File map

### Shared read/query layer

- Modify: src/S1Atlas.Core/Storage/IIndexRepository.cs — add the repository contract for stable bounded symbol pages.
- Modify: src/S1Atlas.Core/Indexing/QueryModels.cs — add page, namespace, and latest-index query result records.
- Modify: src/S1Atlas.Indexing/Query/IndexQueryService.cs — expose bulk namespace/symbol enumeration and latest-completed index selection through query methods.
- Modify: src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs — implement bounded completed-symbol reads for the writable repository used by fixtures and existing tests.
- Modify: src/S1Atlas.Storage/Sqlite/ReadOnlySqliteAtlasRepository.cs — implement the same reads with completed-run predicates and read-only connections.
- Create: src/S1Atlas.Application/Authority/InstalledBuildHistoryQueryService.cs — turn all known builds plus authority resolution into visible status/navigability records and resolve cross-build symbol occurrences.
- Modify: src/S1Atlas.Application/Composition/ReadOnlyAtlasComposition.cs — construct the history service once and expose it through AtlasReadOnlyServices.
- Create: tests/S1Atlas.Storage.Tests/Indexing/IndexBulkQueryTests.cs — verify completed-only paging, stable ordering, and true totals.
- Create: tests/S1Atlas.Storage.Tests/Fixtures/IndexQueryFixture.cs — seed deterministic completed/running/failed index rows for storage paging tests.
- Create: tests/S1Atlas.Indexing.Tests/Query/IndexQueryBulkTests.cs — verify namespace extraction, page coverage, latest API selection, and no API authority gating.
- Create: tests/S1Atlas.Indexing.Tests/Fixtures/IndexQueryFixture.cs — seed deterministic completed/running/failed index rows for query-service tests.
- Create: tests/S1Atlas.IntegrationTests/Authority/InstalledBuildHistoryQueryTests.cs — verify all-build visibility, authority statuses, navigability, adjacent verified subsequence, and canonical-key occurrences.

### New docs project and portal model

- Create: src/S1Atlas.Docs/S1Atlas.Docs.csproj — net8.0 library referencing Application, Core, Indexing, Storage, and Roslyn through the existing Indexing dependency graph.
- Create: src/S1Atlas.Docs/Generation/DocsGenerationRequest.cs — request record for requested game build and output directory.
- Create: src/S1Atlas.Docs/Generation/PortalModel.cs — immutable site, index, build, symbol, environment, history, diff, and status records.
- Create: src/S1Atlas.Docs/Generation/PortalModelBuilder.cs — build the complete model from AtlasReadOnlyServices without renderer/database access.
- Create: tests/S1Atlas.Docs.Tests/S1Atlas.Docs.Tests.csproj — test project referencing S1Atlas.Docs, Application, Core, Indexing, Storage, and test fixture dependencies.
- Create: tests/S1Atlas.Docs.Tests/Fixtures/DocsAtlasFixture.cs — generated SQLite/Atlas-owned fixture with current, historical, API, source, relationship, and integrity-failure cases.
- Create: tests/S1Atlas.Docs.Tests/Generation/PortalModelBuilderTests.cs — verify authority selection, mixed-scope --build, current-only environment, API missing states, and historical build statuses.

### Deterministic identity, source context, and rendering

- Create: src/S1Atlas.Docs/Identity/PortalSlugService.cs — sanitize exact keys, append twelve-hex hash, and derive two-hex shard prefix.
- Create: src/S1Atlas.Docs/Identity/PortalLinkResolver.cs — create relative hrefs and member anchors from page paths.
- Create: src/S1Atlas.Docs/Determinism/DeterministicText.cs — stable ordering, invariant formatting, pluralization, numeral style, and LF normalization.
- Create: src/S1Atlas.Docs/Determinism/DeterministicJsonWriter.cs — fixed-property-order JSON for the canonical and inline search indexes.
- Create: src/S1Atlas.Docs/Source/RoslynLearningConceptDetector.cs — parse exact displayed spans and emit only detected syntax concepts.
- Create: src/S1Atlas.Docs/Source/PortalSourceReader.cs — adapt IndexQueryService.SourceInIndexAsync outcomes and integrity exceptions into explicit source availability states.
- Create: src/S1Atlas.Docs/Content/DerivedContextBuilder.cs — generate deterministic FACT-linked DERIVED overview, relevance, counts, and learning statements.
- Create: src/S1Atlas.Docs/Rendering/StaticSiteGenerator.cs — orchestrate model-to-file output.
- Create: src/S1Atlas.Docs/Rendering/HtmlPageRenderer.cs — render page shells, escaped content, relative navigation, and section composition.
- Create: src/S1Atlas.Docs/Rendering/PortalSectionRenderers.cs — render provenance, code navigation, relationships, source, context, history, diffs, environment, and scene deferral note.
- Create: src/S1Atlas.Docs/Rendering/StaticAssets.cs — deterministic CSS, search JavaScript, inline frozen search-index JavaScript, and canonical search-index JSON.
- Create: tests/S1Atlas.Docs.Tests/Identity/PortalSlugServiceTests.cs — verify Windows-safe collision-proof slugs and shards.
- Create: tests/S1Atlas.Docs.Tests/Determinism/DeterminismTests.cs — verify stable JSON, LF, relative links, and byte-identical repeated generations.
- Create: tests/S1Atlas.Docs.Tests/Source/RoslynLearningConceptDetectorTests.cs — verify syntax-node-only detections.
- Create: tests/S1Atlas.Docs.Tests/Content/DerivedContextBuilderTests.cs — verify FACT/DERIVED labels, true totals, coverage, explicit zero/unavailable states, and deterministic prose.
- Create: tests/S1Atlas.Docs.Tests/Rendering/StaticSiteGeneratorTests.cs — verify generated page paths, HTML structure, provenance, scope, scene deferral, and inline search asset.

### CLI, solution, and documentation

- Modify: S1Atlas.sln — add S1Atlas.Docs and S1Atlas.Docs.Tests projects.
- Modify: src/S1Atlas.Cli/S1Atlas.Cli.csproj — reference S1Atlas.Docs.
- Create: src/S1Atlas.Cli/Commands/DocsCommand.cs — register docs.
- Create: src/S1Atlas.Cli/Commands/DocsGenerateCommand.cs — parse --build/--output, build shared read-only services, enforce output containment, invoke generator, and map failures.
- Modify: src/S1Atlas.Cli/CliApplication.cs — add the docs command while reusing existing Atlas paths and read-only composition.
- Create: tests/S1Atlas.IntegrationTests/DocsGenerateCommandTests.cs — exercise the real CLI command over repository-owned fixtures.
- Modify: .gitignore — add unanchored s1atlas-docs/.
- Modify: README.md — document command, output behavior, mixed authority model, scene deferral, and mark the static portal milestone shipped when implementation completes.
- Modify: docs/worknotes/AT-1.md — append implementation discoveries and final verification facts before close-out.

---

### Task 1: Add bounded shared read/query surfaces

**Files:**
- Modify: src/S1Atlas.Core/Storage/IIndexRepository.cs
- Modify: src/S1Atlas.Core/Indexing/QueryModels.cs
- Modify: src/S1Atlas.Indexing/Query/IndexQueryService.cs
- Modify: src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs
- Modify: src/S1Atlas.Storage/Sqlite/ReadOnlySqliteAtlasRepository.cs
- Test: tests/S1Atlas.Storage.Tests/Indexing/IndexBulkQueryTests.cs
- Test: tests/S1Atlas.Indexing.Tests/Query/IndexQueryBulkTests.cs

**Interfaces:**
- Consumes: existing completed-index repository methods, IndexRunRecord, CodebaseKind, CodeChannel, and SymbolResolver.
- Produces:
  - IndexPageRequest(int Offset, int Limit).
  - IndexedSymbolPageResult(int TotalCount, IReadOnlyList<IndexedSymbolQueryResult> Results, bool HasMore).
  - IndexedSymbolQueryResult(string IndexId, string Codebase, string Channel, string SymbolId, string CanonicalKey, string Kind, string QualifiedName, string Signature, bool IsBestEffort, BodyRecoveryStatus? BodyRecoveryStatus).
  - NamespaceQueryResult(int TotalCount, IReadOnlyList<string> Namespaces).
  - IndexSelectionQueryResult(IndexRunRecord Run, CodeSnapshotRecord Snapshot).
  - RelationshipEvidenceQueryResult(IReadOnlyList<RelationshipQueryResult> References, int ReferenceTotal, IReadOnlyList<RelationshipQueryResult> Callers, int CallerTotal, IReadOnlyList<RelationshipQueryResult> Callees, int CalleeTotal, string CallerCompletenessNotice, string CalleeCompletenessNotice).
  - Task<IndexedSymbolPageResult> ListSymbolsInIndexAsync(IndexRunRecord run, CodebaseKind codebase, CodeChannel channel, IndexPageRequest page, CancellationToken cancellationToken).
  - Task<NamespaceQueryResult> ListNamespacesInIndexAsync(IndexRunRecord run, CodebaseKind codebase, CodeChannel channel, CancellationToken cancellationToken).
  - Task<IndexSelectionQueryResult?> GetLatestCompletedIndexSelectionAsync(CodebaseKind codebase, CodeChannel channel, CancellationToken cancellationToken).
  - Task<RelationshipEvidenceQueryResult> GetRelationshipEvidenceInIndexAsync(IndexRunRecord run, CodebaseKind codebase, CodeChannel channel, string symbolId, CancellationToken cancellationToken).

- [ ] Step 1: Write the failing repository/query tests

Seed a completed index with symbols whose canonical keys include uppercase/lowercase names, fields, methods, and two namespaces; also seed a Running and Failed run containing symbols. Assert the future page API returns only the completed run and that the first page reports the true total:

~~~csharp
[Fact]
public async Task ListSymbolsInIndexAsync_ReturnsCompletedSymbolsWithStableCoverage()
{
    await using var fixture = await IndexQueryFixture.CreateAsync();
    var run = fixture.CompletedRun;

    var page = await fixture.Service.ListSymbolsInIndexAsync(
        run,
        CodebaseKind.S1Api,
        CodeChannel.Release,
        new IndexPageRequest(Offset: 0, Limit: 2),
        CancellationToken.None);

    Assert.Equal(4, page.TotalCount);
    Assert.Equal(2, page.Results.Count);
    Assert.True(page.HasMore);
    Assert.Equal(
        page.Results.OrderBy(symbol => symbol.CanonicalKey, StringComparer.Ordinal).Select(symbol => symbol.CanonicalKey),
        page.Results.Select(symbol => symbol.CanonicalKey));
}
~~~

Add assertions that ListNamespacesInIndexAsync returns ["Alpha", "Beta"] in ordinal order, that GetLatestCompletedIndexSelectionAsync chooses the newest completed API run by completed timestamp then index ID, and that relationship evidence reports true totals separately from the bounded returned rows.

- [ ] Step 2: Run the focused tests and verify failure

Run:

~~~text
dotnet test tests/S1Atlas.Storage.Tests/S1Atlas.Storage.Tests.csproj --filter FullyQualifiedName~IndexBulkQueryTests
dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter FullyQualifiedName~IndexQueryBulkTests
~~~

Expected: FAIL because the new page/result methods and repository contract do not exist.

- [ ] Step 3: Add the repository contract and deterministic result records

Add IndexPageRequest validation for nonnegative offsets and positive limits. Add the result records to QueryModels.cs. Add GetCompletedSymbolPageAsync(string indexId, int offset, int limit, CancellationToken) and CountCompletedSymbolsAsync(string indexId, CancellationToken) to IIndexRepository.

- [ ] Step 4: Implement completed-only SQL paging in both repositories

In both SQLite repository implementations, query only rows joined to index_runs where run.status = 'Completed', order by symbol.canonical_key COLLATE BINARY, symbol.kind COLLATE BINARY, and symbol.symbol_id COLLATE BINARY, and apply $limit/$offset. Keep the read-only repository’s InitializeAsync behavior unchanged: it must continue throwing instead of creating/migrating a database.

- [ ] Step 5: Implement IndexQueryService bulk methods

Map page rows to IndexedSymbolQueryResult. Implement namespace extraction from canonical keys in the query service by walking all stable pages, collecting the namespace portion, sorting ordinally, and returning the true distinct count. Implement latest selection by calling GetLatestCompletedIndexAsync, then GetCodeSnapshotAsync, returning null when either is absent or the snapshot codebase/channel does not match. Implement relationship evidence by loading outgoing/incoming completed edges, counting each relationship set before applying the fixed page limit, resolving endpoints through the existing query mapper, and preserving caller completeness notices.

- [ ] Step 6: Run focused tests and commit

Run the two focused test commands again; expected PASS. Commit:

~~~text
git add src/S1Atlas.Core/Storage/IIndexRepository.cs src/S1Atlas.Core/Indexing/QueryModels.cs src/S1Atlas.Indexing/Query/IndexQueryService.cs src/S1Atlas.Storage/Sqlite/SqliteAtlasRepository.Indexing.cs src/S1Atlas.Storage/Sqlite/ReadOnlySqliteAtlasRepository.cs tests/S1Atlas.Storage.Tests/Indexing/IndexBulkQueryTests.cs tests/S1Atlas.Indexing.Tests/Query/IndexQueryBulkTests.cs
git commit -m "feat: add bounded index query surfaces"
~~~

### Task 2: Add portal-shaped Schedule I history and composition wiring

**Files:**
- Create: src/S1Atlas.Application/Authority/InstalledBuildHistoryQueryService.cs
- Modify: src/S1Atlas.Application/Composition/ReadOnlyAtlasComposition.cs
- Test: tests/S1Atlas.IntegrationTests/Authority/InstalledBuildHistoryQueryTests.cs

**Interfaces:**
- Consumes: InstalledBuildAuthorityResolver, IAtlasRepository.ListBuildsAsync, IndexQueryService, and the completed index APIs.
- Produces:
  - InstalledBuildHistoryStatus values IndexedVerified, NotIndexed, and IntegrityFailed.
  - InstalledBuildHistoryEntry(GameBuild Build, InstalledBuildHistoryStatus Status, InstalledBuildAuthority? Authority, string? Message).
  - InstalledBuildHistoryResult(IReadOnlyList<InstalledBuildHistoryEntry> Entries, IReadOnlyList<InstalledBuildHistoryEntry> NavigableEntries, IReadOnlyList<AdjacentBuildPair> AdjacentPairs).
  - AdjacentBuildPair(InstalledBuildHistoryEntry Before, InstalledBuildHistoryEntry After).
  - SymbolHistoryOccurrence(string BuildId, string IndexId, bool Present, string? SymbolId, string? QualifiedName, string? Signature).
  - Task<InstalledBuildHistoryResult> GetHistoryAsync(CancellationToken cancellationToken).
  - Task<IReadOnlyList<SymbolHistoryOccurrence>> GetSymbolOccurrencesAsync(string canonicalKey, IReadOnlyList<InstalledBuildHistoryEntry> entries, CancellationToken cancellationToken).

- [ ] Step 1: Write failing history tests

Seed four known builds in first-seen order: verified, not indexed, integrity-failed, verified. Assert all four appear in Entries, only the two verified entries are navigable, and exactly one adjacent pair is created between the two verified entries. Assert a canonical key occurrence reports Present = true for one verified index and Present = false for the other.

- [ ] Step 2: Run the focused test and verify failure

Run:

~~~text
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter FullyQualifiedName~InstalledBuildHistoryQueryTests
~~~

Expected: FAIL because the history service and composition property do not exist.

- [ ] Step 3: Implement status mapping and adjacent-pair selection

Call ListBuildsAsync, resolve each build through InstalledBuildAuthorityResolver.ResolveAsync(build.BuildId, ct), map Resolved to IndexedVerified, map NoCompletedIndex/NoPreferredVerifiedExtraction/BuildNotFound to NotIndexed, and map ExtractionIntegrityFailure/IndexBuildMismatch to IntegrityFailed. Preserve all entries. Sort the navigable subsequence by Build.FirstSeenAtUtc, then Build.BuildId, and create only consecutive older-to-newer AdjacentBuildPair values.

- [ ] Step 4: Implement cross-build canonical-key occurrences

For each verified history entry, call GetCompletedSymbolByCanonicalKeyAsync through the query layer for that entry’s Authority.IndexId. Return a deterministic occurrence row even when no symbol is present. Never query unverified entries and never include API indexes.

- [ ] Step 5: Wire one service into AtlasReadOnlyServices

Construct InstalledBuildHistoryQueryService in ReadOnlyAtlasComposition.BuildReadOnlyServices using the already-created repository, authority resolver, index query service, and diff service. Add it as a property on AtlasReadOnlyServices; do not add a second composition path.

- [ ] Step 6: Run tests and commit

Run the focused integration test; expected PASS. Commit:

~~~text
git add src/S1Atlas.Application/Authority/InstalledBuildHistoryQueryService.cs src/S1Atlas.Application/Composition/ReadOnlyAtlasComposition.cs tests/S1Atlas.IntegrationTests/Authority/InstalledBuildHistoryQueryTests.cs
git commit -m "feat: add portal build history authority model"
~~~

### Task 3: Scaffold S1Atlas.Docs and define the immutable portal model

**Files:**
- Create: src/S1Atlas.Docs/S1Atlas.Docs.csproj
- Create: src/S1Atlas.Docs/Generation/DocsGenerationRequest.cs
- Create: src/S1Atlas.Docs/Generation/PortalModel.cs
- Create: src/S1Atlas.Docs/Generation/PortalModelBuilder.cs
- Create: tests/S1Atlas.Docs.Tests/S1Atlas.Docs.Tests.csproj
- Create: tests/S1Atlas.Docs.Tests/Fixtures/DocsAtlasFixture.cs
- Create: tests/S1Atlas.Docs.Tests/Generation/PortalModelBuilderTests.cs
- Modify: S1Atlas.sln

**Interfaces:**
- Consumes: AtlasReadOnlyServices, DocsGenerationRequest, bulk query results, history results, current environment snapshot, and BuildDiffService.
- Produces:
  - DocsGenerationRequest(string? RequestedBuildId, string OutputDirectory).
  - PortalSiteModel(string ResolvedBuildId, IReadOnlyList<PortalIndexModel> Indexes, PortalBuildHistoryModel BuildHistory, PortalEnvironmentModel? CurrentEnvironment, IReadOnlyList<PortalDiffModel> Diffs, IReadOnlyList<PortalStatus> Statuses).
  - PortalIndexModel(IndexRunRecord Run, CodebaseKind Codebase, CodeChannel Channel, string IndexId, string SourceIdentity, string? BuildId, string? ExtractionId, bool IsVerifiedAuthority, IReadOnlyList<PortalNamespaceModel> Namespaces, int SymbolTotal).
  - PortalNamespaceModel(string Name, IReadOnlyList<PortalSymbolModel> Symbols, int TotalCount).
  - PortalSymbolModel(string IndexId, CodebaseKind Codebase, CodeChannel Channel, string SymbolId, string CanonicalKey, SymbolKind Kind, string QualifiedName, string Signature, bool IsBestEffort, BodyRecoveryStatus? BodyRecoveryStatus, string PagePath, string Anchor, PortalSymbolEvidenceModel? Evidence).
  - PortalSymbolEvidenceModel(PortalRelationshipEvidenceModel Relationships, PortalSourceResult Source, DerivedContext Context).
  - PortalRelationshipEvidenceModel(IReadOnlyList<RelationshipQueryResult> References, int ReferenceTotal, IReadOnlyList<RelationshipQueryResult> Callers, int CallerTotal, IReadOnlyList<RelationshipQueryResult> Callees, int CalleeTotal, string CallerCompletenessNotice, string CalleeCompletenessNotice).
  - PortalBuildHistoryModel(IReadOnlyList<PortalBuildEntry> Entries, IReadOnlyList<PortalDiffModel> AdjacentDiffs).
  - PortalBuildEntry(GameBuild Build, InstalledBuildHistoryStatus Status, bool IsNavigable, string? CodePath).
  - PortalEnvironmentModel(EnvironmentSnapshot Snapshot, string PagePath).
  - PortalDiffModel(string BeforeBuildId, string AfterBuildId, BuildDiffResult Result, string PagePath).
  - PortalStatus(string Code, string Label, bool IsError, string? Detail).
  - Task<PortalSiteModel> BuildAsync(AtlasReadOnlyServices services, DocsGenerationRequest request, CancellationToken cancellationToken).

- [ ] Step 1: Write failing model tests

Use DocsAtlasFixture to seed:
- one current preferred verified Schedule I build;
- one older verified Schedule I build;
- one API release index and one API preview index;
- one missing API channel;
- a source file whose recorded hash passes;
- a source file whose recorded hash fails;
- a field, property, event, method, constructor, and type.

Assert the model selects the requested/current Schedule I authority, includes every available latest-completed S1API/S1MAPI channel independently of the requested build, creates one current environment model only, includes historical status entries, and creates one adjacent diff model.

- [ ] Step 2: Run the new test and verify failure

Run:

~~~text
dotnet test tests/S1Atlas.Docs.Tests/S1Atlas.Docs.Tests.csproj --filter FullyQualifiedName~PortalModelBuilderTests
~~~

Expected: FAIL because the project, model types, and builder do not exist.

- [ ] Step 3: Add project files and solution entries

Use net8.0. Reference S1Atlas.Application, S1Atlas.Core, S1Atlas.Indexing, and S1Atlas.Storage; do not add a new package. Reference S1Atlas.Docs and the same fixture dependencies from the docs test project. Add both projects to S1Atlas.sln with Debug/Release configurations.

- [ ] Step 4: Implement the fixture with repository-owned data

Create the SQLite schema through the existing repository initialization only in the test fixture. Write generated source files beneath the fixture’s Atlas-owned index directory and record their SHA-256 values in completed index rows. Use fixed identifiers and timestamps, not Guid.NewGuid() in data that is asserted for determinism. Keep fixture cleanup in IAsyncDisposable.

- [ ] Step 5: Implement PortalModelBuilder

Resolve Schedule I through services.AuthorityResolver.ResolveAsync(request.RequestedBuildId, ct) and fail when the requested/current result is not Resolved. Resolve API indexes through services.IndexQueryService.GetLatestCompletedIndexSelectionAsync for every CodebaseKind.S1Api/S1MApi and every CodeChannel value that has a completed selection. Read the environment only once through services.Repository.GetCurrentSnapshotAsync(ct) and create PortalEnvironmentModel only when its build matches the resolved Schedule I build; never create historical environment models.

- [ ] Step 6: Materialize symbols through bounded pages

For each selected index, page with IndexPageRequest(Offset, Limit) until HasMore is false. Create type, method, and constructor page records; create field/property/event records with a containing-type page path and deterministic member anchor. Include every symbol in the model/search input, while only the three standalone kinds receive files.

- [ ] Step 7: Run model tests and commit

Run the focused docs test; expected PASS. Commit:

~~~text
git add S1Atlas.sln src/S1Atlas.Docs tests/S1Atlas.Docs.Tests
git commit -m "feat: scaffold docs portal model"
~~~

### Task 4: Implement deterministic identity, links, text, and JSON

**Files:**
- Create: src/S1Atlas.Docs/Identity/PortalSlugService.cs
- Create: src/S1Atlas.Docs/Identity/PortalLinkResolver.cs
- Create: src/S1Atlas.Docs/Determinism/DeterministicText.cs
- Create: src/S1Atlas.Docs/Determinism/DeterministicJsonWriter.cs
- Create: tests/S1Atlas.Docs.Tests/Identity/PortalSlugServiceTests.cs
- Create: tests/S1Atlas.Docs.Tests/Determinism/DeterminismTests.cs

**Interfaces:**
- Consumes: exact canonical keys, page paths, portal symbol models, ordinal collections, and relative output paths.
- Produces:
  - PortalSlugResult(string ReadableSlug, string HashSuffix, string HashPrefix, string FileStem).
  - PortalSlugResult Create(string exactKey).
  - string MemberAnchor(string exactCanonicalKey).
  - string RelativeHref(string fromPage, string toPage, string? fragment = null).
  - string FormatCount(int count).
  - string FormatCoverage(int shown, int total).
  - string FormatPlural(int count, string singular, string plural).
  - string NormalizeLf(string text).
  - string WriteSearchIndexJson(IReadOnlyList<PortalSymbolModel> symbols).
  - string WriteInlineSearchIndexJavaScript(IReadOnlyList<PortalSymbolModel> symbols).

- [ ] Step 1: Write failing identity/determinism tests

Assert the following exact invariants:
- ScheduleOne.Employees.Employee.Fire(System.Int32) produces a lower-case safe filename with a 12-hex suffix.
- keys differing only by Employee/employee produce different HashSuffix values.
- <, >, :, /, \, |, ?, *, spaces, parentheses, commas, and backticks never appear in the readable slug.
- Windows device names receive a non-device fallback.
- the shard prefix is exactly the first two characters of the full lower-case SHA-256.
- RelativeHref("code/schedule-i/installed/symbols/aa/a.html", "index.html") contains no leading /.
- repeated JSON/JavaScript serialization returns byte-identical strings with LF and fixed property ordering.

- [ ] Step 2: Run focused tests and verify failure

Run:

~~~text
dotnet test tests/S1Atlas.Docs.Tests/S1Atlas.Docs.Tests.csproj --filter FullyQualifiedName~PortalSlugServiceTests
dotnet test tests/S1Atlas.Docs.Tests/S1Atlas.Docs.Tests.csproj --filter FullyQualifiedName~DeterminismTests
~~~

Expected: FAIL because identity and deterministic serialization types do not exist.

- [ ] Step 3: Implement PortalSlugService

Hash the exact UTF-8 key with SHA-256. Build the readable portion with invariant lowercasing, Unicode normalization, ASCII alphanumeric preservation, hyphen replacement/collapse, and a fixed 80-character cap. Prefix empty or Windows device-name results with x-. Use the first twelve hash characters for the suffix and the first two for the shard.

- [ ] Step 4: Implement relative links and member anchors

Compute Path.GetRelativePath using / separators after normalizing page paths as slash-separated site-relative paths. Reject any resulting root-absolute href. Use member-<hash-prefix>-<hash> anchors so inline members remain deterministic and collision-proof.

- [ ] Step 5: Implement deterministic text and serialization

Use StringComparer.Ordinal, CultureInfo.InvariantCulture, fixed numeral spell-out for counts 0–10, exact plural rules for singular/plural pairs, and explicit “showing N of M” coverage. Serialize the canonical search index sorted by codebase, channel, qualified name, signature, kind, symbol ID, and href. Emit assets/search-index.js as one Object.freeze([...]) constant and emit the same ordered entries as assets/search-index.json.

- [ ] Step 6: Run tests and commit

Run the focused identity/determinism tests; expected PASS. Commit:

~~~text
git add src/S1Atlas.Docs/Identity src/S1Atlas.Docs/Determinism tests/S1Atlas.Docs.Tests/Identity tests/S1Atlas.Docs.Tests/Determinism
git commit -m "feat: add deterministic docs identities and links"
~~~

### Task 5: Implement source integrity states and Roslyn-derived context

**Files:**
- Create: src/S1Atlas.Docs/Source/RoslynLearningConceptDetector.cs
- Create: src/S1Atlas.Docs/Source/PortalSourceReader.cs
- Create: src/S1Atlas.Docs/Content/DerivedContextBuilder.cs
- Create: tests/S1Atlas.Docs.Tests/Source/RoslynLearningConceptDetectorTests.cs
- Create: tests/S1Atlas.Docs.Tests/Content/DerivedContextBuilderTests.cs

**Interfaces:**
- Consumes: SourceSnippetResolutionResult, displayed source text, PortalSymbolModel, relationship query results, measured totals, and provenance links.
- Produces:
  - LearningConcept(string Label, string EvidenceText, string SourceAnchor).
  - IReadOnlyList<LearningConcept> Detect(string displayedSource).
  - PortalSourceState values Available, NoIndexedLocation, IntegrityFailure, and Unavailable.
  - PortalSourceResult(PortalSourceState State, SourceSnippetQueryResult? Snippet, string Label).
  - Task<PortalSourceResult> ReadAsync(PortalIndexModel index, PortalSymbolModel symbol, CancellationToken cancellationToken).
  - DerivedStatement(string Text, string EvidenceHref).
  - DerivedContext(IReadOnlyList<DerivedStatement> Overview, IReadOnlyList<DerivedStatement> ModderRelevance, IReadOnlyList<DerivedStatement> Learning).
  - DerivedContext Build(PortalSymbolModel symbol, PortalRelationshipEvidenceModel relationships, PortalSourceResult source, PortalLinkResolver links).

- [ ] Step 1: Write failing Roslyn tests

Use exact source snippets and assert only syntax nodes produce concepts:

~~~csharp
[Fact]
public void Detect_ReportsSyntaxPropertiesPresentInDisplayedSpan()
{
    const string source = """
        public void M<T>(T value)
        {
            var result = value?.ToString() ?? "none";
            var values = from item in items select item;
        }
        """;

    var concepts = new RoslynLearningConceptDetector().Detect(source);

    Assert.Contains(concepts, concept => concept.Label == "contains generic syntax");
    Assert.Contains(concepts, concept => concept.Label == "contains a null-conditional operator");
    Assert.Contains(concepts, concept => concept.Label == "contains a null-coalescing operator");
    Assert.Contains(concepts, concept => concept.Label == "contains a LINQ query expression");
}
~~~

Add a source with lowered method calls but no query expression and assert no LINQ concept is emitted; do not assert a negative “does not use LINQ” statement.

- [ ] Step 2: Run the focused tests and verify failure

Run:

~~~text
dotnet test tests/S1Atlas.Docs.Tests/S1Atlas.Docs.Tests.csproj --filter FullyQualifiedName~RoslynLearningConceptDetectorTests
dotnet test tests/S1Atlas.Docs.Tests/S1Atlas.Docs.Tests.csproj --filter FullyQualifiedName~DerivedContextBuilderTests
~~~

Expected: FAIL because the detector, source adapter, and derived context types do not exist.

- [ ] Step 3: Implement syntax-node detection through Roslyn

Parse the displayed span with CSharpSyntaxTree.ParseText. Inspect syntax nodes for generic type/method parameters, ConditionalAccessExpressionSyntax, CoalesceExpressionSyntax, QueryExpressionSyntax, ObjectCreationExpressionSyntax, InvocationExpressionSyntax, LambdaExpressionSyntax, EventDeclarationSyntax, property declarations, and static modifiers. Reuse/factor the existing RoslynSourceIndexer parsing conventions; do not scan source text with substring or regex.

- [ ] Step 4: Implement source availability mapping

Call IndexQueryService.SourceInIndexAsync using the IndexRunRecord retained on PortalIndexModel for standalone symbols and containing types. Map a non-null snippet to Available, a resolved symbol with no snippet to NoIndexedLocation, and InvalidDataException from verified source reads to IntegrityFailure with the exact label source unavailable (integrity). Map other missing-source outcomes to Unavailable. Keep the state visible in the model.

- [ ] Step 5: Enrich symbol models and implement deterministic derived statements

For each standalone symbol, call GetRelationshipEvidenceInIndexAsync, PortalSourceReader, and RoslynLearningConceptDetector, then set PortalSymbolModel.Evidence to PortalSymbolEvidenceModel. Generate only evidence-linked DERIVED statements. Use sorted relationship kinds and true totals for overview/relevance. Format bounded lists as “showing N of M,” measured zero as “0 callers in this index” or equivalent, and unavailable states as explicit status statements. Add learning statements only for detected Roslyn concepts and link each to the displayed source span FACT. Emit no INTERPRETATION content.

- [ ] Step 6: Run tests and commit

Run the focused source/context tests; expected PASS. Commit:

~~~text
git add src/S1Atlas.Docs/Source src/S1Atlas.Docs/Content tests/S1Atlas.Docs.Tests/Source tests/S1Atlas.Docs.Tests/Content
git commit -m "feat: add deterministic docs context and source states"
~~~

### Task 6: Render the complete deterministic static site

**Files:**
- Create: src/S1Atlas.Docs/Rendering/StaticSiteGenerator.cs
- Create: src/S1Atlas.Docs/Rendering/HtmlPageRenderer.cs
- Create: src/S1Atlas.Docs/Rendering/PortalSectionRenderers.cs
- Create: src/S1Atlas.Docs/Rendering/StaticAssets.cs
- Create: tests/S1Atlas.Docs.Tests/Rendering/StaticSiteGeneratorTests.cs

**Interfaces:**
- Consumes: PortalSiteModel, PortalSlugService, PortalLinkResolver, DeterministicText, source/context builders, and a caller-provided output directory.
- Produces: an output directory containing the exact page/asset layout from the spec, with no scene files and no renderer database access.

- [ ] Step 1: Write failing rendering tests

Generate a fixture site and assert:
- index.html, search.html, builds/index.html, selected build page, API code landing pages, namespace pages, type/method/constructor pages, history, adjacent diff, and current environment page exist;
- field/property/event pages do not exist;
- field/property/event search entries point to type-page member anchors;
- no file exists below code/schedule-i/installed/scenes/;
- the Schedule I build page contains the exact scene deferral note;
- every provenance claim contains FACT or DERIVED;
- API pages contain “latest completed index” plus commit/index IDs and do not use the verified-extraction chrome;
- historical builds show the current-only environment note and have no environment page;
- href values do not start with /;
- assets/search-index.js contains one frozen constant and assets/search-index.json has matching entry ordering.

- [ ] Step 2: Run the focused rendering test and verify failure

Run:

~~~text
dotnet test tests/S1Atlas.Docs.Tests/S1Atlas.Docs.Tests.csproj --filter FullyQualifiedName~StaticSiteGeneratorTests
~~~

Expected: FAIL because the static generator and renderers do not exist.

- [ ] Step 3: Implement deterministic page shells and section composition

Create a fixed HTML5 shell with title, navigation, stylesheet link, and optional search script link. Escape all text/attribute values. Render sections in this fixed order: provenance, FACT evidence, DERIVED overview, modder relevance, C# learning context, source, inheritance/type navigation, callers, callees, references, history, and related navigation.

- [ ] Step 4: Implement landing, code, namespace, symbol, history, diff, and environment pages

Use exact site-relative paths:
- index.html;
- search.html;
- builds/index.html;
- builds/<build-id>.html;
- history/schedule-i/symbols/<hash-prefix>/<canonical-slug>-<hash>.html;
- diffs/<older-build>--<newer-build>.html;
- environment/<resolved-build-id>.html;
- code/<codebase>/<channel>/index.html;
- code/<codebase>/<channel>/namespaces/<namespace-slug>-<hash>.html;
- code/<codebase>/<channel>/symbols/<hash-prefix>/<symbol-slug>-<hash>.html.

Render only standalone type/method/constructor files. Render fields/properties/events inline under the type page with anchor IDs. Build pages link to adjacent diffs and link to the environment page only when the build is the current resolved build.

- [ ] Step 5: Implement provenance chrome and explicit status states

Use a distinct Schedule I verified-authority block with build/extraction/index IDs. Use a distinct API latest-completed block with codebase/channel/source identity/commit/index IDs. Render all-build statuses, API not-indexed sections, zero relationship counts, source-integrity failure, missing source, unresolved targets, and no-diff state without dropping sections or failing the site.
When the resolved adjacent-diff list contains fewer than two verified builds, render the exact non-error text “no diffs available yet” and do not create a diff file.

- [ ] Step 6: Implement CSS, search assets, and LF output

Emit assets/site.css, assets/search.js, assets/search-index.js, and assets/search-index.json. search.js must read the frozen inline index constant and filter the prebuilt entries without fetch(), so file:// works. Write every generated file with UTF-8/LF and no timestamp.

- [ ] Step 7: Run rendering tests and commit

Run the focused rendering test; expected PASS. Commit:

~~~text
git add src/S1Atlas.Docs/Rendering tests/S1Atlas.Docs.Tests/Rendering
git commit -m "feat: render deterministic static docs portal"
~~~

### Task 7: Add the CLI command and end-to-end generation behavior

**Files:**
- Modify: src/S1Atlas.Cli/S1Atlas.Cli.csproj
- Create: src/S1Atlas.Cli/Commands/DocsCommand.cs
- Create: src/S1Atlas.Cli/Commands/DocsGenerateCommand.cs
- Modify: src/S1Atlas.Cli/CliApplication.cs
- Create: tests/S1Atlas.IntegrationTests/DocsGenerateCommandTests.cs

**Interfaces:**
- Consumes: existing AtlasPaths, ReadOnlyAtlasComposition, StaticSiteGenerator, DocsGenerationRequest, TextWriter/TextWriter error, and cancellation token.
- Produces: s1atlas docs generate [--build <id>] [--output <dir>], exit code 0 on successful generation, and exit code 1 with an explicit error for invalid authority/database/output conditions.

- [ ] Step 1: Write failing CLI integration tests

Use CliRunner.Run and a temporary fixture:
- docs generate creates ./s1atlas-docs/ under the test working directory when no output is supplied;
- --output <dir> writes there and does not write under the Atlas data root;
- --build <id> changes Schedule I content while API content remains the same latest-completed index;
- a missing database reports “scan or migration first” and creates no site;
- a wrong-schema database reports the same class of explicit non-migrating error and creates no site;
- a failed Schedule I authority returns nonzero and creates no site;
- missing API indexes still return zero and render a not-indexed page.

- [ ] Step 2: Run the focused CLI tests and verify failure

Run:

~~~text
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --filter FullyQualifiedName~DocsGenerateCommandTests
~~~

Expected: FAIL because the CLI command and project reference do not exist.

- [ ] Step 3: Add the project reference and command registration

Add S1Atlas.Docs to S1Atlas.Cli.csproj. In CliApplication.InvokeCore, construct AtlasReadOnlyServices through ReadOnlyAtlasComposition.BuildReadOnlyServices(_paths.RootDirectory) for generation and register DocsCommand.Create(...) on the root command. Do not duplicate authority construction for the docs path.

- [ ] Step 4: Implement command options and output validation

Define --build as nullable string and --output as nullable directory. Resolve the default to Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "s1atlas-docs")). Reject an output path equal to or contained by _paths.RootDirectory, using OS-appropriate case comparison and a separator boundary. Create only the output directory after all read-only model validation succeeds.

- [ ] Step 5: Map composition/database failures explicitly

Catch FileNotFoundException, SqliteException caused by missing expected tables/schema, and read-only composition failures before any output directory is created. Write an error containing the database path category and “run scan or migration first”; never call repository initialization/migration and never create an Atlas database.

- [ ] Step 6: Run the end-to-end CLI tests and commit

Run the focused integration test; expected PASS. Commit:

~~~text
git add src/S1Atlas.Cli/S1Atlas.Cli.csproj src/S1Atlas.Cli/Commands/DocsCommand.cs src/S1Atlas.Cli/Commands/DocsGenerateCommand.cs src/S1Atlas.Cli/CliApplication.cs tests/S1Atlas.IntegrationTests/DocsGenerateCommandTests.cs
git commit -m "feat: add docs generate CLI command"
~~~

### Task 8: Update README, Git ignore, and working notes

**Files:**
- Modify: .gitignore
- Modify: README.md
- Modify: docs/worknotes/AT-1.md

**Interfaces:**
- Consumes: the implemented command behavior and spec-approved milestone status.
- Produces: repository documentation that tells a user how to generate and open the portal and makes generated output untracked.

- [ ] Step 1: Write the documentation assertions

Before editing, identify the existing command table and Next Milestone section. The final README must contain this command example:

~~~text
dotnet run --project src/S1Atlas.Cli -- docs generate
dotnet run --project src/S1Atlas.Cli -- docs generate --build <build-id> --output .\portal
~~~

It must state that Schedule I is preferred/integrity-verified, APIs are latest-completed per channel, --build affects only Schedule I, scene pages are deferred to CLI/MCP, and output defaults to ./s1atlas-docs/.

- [ ] Step 2: Add the ignore rule and README content

Add unanchored s1atlas-docs/ beside the existing generated-data ignores. Add the command to the Current Commands table. Replace the static human portal entry in Next Milestone with the shipped status while leaving the agent-skill milestone unchanged. Add a short provenance/trust paragraph that distinguishes game extraction authority from API commit/index provenance.

- [ ] Step 3: Append close-out notes

Append the final non-obvious decisions to docs/worknotes/AT-1.md: query surfaces were extended instead of adding portal SQL; environment is current-only; standalone symbol page scope is type/method/constructor; symbol trees are hash-sharded; inline search JavaScript is mandatory for file://; and the merge gate result.

- [ ] Step 4: Verify documentation and commit

Run:

~~~text
rg -n "docs generate|s1atlas-docs|latest-completed|integrity-verified|scene" README.md .gitignore docs/worknotes/AT-1.md
git diff --check
~~~

Commit:

~~~text
git add .gitignore README.md docs/worknotes/AT-1.md
git commit -m "docs: document static human portal"
~~~

### Task 9: Run the full verification and handoff gates

**Files:**
- No new source files; update docs/worknotes/AT-1.md only if a verification discovery is non-obvious.

- [ ] Step 1: Run focused tests after all implementation tasks

Run:

~~~text
dotnet test tests/S1Atlas.Docs.Tests/S1Atlas.Docs.Tests.csproj --configuration Release
dotnet test tests/S1Atlas.IntegrationTests/S1Atlas.IntegrationTests.csproj --configuration Release
~~~

Expected: PASS.

- [ ] Step 2: Run the required full merge gate

Run exactly:

~~~text
dotnet format S1Atlas.sln --verify-no-changes --no-restore
dotnet build S1Atlas.sln --configuration Release
dotnet test S1Atlas.sln --configuration Release --no-build
~~~

Expected: all commands exit 0. If format reports generated-file line endings or ordering changes, fix them and rerun the full sequence from the beginning.

- [ ] Step 3: Verify deterministic output from the real command

Generate two sites from the same fixture to two different output directories. Compare file lists and SHA-256 hashes after normalizing no content; every relative file path and byte hash must match. Confirm the output directory is outside the Atlas data root and no Atlas database file changes.

- [ ] Step 4: Run the code-map check and inspect the diff

Run:

~~~text
node "C:\\Users\\david\\Documents\\MadJag Studios\\studio-ops\\tools\\codemap\\codemap.mjs" generate
node "C:\\Users\\david\\Documents\\MadJag Studios\\studio-ops\\tools\\codemap\\codemap.mjs" check
git status --short
git diff main...HEAD --stat
~~~

Stage any code-map refresh only if the generated map changes because of the new project; do not stage unrelated generated output.

- [ ] Step 5: Request review and execute close-out

Use superpowers:requesting-code-review after tests and merge gate pass. Then use the MadJag closing-out-work workflow: update AT-1 with the why and final commit/PR SHA, update affected milestone/docs records, harvest docs/worknotes/AT-1.md into the routed lesson location if needed, remove the worknotes file in the close-out commit, delete the merged branch local/remote, fast-forward standing checkouts if applicable, and confirm a clean tree.

---

## Self-review checklist

- [ ] Authority selection is covered by Tasks 2, 3, 5, 6, and 7; no Schedule I renderer can receive an unresolved authority.
- [ ] API latest-completed independent selection and provenance are covered by Tasks 1, 3, 6, and 7.
- [ ] Current-only environment and missing/wrong-schema behavior are covered by Tasks 3, 6, 7, and 8.
- [ ] Bulk namespaces, paged symbols, build history, and symbol history are covered by Tasks 1 and 2 with repository/query tests.
- [ ] Standalone symbol scope, member anchors, hash shards, and collision resistance are covered by Tasks 3, 4, and 6.
- [ ] FACT/DERIVED/INTERPRETATION rules, true totals, explicit zero/unavailable states, and Roslyn detection are covered by Task 5 and rendering tests.
- [ ] Static page set, reserved scene path, relative links, inline file:// search, and deterministic LF output are covered by Tasks 4, 6, and 9.
- [ ] CLI surface, default/override output, outside-data-root guard, README, .gitignore, and full merge gate are covered by Tasks 7–9.
- [ ] No placeholders remain in the plan; every task names files, interfaces, failing tests, implementation actions, verification commands, and a commit.
