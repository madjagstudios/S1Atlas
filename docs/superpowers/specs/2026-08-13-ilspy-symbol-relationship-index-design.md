# S1Atlas ILSpy, Symbol, and Relationship Index Design

**Status:** Proposed design for review  
**Date:** 2026-08-13  
**Baseline:** `main` at `8ac4d52b283802ff926f9c1aae1e72baa953b889`  
**Target platform:** Windows  
**Primary language:** C# / .NET 8

## 1. Purpose

This milestone turns the trusted extraction pipeline into the first genuinely searchable S1Atlas knowledge layer.

At completion, a human developer or coding agent should be able to ask S1Atlas where a Schedule I, S1API, or S1MAPI symbol is defined, inspect readable source, see what it contains, see useful relationships such as inheritance and callers/callees where recoverable, and distinguish what is installed now from what exists only in an upstream release or unreleased preview.

The milestone indexes three codebases:

```text
Schedule I
S1API
S1MAPI
```

It does not yet build the HTML portal, MCP server, agent skill, full build-diff product, or semantic/vector search.

## 2. Design Priorities

The implementation must favor useful answers over architectural completeness.

The governing rule is:

> Build only what directly helps Atlas answer useful Schedule I / S1API / S1MAPI questions in this milestone.

This means:

- one normalized symbol model;
- one SQLite database;
- generated or cached source files on disk, metadata in SQLite;
- one pinned ILSpy decompiler adapter;
- Roslyn only for safe parsing of upstream C# source;
- no graph database;
- no arbitrary upstream repository builds;
- no background service;
- no speculative relationship inference;
- no generalized plugin framework for decompilers, languages, or source providers;
- no duplicate extraction-style trust subsystem when the existing extraction authority is sufficient.

## 3. Scope

### 3.1 Included

This milestone delivers:

```text
ILSpy-based decompilation of the preferred validated Schedule I extraction
readable generated C# source preserved under the Atlas data root
normalized assemblies, namespaces, types, methods, constructors, fields, properties,
events, parameters, and generic parameters
source files and source locations
stable snapshot-scoped symbol identities
useful declaration, structural, method-body, and source fingerprints
inheritance and interface relationships
common type-reference relationships
recoverable calls, constructor calls, and field reads/writes
search/type/method/source/refs/callers/callees CLI commands
installed S1API indexing
installed S1MAPI indexing
GitHub source snapshots for S1API and S1MAPI
Installed / Release / Preview channels
manual upstream sync by default
optional on-use upstream auto-checking
upstream status command
human and JSON output
real Schedule I + S1API + S1MAPI smoke validation
```

### 3.2 Explicitly Deferred

This milestone does not deliver:

```text
HTML portal
MCP server
agent skill
semantic/vector search
AI-generated explanations
full build-diff UX
automatic mod compatibility prediction
automatic Harmony patch generation
advanced control-flow analysis
perfect MSBuild/project evaluation
building or executing upstream repositories
background GitHub polling or scheduled jobs
arbitrary-language indexing
normalized rewriting of reconstructed assemblies
sophisticated rename detection
numeric relationship-confidence scoring
a graph database
validated-extraction deletion
```

## 4. Trust and Authority Model

S1Atlas must keep current installed truth separate from upstream update intelligence.

### 4.1 Schedule I

Schedule I indexing may consume only the currently preferred validated extraction for the selected build.

Before indexing begins, Atlas performs a fresh existing Phase 4/5 integrity verification of that extraction. A Phase 3 candidate, failed output, retained output, non-preferred divergent retry, database row without matching filesystem evidence, or otherwise unverified extraction cannot feed the index.

The index records:

```text
game build ID
preferred extraction ID
raw extraction artifact hashes
decompiler identity/version/settings
index identity
```

The existing extraction authority remains the source of truth. Indexing does not redefine extraction identity or preference.

### 4.2 Installed S1API and S1MAPI

The locally installed binary is authoritative for what a mod can actually use in the current environment.

Installed API snapshots record:

```text
codebase
installed binary path
binary SHA-256
observed assembly/version metadata
captured-at time
matching upstream source provenance when available
```

GitHub source may enrich an installed API snapshot with readable original source, comments, and locations, but it does not override facts observed in the installed binary.

If the installed binary and matched source disagree, Atlas preserves the mismatch and keeps binary facts authoritative.

### 4.3 Upstream S1API and S1MAPI

Each API may have two additional channels:

```text
Release
Preview
```

`Release` represents the latest indexed official release/tag.

`Preview` represents the latest indexed commit of the configured default development branch, normally `main`, and is always labeled unreleased/preview.

Neither channel may masquerade as installed/current truth.

## 5. Code Snapshot Model

Every symbol, source file, relationship, and fingerprint belongs to one immutable `CodeSnapshot`.

Representative fields:

```text
code_snapshot_id
codebase                 ScheduleI | S1API | S1MAPI
channel                  Installed | Release | Preview
captured_at_utc

# Schedule I / installed provenance
game_build_id?
extraction_id?
binary_sha256?
binary_version?

# upstream provenance
repository?
commit_sha?
tag?
release_version?
default_branch?

source_match             ExactCommit | ExactBinaryHash | ExactTag | VersionMatched |
                         Unmatched | NotApplicable
```

The exact schema may use normalized supporting tables where that makes queries clearer, but the public domain model should remain simple.

A code snapshot is immutable after successful indexing. A new game extraction, installed API binary, release commit, or preview commit creates or selects another snapshot; it does not edit the historical snapshot in place.

## 6. Channels and Default Query Semantics

Normal queries default to what is actually installed and usable now:

```text
Schedule I / Installed
S1API / Installed
S1MAPI / Installed
```

A normal query does not silently mix future release or preview code into current results.

Example:

```text
s1atlas search Dealer
```

searches current installed truth.

Users may explicitly request upstream channels:

```text
s1atlas search Dealer --channel release
s1atlas search Dealer --channel preview
s1atlas search Dealer --channel all

s1atlas search Dealer --codebase schedule-i
s1atlas search Dealer --codebase s1api
s1atlas search Dealer --codebase s1mapi

s1atlas search Dealer --codebase s1api --channel preview
```

When upstream data is shown, human and JSON output must preserve the channel and provenance so agents cannot confuse unreleased code with installed API availability.

## 7. ILSpy Integration

### 7.1 Approach

S1Atlas uses the `ICSharpCode.Decompiler` library directly behind an S1Atlas-owned adapter.

The package version is pinned to an exact reviewed stable release compatible with the .NET 8 S1Atlas solution. The selected version and relevant decompiler settings become index provenance. Upgrades are explicit reviewed changes rather than floating dependencies.

The adapter isolates ILSpy-specific types from Core, Storage, CLI, and future MCP/Docs layers.

Representative boundary:

```text
S1Atlas-owned IDecompilerAdapter
        ↓
ICSharpCode.Decompiler implementation
```

S1Atlas does not require ILSpyX or a .NET runtime upgrade solely for analyzer conveniences in this milestone.

### 7.2 Why Embedded Library Instead of `ilspycmd`

An embedded adapter gives Atlas direct access to metadata/type-system and recovered method information while also generating C# source. An external console process would make source generation easy but would force Atlas to reconstruct semantic relationships by parsing console-generated text.

Generated C# is a human/source artifact. Relationship facts should come from resolved metadata or recovered method structures whenever possible, not from scraping formatted source text.

### 7.3 Assembly Safety

Reconstructed Schedule I assemblies are data inputs. Atlas must not execute them or load them into the runtime through `Assembly.Load`/`AssemblyLoadContext` merely to inspect them.

The decompiler adapter uses ILSpy metadata/decompiler facilities for analysis.

## 8. Generated Source

Readable Schedule I source is written outside SQLite beneath the Atlas data root.

Representative layout:

```text
%LOCALAPPDATA%\S1Atlas\
  builds\<build-id>\
    indexes\<index-id>\
      source\
        schedule-i\
          ...
      index-manifest.json
      complete.marker
```

Exact folder names may be adjusted during implementation to follow existing `AtlasPaths` conventions.

SQLite stores source metadata and locations rather than complete source text blobs.

Each generated source file records:

```text
source_file_id
code_snapshot_id
origin = GeneratedDecompilation
relative_path
sha256
language = CSharp
```

Generated source is local proprietary-derived content and remains excluded from Git and CI artifacts.

## 9. Upstream GitHub Source Model

### 9.1 Immutable Commit Cache

S1API and S1MAPI upstream source is cached by exact Git commit SHA.

Representative layout:

```text
upstream\
  s1api\
    commits\<sha>\source\...
  s1mapi\
    commits\<sha>\source\...
```

A commit snapshot records repository identity, commit SHA, branch/tag/release labels, retrieval time, and a file manifest with hashes.

If Release and Preview happen to point to the same commit, Atlas stores one immutable commit snapshot and lets both channel records reference it.

### 9.2 Upstream Repositories Are Untrusted Input

Atlas may read source but does not automatically execute it.

Upstream ingestion must not run:

```text
dotnet build
MSBuild
repository scripts
source generators
repository tests
arbitrary package restore
arbitrary executable content
```

Project and solution files may be parsed as data where useful. Atlas does not promise perfect MSBuild conditional evaluation.

### 9.3 Source Parsing

Roslyn is used only as needed to parse C# source safely.

For upstream Release/Preview source, Atlas aims to recover:

```text
declarations
namespaces/types/members
comments/documentation where useful
written attributes
simple type references
syntactically evident calls and accesses when they can be bound safely to the local snapshot
source locations
```

Atlas does not need to construct a perfect compilable solution for Release/Preview indexing.

When a target cannot be confidently resolved, the relationship remains unresolved with textual target evidence rather than being guessed.

## 10. Matching Installed API Binaries to GitHub Source

Installed source matching is conservative, strongest evidence first:

```text
1. exact embedded/source commit metadata when available
2. exact produced binary/package hash mapped to an upstream commit/release
3. exact reviewed tag/release association
4. semantic version/tag match
5. unmatched
```

The recorded `source_match` tells users and agents how strong the association is.

A version-only match is useful enrichment but is not represented as cryptographic proof.

## 11. Upstream Network Policy

This is a public-tool design. S1Atlas must not surprise users with background network activity.

### 11.1 Default

Automatic GitHub checks are disabled by default.

Normal commands use installed data and whatever upstream snapshots are already cached.

Explicit sync commands perform network access:

```text
s1atlas upstream sync
s1atlas upstream sync s1api
s1atlas upstream sync s1mapi
```

### 11.2 Optional Auto-Check

Users may opt into on-use automatic refresh:

```text
upstream.autoCheck = true
upstream.checkInterval = 24h
```

The exact configuration storage format will follow existing S1Atlas configuration conventions.

Auto-check means only:

- when Atlas is already executing a relevant scan/index/upstream operation;
- if the last successful/attempted check is older than the configured interval;
- perform a lightweight upstream metadata check;
- fetch immutable new snapshots only when upstream refs changed.

It does **not** mean:

```text
background daemon
scheduled task
service
startup process
six-hour timer
continuous polling
```

If GitHub is unavailable, Atlas keeps using cached/local data and reports upstream freshness truthfully.

### 11.3 Status

```text
s1atlas upstream status
```

shows for each API:

```text
installed version/hash
installed-source match state
latest cached release/tag + commit
latest cached preview branch + commit
last checked time
whether auto-check is enabled
whether cached upstream information may be stale
```

## 12. Normalized Symbol Model

One normalized model serves Schedule I, S1API, and S1MAPI.

Symbol kinds:

```text
Assembly
Namespace
Type
Method
Constructor
Field
Property
Event
Parameter
GenericParameter
```

Common symbol facts include:

```text
symbol_instance_id
code_snapshot_id
kind
name
qualified_name
canonical_key
lineage_key
parent_symbol_id?
assembly_symbol_id?
namespace_symbol_id?
accessibility
static
abstract
virtual
override
sealed
readonly
generic_arity
metadata_token?
source_location_id?
```

Not every field applies to every symbol kind. The storage schema should favor a simple central `symbols` representation plus only the supporting tables that materially improve correctness or querying.

Do not create a large table-per-symbol-kind hierarchy unless implementation demonstrates a concrete need.

## 13. Symbol Identity

### 13.1 Snapshot-Scoped Identity

`symbol_instance_id` identifies one exact logical symbol inside one exact `CodeSnapshot`.

Conceptually:

```text
hash(code_snapshot_id + canonical_symbol_key)
```

### 13.2 Canonical Key

The canonical key includes enough normalized signature detail to uniquely identify overloads and generic/member shapes inside a snapshot.

Examples:

```text
ScheduleOne.Economy.Dealer
ScheduleOne.Economy.Dealer::AddProduct(Product,int)
ScheduleOne.Economy.Dealer::_inventory
```

Constructors, ref/out/in parameters, arrays, nested types, generic arity, and overload-significant details must be represented deterministically.

### 13.3 Lineage Key

A coarser lineage key may be recorded to help later diffing, for example:

```text
ScheduleOne.Economy.Dealer::AddProduct
```

Lineage is comparison assistance only. It is not authoritative identity and does not need sophisticated rename detection in this milestone.

## 14. Source Locations

A source location points at an Atlas-owned generated source file or immutable cached upstream source file.

Representative facts:

```text
source_file_id
start_line
start_column
end_line
end_column
```

Origins:

```text
GeneratedDecompilation
GitHubSource
```

The `source` command must include provenance in human and JSON output.

For example:

```text
Schedule I — Installed
Build: <build-id>
Extraction: <extraction-id>
Decompiler: <pinned version>
Source: generated decompilation
```

or:

```text
S1API — Preview / Unreleased
Commit: <sha>
Source: cached GitHub snapshot
```

## 15. Relationship Model

Relationships are stored separately from symbols in one simple relationship model.

Representative fields:

```text
relationship_id
code_snapshot_id
source_symbol_id
target_symbol_id?
target_text?
kind
evidence
resolved
source_location_id?
```

Initial relationship kinds are deliberately limited to useful mod-development questions:

```text
Inherits
ImplementsInterface
FieldType
PropertyType
EventType
ParameterType
ReturnType
Calls
Constructs
ReadsField
WritesField
```

Initial evidence kinds:

```text
Metadata
RecoveredIL
UpstreamSource
```

No numerical confidence model is required.

A relationship is either resolved to an indexed target or remains unresolved with textual target evidence. Atlas must not invent a target to improve coverage statistics.

## 16. Cross-Codebase Relationships

Installed Schedule I, S1API, and S1MAPI snapshots may resolve relationships to one another when the target identity is unambiguous and the participating snapshots represent the same current environment.

Upstream Release/Preview relationships remain within their explicit source snapshot/channel unless a relationship to an external installed dependency is represented as unresolved textual evidence.

Atlas must not silently bind a Preview symbol to an Installed symbol merely because names happen to match.

## 17. Fingerprints

Raw extraction/package hashes remain authoritative provenance. Normalized fingerprints are additional comparison signals, not replacement identity.

Useful layers:

```text
RawArtifact
Declaration
Structural
MethodBody
Source
```

### RawArtifact

Existing SHA-256 of exact binary/source artifact bytes.

### Declaration

Normalized symbol signature.

### Structural

Normalized member/type shape useful for detecting declaration changes.

### MethodBody

A deterministic normalized representation of recovered method behavior where ILSpy exposes enough information to compute one reliably.

If useful recovered behavior is unavailable, the fingerprint is absent rather than guessed.

### Source

Normalized generated/upstream C# source for human-oriented source comparison.

The Phase 5 Cpp2IL nondeterminism finding is handled by keeping raw identity exact while using normalized fingerprints only as separate signals.

## 18. Index Lifecycle

Indexing deliberately reuses the existing extraction trust boundary rather than creating another multi-phase promotion framework.

High-level lifecycle:

```text
trusted code snapshot input
        ↓
index attempt
        ↓
decompile / parse
        ↓
write generated source to temporary Atlas-owned location
        ↓
write symbols/relationships in a SQLite transaction
        ↓
validate basic integrity
        ↓
promote source directory and mark index complete
```

If an index attempt fails, it never becomes queryable and the previous completed index remains usable.

A changed preferred Schedule I extraction, installed API binary hash, or upstream commit makes only the affected snapshot stale/new. Atlas does not reindex unrelated snapshots without reason.

## 19. Minimal Persistence Groups

The design should start with approximately these logical persistence groups:

```text
code_snapshots
index_runs
assemblies / namespaces / symbols / parameters as needed
source_files
source_locations
relationships
symbol_fingerprints
upstream_repositories
upstream_snapshots
upstream_state
```

These are logical groups, not a requirement to create exactly one table per line.

The implementation should prefer the smallest schema that preserves constraints and supports the required queries.

One central symbols table and one relationships table are preferred unless focused tests demonstrate that a different split materially simplifies correctness.

## 20. Index Validation

A candidate index becomes queryable only after basic validation succeeds.

Required checks include:

### Authority

```text
Schedule I input is the preferred validated extraction
preferred extraction still passes full integrity verification
installed API binary hashes still match the indexed observation
cached upstream commit/file manifest still matches its immutable snapshot
```

### Source

```text
all generated/cached source paths remain under Atlas-owned roots
no reparse-point/path traversal
recorded source hashes match
recorded source locations resolve within their source files
```

### Symbols

```text
all symbols belong to a known code snapshot
parent/declaring symbol references resolve
canonical keys are unique where uniqueness is required
parameter ordinals are valid
assembly/namespace ownership is internally consistent
```

### Relationships

```text
all source symbol IDs exist
resolved target IDs exist
resolved targets belong to an allowed snapshot relationship context
unresolved relationships retain textual evidence
```

### Counts

Expected installed codebases should contain nonzero types and methods. Major unexplained collapses are reported and may block promotion when they indicate an obviously unusable index.

This milestone does not add a generalized configurable validation-policy engine unless implementation discovers a concrete requirement for one.

## 21. Query and CLI Surface

### 21.1 Index

```text
s1atlas index
s1atlas index --force
```

Normal index behavior:

1. resolve the preferred validated Schedule I extraction;
2. verify it;
3. index Schedule I if the exact required index is missing/stale;
4. index installed S1API/S1MAPI when present;
5. attach/use cached verified upstream source where appropriate;
6. index cached Release/Preview commits when needed;
7. perform no GitHub traffic unless the user explicitly synced or opted into on-use auto-checking.

`--force` rebuilds from the same trusted inputs; it never bypasses extraction integrity or preference rules.

### 21.2 Search

```text
s1atlas search <query> [--codebase ...] [--channel ...] [--json]
```

Ranking should remain simple and deterministic:

```text
exact symbol name
exact qualified-name segment
prefix
substring
canonical signature
namespace
```

No embeddings are required.

### 21.3 Type

```text
s1atlas type <symbol> [--codebase ...] [--channel ...] [--json]
```

Returns type provenance, declaration, base/interface relationships, fields, properties, events, methods, and source location.

### 21.4 Method

```text
s1atlas method <symbol> [--codebase ...] [--channel ...] [--json]
```

Returns method provenance, signature, parameters, source location, fingerprints where available, and relationship summaries.

Ambiguous method names return candidates rather than guessing.

### 21.5 Source

```text
s1atlas source <symbol> [--codebase ...] [--channel ...] [--json]
```

Returns the relevant source slice and provenance.

### 21.6 Relationships

```text
s1atlas refs <symbol> [--json]
s1atlas callers <symbol> [--json]
s1atlas callees <symbol> [--json]
```

`refs` groups useful structural/type/behavioral relationships.

`callers` and `callees` include evidence type and unresolved status where applicable.

## 22. Missing or Incomplete Recovered Behavior

Cpp2IL/ILSpy may not recover equally useful method bodies for every Schedule I method.

S1Atlas must measure and report this instead of assuming completeness.

If a method has no useful recoverable body:

```text
its declaration remains searchable
its generated source may still exist
metadata relationships remain available
behavioral relationships may be absent
method-body fingerprint may be absent
```

No inferred caller/callee edges are invented to fill gaps.

A real capability smoke in the first implementation phase should quantify how many representative methods expose useful recoverable behavior before later relationship work is considered complete.

## 23. Installed API Missing State

S1API and S1MAPI are independently optional installed dependencies.

If one is not installed:

- current/Installed queries report it as unavailable;
- Schedule I and the other installed API remain indexable;
- cached Release/Preview upstream snapshots may still be browsed when the user explicitly selects those channels;
- Atlas never implies that an upstream-only API is locally available.

## 24. Failure Behavior

Failures are fail-closed but simple.

Examples:

```text
preferred extraction fails integrity -> Schedule I indexing stops; prior complete index remains
ILSpy cannot decompile a required assembly -> new index fails; prior complete index remains
source file/hash changes during indexing -> new index fails
upstream GitHub unavailable -> cached/local indexing continues; status reports stale/unavailable
Release/Preview source contains unresolved dependency -> preserve unresolved relationship; do not fail entire snapshot solely for that
SQLite transaction fails -> no completed index is exposed
```

Normal human output does not expose raw stack traces.

## 25. Repository and Privacy Rules

Schedule I reconstructed assemblies and generated decompiled C# remain local proprietary-derived artifacts and must never enter Git or CI artifacts.

The existing repository hygiene gate is extended only as needed for new index/source output paths.

Public S1API/S1MAPI source snapshots are still runtime/cache data and are not vendored wholesale into the S1Atlas repository by normal indexing.

Tests use source-built fixtures or small authored test source, not proprietary Schedule I files.

## 26. Testing Strategy

### 26.1 Unit Tests

Cover S1Atlas-owned behavior:

```text
snapshot/channel rules
canonical symbol keys
symbol IDs
fingerprints
search ranking
relationship resolution rules
Installed/Release/Preview isolation
upstream freshness/config logic
source path safety
validation rules
```

### 26.2 Integration Tests

Use fixture assemblies and authored C# repositories/snapshots to verify:

```text
ILSpy decompilation adapter
source generation
symbol normalization
SQLite persistence
query commands
relationship indexing
source locations
upstream cache and manual sync seams
no-build/no-execution upstream ingestion
failed index preserves prior completed index
```

CI does not require the real Schedule I installation and does not need network access.

### 26.3 Real Smoke

On the operator's Windows machine:

```text
index preferred Schedule I extraction
index installed S1API when present
index installed S1MAPI when present
sync/index S1API/S1MAPI Release and Preview snapshots
run representative search/type/method/source/refs/callers/callees queries
inspect representative game systems and API surfaces
verify source locations resolve
measure relationship/body recovery coverage
prove Preview/Release cannot masquerade as Installed
prove game files remain untouched
prove no generated/proprietary output is tracked
```

If an installed dependency is absent in the reference environment, that absence is recorded; upstream indexing for it may still be tested separately.

## 27. Implementation Sequence

This is one milestone with five implementation phases, not five separate product destinations.

### Phase 1 — Index Authority and ILSpy Capability

Deliver:

```text
pinned ICSharpCode.Decompiler dependency
ILSpy adapter boundary
preferred-extraction authority check
safe generated-source layout
fixture tests
real Schedule I decompilation capability smoke
measurement of recoverable method-body usefulness
```

### Phase 2 — Normalized Symbols and Persistence

Deliver:

```text
code snapshots
index runs
assemblies/namespaces/symbols/parameters
source files/locations
canonical IDs
fingerprints
atomic completed-index behavior
```

### Phase 3 — Relationships and Query CLI

Deliver:

```text
inheritance/interfaces
type references
calls/constructs/field reads/writes where recoverable
search/type/method/source/refs/callers/callees
human + JSON output
```

### Phase 4 — S1API/S1MAPI Upstream Intelligence

Deliver:

```text
installed API indexing
configured upstream repositories
immutable commit cache
Installed/Release/Preview channel handling
conservative installed-source matching
upstream status
upstream sync
manual network default
optional on-use 24h auto-check configuration
```

### Phase 5 — Real Smoke and Hardening

Deliver:

```text
real indexing of Schedule I and available installed APIs
real Release/Preview source indexing
representative query proof
relationship/body recovery report
privacy/hygiene checks
final docs and QA
```

## 28. Definition of Done

The milestone is complete when all applicable items are true:

```text
[ ] Schedule I consumes only the preferred integrity-verified extraction
[ ] pinned ILSpy engine produces useful readable source from the real extraction
[ ] generated source is local, hashed, and linked to symbols
[ ] assemblies/namespaces/types/methods/constructors/fields/properties/events/parameters indexed
[ ] canonical symbol identities are deterministic inside a snapshot
[ ] useful declaration/structural/source/method-body fingerprints exist where evidence supports them
[ ] inheritance and interface relationships indexed
[ ] common type-reference relationships indexed
[ ] recoverable caller/callee relationships indexed without guessing
[ ] search/type/method/source/refs/callers/callees work in human and JSON forms
[ ] installed S1API indexed when present
[ ] installed S1MAPI indexed when present
[ ] matching GitHub source attached conservatively when available
[ ] latest cached Release and Preview channels remain distinct from Installed
[ ] upstream sync is explicit network behavior by default
[ ] optional auto-check runs only during use and never in the background
[ ] GitHub failure never breaks cached/local queries
[ ] failed indexing never replaces a completed index
[ ] real smoke demonstrates useful symbols, source, and relationships
[ ] limitations in recovered method bodies/relationships are measured and documented
[ ] Schedule I installation remains read-only
[ ] no proprietary/generated Schedule I content enters Git or CI artifacts
[ ] build/tests/format/hygiene pass
```

## 29. Hard Invariants

```text
A Phase 3 candidate can never feed the symbol index.
A non-preferred Schedule I extraction can never silently become the default indexed game truth.
A failed index is never queryable as complete.
Preview or Release code can never masquerade as Installed code.
Installed API binary facts take precedence over unmatched/mismatched GitHub source.
Unresolved relationships remain unresolved rather than guessed.
Generated C# is not used as the sole evidence for binary caller/callee relationships when metadata/IL evidence is available.
Raw artifact hashes remain authoritative provenance; normalized fingerprints do not replace them.
Upstream repositories are never automatically built or executed during indexing.
Automatic GitHub checking is opt-in and never runs as a background process.
Normal queries default to current Installed truth.
Schedule I game files remain read-only.
```

## 30. Follow-On Work

After this milestone, the indexed data can support independent later work such as:

```text
build and API diffing
static HTML exploration portal
plain-English/C# learning context
S1API/S1MAPI update-impact views
read-only MCP server
S1Atlas agent skill
```

Those follow-on features must consume the same normalized query layer rather than creating separate facts.
