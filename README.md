# S1Atlas

S1Atlas is a local, version-aware developer-intelligence platform for Schedule I mod development. It is designed to make game internals searchable and understandable for both human developers and coding agents.

> **Current state:** the V1 Foundation and its metadata/database-migration phase are implemented. The managed Cpp2IL tool supply chain, Cpp2IL execution, assembly validation, ILSpy decompilation, symbol indexing, generated HTML documentation, build diffing, MCP, and the S1Atlas agent skill remain later milestones.

## What the Foundation Can Do

The current CLI can:

- locate a standard Windows Schedule I installation or accept an explicit path;
- hash `GameAssembly.dll` and `global-metadata.dat` to create the authoritative content-derived build ID;
- report the executable file version and read the matching local Steam app ID and Steam build ID entirely offline;
- detect installed S1API, S1MAPI, MelonLoader, and Sideload components;
- persist immutable, identity-versioned environment snapshots in one local SQLite database;
- safely migrate the exact shipped Foundation database schema with checksummed migrations and a pre-migration backup;
- refuse unknown nonempty database schemas rather than guessing or mutating them;
- atomically promote a validated snapshot as the current Atlas environment;
- preserve the previous valid environment if discovery or persistence fails;
- show current status, dependency information, and indexed build history in human-readable or stable JSON form.

S1Atlas treats both the Schedule I installation and local Steam app manifest as **read-only input**. Integration tests verify that a scan does not add, remove, or change game files, game directories, or the matching Steam manifest.

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

For machine-readable output, add `--json` to the query commands:

```powershell
dotnet run --project src/S1Atlas.Cli -- status --json
dotnet run --project src/S1Atlas.Cli -- env --json
dotnet run --project src/S1Atlas.Cli -- builds --json
```

Each JSON invocation writes exactly one top-level envelope to stdout with `schemaVersion`, `command`, `success`, `exitCode`, `data`, and `error`. Schema version 1 defines that top-level contract. Later command-specific error objects may add fields, so consumers should ignore error properties they do not recognize.

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

When an existing recognized Foundation database requires migration, S1Atlas creates one recoverable SQLite backup under:

```text
%LOCALAPPDATA%\S1Atlas\backups
```

Unknown nonempty schemas are rejected without a migration ledger, schema mutation, or backup because S1Atlas cannot safely infer their origin.

Generated data, databases, backups, extraction artifacts, decompiled output, and logs are intentionally excluded from Git.

## Build and Environment Identity

The build ID remains derived only from the `GameAssembly.dll` and `global-metadata.dat` content hashes. Executable version, Steam app/build IDs, installation paths, dependency versions, and Atlas version describe an environment snapshot; they do not redefine the game build.

After a Foundation-v1 database is migrated, its existing snapshot remains identity version 1 with the same build ID, snapshot ID, dependencies, and current pointer. The first subsequent scan intentionally creates and promotes an identity-version 2 environment snapshot even when the observed installation is otherwise unchanged. The migrated v1 snapshot remains as history; this one-time transition is expected and is not duplicate-build churn.

## Foundation Architecture

```text
S1Atlas.Core        Domain records and interfaces
S1Atlas.Extraction  Read-only discovery, hashing, dependency, and local Steam metadata detection
S1Atlas.Storage     Checksummed migrations and transactional SQLite persistence
S1Atlas.Cli         Human and machine-readable command-line interface
```

The dependency direction keeps game/tool-specific details out of the Core domain model and allows later Cpp2IL and ILSpy adapters to be replaced without changing the CLI, docs portal, or MCP query surface.

## Current Commands

| Command | Purpose |
|---|---|
| `scan [--game-path <path>]` | Discover and persist the current local environment |
| `status [--json]` | Show the current indexed build and installation observation |
| `env [--json]` | Show the current build, installation paths, and tracked dependencies |
| `builds [--json]` | List content-derived builds, newest first-seen first |

## Next Milestone

The next implementation phase adds the managed Cpp2IL tool supply chain: repository-pinned tool definitions, checksum verification, explicit installation, local cache management, and capability probing. It does not yet imply trusted reconstructed assemblies.

Isolated Cpp2IL execution and layered assembly validation follow after the managed tool supply chain. ILSpy decompilation and normalized symbol indexing follow only after reconstructed assemblies are validated and trusted.

## Project Documents

- [V1 design specification](docs/superpowers/specs/2026-08-12-s1atlas-design.md)
- [Foundation implementation plan](docs/superpowers/plans/2026-08-12-s1atlas-v1-foundation-plan.md)
- [Validated Cpp2IL extraction design](docs/superpowers/specs/2026-08-12-cpp2il-extraction-design.md)
- [Cpp2IL Phase 1 metadata and migration implementation plan](docs/superpowers/plans/2026-08-12-cpp2il-phase1-metadata-migration-plan.md)
