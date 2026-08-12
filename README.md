# S1Atlas

S1Atlas is a local, version-aware developer-intelligence platform for Schedule I mod development. It is designed to make game internals searchable and understandable for both human developers and coding agents.

> **Current state:** the V1 Foundation milestone is implemented. The validated Cpp2IL extraction milestone is designed but not yet implemented. ILSpy decompilation, symbol indexing, generated HTML documentation, build diffing, MCP, and the S1Atlas agent skill remain later milestones.

## What the Foundation Can Do

The current CLI can:

- locate a standard Windows Schedule I installation or accept an explicit path;
- hash `GameAssembly.dll` and `global-metadata.dat` to create a stable build ID;
- detect installed S1API, S1MAPI, MelonLoader, and Sideload components;
- persist immutable environment snapshots in one local SQLite database;
- atomically promote a validated snapshot as the current Atlas build;
- preserve the previous valid build if discovery or persistence fails;
- show current status, dependency information, and indexed build history.

S1Atlas treats the Schedule I installation as **read-only input**. Foundation integration tests verify that a scan does not add, remove, or change files or directories in the game installation.

## Requirements

- Windows 10 or later
- .NET 8 SDK
- A local Schedule I installation for real scans

## Build and Test

From the repository root:

```powershell
dotnet restore S1Atlas.sln
dotnet build S1Atlas.sln --configuration Release
dotnet test S1Atlas.sln --configuration Release --no-build
```

## Run the CLI

Using an explicit game path is the most reliable first run:

```powershell
dotnet run --project src/S1Atlas.Cli -- scan --game-path "C:\Program Files (x86)\Steam\steamapps\common\Schedule I"
```

Then explore the stored environment:

```powershell
dotnet run --project src/S1Atlas.Cli -- status
dotnet run --project src/S1Atlas.Cli -- env
dotnet run --project src/S1Atlas.Cli -- builds
```

Without `--game-path`, S1Atlas checks the standard Steam locations under `Program Files (x86)` and `Program Files`.

## Local Data Location

By default, Atlas data is stored at:

```text
%LOCALAPPDATA%\S1Atlas
```

Override that location with the `S1ATLAS_HOME` environment variable:

```powershell
$env:S1ATLAS_HOME = "C:\Users\david\Documents\S1Atlas Data"
dotnet run --project src/S1Atlas.Cli -- status
```

Generated data, databases, extraction artifacts, decompiled output, and logs are intentionally excluded from Git.

## Foundation Architecture

```text
S1Atlas.Core        Domain records and interfaces
S1Atlas.Extraction  Read-only discovery, hashing, and dependency detection
S1Atlas.Storage     Transactional SQLite persistence and queries
S1Atlas.Cli         Human and automation command-line interface
```

The dependency direction keeps game/tool-specific details out of the Core domain model and allows later Cpp2IL and ILSpy adapters to be replaced without changing the CLI, docs portal, or MCP query surface.

## Current Commands

| Command | Purpose |
|---|---|
| `scan [--game-path <path>]` | Discover and persist the current local environment |
| `status` | Show the current indexed build |
| `env` | Show the current build and tracked dependencies |
| `builds` | List all indexed builds, newest first |

## Next Milestone

The next implementation milestone adds validated Cpp2IL extraction: a repository-pinned managed tool cache, explicit tool installation, isolated process execution, immutable toolchain-specific extraction runs, layered assembly validation, recovery, and preferred-extraction tracking.

ILSpy decompilation and normalized symbol indexing follow only after reconstructed assemblies are validated and trusted.

### Planned Phase 1 migration behavior

Phase 1 first corrects the Foundation metadata model and introduces identity-version 2 environment snapshots. After a Foundation database is migrated, the first new scan intentionally creates and promotes a version-2 snapshot even when the observed environment is otherwise unchanged. The migrated identity-version 1 snapshot remains in the database as history; this transition is expected and is not duplicate-build churn.

## Project Documents

- [V1 design specification](docs/superpowers/specs/2026-08-12-s1atlas-design.md)
- [Foundation implementation plan](docs/superpowers/plans/2026-08-12-s1atlas-v1-foundation-plan.md)
- [Validated Cpp2IL extraction design](docs/superpowers/specs/2026-08-12-cpp2il-extraction-design.md)
