# S1Atlas

**A local, offline developer-intelligence platform for Schedule I mod development.** It turns the game's compiled internals into a searchable, provenance-tracked map — for both human developers and coding agents.

> **Disclaimer:** S1Atlas is an unofficial, fan-made developer tool. It is not affiliated with, endorsed by, or connected to the developers or publishers of Schedule I. It requires you to supply your own legitimately obtained copy of the game, and it neither includes nor distributes any game assets, binaries, or decompiled output — all generated data stays local on your machine. It is provided for interoperability, modding, and educational purposes under the [MIT License](LICENSE).

## What it does

Point S1Atlas at your installed copy of Schedule I and it:

- **Fingerprints the build** and tracks every version you scan, immutably.
- **Extracts and decompiles** the IL2CPP game assemblies through a verified Cpp2IL + ILSpy pipeline.
- **Indexes** every type, method, field, and relationship — searchable by name, with decompiled source, callers, callees, and references.
- **Diffs builds** so you can see exactly what a game update changed.
- **Deep-indexes S1API and S1MAPI** so you can check the modding API before patching the game directly.
- **Serves the same knowledge three ways** — a CLI, a read-only [MCP](https://modelcontextprotocol.io) server for coding agents, and a generated static HTML portal.

Every answer is labeled by provenance — `FACT` (extracted), `DERIVED` (computed) — and traced to the exact build. S1Atlas reports only what it can prove and stays explicit about what it can't.

### Example — "what changes the player's cash?"

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

# generate a browsable, offline HTML portal (opens as ./s1atlas-docs/index.html)
dotnet run --project src/S1Atlas.Cli -- docs generate
```

The full command walkthrough, every option, the MCP server, and the agent skill are in **[docs/USAGE.md](docs/USAGE.md)**.

## Interfaces

- **CLI** — `scan`, `extract`, `index`, `search` / `type` / `method` / `source` / `refs` / `callers` / `callees`, `diff`, the `scenes` / `scene` / `gameobject` / `prefab` / `component` graph queries, `upstream`, and `docs generate`.
- **Read-only MCP server** — 15 tools over the Schedule I Installed surface, for coding agents (`dotnet run --project src/S1Atlas.Mcp -- mcp serve`).
- **Static portal** — `docs generate` builds a deterministic, fully offline, provenance-labeled HTML site.
- **Agent skill** — an evidence-first usage methodology at [`skills/s1atlas/SKILL.md`](skills/s1atlas/SKILL.md).

## How it works

```text
S1Atlas.Core        Domain records and interfaces
S1Atlas.Extraction  Read-only discovery, hashing, dependency, local Steam metadata detection, and Cpp2IL orchestration
S1Atlas.Indexing    ILSpy decompilation, Roslyn source/symbol indexing, relationships, scene intelligence, and index queries
S1Atlas.Storage     Checksummed migrations and transactional SQLite persistence
S1Atlas.Application Shared read-only composition and Schedule I Installed build authority
S1Atlas.Cli         Human and machine-readable command-line interface
S1Atlas.Mcp         Read-only MCP stdio server for Schedule I Installed queries
```

S1Atlas treats the game install and Steam manifest as **read-only input**. Extraction runs Cpp2IL in isolation, validates the result against a committed policy, and immutably promotes only an integrity-verified extraction. Every query — CLI, MCP, or portal — resolves through one shared authority path, so human and agent answers stay in parity, and it will never return an unverified candidate as if it were fact.

Deep internals — on-disk data layout, the pinned Cpp2IL definition, the validation policy, and build/environment identity — are documented in **[docs/REFERENCE.md](docs/REFERENCE.md)**.

## Status

**V1 is complete.** The environment can be discovered; builds can be fingerprinted, extracted, and indexed; symbols, source, relationships, and build diffs are queryable; S1API and S1MAPI are deep-indexed; the static portal, read-only MCP server, and agent skill all ship; and a failed scan never damages the last valid state. The static portal intentionally defers scene HTML in V1 — scene intelligence remains available through the CLI and MCP.

Post-V1 work and known issues are tracked in [Issues](../../issues).

## Contributing

Contributions welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). The one hard rule: **never commit game content.** S1Atlas distributes no game assets or decompiled output; all extracted and generated data stays local and gitignored.

## License

[MIT](LICENSE). Unofficial and not affiliated with the developers or publishers of Schedule I — see the disclaimer above.
