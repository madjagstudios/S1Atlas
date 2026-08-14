# S1Atlas ILSpy, Symbol, and Relationship Index Design

**Status:** Proposed design for review  
**Date:** 2026-08-13  
**Baseline:** `main` at `8ac4d52b283802ff926f9c1aae1e72baa953b889`  
**Target platform:** Windows  
**Primary language:** C# / .NET 8

## 1. Purpose

This milestone turns the trusted extraction pipeline into the first searchable S1Atlas knowledge layer.

At completion, S1Atlas should let a human developer or coding agent:

- find Schedule I, S1API, and S1MAPI symbols;
- inspect readable source;
- inspect practical relationships such as inheritance, references, callers, and callees where recoverable;
- distinguish current installed truth from upstream Release and Preview code;
- prepare for S1API/S1MAPI updates without confusing unreleased code with what is installed now.

The milestone indexes three codebases:

```text
Schedule I
S1API
S1MAPI
```

It does **not** yet deliver the HTML portal, MCP server, agent skill, full build-diff product, or semantic/vector search.

## 2. Anti-Overengineering Rule

The implementation must favor useful answers over architectural completeness.

> Build only what directly helps Atlas answer useful Schedule I / S1API / S1MAPI questions in this milestone.

Use:

- one normalized symbol model;
- one SQLite database;
- generated/cached source on disk, metadata in SQLite;
- one pinned ILSpy adapter;
- Roslyn only where upstream C# parsing needs it;
- one simple relationship table/model;
- existing extraction authority rather than a second extraction-style trust framework.

Do not add:

```text
graph database
general plugin/decompiler framework
arbitrary upstream repository builds
background service or scheduled polling
semantic/vector search
AI explanations
numerical confidence scoring
advanced control-flow analysis
perfect MSBuild evaluation
portal
MCP
agent skill
full diff UX
```

## 3. Scope

### Included

```text
ILSpy decompilation of preferred validated Schedule I output
readable generated C# stored locally
assemblies / namespaces / types / methods / constructors
fields / properties / events / parameters / generic parameters
source files and source locations
canonical symbol identities
practical fingerprints
inheritance and interfaces
common type references
recoverable calls / constructors / field reads-writes
search / type / method / source / refs / callers / callees
installed S1API and S1MAPI indexing
S1API/S1MAPI GitHub Release and Preview snapshots
manual upstream sync by default
optional on-use upstream auto-check
upstream status
human and JSON output
real Schedule I + API smoke validation
```

### Deferred

```text
portal / MCP / agent skill
semantic search
full build-diff product
automatic compatibility prediction
automatic patch generation
background polling
advanced control-flow analysis
normalized binary rewriting
sophisticated rename detection
validated-extraction deletion
```

## 4. Authority Model

### 4.1 Schedule I Authority Is a Mandatory Three-Step Resolution

A preferred-extraction pointer is **not** sufficient authority by itself.

Every Schedule I index operation must resolve authority through this exact sequence:

```text
1. IValidatedExtractionRepository.GetPreferredExtractionAsync(buildId)
2. IValidatedExtractionRepository.GetValidatedExtractionAsync(extractionId)
3. ValidatedExtractionIntegrityVerifier.VerifyAsync(...)
```

Only after all three succeed may the extraction feed ILSpy.

Implementation must centralize this sequence behind one narrow indexing authority resolver so callers cannot accidentally stop after step 1. Do not create a broad new trust framework; one resolver is enough.

The following can never feed the Schedule I symbol index:

```text
ExtractionAttemptStatus.ProcessCompleted candidate
Failed/Canceled/Abandoned attempt output
retained-output
non-preferred divergent validated extraction used as implicit current truth
preferred pointer whose validated row is missing
validated row whose filesystem/manifests/hashes fail integrity verification
```

If the existing preferred extraction is cleared because of `PolicyInvalidated` or `IntegrityInvalidated`, the previously completed Schedule I index becomes stale and must not be presented as current truth.

### 4.2 Installed S1API and S1MAPI

The locally installed binary is authoritative for what the current mod environment can actually use.

Installed API snapshots record:

```text
persisted environment snapshot identity
binary path
binary SHA-256
assembly/version metadata
captured-at time
optional matched GitHub source provenance
```

GitHub source may enrich installed API symbols with original source/comments/locations, but binary facts win when source and binary disagree.

### 4.3 Upstream Release and Preview

S1API and S1MAPI may additionally have:

```text
Release  = latest indexed official release/tag
Preview  = latest indexed configured development branch, normally main
```

These are intentionally new scope for this milestone and were explicitly approved during design discussion.

They remain **non-authoritative update intelligence** and can never masquerade as Installed.

## 5. Code Snapshots and Channels

Every symbol, relationship, source file, and fingerprint belongs to one immutable code snapshot.

Representative facts:

```text
code_snapshot_id
codebase                 ScheduleI | S1API | S1MAPI
channel                  Installed | Release | Preview
environment_snapshot_id?  # Installed channel only
captured_at_utc

game_build_id?
extraction_id?
binary_sha256?
binary_version?

repository?
commit_sha?
tag?
release_version?
default_branch?

source_match             ExactCommit | ExactBinaryHash | ExactTag |
                         VersionMatched | Unmatched | NotApplicable
```

Channel validity is constrained:

```text
ScheduleI -> Installed only
S1API     -> Installed | Release | Preview
S1MAPI    -> Installed | Release | Preview
```

A `ScheduleI / Preview` snapshot is invalid.

Installed Schedule I, S1API, and S1MAPI snapshots may resolve cross-codebase relationships only when they share the same persisted environment snapshot identity.

## 6. Default Query Semantics

Normal queries search only what is installed/current:

```text
Schedule I / Installed
S1API / Installed
S1MAPI / Installed
```

Example:

```text
s1atlas search Dealer
```

Release/Preview must be explicitly selected:

```text
s1atlas search Dealer --channel release
s1atlas search Dealer --channel preview
s1atlas search Dealer --channel all
s1atlas search Dealer --codebase s1api --channel preview
```

Human and JSON output always include codebase/channel provenance when non-installed data is shown.

## 7. ILSpy Integration

S1Atlas uses a pinned stable `ICSharpCode.Decompiler` package behind one S1Atlas-owned adapter.

The adapter:

- consumes only verified authoritative binary inputs;
- generates readable C#;
- exposes metadata/type/method facts needed for indexing;
- does not leak ILSpy-specific types into Core/Storage/CLI;
- does not use `Assembly.Load` or `AssemblyLoadContext` on reconstructed game assemblies.

Generated source is for humans and source locations. Binary relationships should come from metadata/recovered IL where available, not from scraping formatted C#.

`ILSpyX` is not required in this milestone if doing so would force an unnecessary runtime upgrade.

## 8. On-Disk Artifact Layout

### Schedule I

```text
%LOCALAPPDATA%\S1Atlas\
  builds\<build-id>\
    indexes\<index-id>\
      source\schedule-i\...
      index-manifest.json
      complete.marker
```

### Installed APIs

Installed API indexes are not game-build-owned and therefore have their own binary-hash roots:

```text
%LOCALAPPDATA%\S1Atlas\
  installed\
    s1api\<binary-sha256>\indexes\<index-id>\source\...
    s1mapi\<binary-sha256>\indexes\<index-id>\source\...
```

### Upstream source cache

```text
%LOCALAPPDATA%\S1Atlas\
  upstream\
    s1api\commits\<commit-sha>\source\...
    s1mapi\commits\<commit-sha>\source\...
```

`complete.marker` is intentionally runtime-only and remains covered by the repository hygiene never-track rule.

SQLite stores metadata and source locations rather than whole source blobs.

## 9. Upstream GitHub Source

### 9.1 Trust Model

Upstream source is untrusted enrichment input.

Atlas may fetch repository metadata/source, but must not automatically:

```text
run dotnet build
run MSBuild project evaluation
restore arbitrary packages
execute repository scripts
execute source generators
run repository tests
execute downloaded binaries
```

### 9.2 Commit SHA Semantics

A cached upstream snapshot is keyed by the exact commit SHA reported by GitHub and Atlas hashes every cached file itself.

Unless implementation explicitly uses Git object transport and independently verifies object hashes, Atlas does **not** claim independent cryptographic verification of Git's commit/tree object graph. The provenance claim is:

```text
GitHub reported commit <sha>
Atlas fetched source for that snapshot
Atlas independently SHA-256 hashed the cached file bytes
```

That trust level is acceptable because Release/Preview are non-authoritative enrichment.

### 9.3 Source Parsing

Roslyn parses upstream C# without building the repository.

A lightweight in-memory `CSharpCompilation` may be used when it materially improves symbol/type binding, but it must be constructed only from cached source plus explicitly trusted framework/reference assemblies. It must not evaluate project files, restore packages, or execute repository code.

If semantic binding is unavailable, source relationships remain unresolved textual evidence rather than guessed targets.

## 10. Matching Installed APIs to GitHub Source

Strongest evidence wins:

```text
1. exact embedded/source commit metadata
2. exact binary/package hash mapped to upstream provenance
3. exact reviewed tag/release association
4. semantic version/tag match
5. unmatched
```

A version match is useful but not cryptographic proof.

Binary facts remain authoritative.

## 11. Network Policy

S1Atlas is intended to become a public tool, so network behavior must be explicit.

### Default

No automatic GitHub traffic.

```text
s1atlas upstream sync
s1atlas upstream sync s1api
s1atlas upstream sync s1mapi
```

perform explicit network access.

### Optional Auto-Check

Users may opt in:

```text
upstream.autoCheck = true
upstream.checkInterval = 24h
```

Auto-check happens only while Atlas is already running a relevant command and the freshness interval has expired.

It does not create a daemon, scheduled task, service, startup process, timer, or continuous poller.

GitHub failure never breaks cached/local queries.

### Status

```text
s1atlas upstream status
```

shows installed version/hash, source-match state, cached release/preview commits, last-check time, stale state, and auto-check configuration.

## 12. Normalized Symbol Model

One symbol model serves all codebases.

Kinds:

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

Common fields include:

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

Use one central symbols table unless focused implementation tests show a concrete reason to split it.

## 13. Canonical Symbol Identity

### 13.1 Shared Renderer Is Mandatory

ILSpy metadata and Roslyn source must not each invent their own signature strings.

Both frontends feed a shared S1Atlas normalized type/signature model into one canonical-signature renderer.

The renderer defines one representation for:

```text
CLR built-in types
namespace qualification
nested types
generic arity and generic arguments
arrays and ranks
pointer types
nullable annotations where semantically known
ref / out / in modifiers
tuples
constructors
method overload parameters
```

Example goal:

```text
System.Int32, not sometimes "int" and sometimes "System.Int32"
```

`symbol_instance_id` is conceptually:

```text
hash(code_snapshot_id + canonical_key)
```

### 13.2 Upstream Resolution Limit

Source-only Release/Preview parsing cannot always resolve a type to the same semantic identity as metadata-derived installed binaries.

When required type identities are unresolved:

- the source symbol is still indexable;
- its source-local key remains deterministic within that snapshot;
- Atlas must not claim byte-identical cross-frontend canonical identity;
- cross-channel "same symbol" comparison uses only keys whose normalized type references are sufficiently resolved.

This avoids false equivalence while keeping upstream browsing useful.

### 13.3 Lineage Key

A coarser lineage key such as:

```text
ScheduleOne.Economy.Dealer::AddProduct
```

may support later diffing, but is not authoritative identity and does not require sophisticated rename detection now.

## 14. Source Files and Locations

Each source file records:

```text
source_file_id
code_snapshot_id
origin                  GeneratedDecompilation | GitHubSource
relative_path
sha256
language
```

Locations record line/column ranges.

`s1atlas source` includes provenance, e.g.:

```text
Schedule I — Installed
Build: <build-id>
Extraction: <extraction-id>
Decompiler: <version>
```

or:

```text
S1API — Preview / Unreleased
Commit: <sha>
```

## 15. Relationship Model

Use one simple relationship model:

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

Initial kinds:

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

Evidence:

```text
Metadata
RecoveredIL
UpstreamSource
```

No numerical confidence score.

Resolved targets must be exact. Unresolved targets retain textual evidence.

Preview/Release symbols must not silently bind to Installed symbols because names happen to match.

## 16. Fingerprints

Raw artifact SHA-256 remains the exact provenance/integrity identity.

Normalized comparison layers may include:

```text
Declaration
Structural
MethodBody
Source
```

### Stability Goal

Declaration/Structural/MethodBody fingerprints should be stable across re-extractions when the only differences are volatile/non-semantic metadata such as MVIDs.

They must deliberately exclude reviewed volatile fields that do not represent the normalized declaration or behavior being fingerprinted.

This is a **goal to measure**, not an assumption.

If Cpp2IL also changes member ordering, names, recovered body structure, or other meaningful/unstable content, normalized fingerprints may still differ. Atlas reports that rather than forcing equality.

### Required Real Measurement

The first implementation capability smoke should compare normalized fingerprints across the already-existing same-recipe divergent validated Schedule I outputs created during Phase 5, where available locally.

That measurement should report:

```text
raw artifact equality rate
normalized declaration fingerprint equality rate
normalized structural fingerprint equality rate
method-body fingerprint availability/equality rate
source fingerprint equality rate
```

No new Cpp2IL run is required solely to perform this comparison if the existing validated outputs remain available.

## 17. Index Lifecycle

Keep indexing simple:

```text
trusted input
   ↓
index attempt
   ↓
decompile / parse
   ↓
write source to Atlas-owned staging
   ↓
write symbols/relationships inside SQLite transaction
   ↓
validate basic referential/source integrity
   ↓
promote source + mark index complete
```

A failed attempt never becomes queryable and never replaces the previous completed index.

No separate preference policy, validation-policy engine, promotion journal family, or quarantine framework is added unless a real failure mode proves it necessary.

## 18. Persistence

Append **migration 6** only. Migrations 1–5 remain byte-for-byte unchanged.

Start with the minimum logical groups needed:

```text
code_snapshots
index_runs
symbols
parameters                  # only if materially cleaner than symbols-only
source_files
source_locations
relationships
symbol_fingerprints
upstream_repositories
upstream_snapshots
upstream_state
```

The implementation must validate against the **shipped code/schema**, not an older design document description.

## 19. Index Validation

A completed index requires:

### Authority

```text
Schedule I preferred pointer resolves
validated extraction row exists
fresh ValidatedExtractionIntegrityVerifier result is Valid
installed API binary hashes still match the same environment observation
cached upstream file hashes still match Atlas cache manifests
channel/codebase combination is valid
```

### Source

```text
paths stay under Atlas-owned roots
no reparse/path traversal
source hashes match
source locations stay within files
```

### Symbols

```text
snapshot ownership valid
parent/declaring references resolve
canonical keys unique where required
parameter ordinals valid
```

### Relationships

```text
source symbols exist
resolved targets exist
cross-snapshot target context is allowed
unresolved edges retain target text
```

Expected installed snapshots must contain useful nonzero types/methods.

Do not add a configurable validation-policy engine in this milestone.

## 20. CLI

### Index

```text
s1atlas index
s1atlas index --force
```

`index`:

1. resolves current environment;
2. resolves and freshly verifies Schedule I authority through the mandatory three-step sequence;
3. indexes Schedule I when needed;
4. indexes installed S1API/S1MAPI when present;
5. indexes already-cached Release/Preview snapshots when needed;
6. performs no GitHub traffic unless explicitly synced or auto-check was enabled.

`--force` rebuilds the same trusted inputs; it does not bypass authority checks.

### Search and Symbol Commands

```text
s1atlas search <query> [--codebase ...] [--channel ...] [--json]
s1atlas type <symbol> [--codebase ...] [--channel ...] [--json]
s1atlas method <symbol> [--codebase ...] [--channel ...] [--json]
s1atlas source <symbol> [--codebase ...] [--channel ...] [--json]
```

Search ranking stays simple: exact name, qualified segment, prefix, substring, canonical signature, namespace.

Ambiguous symbol names return candidates rather than guessing.

### Relationship Commands

```text
s1atlas refs <symbol> [--codebase ...] [--channel ...] [--json]
s1atlas callers <symbol> [--codebase ...] [--channel ...] [--json]
s1atlas callees <symbol> [--codebase ...] [--channel ...] [--json]
```

The same codebase/channel disambiguation applies to relationship queries.

### Upstream

```text
s1atlas upstream status
s1atlas upstream sync
s1atlas upstream sync s1api
s1atlas upstream sync s1mapi
```

## 21. Missing Recovered Behavior

Cpp2IL/ILSpy may not recover useful method bodies for every method.

If a body is not useful:

```text
declaration remains searchable
metadata relationships remain available
behavioral relationships may be absent
method-body fingerprint may be absent
```

Atlas reports coverage rather than inventing caller/callee edges.

## 22. Failure Behavior

```text
preferred extraction integrity failure
  -> Schedule I reindex stops; prior complete index remains historical, not current

ILSpy required-source failure
  -> new index fails; prior completed index stays usable where still authoritative

GitHub unavailable
  -> cached/local queries continue; upstream status reports stale/unavailable

upstream unresolved dependency
  -> unresolved relationship retained; snapshot need not fail solely for that

SQLite failure
  -> no completed index exposed
```

Normal human output contains no raw stack traces.

## 23. Privacy and Repository Hygiene

Schedule I reconstructed assemblies and generated C# remain local and must never enter Git or CI artifacts.

Installed API decompilation and upstream runtime caches are also runtime data, not vendored project source.

Extend the existing hygiene gate only for the new runtime path/file patterns actually introduced.

## 24. Testing

### Unit

```text
channel/codebase validity
canonical type/signature renderer
symbol IDs
fingerprints
search ranking
relationship resolution
Installed/Release/Preview isolation
environment grouping
upstream freshness logic
path safety
```

### Integration

Use source-built fixture assemblies and authored C# source snapshots to verify:

```text
ILSpy adapter
Roslyn source frontend
shared canonical renderer
source generation
SQLite migration 6/persistence
query CLI
relationship indexing
upstream cache seams
no-build/no-execution upstream ingestion
failed index preserves completed index
```

CI remains network-free and uses no Schedule I files.

### Real Windows Smoke

```text
index preferred verified Schedule I extraction
index installed S1API/S1MAPI when present
sync/index Release and Preview snapshots
run representative queries
verify source locations
measure method-body/caller coverage
measure normalized fingerprint stability across existing divergent same-recipe outputs
prove Release/Preview cannot masquerade as Installed
prove game files remain untouched
prove no generated/proprietary output is tracked
```

## 25. Implementation Sequence

This is one milestone with five internal implementation phases.

### Phase 1 — Authority + ILSpy Capability

```text
central preferred-verified extraction resolver
pinned ICSharpCode.Decompiler
safe generated-source layout
fixture tests
real decompilation capability smoke
method-body recovery measurement
fingerprint stability measurement on existing divergent outputs
```

### Phase 2 — Symbols + Persistence

```text
migration 6
code snapshots/index runs
shared canonical type/signature renderer
symbols/parameters
source files/locations
fingerprints
completed-index lifecycle
```

### Phase 3 — Relationships + Query CLI

```text
inheritance/interfaces
type references
calls/constructs/field reads/writes
search/type/method/source/refs/callers/callees
human + JSON
```

### Phase 4 — S1API/S1MAPI Upstream Intelligence

```text
installed API indexing and environment grouping
installed artifact roots
GitHub commit cache
Release/Preview separation
conservative installed-source matching
upstream status/sync
manual network default
optional on-use 24h auto-check
```

### Phase 5 — Real Smoke + Hardening

```text
real Schedule I/API indexing
real upstream indexing
representative query proof
coverage/fingerprint report
privacy/hygiene
final docs/QA
```

## 26. Definition of Done

```text
[ ] Schedule I authority always uses preferred pointer -> validated row -> fresh integrity proof
[ ] ProcessCompleted/failed/retained/non-authoritative output cannot feed the game index
[ ] pinned ILSpy produces useful source from the real preferred extraction
[ ] installed S1API/S1MAPI binaries indexed when present
[ ] installed snapshots share environment identity for cross-codebase binding
[ ] ScheduleI channel is Installed-only
[ ] assemblies/namespaces/types/methods/fields/properties/events/parameters searchable
[ ] shared canonical renderer used by ILSpy and Roslyn frontends
[ ] unresolved source-only identities do not claim false cross-channel equivalence
[ ] source locations resolve
[ ] practical inheritance/type/caller/callee/field relationships indexed without guessing
[ ] refs/callers/callees accept codebase/channel disambiguation
[ ] raw artifact hashes remain exact authority
[ ] normalized fingerprint stability measured across existing divergent real outputs
[ ] Release and Preview remain distinct from Installed
[ ] GitHub sync is explicit by default
[ ] optional auto-check runs only during use, never in background
[ ] GitHub failure does not break cached/local queries
[ ] failed indexing does not replace a completed index
[ ] migrations 1-5 unchanged; migration 6 only
[ ] real smoke documents recovered-body/relationship limitations
[ ] game files remain read-only
[ ] no proprietary/generated Schedule I output enters Git/CI
[ ] build/tests/format/hygiene pass
```

## 27. Hard Invariants

```text
An ExtractionAttemptStatus.ProcessCompleted candidate cannot feed the symbol index.
A preferred-extraction pointer is not authority until the validated row and integrity proof succeed.
A non-preferred extraction cannot silently become current indexed Schedule I truth.
A failed index is never queryable as complete.
Schedule I has no Release/Preview channel.
Preview/Release cannot masquerade as Installed.
Installed API binary facts beat mismatched/unverified source enrichment.
Installed cross-codebase binding requires the same environment snapshot identity.
Both frontends use one canonical-signature renderer.
Unresolved source semantics remain unresolved rather than guessed.
Raw hashes remain authoritative; normalized fingerprints are comparison signals only.
Upstream repositories are never automatically built or executed.
Automatic GitHub checking is opt-in and never background polling.
Normal queries default to Installed truth.
Schedule I files remain read-only.
```

## 28. Follow-On Work

After this milestone, the same query/index layer can support:

```text
build/API diffing
static HTML portal
plain-English and C# learning context
API update-impact views
read-only MCP
S1Atlas agent skill
```

Those later features should consume this shared index rather than inventing separate facts.
