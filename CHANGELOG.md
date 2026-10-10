# Changelog

All notable changes to S1Atlas are documented here. The format is loosely based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). S1Atlas ships on a rolling
`main`; dated entries mark notable milestones rather than formal released packages.

## [2.1.0] - 2026-10-10: ScheduleOne.Core coverage, manual Harmony patches, and callable fixes

The game index now covers the game's second assembly, `ScheduleOne.Core`, so item
definitions, item instances and avatar types can be searched, diffed and checked,
and `check-mod` no longer reports a mod's references into them as missing. `check-mod`
also follows the manual `harmony.Patch(...)` registrations real mods use, so far more
patch targets are tracked across a game update. `callable` accepts the selectors
people actually type, such as `HUD.topScreenText`, says "unknown" when no interop
assembly was indexed instead of a false "unavailable", and no longer labels public
game members as unavailable. `setup` now takes a game update all the way to ready in
one run. Existing atlases need the one-time upgrade below.

### Upgrading

- Run `s1atlas doctor` first, then run the command it prints (`s1atlas status`)
  to migrate the database to schema 19 (AT-134). The pre-migration backup is a
  full copy of `atlas.db`, so make sure you have that much free disk space. Until
  the upgrade runs, serve and the MCP server refuse queries with the
  `s1atlas status` hint.
- Then run `s1atlas index` once. The code index format changed (AT-134, AT-135),
  so the current build is re-indexed instead of reused and callable evidence is
  rebuilt. API indexes rebuild the next time they run, and reference collections
  rebuild on the next `reference index` (AT-122, AT-136).
- To compare against an older build with `check-mod` or `diff`, re-index that
  build too: `s1atlas index --build <build-id>` (AT-135).

### Added

- `s1atlas index --build <build-id>` indexes the code of an earlier build that
  still has a preferred, verified extraction. Without `--build` the current build
  is indexed, as before (AT-135).

### Fixed

- Reference-mod edges whose target matches more than one symbol now stay
  unresolved instead of being credited to whichever symbol loaded first (AT-122).
- `setup` now takes a game update all the way to ready in one run (AT-138).
  Before, a stale build scan planned only the rescan, so setup stopped with
  `Next: s1atlas extract` and had to be run again. When the scan is stale, the
  plan now also lists extract and index (and the scene snapshot with
  `--include-optional`), marked "if the scan records a new build", and skips
  them if the rescan keeps the same build.
- Callable human output follows availability status instead of printing a
  misleading `Interop: unavailable` for every missing signature. Public game
  members that need no wrapper show `Interop: not needed (public game member)`
  (AT-134).
- Callable queries accept dotted `Type.member` selectors, including
  `HUD.topScreenText`, without requiring a member-kind filter (AT-134).
- Missing interop input and legacy indexes without retained callable mappings
  report availability as unknown instead of unavailable (AT-134).
- The game index now covers every game-owned assembly that Cpp2IL reconstructs,
  not only `Assembly-CSharp`. Types in `ScheduleOne.Core` (item definitions, item
  instances, avatar types) can be searched, resolved and diffed, and `check-mod`
  resolves mod references into them instead of reporting `target-type-not-found`
  (AT-135).
- Each game assembly is matched to its own Il2CppInterop assembly, for example
  `Il2CppScheduleOne.Core.dll` beside `Assembly-CSharp.dll` in
  `MelonLoader\Il2CppAssemblies`, so the callable surface covers those types too
  (AT-135).
- Scene and prefab components whose script lives in a `ScheduleOne.*` assembly now
  resolve to their indexed type (AT-135).
- Referenced assemblies are released right after decompiling, so indexing no
  longer holds a lock on the extraction folder (AT-135).
- `check-mod` and the reference patch index now resolve more constant manual
  `harmony.Patch(...)` registrations (AT-136).
  Before, a target stored in a local and checked for `null`, a
  `PropertySetter`/`PropertyGetter` target, or a target passed through a helper
  method was reported as `unrecognized-manual-shape` or dropped, so an update
  check could miss broken patch targets. Constant `typeof`, `const` and `nameof`
  arguments are now followed into one level of helper, including a static,
  parameterless helper returning a constant method lookup. Simple `?? throw`
  guards retain the lookup on the surviving path. Unresolved call sites are
  reported at the caller, and patches whose patch method cannot be identified are
  kept. Existing reference collections are rebuilt on the next `reference index`.

## [2.0.0] - 2026-10-04: Local web app, Harmony patch index, and mod checks

S1Atlas now runs as a local web app as well as a CLI and MCP server: `s1atlas serve`
opens a loopback-only, read-only browser view for searching symbols, reading
members, and diffing builds. It also indexes which reference mods patch which game
methods (`patched-by` / `find_patches`), follows virtual and interface dispatch
when answering "who calls this", credits compiler-generated code to the method you
wrote, and adds `check-mod`, which lists the game symbols your mod depends on and
what a game update changed or removed. Guided `doctor` and `setup` commands and
installable `s1atlas` / `s1atlas-mcp` dotnet tools make first runs easier. The CLI
and MCP surfaces were renamed and consolidated with no deprecated aliases, hence
the major version, and existing atlases need the one-time upgrade below.

### Upgrading

- Run `s1atlas doctor` first, then run the command it prints
  (`s1atlas status`) to migrate a behind database in place.
- Expect about 1-2 minutes on a large atlas (~720k symbols).
- The pre-migration backup is a full copy, as large as `atlas.db` itself
  (about 8 GB on a large atlas), so make sure you have that much free disk
  space first. It is written to the `backups` directory before any
  migration runs.
- Until the upgrade runs, serve and the MCP server refuse to query the
  database: every page and API route answers 503, every MCP tool answers
  `atlas_unavailable`, each with the `s1atlas status` hint. The hosts
  recover without a restart once the upgrade lands.

### Breaking

- **Breaking: CLI** (AT-86): command and option names are kebab-case with no
  deprecated aliases; the old spellings fail with the normal
  unknown-command/unknown-option errors. Renames, each with its `--json`
  envelope `command` value following the command:
  `investigate_seam` to `investigate-seam`, `gameobject` to `game-object`,
  `callsites` to `call-sites`, `fieldrefs` to `field-refs`,
  `recover-native-body --build-id` to `--build`,
  `recover-native-body --traversal-budget` to `--native-traversal-budget`,
  `source --limit` to `--candidate-limit` (it caps resolution candidates,
  not result rows), and the `upstream sync <codebase>` positional to the
  `--codebase` option both upstream subcommands share. An invalid
  `--codebase` now fails with `InvalidCodebase` on `upstream status`,
  `upstream sync`, and `diff` instead of the `OperationalFailure` crash
  shape. All other `--json` error codes are unchanged. Every command and option has a
  description, every leaf `--help` shows an example, and required/dependent
  option rules are enforced at parse time with the same human/`--json`
  triples.

### Added

- **Mod update checks** (AT-89): `s1atlas check-mod <path-to-mod.dll>` checks
  direct references, Harmony patch targets, and constant reflection lookups
  against completed game indexes without changing the mod or atlas. The report
  identifies removed, signature-changed, moved, unchanged, and unresolved
  dependencies, flags broken patch targets, and compares stored body fingerprints.
  Build prefixes, single-build mode, and `--json` are supported. Breaking
  dependencies exit 3; cancellation remains 2. CLI exit codes are documented
  in USAGE.

- **Shell completion and scoped type/method queries** (AT-86):
  `s1atlas completion <pwsh|bash|zsh>` prints a self-contained completion
  script that completes through `s1atlas` itself with no extra tooling.
  `type` and `method` accept `--scope`/`--collection` for reference and
  cross-origin queries like the other relationship commands (`callable`
  stays installed-Schedule-I-only: its service cannot scope). `type` and
  `method` also gained `--include-generated`. The USAGE command reference
  table is generated from the real command tree with a drift test.

- **Guided setup, readiness doctor, and next-step hints** (AT-87):
  `s1atlas setup` plans the missing scan, tools-install, extract, and index
  steps from one shared readiness report, prints the plan, and asks before
  changing anything, with a second confirmation for network tool downloads
  (`--yes` answers both; `--include-optional` adds the scene snapshot). Each
  step re-checks readiness so newly satisfied work is skipped, and the run
  stops at the first failure with the next step. `s1atlas doctor` renders the
  same checklist read-only (exit `0` only when ready, `--json` supported),
  `status` appends the Ready-or-Next summary, and setup errors on the CLI,
  MCP, and serve envelopes carry a `hint` field with the exact fix command.

- **Selector UX: candidate tables, near matches, short IDs** (AT-78): every
  symbol-taking command and MCP tool resolves the same selector forms (full
  ID, unique 8-63 hex short-ID prefix of either case, canonical key,
  signature, qualified name, ranked fuzzy text). Ambiguous selectors render a
  numbered candidate table capped at 10 human rows with the exact total and
  the narrowest working hint, and fail with `AmbiguousSymbol`; unknown
  selectors print `Found 0 matches.` with up to 5 `Nearest matches` rows and
  a spelling/`search` hint, and fail with `SymbolNotFound`. Human tables show
  12-character short IDs while `--json` keeps full IDs and adds `shortId`.
  Build, extraction, attempt, and native symbol IDs accept unique short-ID
  prefixes on `--build`/`--build-id`, `diff`, `extractions show`, and
  `recover-native-body --symbol-id`, with ambiguous prefixes failing over
  labeled short IDs. MCP `not_found` envelopes carry `suggestions` and
  `ambiguous` envelopes carry `totalCandidateCount`. Scene, GameObject,
  component, and asset ambiguity renders numbered candidate rows, and all
  symbol query arguments share one help text naming the accepted forms.

- **Delegate-creation and address-taken relationships** (AT-67): indexing
  now distinguishes the `ldftn`/`ldvirtftn` opcodes as a new
  `ReferencesMethod` edge kind and `ldflda`/`ldsflda` as a new
  `TakesFieldAddress` edge kind, so `callers`/`callees` no longer mislabel
  delegate creation as calls and `fieldrefs` shows address-taken sites in
  both readers and writers. `callers` and `callees` exclude delegate
  references by default; `--include-delegates` (CLI), `includeDelegates`
  (MCP caller and callee tools), and `?delegates=1` (serve page and
  callers/callees APIs) include them labeled `delegate created (not
  called)`. Address-taken sites are labeled `possible write (address
  taken)` in writers and `possible read (address taken)` in readers.
  `ldtoken` of a method or field records a metadata reference, never a
  call or read; `constrained.` + `callvirt` stays a virtual call; `calli`
  records nothing. Compiler-generated-body credit and virtual-dispatch
  expansion are unchanged. Indexes rebuild (schema v14).

- **Compiler-generated bodies credited to declaring methods** (AT-66):
  calls and field accesses inside lambdas, async/iterator state machines,
  and local functions are now credited to the method that declares them,
  labeled with an `in ...` detail (`in lambda`, `in async state machine`,
  `in iterator state machine`, `in local function {Name}`, nested bodies
  joined outermost-first). `callees` includes calls made from generated
  bodies; a single resolver maps bodies attributes-first with naming plus
  `newobj` confirmation, and unmapped bodies stay on the generated member
  with an `unmapped: {reason}` detail rather than a guess.
  Attribute-mapped async/iterator overloads resolve, while `<>c` lambda
  overloads stay unmapped with a reason when the declaring overload is
  ambiguous. `--include-generated` (CLI
  `callers`/`callees`/`refs`/`fieldrefs`/`search`), `includeGenerated`
  (MCP relationship and search tools), and `?generated=1` (serve pages
  and relationship/search APIs) show the raw generated rows instead.
  Symbol search hides generated members by default with an `<n> generated
  result(s) hidden` notice, and `fieldrefs` excludes compiler-captured
  fields unless asked. The credit is stored at index time (mapping plus
  pre-rendered detail on the edge, flag on the symbol) so queries stay
  declarative and search totals stay exact in SQL; compiler attributes
  exist only at index time. Indexes rebuild (schema v13).

- **Dispatch-aware callers with routes and `--exact`** (AT-65): `callers`
  now returns exact (statically bound) callers plus may-dispatch callers —
  call sites targeting an overridden base slot or interface method the
  selected method fills — labeled `DERIVED` with the route taken (immediate
  slot first, full multi-hop chain), exact/derived/combined split totals,
  and FACT-first paging. Indexing distinguishes the `callvirt` opcode as a
  new `CallsVirtual` edge kind so only virtual sites feed the expansion and
  non-virtual base calls stay exact-only. `--exact` (CLI), `exact: true`
  (MCP `find_callers`/`find_api_callers`, with a `dispatch-expansion`
  provenance entry on expanded results), and `?exact=1` (serve page and
  `/api/symbol/<id>/callers`) preserve the previous exact-only behavior;
  ownership-seam investigation keeps exact callers. The parity harness now
  requires the `DERIVED` label on dispatch rows (all 12 virtual/interface
  rows found) and cross-checks callee-side dispatch rows against expanded
  callers. Indexes rebuild (schema v12).

- **Override/implementation graph with `overrides`, `overridden-by`, and
  `derived` queries** (AT-65): indexing records `Overrides` (method to the
  base-class slot it fills, immediate slot only) and `ImplementsMethod`
  (method to the interface method it implements, implicit or explicit)
  metadata edges, so `new`/newslot hides record nothing while virtual,
  abstract, generic-base, and accessor overrides resolve to the open
  definition. Three queries walk the graph: `overrides` lists the slots a
  method fills up to the root, `overridden-by` lists transitive overriders
  and implementations with a depth limit, and `derived` lists transitive
  subclasses and implementers with paging and true totals. All three ship in
  the CLI, as MCP tools (`find_overrides`, `find_overriders`,
  `find_derived_types`), and as symbol-page sections in `serve`. The parity
  harness now compares overriders and implementers against the oracle (all
  found); dispatch-aware callers remain a follow-up. Indexes rebuild (schema
  v11).

- **Indexed, ranked search for `s1atlas serve` plus serve performance targets**
  (AT-88): schema migration v16 adds a trigram FTS5 index over symbol names
  and signatures (kept in sync by triggers, with a prefix index for 1-2
  character queries), and serve search now ranks exact simple-name matches
  first, then name prefixes, then substring matches by relevance. On an atlas
  that has not been migrated yet, serve falls back to the previous unranked
  search with a visible note instead of migrating. Serve also rejects `/diff`
  pages beyond the last page and caches the last 8 diff results in memory.
  CLI and MCP search behaviour is unchanged. An opt-in local-game test
  measures serve cold start, search latency, and diff page times against the
  acceptance targets (start under 3 s, search p95 under 200 ms).

- **`s1atlas serve` parity views: builds, environment, diffs, navigation**
  (AT-88): the local web app gains `/builds` and `/builds/<id>` (status
  labels, per-codebase counts linking into build-scoped search, adjacent
  diffs, current-build environment link), `/environment` (current snapshot
  with the install root redacted), and `/diff` (any two builds, with
  classification counts, signature before/after, true totals, unbounded
  paging, and a build picker), each with a mirrored `/api/*` endpoint that
  returns the same tool envelopes as the matching MCP tools. Search accepts
  `?build=` to scope game results to one build. Every page carries header
  navigation, symbol pages carry breadcrumbs, coverage sentences read
  grammatically, and every response carries `Content-Security-Policy`,
  `X-Content-Type-Options`, and `Referrer-Policy` headers. The install-path
  display helper moved to `S1Atlas.Core` for reuse by both the portal and
  serve. Web tests cover the new views, MCP parity, a link-reachability
  crawl from `/`, header assertions on HTML/JSON/404/421 responses, and a
  path-leak sweep over every view and endpoint.
- **Loopback-only read-only `s1atlas serve` web app** (AT-88): a new `serve`
  command starts a local web app (default port 5217, `--port 0` for an
  ephemeral port, `--open` to launch the browser) with landing, search, and
  symbol pages plus mirrored `/api/*` endpoints that return the same tool
  envelopes and JSON shapes as the matching MCP tools. The host binds
  loopback only, answers only loopback hosts on its own port, accepts only
  `GET`/`HEAD`, sends no CORS headers, and never writes the Atlas. A new web
  test project covers the host guards, views, MCP parity, and read-only
  behavior against a synthetic seeded atlas, and CI runs it in the test
  matrix with a serve smoke on the installed tool.
- **Installable `s1atlas` and `s1atlas-mcp` dotnet tools** (AT-77):
  both commands now pack as .NET tools with one shared version taken from
  `Directory.Build.props` (`--version` on either prints it, and the MCP
  `initialize` server info carries the same version). Install from locally
  packed packages to any tool-path, put the tools on `PATH`, and run `s1atlas`
  directly; `s1atlas-mcp` serves stdio with no arguments (`mcp serve` stays as
  an alias). Atlas configuration ships inside the CLI package. The usage
  guide, README, agent skill, and reference now document the installed
  commands, with per-host MCP registration snippets, and the checks CI job
  smoke-tests pack, install, and run.
- **Faster CI with fail-fast static checks and parallel test runs** (AT-61):
  static checks (restore, lockfile, format, repository hygiene, public
  content) now run in a `checks` job before and beside the tests, the eight
  test projects run in parallel across matrix runners with per-project
  timing summaries, NuGet packages are cached, and a final `build-test` job
  keeps the required check name stable while superseded PR runs cancel
  automatically.
- **CI gate for public-repo content** (AT-102): a new `Public content` step
  checks PR commit messages and added diff lines for AI attribution,
  machine-specific paths, non-AT ticket keys, agent-workflow phrases,
  tracker URLs, and AI tool or vendor names, failing the job with the full
  violation list. The pull request template carries a public-repo checklist
  and the contributing guide states the rules.
- **MCP tools advertise read-only annotations and server instructions**
  (AT-80): every tool now carries `readOnlyHint`, `destructiveHint`,
  `idempotentHint`, `openWorldHint`, and a short title in `tools/list`, and the
  server exposes instructions through `initialize` covering the evidence loop,
  selector syntax, FACT vs DERIVED provenance, optional scope IDs, and the
  static-evidence boundary. `not_found`, `invalid`, and `unavailable`
  envelopes now arrive with `isError=true` (the full envelope text is
  unchanged); `resolved` and `ambiguous` stay `isError=false`. Structured
  output was measured and left off: output schemas for just two tools grew
  `tools/list` from 26,635 to 32,428 bytes (+21.8%), so enabling it for all
  tools waits for the envelope trim.

- **Local-only golden-facts suite for real-build checks** (AT-61): a new `LocalGameRequired` suite pins a few facts from your own live atlas (a serialized scene field value, an object-reference target name, a method callee set) plus value-free structural invariants, reading expected values from a gitignored `golden-facts.local.json` that the repository-hygiene gate refuses to track. Run it after each game update; see the usage guide.
- **Harmony patch-target index** (AT-70): reference indexing now records
  which reference mods patch each game method as `Patches` edges, covering
  declared `[HarmonyPatch]` attributes (matched by full type name, no
  package) and constant manual `harmony.Patch(AccessTools.Method(...))`
  calls labeled `DERIVED`. New `s1atlas patched-by` command and MCP
  `find_patches` tool answer the question per game method, including
  unresolved patches that name the method, and `investigate-seam` gains a
  `Patches` evidence section with a prior-art claim on both surfaces.
  Patch rows carry the kind (`Prefix`, `Postfix`, `Transpiler`,
  `Finalizer`) and `attribute`/`DERIVED` evidence labels. Reference
  collections indexed before this version have no patch edges; re-run
  `s1atlas reference index <manifest>` to rebuild with patches.

### Changed

- **MCP tool surface consolidated from 35 tools to 26** (AT-81): the eight
  API-only tools retire in favor of shared code tools with an optional
  `codebase` (`scheduleI`, `s1api`, `s1mapi`; `scheduleI` by default) and an
  optional `channel`
  (`Installed` by default): `search_api_symbols` becomes `search_symbols`,
  `get_api_source` becomes `get_source`, `find_api_callers` becomes
  `find_callers`, `find_api_callees` becomes `find_callees`,
  `find_api_references` becomes `find_references`,
  `find_api_related_types` becomes `find_related_types`,
  `find_api_call_sites` becomes `find_call_sites`, and
  `find_api_field_references` becomes `find_field_references`, each with
  the matching `codebase` and `channel` and identical results. `get_prefab`
  merges into `get_scene` with `kind: Prefab`; omitting `kind` resolves
  scenes. `find_derived_types` pages with `cursor` instead of `offset`.
  Fixed vocabularies (`codebase`, `scope`, `kind`, `channel`, scene kind,
  execution boundary) are advertised as schema enums with a description on
  every parameter, and the usage guide documents each value; an invalid
  enum value fails with a readable error naming the parameter and its
  allowed values, and responses report the same spellings the schemas
  advertise. Paged list and relationship tools return
  `nextCursor`/`hasMore`, with the cursor bound to the exact query and
  index: reuse with different arguments fails with `invalid_cursor`.
  Envelopes omit empty `candidates`/`suggestions`, drop provenance IDs
  that duplicate the build context, and return slimmer candidate rows.
  MCP error codes unify into ten snake_case wire codes; see the usage
  guide for the retired-tool map and the code table. Measured on the
  deterministic test atlas, `tools/list` shrinks from 24838 to 23824 bytes
  and the four probe responses shrink 22.6% in aggregate (6793 to 5258
  bytes).

### Removed

- **`docs generate` and the static portal are gone** (AT-108): `serve` covers
  everything the static portal did, plus ranked search and diffs between any
  two builds, so the deprecated `docs generate` command and the S1Atlas.Docs
  projects are removed. `s1atlas docs generate` now fails with the normal
  unknown-command error; use `s1atlas serve` instead.

### Fixed

- **Mod checks resolve generated interop member shapes** (AT-89): stored
  callable surfaces and shared projection rules resolve property accessors,
  sanitized backing fields, and array/collection signatures before comparing
  builds. The default baseline is older than the target, and schema errors
  use the shared retry or upgrade guidance.
- **Reference mod field references resolve to game symbols** (AT-89):
  qualified field identities and normalized signatures resolve field reads
  and writes, including interop references. Existing reference collections
  rebuild automatically with reference resolver v2.
- **Short-name lookup and search ignore member return types when ranking names**
  (AT-124): only types and namespaces receive the boost for qualified names
  ending in the query. A singleton getter no longer makes its type's short
  name ambiguous, and a method returning `Demo.Run` no longer outranks a
  member named `Run`. Exact-name matching keeps its existing priority.
- **Serve search matches members by name** (AT-110): stored simple names
  took the text after the last dot, so methods kept their return type and
  nested types kept their outer name. Short serve queries matched by return
  type instead of member name, and exact-name matches never ranked first.
  New rows store the declared member name, and schema migration v18 rewrites
  existing rows (with a pre-migration backup and one search-index rebuild),
  so one- and two-character serve queries match member-name prefixes and
  exact names rank first. CLI `search` and MCP results are unchanged.
- **Generated portal no longer prints absolute local paths** (AT-82):
  the environment page rendered the installation root, the GameAssembly and
  global-metadata paths, and every dependency path verbatim, leaking
  machine-specific locations into a shareable page. Paths inside the
  installation root now render relative to it with forward slashes, paths
  outside it render as their file name with an explicit marker, and the
  installation root line only records that it was recorded. Build IDs,
  hashes, Steam IDs, and versions are unchanged, as is everything stored or
  returned by the CLI and MCP.
- **Shared stdio test servers stop leaking and run faster** (AT-61, AT-101):
  every test-spawned `mcp serve` process is now tracked from spawn to exit and
  reaped on dispose and on failed connect, with a regression test proving no
  server survives a passing, failing, or failed-connect run. Read-only stdio
  tests share one seeded atlas and one server per test class instead of
  spawning a fresh server per call, cutting the MCP test project from minutes
  to well under its CI budget with no coverage change. The `checks` CI job
  now also builds the full solution in Release.
- **get_type/get_method resolve through the shared symbol resolver** (AT-79):
  both tools ran their own substring search and resolved only on a single
  total hit, so an exact name that prefixes a longer sibling came back
  ambiguous, ambiguity candidate IDs could not be fed back in, and the same
  selector could resolve in `get_source` but not in `get_type`. Both tools now
  resolve through `SymbolResolver` with the kind enforced inside the query
  (IDs, canonical keys, and exact names behave exactly like `get_source`),
  and a wrong-kind ID or key returns `not_found` naming the expected and
  actual kinds instead of a wrong-kind success. `get_method` stays Method
  only; constructors are not accepted.
- **Omitted optional MCP parameters bind as null** (AT-57): nullable tool
  parameters without a C# default were marked `required` in the generated JSON
  schema, so a `tools/call` that omitted them died in SDK argument binding with
  an opaque `An error occurred invoking '<tool>'.` before the tool body ran.
  Seventeen parameters across the six scene tools and `compare_symbol` now
  default to `null`, and a new all-tools stdio test pins every tool's required
  set so an optional parameter cannot silently become required again. Binding
  failures that still occur (wrong types, missing required arguments) now name
  the offending parameter instead of returning the opaque generic error.

- **Test suite runs safely on developer machines** (AT-62, AT-48, AT-43,
  AT-61): `LocalGameRequired` tests now run only with
  `S1ATLAS_RUN_LOCAL_GAME_TESTS=1`, and then only against a temp copy of the
  live atlas, so a default `dotnet test` never opens the developer database;
  cancelled extraction commands release `atlas.db` deterministically instead
  of leaving it locked; tool installation retries transient file moves; and
  migration tests derive the latest schema version from the migration catalog
  instead of pinning v15, leaving only the catalog test and the foundation
  integration test version-pinned.

- **Pruned tests that restated shared validation branches** (AT-61): removed nine MCP tool tests duplicating the same blank-selector and non-positive-limit branches; one test per branch still pins each behavior.
- **Reference indexing survives duplicate game signatures** (AT-70):
  building the relationship lookup crashed with a duplicate-key
  `ArgumentException` on game snapshots where two symbols share one
  signature, aborting the whole collection. The lookup now keeps the first
  loaded symbol (game symbols load ordered by canonical key, so the winner
  is deterministic) and resolution is unchanged when keys are unique.
- **Relationship reads return the generated columns** (AT-70): the
  completed-relationships reader selected the generated source and detail
  columns but dropped them when mapping rows, so `generatedDetail` always
  came back null. Both columns are now mapped like the read-only
  repository already did.
- **Serve and search handle real member names** (AT-115, AT-116):
  member identities use `Type::Member(Params):Return`, but serve assumed
  dotted names, so type pages listed no members and breadcrumbs split at the
  wrong separator. Type pages now list every member of the exact declaring
  type, member breadcrumbs render the simple name with a resolved
  declaring-type link, and an exact member-name match ranks in the top
  search tier. Name lookup still prefers `.-terminal` types and namespaces,
  then resolves a unique exact member name instead of returning an ambiguous
  list (several exact matches stay ambiguous, without substring rows),
  independent of result order on every resolution path.
- **Read-only hosts check the atlas schema before serving** (AT-117): `serve`
  and the MCP server used to query whatever schema they found, failing
  mid-request on a database migrated by neither. Both hosts now log the
  schema status at startup and check it before every request or tool call
  (a current schema is checked once; anything else is re-checked, so
  upgrading in another terminal recovers without a restart). A behind,
  ahead, unrecognized, or temporarily unreadable database answers 503 /
  `atlas_unavailable` with the shared message and fix hint instead of
  running the query; the database file is never written. A missing
  database keeps each host's existing missing-store behavior. A locked or
  otherwise unreadable database is never reported as unrecognized, so the
  hosts never advise deleting a database another command is using.
- **A cancelled diff no longer poisons serve's diff cache** (AT-114): the
  cached computation was bound to the first waiter's cancellation token, so
  cancelling one diff request cancelled the shared entry and every later
  waiter failed on it. The computation now runs on a cache-owned token
  while each waiter cancels only its own wait; entries run outside the
  cache lock exactly once, faulted or cancelled entries evict themselves
  even when no waiter observes the failure, and the bounded queue can no
  longer evict a live re-added entry through a stale slot.
- **CI and repository gates** (AT-121): pass workflow values through environment
  variables so PR branch names cannot become PowerShell commands. CI now uses a
  read-only repository token and pins each action to a release commit within its
  existing major version. The public-content check catches JSON-escaped Windows
  paths and paths with forward slashes or mixed separators. Repository hygiene
  rejects tracked binaries and Unity assets by extension, with an empty list for
  exact-path exceptions.
- **Doctor and setup use the scanned game path** (AT-120): readiness compared
  the snapshot against Steam discovery instead of the recorded install, so a
  `--game-path` scan reported stale on every run (or "moved" when Steam held
  another copy), and setup rescanned through Steam discovery, silently
  switching the current build. Doctor now locates the recorded folder first
  and only falls back to discovery when there is no snapshot or the folder
  is gone; a stale scan names its folder explicitly in the fix command, and
  a missing folder names both the recorded and the discovered install.
  Setup scans the planned folder (shown in the plan) and refuses to switch
  installs non-interactively, stopping with the fix command instead.

## [1.5.0] - 2026-09-26: Serialized script values

Scene indexes now carry the values Schedule I's scripts are configured with, not
just which scripts sit on which objects: prices, wages, order sizes, and the named
objects and assets a field points at. The same release fixes native-body recovery,
which had been attributing the next function's calls to methods that end in a tail
jump or a no-return call.

### Upgrading

Existing databases keep working, but gain nothing new until rebuilt:

1. `extract`, whose default profile is now
   `cpp2il-reconstructed-assemblies-v2`.
2. `extractions promote <extraction-id>` if another extraction is still preferred.
3. `index`, then `index --scene`, to rebuild the code index and scene snapshots.
4. `recover-native-body` for any method whose native evidence you relied on;
   schema migration 15 drops the evidence stored by earlier releases.

### Added

- **Serialized field values for game scripts** (AT-50): scene indexes now decode
  the serialized fields of Schedule I MonoBehaviours and ScriptableObjects
  (property prices, employee wages, special-customer order sizes) instead of
  stopping at `GraphOnly`. A new extraction profile,
  `cpp2il-reconstructed-assemblies-v2` (now the default), keeps the
  `[SerializeField]` attributes Cpp2IL otherwise drops. Layouts built from them are
  trusted per object only when they decode it byte-exact; anything else stays
  `GraphOnly` with a stored reason. `get_component` / `component` return the
  values with FACT provenance, and the new `get_scriptable_object` tool /
  `scriptable-object` command covers ScriptableObjects such as
  `SpecialCustomerData`. Every scene snapshot records its script-layout source.
  Existing builds need a v2 extraction, `extractions promote`, a code-index
  rebuild, and `index --scene` to gain values.
- **Named targets for object-reference fields** (AT-53): a decoded `PPtr` field
  now names what it points to (the GameObject, component, ScriptableObject, script
  or other asset, with its name, type and indexed ID) alongside its raw
  `fileId:localFileId`. Null and unresolvable pointers are labelled as such. The
  scene parser version is now `3.0.5+script-layouts.2`, so `index --scene`
  rebuilds existing snapshots to gain targets.

### Fixed

- **Native-body recovery no longer reads past a method's end** (AT-58): the
  decoder swept linearly to the first `ret`, so a method ending in a tail jump
  or a no-return call ran into the next function and reported that function's
  calls as its own, marked complete. On the 0.4.7f6 build that was 42% of the
  edges it produced. Methods are now decoded to the start of the next function,
  tail jumps are recorded as calls, and a method with an unknown end is
  incomplete. Schema migration 15 drops previously stored native evidence;
  run `recover-native-body` again to rebuild it.

## [1.4.0] - 2026-09-20: Scene intelligence on release builds

Scene intelligence now works on the game as it actually ships. Schedule I strips
the Unity type trees from its scene containers, which left every earlier scene
index empty; 1.4.0 decodes those containers through a pinned Unity class database,
records the type-tree source on every snapshot, and fails loudly instead of
publishing an empty index when it cannot decode. Three gate and lookup bugs found
on the way are fixed in the same release.

### Added

- **Scene intelligence on release builds** (AT-46, AT-47): Schedule I ships its
  scene containers without embedded Unity type trees, so the scene parser could
  not name a single object and `index --scene` published a completed, empty
  snapshot. The parser now decodes stripped containers through a pinned Unity
  class database, installed with `tools install unity-classdata` and re-hashed
  against the pin before every read; each scene snapshot records its type-tree
  source and surfaces it on every scene command and MCP scene tool. When a
  stripped build has no matching class database the run fails with
  `SceneTypeTreeUnavailable` instead of completing empty, and a snapshot that
  recovered no GameObject fails with `NoRecoverableSceneObjects` and is not
  served as a completed index. `gameobject` now returns the selected object's
  transform. Pin, licence, and version-substitution details are in
  `docs/dependencies/unity-classdata-uabea-5adb448.md`.

### Fixed

- **Scene index prerequisite gate** (AT-44): the Schedule I Installed code
  snapshot never recorded its environment snapshot id, so `index --scene` always
  failed with `CrossBuildCodeIndex` even when the preferred extraction was
  replay-verified and the code index was current. The code snapshot now records
  the build-matching environment snapshot id, and a pre-existing null is healed
  in place on the next `index` run without overwriting a populated value.
- **`gameobject <scene-id>/<name>` lookup** (AT-45): the exact-name query joined
  `scene_snapshots` with an unqualified select list, so SQLite rejected it with
  `ambiguous column name: recovery_status`. The select list is now table-qualified.
- **MCP `get_gameobject` with a `<scene-id>/<name>` selector** (AT-49): the read-only
  repository the MCP server uses carried its own copy of that query, so the same
  lookup that AT-45 fixed on the CLI still failed on MCP with `UnexpectedToolFailure`.
  Both copies are now qualified.

## [1.3.0] - 2026-09-10: Native-body recovery

The targeted native-body recovery that 1.2.0 could only *plan* is now
implemented. For a game method exposed as nothing but a `throw null` stub, S1Atlas
can map the managed symbol to its native `GameAssembly.dll` address and recover
bounded, provenance-stamped evidence of what the method actually calls and reads,
without executing the game.

### Added

- **`recover-native-body`**: a CLI command that maps a stubbed IL2CPP managed
  method to its native address, decodes bounded direct-call and field-access
  evidence, and persists a provenance-stamped record. Runs are deterministic and
  idempotent. Recovered pseudocode is static evidence and requires runtime
  validation before being treated as behavioral fact.
- **Native evidence on `investigate_seam`**: the persisted record is surfaced
  read-only on both the CLI and the MCP server through the same bounded evidence
  model (`--native-symbol-id` / `--native-traversal-budget`).
- **Pinned-library provenance**: the recovery provider is an in-process build on
  `Samboy063.LibCpp2IL` and `Iced` (both MIT), pinned with a committed lockfile and
  a CI lockfile-drift check; records are stamped with the library tool identity, the
  build/index/GameAssembly provenance, and never carry a game binary, raw
  disassembly, or a filesystem path.

### Notes

- The provider is read-only with respect to the game: it reads `GameAssembly.dll`
  and `global-metadata.dat` and never launches or mutates the game.

## [1.2.0] - 2026-08-30: Evidence-first agent parity

This release makes S1Atlas more decisive and safer for Schedule I mod
investigation by sharing deterministic evidence packets across the CLI and
read-only MCP server.

### Added

- Behavior-ownership seam investigation with deterministic candidate ordering,
  explicit coverage states, negative seam results, and bounded next actions.
- S1API/S1MAPI index, symbol, source, and relationship queries through MCP with
  build/index provenance and stale-index visibility.
- Targeted native-body recovery planning with build, tool, and input-integrity
  provenance; unsupported or failed recovery remains explicit.
- Runtime-proof planning scoped to one execution boundary with
  `PASS`/`INCONCLUSIVE`/`STOP` outcomes and no invented telemetry.
- Shared agent guidance and contract tests covering the evidence-first workflow.
- A reproducible MCP launch benchmark and direct Release-DLL registration
  guidance.

### Improved

- MCP host documentation now explains one server process per independent stdio
  client, protocol-only stdout, stderr diagnostics, and stale-session process
  inspection.

## [1.1.0] - 2026-08-28: Agent usability

This release adds the evidence surfaces that reduce repeated manual decompilation
and make the boundary between static code evidence and live-game behavior clear.

### Added

- Body-recovery classification for generated interop wrappers.
- Callable-surface queries for directly callable game members.
- Reference-mod collections with cross-assembly relationships resolved against a
  pinned game index.
- Static call-site and field-reference queries with explicit resolution and
  completeness details.
- Focused source queries with deterministic runtime-verification hints, bounded
  caller/callee neighborhoods, and explicit containing-type source spans.
- Public usage and agent-skill guidance for the new query surfaces.

### Maintained

- Public-repository hygiene files, issue/PR templates, and release documentation.

## [1.0.0] - 2026-08-20: V1

First complete version. All V1 "Definition of Done" criteria met.

### Added
- **Build fingerprinting** and immutable, version-aware scan tracking.
- **Cpp2IL + ILSpy extraction pipeline** (verified, provenance-tracked) for the
  IL2CPP game assemblies.
- **Code index**: types, methods, fields, and relationships, searchable by name,
  with decompiled source, callers, callees, and references.
- **Upstream S1API / S1MAPI deep-indexing** so the modding API can be checked before
  patching the game directly.
- **Scene intelligence**: scenes, prefabs, GameObjects, and components (CLI + MCP).
- **Build diffing**: see exactly what a game update changed.
- **Read-only MCP server** for coding agents.
- **Deterministic static HTML portal** for human browsing.
- **Agent skill** (`skills/s1atlas/`) for evidence-first modding workflows.
- Provenance labeling throughout: `FACT` (extracted) and `DERIVED` (computed).

[Unreleased]: ../../compare/main...HEAD
