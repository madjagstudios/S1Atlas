# S1Atlas

**A local, offline developer-intelligence platform for Schedule I mod development.** It turns the game's compiled internals into a searchable, provenance-tracked map for both human developers and coding agents.

> **Disclaimer:** S1Atlas is an unofficial, fan-made developer tool. It is not affiliated with, endorsed by, or connected to the developers or publishers of Schedule I. It requires you to supply your own legitimately obtained copy of the game, and it neither includes nor distributes any game assets, binaries, or decompiled output. All generated data stays local on your machine. It is provided for interoperability, modding, and educational purposes under the [MIT License](LICENSE).

## What it does

Point S1Atlas at your installed copy of Schedule I and it:

- **Fingerprints the build** and tracks every version you scan, immutably.
- **Extracts and decompiles** the IL2CPP game assemblies through a verified Cpp2IL + ILSpy pipeline.
- **Indexes** every type, method, field, and relationship, searchable by name, with decompiled source, callers, callees, and references.
- **Traces static engine/BCL relationships** so you can ask who calls a Unity or framework API and who reads or writes a field, across the verified game index and explicitly selected local reference collections.
- **Diffs builds** so you can see exactly what a game update changed.
- **Deep-indexes S1API and S1MAPI** so you can check the modding API before patching the game directly.
- **Indexes explicitly selected local reference-mod collections** so prior-art symbols and relationships can be queried beside the verified game index.
- **Recovers native bodies for stubbed methods** so a game method that decompiles to nothing but a `throw null` stub can be mapped to its native `GameAssembly.dll` address, with bounded, provenance-stamped evidence of what it calls and reads, recovered statically without running the game.
- **Investigates behavior-ownership seams and plans runtime proofs** so you can find which type actually owns a behavior, with explicit coverage and negative results, and get a bounded, read-only plan to confirm it at runtime when static evidence cannot.
- **Serves the same knowledge three ways**: a CLI, a read-only [MCP](https://modelcontextprotocol.io) server for coding agents, and a generated static HTML portal.

Every answer is labeled by provenance, `FACT` for extracted and `DERIVED` for computed, and traced to the exact build. S1Atlas reports only what it can prove and stays explicit about what it cannot.

### Example: "what changes the player's cash?"

> Examples abbreviate `dotnet run --project src/S1Atlas.Cli --` as `s1atlas`.

```powershell
# find it
> s1atlas search "ChangeCashBalance"
ScheduleOne.Money.MoneyManager::ChangeCashBalance(System.Single, System.Boolean, System.Boolean)

# read the decompiled source
> s1atlas source "MoneyManager.ChangeCashBalance"

# see every call site in the game
> s1atlas callers "MoneyManager.ChangeCashBalance"
```

## Requirements

- Windows 10 or later
- .NET 8 SDK
- A local, legitimately owned Schedule I installation (for real scans)

## Quick start

```powershell
# build
dotnet build S1Atlas.sln --configuration Release

# scan your installation
dotnet run --project src/S1Atlas.Cli -- scan --game-path "C:\Program Files (x86)\Steam\steamapps\common\Schedule I"

# extract + index the current build, then query it
dotnet run --project src/S1Atlas.Cli -- extract
dotnet run --project src/S1Atlas.Cli -- index
dotnet run --project src/S1Atlas.Cli -- search "Player" --limit 20
dotnet run --project src/S1Atlas.Cli -- callsites "UnityEngine.AI.NavMeshAgent.CompleteOffMeshLink"
dotnet run --project src/S1Atlas.Cli -- fieldrefs "MoneyManager.cashBalance" --writers

# validate and index a local reference-mod collection selected by a manifest
dotnet run --project src/S1Atlas.Cli -- reference collections validate "C:\path\to\reference-manifest.json"
dotnet run --project src/S1Atlas.Cli -- reference index "C:\path\to\reference-manifest.json"
dotnet run --project src/S1Atlas.Cli -- search "ModEntry" --scope reference --collection qol

# generate a browsable, offline HTML portal (opens as ./s1atlas-docs/index.html)
dotnet run --project src/S1Atlas.Cli -- docs generate
```

The full command walkthrough, every option, the MCP server, and the agent skill are in **[docs/USAGE.md](docs/USAGE.md)**.

## Interfaces

- **CLI**: `scan`, `extract`, `index`, `search` / `type` / `method` / `source` / `refs` / `callers` / `callees` / `callsites` / `fieldrefs` / `callable`, `investigate_seam`, `recover-native-body`, `diff`, the `scenes` / `scene` / `gameobject` / `prefab` / `component` graph queries, `upstream`, and `docs generate`.
- **Read-only MCP server**: the Schedule I Installed query surface (symbols, source, relationships, call sites, field references, callable surface, scenes), `investigate_seam` with read-only native evidence, `plan_runtime_proof`, S1API/S1MAPI queries, and completed local reference-collection queries, for coding agents (`dotnet src/S1Atlas.Mcp/bin/Release/net8.0/S1Atlas.Mcp.dll mcp serve` after a Release build).
- **Static portal**: `docs generate` builds a deterministic, fully offline, provenance-labeled HTML site.
- **Agent skill**: an evidence-first usage methodology at [`skills/s1atlas/SKILL.md`](skills/s1atlas/SKILL.md).

## How it works

```text
S1Atlas.Core           Domain records and interfaces
S1Atlas.Extraction     Read-only discovery, hashing, dependency, local Steam metadata detection, and Cpp2IL orchestration
S1Atlas.Indexing       ILSpy decompilation, Roslyn source/symbol indexing, relationships, scene intelligence, and index queries
S1Atlas.NativeRecovery Bounded native-body recovery over GameAssembly.dll for stubbed IL2CPP methods
S1Atlas.Storage        Checksummed migrations and transactional SQLite persistence
S1Atlas.Application    Shared read-only composition and Schedule I Installed build authority
S1Atlas.Cli            Human and machine-readable command-line interface
S1Atlas.Mcp            Read-only MCP stdio server for Schedule I Installed and completed local reference queries
```

S1Atlas treats the game install and Steam manifest as **read-only input**. Extraction runs Cpp2IL in isolation and promotes only results that pass the validation policy. Native-body recovery reads `GameAssembly.dll` and `global-metadata.dat` and never launches or mutates the game. CLI, MCP, and portal queries use the same verified index.

Reference collections are local and CLI-indexed. Each completed collection records its selected mods, hashes, and Schedule I base index; MCP exposes the resulting read-only queries and collection list. `reference` stays within one collection, while `all` is the explicit cross-origin view. Body recovery, callability, and reference prior art are separate evidence dimensions, and none establishes the others.

`callsites` and `fieldrefs`, in both CLI and MCP form, are static recovered-IL relationship evidence. They can prove that the indexed code references a target or field in the recovered body set; they do not prove runtime scene behavior, geometry behavior, lifecycle sequencing, or call order. Recovered native pseudocode is static evidence in the same sense: it requires runtime validation before being treated as behavioral fact.

Deep internals, including on-disk data layout, the pinned Cpp2IL definition, the validation policy, and build/environment identity, are documented in **[docs/REFERENCE.md](docs/REFERENCE.md)**.

## Status

**V1 shipped, and development continues on `main`.** The environment can be discovered; builds can be fingerprinted, extracted, and indexed; symbols, source, relationships, and build diffs are queryable; S1API and S1MAPI are deep-indexed; the static portal, read-only MCP server, and agent skill all ship; and a failed scan never damages the last valid state.

Since V1, S1Atlas has added static call-site and field-reference queries, callable-surface queries, local reference-mod collections, behavior-ownership seam investigation, runtime-proof planning, and native-body recovery for stubbed IL2CPP methods. The [CHANGELOG](CHANGELOG.md) has the per-version detail.

The static portal defers scene HTML for now; scene intelligence remains available through the CLI and MCP. Known issues and further work are tracked in [Issues](../../issues).

## Contributing

Contributions welcome. See [CONTRIBUTING.md](CONTRIBUTING.md). The one hard rule: **never commit game content.** S1Atlas distributes no game assets or decompiled output; all extracted and generated data stays local and gitignored.

## License

[MIT](LICENSE). Unofficial and not affiliated with the developers or publishers of Schedule I. See the disclaimer above.
