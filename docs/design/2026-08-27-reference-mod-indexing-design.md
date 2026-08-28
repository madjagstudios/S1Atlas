# Reference-mod indexing design

**Status:** Shipped in PR #43 (`d2d909b`).

**Goal:** Let an agent search a deliberately selected, local collection of reference mods alongside the verified Schedule I game index when investigating prior-art.

## User workflow

The user supplies a local manifest describing one named collection, such as `qol`, and the mods that belong to it. The manifest is explicit: S1Atlas indexes only the listed local roots and never discovers, downloads, or redistributes mods. A collection can be rebuilt after a mod update and its identity changes when selected file content or declared metadata changes.

The first delivery supports managed assemblies, C# source, and text documentation. Documentation includes README, changelog, dev-log, and other text files selected by the manifest. Query results return bounded excerpts and provenance; they do not turn the MCP server into a source-distribution channel.

Example manifest:

```json
{
  "collection": "qol",
  "mods": [
    {
      "id": "chemical-plant",
      "displayName": "Chemical Plant",
      "rootPath": "C:/local/reference-mods/ChemicalPlant",
      "version": "local",
      "license": "MIT",
      "include": ["**/*.dll", "**/*.cs", "**/*.md", "**/*.txt"],
      "exclude": ["**/bin/**", "**/obj/**", "**/BepInEx/cache/**"]
    }
  ]
}
```

The manifest is local input and must not be committed to a public mod repository when it contains machine-specific paths. Automatic similarity ranking and downloading are explicitly outside AT-26; named collections are the selection mechanism for related QoL or other prior-art sets.

## Evidence and trust

- The Schedule I side remains the integrity-verified game extraction selected by the existing authority chain.
- Reference-mod files are user-supplied local inputs. They are hashed before indexing and re-hashed after every decompilation/read operation; drift fails the run rather than publishing mixed evidence.
- The index identity includes the normalized manifest, declared metadata, selected file hashes, game extraction identity, decompiler/tool versions, and schema version. It excludes local paths so byte-identical collections are reproducible across machines.
- Every reference result carries collection, mod ID, display name, version, license declaration, relative path, content hash, and `LocalOnly` provenance. S1Atlas does not certify that a reference mod is safe, compatible, or licensed for redistribution.
- A missing or invalid license declaration is reported as local-only metadata, not silently treated as permission to redistribute.

## Index and query model

AT-26 adds a `ReferenceMod` codebase with the `Installed` channel. A completed reference index contains only the selected reference-mod symbols and documents. It records the completed Schedule I index ID used as its base, then loads that index's persisted game symbols as resolution targets; it does not re-decompile or copy the game assembly for each collection. Game target symbol IDs remain the IDs from the verified Schedule I index, while reference symbols include the mod ID in their canonical identity so same-named types from different mods cannot collide.

The index stores mod metadata and selected text documents in addition to normalized reference symbols, generated assembly source, fingerprints, and relationships. A mod-to-game relationship may point at a symbol row in the completed Schedule I index because `relationships.target_symbol_id` is a database-wide foreign key; the query layer resolves that external target through the recorded base game index. Source/document reads verify the stored hash before returning an excerpt. Relationship extraction uses a dictionary keyed by owner/mod ID, type, member name, arity, and signature; it must not compare every reference symbol with every game symbol.

Existing search, source, callers, callees, and references gain an explicit scope: `game`, `reference`, or `all`, plus a collection selector for reference/all queries. Type and method convenience commands remain their existing game/API-only surfaces in AT-26. Ambiguous matches remain ambiguous and show their mod provenance. `callable` remains a Schedule I game-member query; it does not claim that a reference-mod wrapper is directly callable in the installed game.

AT-24 and AT-25 remain orthogonal: body recovery says whether decompiled text is behavioral evidence, while callable surface says how a game member can be reached through the local interop projection. Reference-mod source is prior-art evidence and must carry its own provenance and recovery status.

## Persistence and compatibility

Migration 10 adds reference-index context, reference-mod metadata, document content metadata, reference-symbol ownership, and the indexes required for scoped queries. It also widens the `code_snapshots` check to allow `ReferenceMod` only with `Installed`, using a parent-table rebuild that preserves populated v9 data and every dependent foreign key. Existing game/API indexes remain queryable. `IndexWriteSet` gains nullable trailing collections so existing producers and test fixtures remain source-compatible; it does not copy game symbols into reference indexes.

The public query models append optional provenance fields rather than changing existing positional construction sites. The read-only MCP server exposes reference queries and collection metadata, but indexing remains an explicit CLI operation.

## CLI and MCP surface

CLI additions:

```text
reference collections validate <manifest>
reference index <manifest> [--force] [--json]
reference collections list [--json]
search <query> --scope reference --collection qol
callers <query> --scope all --collection qol
callees <query> --scope all --collection qol
```

`--scope reference|all` requires `--collection`; `--scope game` rejects it. Reference indexing is local/offline and accepts no network, upstream, scene, or API-codebase options. The MCP read surface adds collection listing and optional scope/collection arguments to the existing symbol and relationship tools.

## Cost and limits

Assembly matching is dictionary-based. Each selected managed assembly is decompiled once, each selected text document is read once, and cross-origin relationship resolution is keyed lookup plus bounded candidate filtering. The expected cost is approximately linear in selected input size plus the number of indexed relationships. The CLI reports mod/file/symbol/document counts and elapsed phases so a large collection can be narrowed without guesswork.
