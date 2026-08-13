# S1Atlas

S1Atlas is a local, version-aware developer-intelligence platform for Schedule I mod development. It is designed to make game internals searchable and understandable for both human developers and coding agents.

> **Current state:** the V1 Foundation, metadata/database migrations, and the managed Cpp2IL tool supply chain are implemented. Cpp2IL game execution, assembly validation, ILSpy decompilation, symbol indexing, generated HTML documentation, build diffing, MCP, and the S1Atlas agent skill remain later milestones.

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
- show current status, dependency information, and indexed build history in human-readable or stable JSON form;
- inspect the repository-pinned Cpp2IL installation entirely offline;
- explicitly download, checksum, capability-probe, and register the approved Cpp2IL pin;
- repair an invalid managed installation only with `--repair`, quarantining the replaced files.

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

Inspect or explicitly install the managed Cpp2IL pin:

```powershell
dotnet run --project src/S1Atlas.Cli -- tools status cpp2il
dotnet run --project src/S1Atlas.Cli -- tools install cpp2il
dotnet run --project src/S1Atlas.Cli -- tools install cpp2il --repair
```

`tools status` is always offline. Of the implemented commands, only
`tools install cpp2il` can access the network, and installation never happens
implicitly during a scan or status query.

For machine-readable output, add `--json` to the query commands:

```powershell
dotnet run --project src/S1Atlas.Cli -- status --json
dotnet run --project src/S1Atlas.Cli -- env --json
dotnet run --project src/S1Atlas.Cli -- builds --json
dotnet run --project src/S1Atlas.Cli -- tools status cpp2il --json
dotnet run --project src/S1Atlas.Cli -- tools install cpp2il --json
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

An existing schema-version-2 database can produce one
`atlas-before-schema-3-*.db` backup when managed-tool provenance tables are
added. New databases apply all migrations without a backup.

Managed tools are stored only below the Atlas data root:

```text
%LOCALAPPDATA%\S1Atlas\tools\cpp2il\<version>
%LOCALAPPDATA%\S1Atlas\tools\.staging
%LOCALAPPDATA%\S1Atlas\tools\quarantine
```

`S1ATLAS_HOME` moves the database, backups, staging, quarantine, and final tool
installation together. A successful reinstall of an exact verified pin is a
no-op. An invalid installation is never silently overwritten; `--repair`
stages and fully verifies a replacement before moving the prior installation
to quarantine.

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

## Managed Cpp2IL Pin

The committed Windows x64 definition is immutable runtime input reviewed with
the repository:

```text
Version:       2022.1.0-pre-release.21
Asset:         Cpp2IL-2022.1.0-pre-release.21-Windows.exe
Expected size: 15,137,811 bytes
SHA-256:       663fb432433b4371fd1ee0ebc321a8fff2a9aac5ac4230c843f9e03ddee4e04c
Local name:    Cpp2IL.exe
Capability:    dll_il_recovery
```

S1Atlas verifies the exact size and SHA-256 before the downloaded executable is
ever started. It then runs controlled `--help` and `--list-output-formats`
probes and requires `dll_il_recovery`. Automated tests inject fake local bytes
and fake HTTP handlers; they do not download the official package.

This phase does not point Cpp2IL at Schedule I and does not execute an
extraction. The Schedule I installation remains read-only.

## Current Commands

| Command | Purpose |
|---|---|
| `scan [--game-path <path>]` | Discover and persist the current local environment |
| `status [--json]` | Show the current indexed build and installation observation |
| `env [--json]` | Show the current build, installation paths, and tracked dependencies |
| `builds [--json]` | List content-derived builds, newest first-seen first |
| `tools status [tool-id] [--json]` | Inspect pinned managed-tool state offline |
| `tools install <tool-id> [--repair] [--json]` | Explicitly download, verify, install, or repair a managed tool |

## Next Milestone

Phase 3 adds repository-controlled extraction profiles and validation policies,
typed game-input resolution, extraction attempts, isolated Cpp2IL process
execution, Ctrl+C/timeout process-tree termination, and bounded logs. Layered
assembly validation and immutable reconstructed-output promotion follow before
ILSpy decompilation or normalized symbol indexing.

## Project Documents

- [V1 design specification](docs/superpowers/specs/2026-08-12-s1atlas-design.md)
- [Foundation implementation plan](docs/superpowers/plans/2026-08-12-s1atlas-v1-foundation-plan.md)
- [Validated Cpp2IL extraction design](docs/superpowers/specs/2026-08-12-cpp2il-extraction-design.md)
- [Cpp2IL Phase 1 metadata and migration implementation plan](docs/superpowers/plans/2026-08-12-cpp2il-phase1-metadata-migration-plan.md)
