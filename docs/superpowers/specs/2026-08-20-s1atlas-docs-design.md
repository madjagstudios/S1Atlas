# S1Atlas.Docs static human portal design

**Status:** Approved design; implementation tracked by AT-1.

## Goal

Add a `S1Atlas.Docs` project and the `docs generate [--build <id>] [--output <dir>]`
CLI command. The command creates a self-contained, offline static HTML portal over
the trusted S1Atlas read model. The portal is for human exploration of Schedule I,
S1API, and S1MAPI code indexes, with evidence and provenance visible at every level.

The portal is generated from a model first and rendered second. Renderers do not
open SQLite or inspect Atlas-owned files directly. They consume read-only query
services and an immutable, canonically ordered portal model.

## Settled V1 decisions

- Generate pre-rendered HTML/CSS pages for all navigation and detail surfaces.
- Use only a small vanilla JavaScript search script. No JavaScript application
  framework is allowed.
- Schedule I Installed is selected only through the existing
  `InstalledBuildAuthorityResolver` path. A page is navigable only when the
  preferred extraction, integrity check, completed index, and build association all
  resolve successfully.
- S1API and S1MAPI use the latest completed index for each available
  `(codebase, channel)`. They are not gated by the Schedule I preferred-extraction
  authority because their source identity is a cached upstream commit, not a game
  build extraction. Their provenance is visibly different.
- `--build <id>` applies only to Schedule I Installed. Omitted `--build` resolves the
  current Schedule I build. API selection is always latest-completed and independent
  of the game build pin.
- The generated code tree contains one resolved Schedule I code snapshot per site.
  Multi-build code browsing is out of scope; cross-build information lives under
  build and symbol history pages.
- Scene HTML is deferred from V1. Reserve
  `code/schedule-i/installed/scenes/` but generate no files there. The Schedule I
  landing page must state that scenes, prefabs, GameObjects, and components remain
  available through CLI and MCP and that static scene pages are post-V1.
- Build history shows every known Schedule I build with an explicit status. Only
  resolved builds link to code and diff pages. Historical failures are visible,
  labeled, and non-navigable.
- Build diffs are generated only for adjacent resolved builds in chronological order,
  not for every pair. Arbitrary-pair comparison remains a CLI feature.
- The default output is `./s1atlas-docs/`, relative to the invocation directory.
  `--output` overrides it. The output is outside the Atlas data root and
  `s1atlas-docs/` is ignored by Git.

## Command behavior

The CLI adds a `docs` command with a `generate` subcommand:

```text
s1atlas docs generate [--build <id>] [--output <dir>]
```

The command uses the same Atlas data-root resolution as the existing CLI. It builds
the shared `ReadOnlyAtlasComposition` once and passes its `AtlasReadOnlyServices`
through the generation pipeline. It does not create or migrate the database and
does not write anywhere under the Atlas data root.

For Schedule I, an omitted or explicit requested/current build that cannot resolve
through `InstalledBuildAuthorityResolver` is a generation error and produces no
misleading partial site. For historical builds, authority failure is represented
in the build-history model and the entry is not navigable. S1API/S1MAPI absence of
a completed index is a visible, nonfatal “not indexed” state.

The command reports the resolved site output directory and the selected identifiers
without embedding a wall-clock generation time in the site.

## Authority and provenance model

The portal has two deliberately different trust surfaces.

### Schedule I Installed

The generator resolves the requested/current build through
`InstalledBuildAuthorityResolver.ResolveAsync`. It renders Schedule I symbols,
source, relationships, history, and diffs only from `InstalledBuildAuthority` rows
with status `Resolved`. The page provenance includes:

- game build ID;
- preferred validated extraction ID;
- completed index ID;
- code snapshot/source identity where useful; and
- the integrity-verified authority statement.

No Phase 3 candidate, retained failure output, unchecked source file, failed index,
or unverified database row can become Schedule I page content.

### S1API/S1MAPI

For each supported API channel with a completed run, the generator resolves the
latest completed index using the existing query layer. It shows:

```text
latest completed index — <codebase>/<channel> @ commit <sha>, index <index-id>
```

The exact `sourceIdentity` is also retained in the portal model. The API chrome is
visibly distinct from the Schedule I “integrity-verified extraction” chrome. The
integrity basis shown for API content is the cached upstream commit SHA plus index
ID; it must never imply a Schedule I game-build verification.

API commit history and per-commit symbol history are out of V1 scope. There are no
API pin flags in V1.

## Query-service seams

Existing selector-oriented `IndexQueryService` methods are insufficient for a full
static site. Add first-class, tested bulk read methods to the shared read/query
layer rather than adding SQL to `S1Atlas.Docs`:

- list namespaces for a resolved index, with deterministic ordering;
- list all symbols for a resolved index through bounded pages, with stable page
  boundaries and a true total count;
- return portal-shaped Schedule I build history with per-build indexed/verified
  status and navigability;
- return cross-build occurrences of a canonical Schedule I symbol for
  `history/schedule-i/symbols/…`;
- expose the source locations/files and relationship totals needed to distinguish
  measured zero from unavailable or truncated results.

The exact repository additions may use offset/limit or an equivalent continuation
contract, but the public query result must make total count and page coverage
explicit. Stable ordering is by ordinal qualified name, ordinal signature, symbol
kind, and symbol ID (or the corresponding documented tuple for each query).
The portal calls these services through the existing composition; it does not create
a second SQLite composition.

`BuildDiffService` remains the source of truth for classifications and diff counts.
The portal supplies only adjacent, authority-resolved index IDs and renders its
returned model; it does not reimplement diff classification.

## Site layout and URL rules

The generated site has this shape:

```text
index.html
search.html
builds/index.html
builds/<build-id>.html
history/schedule-i/symbols/<canonical-slug>-<hash>.html
diffs/<older-build>--<newer-build>.html
environment/<build-id>.html
code/<codebase>/<channel>/index.html
code/<codebase>/<channel>/namespaces/<namespace-slug>-<hash>.html
code/<codebase>/<channel>/symbols/<symbol-slug>-<hash>.html
assets/site.css
assets/search.js
assets/search-index.json
```

The reserved scene location is not populated:

```text
code/schedule-i/installed/scenes/
```

All hrefs are relative. No root-absolute links are emitted, so the same output can
be opened from `file://` or served below another URL prefix.

### Filesystem-safe slugs

Every namespace, canonical key, and symbol path segment uses the same deterministic
scheme:

1. take the exact key as the hash input, encoded as UTF-8;
2. create a human-readable projection by Unicode-normalizing, lowercasing with
   invariant rules, replacing all non-ASCII letters/digits with `-`, collapsing
   repeated `-`, trimming, and applying a fixed length cap;
3. append `-` plus the first twelve lowercase hexadecimal characters of SHA-256 of
   the exact key; and
4. use a safe fallback when the readable projection is empty or is a Windows device
   name.

The hash suffix defeats case-insensitive collisions and differentiates members
whose punctuation or casing would otherwise sanitize to the same name. The exact
canonical key remains visible in the page and in the search index. Slug generation
has no dependence on the output directory or host filesystem.

Diff filenames use the older and newer 64-character build IDs in chronological
order. The pair is canonicalized so one adjacent pair maps to one file.

## Generated pages

- `index.html`: selected Schedule I build, API index summaries, navigation, trust
  model explanation, and links to history/environment.
- `search.html`: an offline search form and deterministic prebuilt symbol index.
- `code/<codebase>/<channel>/index.html`: codebase/channel provenance, namespace
  tree, totals, and explicit missing-index state where applicable.
- namespace pages: namespace/type/member navigation and true totals.
- symbol pages: signature, exact key, containing links, inheritance, references,
  callers, callees, source, history where supported, deterministic overview,
  modding relevance signals, C# learning context, and provenance.
- `builds/index.html`: all known game builds with status `indexed + verified`,
  `not indexed`, or `integrity-failed`. Only verified rows link to navigable pages.
- `builds/<build-id>.html`: build provenance, linked code surface, environment,
  adjacent diff links, and the explicit deferred-scene note.
- `history/schedule-i/symbols/...`: canonical-key history across resolved
  Schedule I builds, with measured occurrence/missing status per build.
- `diffs/<older>--<newer>.html`: `BuildDiffService` output for adjacent verified
  builds, counts, classifications, and links to affected symbols.
- `environment/<build-id>.html`: recorded installation, game/build identifiers,
  dependency versions, and their FACT provenance.

There are no static API build-history pages. API pages show their independent
latest-completed commit/index provenance instead.

## Deterministic human context

The portal emits no general-purpose AI explanation and no interpretation in V1.
Every rendered claim is visibly labeled `FACT` or `DERIVED`. `INTERPRETATION` is a
reserved renderer/provenance label but produces no V1 content.

### FACT

FACT cards render direct indexed evidence: kind, exact canonical key, qualified name,
signature, source location, source hash/size/provenance, relationship endpoint and
evidence, index/build/commit IDs, measured totals, and explicit unavailable states.

### DERIVED overview and modder relevance

Overview templates use only indexed symbol shape, containment, relationship kinds,
and measured totals. They say what the indexed record contains, not what the game
must do at runtime. Modding context is a list of deterministic relevance signals:
callers/callees, inheritance/interfaces, type references, construction, field or
property access, and source availability, each linked to supporting FACT cards.

Every derived count cites the true total, not only the rendered page. Bounded lists
state the coverage, such as “showing N of M.” A measured zero is rendered explicitly
and is distinct from an unavailable index, unavailable source, unresolved endpoint,
or truncated page.

All generated prose uses ordinal stable ordering, invariant-culture number
formatting, one consistent numeral style with a small fixed spell-out threshold,
deterministic pluralization, and deterministic deduplication.

### Roslyn learning context

Learning concepts are detected with Roslyn syntax analysis over the exact decompiled
source span shown on the page, reusing/factoring the existing
`RoslynSourceIndexer` parsing path. Substring and regex searches are not accepted.
The output names syntactic properties, for example:

- inheritance and interfaces from indexed relationships;
- generic syntax from the parsed syntax tree;
- property, event, constructor, static, and instance member syntax;
- object creation and invocation syntax;
- null-conditional and null-coalescing operators;
- lambda, delegate/event syntax, and LINQ query expressions when their syntax nodes
  are present in the shown span.

Concepts are emitted only on detection. V1 never claims that a construct is absent,
because decompiler lowering can hide source constructs. Every learning statement is
`DERIVED` and links to the exact source span FACT evidence.

## Source and completeness states

Source is read only through the integrity-checking query path. If the recorded source
file fails its SHA-256 check, the symbol page shows the explicit FACT state:

```text
source unavailable (integrity)
```

Missing source locations, unavailable source files, source-integrity failure,
measured zero relationships, unresolved relationship targets, and no completed index
are separate states. No failed or missing state is silently omitted or converted to
an interpretation.

## Build history and adjacent diffs

The build-history query includes all known builds. A build can be visible without
being navigable:

- `indexed + verified`: authority resolves and a completed matching index exists;
- `not indexed`: no completed matching Schedule I Installed index is available;
- `integrity-failed`: a preferred extraction or authority integrity check fails.

For diff generation, keep only builds with resolved authority. Sort that subsequence
by the stable recorded build first-seen timestamp, using extraction creation time and
then build ID as deterministic tie-breakers. Generate one older-to-newer diff for
each consecutive pair. Skip non-resolved builds without diffing across them. With
fewer than two resolved builds, render “no diffs available yet” as an explicit
non-error state.

## Search index and determinism

The canonical search index contains one entry per rendered symbol with exact key,
display name, kind, codebase, channel, provenance identifiers, and relative page
href. Entries are sorted by codebase, channel, qualified name, signature, kind,
symbol ID, and href using ordinal comparison. JSON uses a fixed property order,
stable indentation/escaping policy, and LF newlines.

To keep local `file://` use reliable, the search page may consume a deterministic
inline copy of the same sorted index rather than requiring a fetch. The emitted
`assets/search-index.json` remains the canonical auditable artifact. If the index
needs chunking for size, chunks use fixed entry-count boundaries from the same global
sort and a stable manifest; there is no data-dependent or dictionary-insertion-order
partitioning and no silent result cap.

All text files are UTF-8 with LF line endings regardless of host platform. HTML
escaping, JSON escaping, CSS, JavaScript, CSS/JS asset order, page order, and output
file order are fixed. Generated pages do not include wall-clock timestamps, random
IDs, absolute paths, or machine-specific values. The same selected identifiers and
same source data therefore produce byte-identical output regardless of output
directory.

## Pluggable sections and future scene seam

The generator separates page shells from page sections. Sections receive the portal
model and a link resolver and return deterministic HTML fragments plus any required
asset declarations. V1 registers code navigation, provenance, source, relationships,
history, diffs, environment, deterministic overview, relevance, and learning
sections. The scene section interface is reserved but not registered; no scene,
GameObject, prefab, or component HTML is generated in V1.

## Testing strategy

Tests use generated or repository-owned fixtures only. They do not access proprietary
game bytes or the network. Coverage includes:

- bulk namespace/symbol/build-history/symbol-history query ordering, paging, true
  totals, and authority statuses;
- slug collision resistance for punctuation, casing, generics, Windows-reserved
  names, and long keys;
- authority enforcement: Schedule I never renders an unverified or failed source;
- API latest-completed selection and independent provenance;
- `--build` affecting only Schedule I;
- explicit zero, unavailable, integrity-failed, and “not indexed” states;
- Roslyn syntax concept detection only when a syntax node is present;
- FACT/DERIVED labels and evidence links on every generated claim;
- relative links, reserved scene path/no scene files, adjacent diff selection, and
  the no-diff state;
- generated HTML structure and content, not pixel output;
- two generations from the same fixture producing byte-identical directory trees;
- CLI default/override output paths and LF normalization.

The completion gate is always run in full:

```text
dotnet format S1Atlas.sln --verify-no-changes --no-restore
dotnet build S1Atlas.sln --configuration Release
dotnet test S1Atlas.sln --configuration Release --no-build
```

## Non-goals

- No embedded AI runtime, network call, or new indexing pipeline dependency.
- No JavaScript application framework or runtime server.
- No static scene pages in V1.
- No API commit-history browsing or API snapshot pin flags.
- No arbitrary-pair static diff pages.
- No multi-build code tree in one generated site.
- No writes into the Atlas data root or mutation of indexed data.
