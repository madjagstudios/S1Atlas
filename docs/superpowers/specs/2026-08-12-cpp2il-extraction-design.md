# S1Atlas Validated Cpp2IL Extraction Milestone Design Specification

**Status:** Approved design  
**Date:** 2026-08-12  
**Target platform:** Windows 10 or later  
**Primary implementation:** C# / .NET 8  
**Depends on:** S1Atlas V1 Foundation on `main`  
**Reference Schedule I build:** `6fbd38f8401afa2241a1322afd4b8a8eadc99aa1f1c660ece253da7859d54bdc`

## 1. Purpose

This milestone adds a trustworthy IL2CPP extraction layer to S1Atlas.

It begins with an immutable Schedule I build already known to the Foundation scanner and ends with one or more immutable, structurally validated sets of reconstructed managed assemblies. Those assemblies become the only permitted input to the later ILSpy, symbol-indexing, documentation, and MCP milestones.

The milestone is deliberately narrower than “decompile Schedule I.” It establishes a reproducible and recoverable boundary around Cpp2IL before S1Atlas depends on its output.

The central output contract is:

> A validated extraction is an immutable, provenance-rich set of reconstructed managed assemblies whose game inputs, tool executable, controlled arguments, output files, and validation results are all independently recorded and verifiable.

Structural validation does **not** claim that reconstructed method bodies perfectly represent original game behavior. The current Cpp2IL output format is accepted here as a managed-assembly reconstruction source. Behavioral decompilation quality is evaluated separately in the ILSpy and symbol-indexing milestone.

## 2. Baseline Proven by the Foundation

The Foundation has already passed Windows CI and a real local scan.

Current locally observed facts:

```text
Atlas build ID:
6fbd38f8401afa2241a1322afd4b8a8eadc99aa1f1c660ece253da7859d54bdc

Executable file version:
2022.3.62.7762112

S1API:
3.1.12.0

S1MAPI:
missing

MelonLoader:
0.7.3.0

Sideload:
1.30.0.0
```

The content-derived build ID is based on the hashes of `GameAssembly.dll` and `global-metadata.dat`. It remains the authoritative game-content identity.

The value previously displayed as “Game version” is the Windows executable file version. This milestone corrects that terminology and adds offline Steam metadata detection.

## 3. Scope

This milestone includes:

- correcting executable-version terminology;
- detecting the local Steam app ID and Steam build ID without network access;
- introducing versioned SQLite schema migrations and pre-migration backups;
- defining and installing a repository-pinned Cpp2IL executable;
- preserving the exact tool definition, observed executable hash, and license metadata;
- supporting a checksum-verified S1Atlas-managed tool cache;
- allowing explicitly supplied custom Cpp2IL executable paths at a lower trust level;
- defining repository-controlled, versioned extraction and validation profiles;
- running Cpp2IL as an isolated external process;
- recording every extraction attempt, including failures, cancellation, timeouts, and interrupted work;
- verifying live Schedule I inputs before and after extraction;
- optionally archiving exact replayable extraction inputs;
- inventorying and hashing every promoted artifact;
- validating reconstructed DLLs through PE and managed-metadata inspection;
- applying absolute and comparative sanity checks;
- atomically promoting immutable validated extraction directories;
- tracking one preferred validated extraction per game build;
- retaining diagnostic evidence while controlling failed-run disk usage;
- exposing human-readable and JSON CLI commands;
- testing all behavior without downloading tools or using proprietary game files in CI;
- completing one real local extraction against the reference Schedule I build.

## 4. Explicit Exclusions

This milestone does not:

- embed LibCpp2IL into the S1Atlas process;
- run ILSpy;
- generate C# source files;
- claim recovered method bodies are behaviorally authoritative;
- index assemblies, namespaces, types, methods, fields, properties, parameters, callers, or callees into the future symbol model;
- provide symbol search;
- compare decompiled source across builds;
- generate the human HTML portal;
- expose MCP tools;
- predict whether a mod is broken;
- generate patches;
- modify Schedule I;
- upload game binaries, metadata, reconstructed assemblies, or retained tool output to GitHub or CI artifacts;
- accept arbitrary raw Cpp2IL command-line argument strings.

## 5. Approved Design Decisions

The following decisions are fixed for this milestone:

1. S1Atlas uses a hybrid tool strategy: a managed pinned cache is the normal path, with explicit custom executable overrides available.
2. Cpp2IL runs as an isolated external process behind a replaceable `IIl2CppExtractor` interface.
3. Tool installation is explicit. `extract` never downloads anything.
4. Tool definitions and extraction profiles are committed to the repository and changed through reviewed pull requests.
5. A Schedule I build may have multiple immutable toolchain-specific extractions.
6. Cpp2IL process success alone is insufficient; layered validation is mandatory.
7. A successful managed-pinned extraction may become preferred automatically.
8. A custom-tool extraction cannot become preferred automatically.
9. The milestone ends at validated reconstructed assemblies.
10. Cpp2IL reads the live installation by default rather than requiring duplicate input copies.
11. Live inputs are hashed before and after the process; changed inputs invalidate the run.
12. Exact input snapshots are optional and deliberate.
13. Validated outputs are retained in full.
14. Failed or canceled runs retain metadata and logs; partial output is discarded unless preservation was explicitly requested.
15. Cpp2IL arguments come only from typed, versioned repository profiles.
16. `extract` defaults to the current Atlas build.
17. Historical extraction requires matching live inputs or a verified archived input snapshot.
18. Hashes alone do not imply that historical input bytes remain available.
19. “Game version” is replaced by accurate executable-version terminology.
20. Steam build metadata is detected locally and is descriptive, not authoritative build identity.

## 6. Architecture and Project Boundaries

The high-level data flow is:

```text
Selected Atlas build
        |
        +-- resolve and verify exact game inputs
        +-- resolve verified Cpp2IL tool instance
        +-- resolve committed extraction profile
        +-- calculate deterministic recipe identity
        |
        v
Create durable attempt + Atlas-owned staging directory
        |
        v
Run Cpp2IL as an isolated process
        |
        +-- Atlas-owned working directory
        +-- Atlas-owned output directory
        +-- stdout/stderr capture
        +-- timeout and cancellation
        +-- exact executable and arguments recorded
        |
        v
Re-verify live inputs
        |
        v
Layered output validation
        |
        +-- process result
        +-- path containment
        +-- artifact inventory and hashing
        +-- managed PE/metadata validation
        +-- absolute sanity checks
        +-- comparative sanity checks
        +-- reproducibility comparison
        |
        v
Recoverable immutable promotion
        |
        +-- filesystem finalization
        +-- SQLite transaction
        +-- preferred-extraction policy
```

### 6.1 `S1Atlas.Core`

Core owns domain records, deterministic identity logic, lifecycle rules, validation results, and interfaces.

Representative concepts:

```text
ToolDefinition
ManagedToolInstallation
ToolInstance
ToolTrustLevel
ExtractionProfile
ValidationPolicy
ExtractionRecipe
ExtractionAttempt
ExtractionAttemptStatus
ExtractionFailureStage
ExtractionFailureCode
ExtractionResult
ArtifactManifest
ArtifactManifestEntry
ValidationReport
ValidationIssue
ValidationSeverity
ExtractionStatistics
ValidatedExtraction
PreferredExtraction
InputSnapshot
```

Representative interfaces:

```csharp
public interface IToolDefinitionProvider
{
    ToolDefinition GetRequired(string toolId, string platform);
}

public interface IToolInstaller
{
    Task<ManagedToolInstallation> InstallAsync(
        ToolDefinition definition,
        bool repair,
        CancellationToken cancellationToken);
}

public interface IIl2CppExtractor
{
    Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken);
}

public interface IExtractionValidator
{
    Task<ValidationReport> ValidateAsync(
        ExtractionResult result,
        CancellationToken cancellationToken);
}

public interface IExtractionRepository
{
    Task SaveAttemptAsync(
        ExtractionAttempt attempt,
        CancellationToken cancellationToken);

    Task PromoteAsync(
        ValidatedExtraction extraction,
        bool makePreferred,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ValidatedExtraction>> ListAsync(
        string buildId,
        CancellationToken cancellationToken);

    Task<ValidatedExtraction?> GetPreferredAsync(
        string buildId,
        CancellationToken cancellationToken);
}
```

Exact implementation signatures may be split into smaller read/write interfaces during planning, but the responsibility boundaries remain fixed.

### 6.2 `S1Atlas.Extraction`

Extraction remains the boundary for Windows, Steam, third-party executables, process control, filesystem staging, and managed-assembly validation.

Focused areas:

```text
Steam/
  SteamAppManifestLocator
  SteamAppManifestParser
  SteamInstallationMetadataReader

Tools/
  RepositoryToolDefinitionProvider
  ManagedToolInstaller
  ToolDownloadClient
  ToolPackageVerifier
  SafeToolPackageInstaller
  ToolInstallationValidator

Profiles/
  RepositoryExtractionProfileProvider
  RepositoryValidationPolicyProvider

Processes/
  ProcessRunner
  ProcessRequest
  ProcessResult
  BoundedLogWriter

Cpp2Il/
  Cpp2IlProcessExtractor
  Cpp2IlArgumentBuilder

Inputs/
  ExtractionInputResolver
  LiveInputVerifier
  InputSnapshotService

Validation/
  ExtractionValidator
  OutputContainmentValidator
  ArtifactManifestBuilder
  ManagedAssemblyValidator
  ExtractionSanityValidator
  ReproducibilityValidator

Recovery/
  ExtractionRecoveryService
  ExtractionLock
```

`Cpp2IlProcessExtractor` does not download tools, access SQLite directly, select preference, or interpret symbols.

### 6.3 `S1Atlas.Storage`

Storage owns:

- schema recognition and migrations;
- SQLite backups;
- environment observation persistence;
- managed tool installation and tool-instance provenance;
- extraction attempts;
- validated extractions;
- artifacts and validation issues;
- input snapshots;
- preferred extraction state and audit events.

Large executables, logs, archived inputs, and reconstructed assemblies remain on disk. SQLite stores identities, statuses, paths, hashes, sizes, statistics, and relationships.

### 6.4 `S1Atlas.Cli`

The CLI composes services and formats human or JSON results. It contains no Cpp2IL parsing logic, archive validation logic, PE metadata logic, or SQL.

## 7. Repository-Controlled Configuration

The milestone adds:

```text
config/
  tools/
    cpp2il.win-x64.json
  extraction/
    cpp2il-reconstructed-assemblies-v1.json
  validation/
    managed-assemblies-v1.json
```

Configuration files are validated against typed schemas before use. Their canonical digests participate in extraction identity.

### 7.1 Initial Cpp2IL pin

The initial managed Windows pin is the official upstream release asset below:

```text
Tool:                Cpp2IL
Version:             2022.1.0-pre-release.21
Platform:            win-x64
Package kind:        singleFile
Asset name:          Cpp2IL-2022.1.0-pre-release.21-Windows.exe
Asset size:          15,137,811 bytes
Asset SHA-256:       663fb432433b4371fd1ee0ebc321a8fff2a9aac5ac4230c843f9e03ddee4e04c
Local executable:    Cpp2IL.exe
License:             MIT
Release published:   2026-02-22
```

Official source:

```text
https://github.com/SamboyCoding/Cpp2IL/releases/tag/2022.1.0-pre-release.21
```

Official asset:

```text
https://github.com/SamboyCoding/Cpp2IL/releases/download/2022.1.0-pre-release.21/Cpp2IL-2022.1.0-pre-release.21-Windows.exe
```

Official license:

```text
https://github.com/SamboyCoding/Cpp2IL/blob/2022.1.0-pre-release.21/LICENSE
```

The initial asset is a standalone executable, not an archive. The installer design still supports a future `archive` package kind, but archive extraction logic is not invoked for this pin.

Any change to the version, asset, size, digest, executable path, package kind, or license metadata is a reviewed toolchain change.

### 7.2 Initial extraction profile

Profile identity:

```text
Profile ID:       cpp2il-reconstructed-assemblies-v1
Profile version:  1
Adapter version:  1
Schema version:   1
```

Controlled Cpp2IL invocation:

```text
--game-path=<resolved game root>
--exe-name=Schedule I
--output-to=<Atlas staging output root>
--output-as=dll_il_recovery
```

The process is launched without a shell. Arguments are added through `ProcessStartInfo.ArgumentList` or equivalent typed construction. `NO_COLOR=true` is set in the child environment.

Profile limits:

```text
Timeout:                    30 minutes
Retained stdout limit:      64 MiB
Retained stderr limit:      64 MiB
Accepted process exit code: 0
Required assembly identity: Assembly-CSharp
```

No processor layer is enabled in profile version 1.

Cpp2IL’s `dll_il_recovery` output is selected because it produces managed DLL output intended for downstream managed tooling. This milestone validates structure and metadata only. It does not assert that generated method bodies faithfully recover native game behavior.

### 7.3 Initial validation policy

Policy identity:

```text
Policy ID:       managed-assemblies-v1
Policy version:  1
Schema version:  1
```

Initial comparative thresholds:

```text
More than 80% decrease in a major count: hard failure
More than 25% absolute difference:       warning
Large increases:                         warning unless another hard invariant fails
```

Initial absolute requirements:

```text
Managed assembly count > 0
Total type-definition count > 0
Total method-definition count > 0
Required Assembly-CSharp identity exists
Total managed output size >= 1 MiB
```

Thresholds and required identities live in the committed policy, not in scattered validator code.

## 8. Version Metadata Correction and Steam Detection

### 8.1 Terminology

`GameVersion` is renamed to `ExecutableVersion` throughout the domain, storage, CLI, JSON, documentation, and tests.

Human output becomes:

```text
Current build:       <build-id>
Executable version:  2022.3.62.7762112
Steam app ID:        <detected or unknown>
Steam build ID:      <detected or unknown>
Captured:            <UTC timestamp>
Dependencies:        3/4 installed
```

S1Atlas does not display a field named “Game version” unless a future trusted source for an actual Schedule I release label is introduced.

### 8.2 Offline Steam detection

S1Atlas derives candidate `steamapps` directories from the resolved installation path and inspects local `appmanifest_*.acf` files.

A manifest is accepted only when its normalized `installdir` resolves to the discovered Schedule I installation. S1Atlas then records the manifest’s app ID and local `buildid`.

Rules:

- no network access;
- no hard-coded Schedule I app ID required for matching;
- quoted ACF strings are parsed explicitly;
- malformed, locked, or partially written manifests produce unknown metadata rather than invented values;
- Steam app/build IDs are descriptive observations and do not alter the content-derived build ID.

## 9. Schema Migration Strategy

The existing Foundation database has no migration ledger. This milestone introduces one before structural changes.

```sql
CREATE TABLE schema_migrations (
    version         INTEGER PRIMARY KEY,
    name            TEXT NOT NULL,
    checksum        TEXT NOT NULL,
    applied_at_utc  TEXT NOT NULL
);
```

### 9.1 Foundation baseline recognition

The migrator inspects the current tables, columns, primary keys, and indexes.

If the database exactly matches the known Foundation schema, it records schema version 1 as the recognized baseline.

If the shape is unknown, S1Atlas refuses to guess and makes no changes:

```text
S1Atlas cannot recognize the existing database schema.
No migration was attempted.
```

### 9.2 Backup

Before the first structural migration, S1Atlas uses SQLite’s backup API to create:

```text
%LOCALAPPDATA%\S1Atlas\backups\atlas-before-schema-2-<UTC timestamp>.db
```

The backup is created before altering tables and is retained after a successful migration.

### 9.3 Version 2 metadata migration

The authoritative build table becomes:

```text
builds
  build_id
  game_assembly_sha256
  metadata_sha256
  first_seen_at_utc
  is_valid
```

Environment snapshots gain:

```text
identity_version
executable_version
steam_app_id
steam_build_id
installation_root
 game_assembly_path
 global_metadata_path
```

The leading spaces before `game_assembly_path` and `global_metadata_path` above are formatting only; the actual column names contain no leading whitespace.

Migration behavior:

- existing build IDs and hashes are unchanged;
- `first_seen_at_utc` is copied from the prior build scan time;
- prior `game_version` values are copied to `executable_version` for snapshots referencing that build;
- prior Steam build values are copied to snapshot observations;
- existing dependencies remain attached to the same snapshot IDs;
- `atlas_state.current_snapshot_id` remains unchanged;
- installation path fields begin as null where the Foundation did not persist them;
- the next successful scan or matching extraction resolution may create a version-2 environment snapshot containing complete observations.

Existing snapshot IDs are preserved with `identity_version = 1`. New snapshots use identity version 2 and include the corrected metadata and normalized installation observations in their identity calculation.

The migration is transactional and idempotent. A failed migration leaves the original database usable.

### 9.4 Extraction schema migrations

Later schema versions in this same milestone add tool, attempt, extraction, artifact, validation, preference, and input-snapshot tables. Every migration has a committed checksum and an idempotence test.

## 10. Identity Model

S1Atlas distinguishes game content, tool bytes, controlled settings, process attempts, and resulting output.

### 10.1 Existing game build ID

The existing Foundation build ID remains authoritative and is not retroactively changed.

```text
build_id = existing BuildFingerprint algorithm over
           GameAssembly.dll SHA-256 and global-metadata.dat SHA-256
```

Executable version, Steam IDs, path, and timestamps are excluded.

### 10.2 Canonical identity writer

New deterministic IDs use `CanonicalHashWriter v1`.

Rules:

1. Start with an identity-kind string and identity schema version.
2. Encode each scalar as a four-byte little-endian UTF-8 byte length followed by the UTF-8 bytes.
3. Encode null distinctly from an empty string.
4. Encode integers using invariant decimal text.
5. Prefix collections with their item count.
6. Sort semantically unordered collections using documented ordinal comparers before writing.
7. Normalize relative paths to `/`.
8. Exclude timestamps, process IDs, machine names, and absolute paths from reproducibility identities.
9. Hash the resulting bytes with SHA-256 and store lower-case hexadecimal.

Readable JSON manifests are separate from the canonical identity encoding. JSON whitespace and property ordering therefore cannot change an ID.

### 10.3 Tool instance ID

```text
tool_instance_id = SHA256 canonical identity of:
  tool name
  observed executable SHA-256
  platform
  trust level
```

The absolute executable path is recorded as provenance but excluded from identity.

Trust levels:

```text
ManagedPinned
CustomOverride
```

### 10.4 Profile and policy digests

The extraction profile and validation policy each have:

```text
friendly ID
integer version
canonical content digest
```

Changing any effective profile or policy field changes its digest even if a friendly name is accidentally left unchanged.

### 10.5 Recipe ID

```text
recipe_id = SHA256 canonical identity of:
  game build ID
  tool instance ID
  extraction profile digest
  validation policy digest
  Cpp2IL adapter version
  extraction schema version
```

Absolute paths, timestamps, and attempt-specific facts are excluded.

### 10.6 Attempt ID

Each actual process run receives a random GUID attempt ID. Retries of the same recipe remain separately inspectable.

### 10.7 Artifact manifest digest

The artifact manifest is sorted by normalized relative path and hashed canonically.

### 10.8 Extraction ID

```text
extraction_id = SHA256 canonical identity of:
  recipe ID
  artifact manifest digest
```

The same recipe producing different bytes creates a distinct extraction rather than overwriting history.

The same recipe producing byte-identical output links the new attempt to the existing extraction and discards duplicate reconstructed files.

### 10.9 Input snapshot ID

```text
input_snapshot_id = SHA256 canonical identity of:
  game build ID
  canonical archived input manifest digest
```

## 11. Manifest Model

### 11.1 Repository tool definition

`config/tools/cpp2il.win-x64.json` records:

```text
schema version
tool ID and display name
version
platform
package kind
official source and release URLs
asset name
expected byte size
expected SHA-256
local executable relative path
license SPDX identifier and source
probe requirements
package safety limits
```

### 11.2 Managed installation record

`installation.json` records local facts:

```text
tool-definition digest
downloaded package digest
observed executable digest
installation time
install root
probe command and result
trust level
installation status
quarantine predecessor, when repaired
```

### 11.3 Build manifest

`builds/<build-id>/build.json` stores immutable content facts:

```text
build ID
GameAssembly SHA-256
global-metadata SHA-256
first-seen timestamp
manifest schema version
```

Mutable path and Steam observations are not placed in this immutable file.

### 11.4 Attempt manifest

`attempt.json` records one execution:

```text
attempt ID
recipe ID
build ID
tool instance and trust
profile and validation-policy identities
input source: live or archived
resolved input paths
full pre-run input manifest
full post-run input manifest
working and output directories
exact argument list
child environment overrides
start/end timestamps
profile timeout
process ID
exit code
terminal status
failure stage, code, and message
log truncation facts
partial-output retention or deletion facts
result extraction ID, when successful
```

The manifest may be updated while active. It becomes immutable at a terminal state.

### 11.5 Artifact manifest

Each promoted file records:

```text
normalized relative path
artifact kind
byte size
SHA-256
managed assembly identity, when applicable
module identity, when applicable
type/method/field/property/event counts, when applicable
```

Absolute paths do not appear in artifact entries.

### 11.6 Validation report

`validation.json` records:

```text
policy identity
overall outcome
hard checks
warnings and errors
input-integrity result
process-integrity result
output-containment result
artifact-manifest digest
assembly and aggregate statistics
comparative statistics
reproducibility result
preference eligibility
```

### 11.7 Extraction manifest

`extraction.json` records immutable accepted facts:

```text
extraction ID
recipe ID
source attempt ID
build ID
tool/profile/policy provenance
artifact-manifest digest
statistics
creation timestamp
trust level
```

Preferred status is mutable and is not stored in this immutable manifest.

### 11.8 Complete marker

`complete.marker` is written last and contains:

```text
marker schema version
extraction ID
artifact manifest digest
extraction manifest SHA-256
validation report SHA-256
```

Downstream systems trust an extraction only when the database, marker, manifests, and current artifact hashes agree.

## 12. Filesystem Model

```text
%LOCALAPPDATA%\S1Atlas\
|-- atlas.db
|-- backups\
|   `-- atlas-before-schema-<version>-<timestamp>.db
|
|-- tools\
|   |-- .staging\
|   |-- quarantine\
|   `-- cpp2il\
|       `-- <version>\
|           |-- tool-manifest.json
|           |-- installation.json
|           `-- Cpp2IL.exe
|
`-- builds\
    `-- <build-id>\
        |-- build.json
        |
        |-- inputs\
        |   `-- <input-snapshot-id>\
        |       |-- complete.marker
        |       |-- input-manifest.json
        |       `-- game-root\...
        |
        |-- attempts\
        |   `-- <attempt-id>\
        |       |-- attempt.json
        |       |-- validation.json
        |       |-- logs\
        |       |   |-- stdout.log
        |       |   `-- stderr.log
        |       `-- retained-output\     only when explicitly requested
        |
        `-- extractions\
            |-- .staging\
            |   `-- <attempt-id>\
            |       |-- attempt.json
            |       |-- output\
            |       `-- logs\
            `-- <extraction-id>\
                |-- complete.marker
                |-- extraction.json
                |-- validation.json
                |-- artifact-manifest.json
                |-- reconstructed\
                `-- logs\
```

All paths are resolved under the configured Atlas data root, including a `S1ATLAS_HOME` override.

Validated extraction directories are immutable from S1Atlas’s perspective. S1Atlas never edits artifacts in place. External changes are detected by integrity verification and cause the extraction to become unavailable until repaired or re-created.

## 13. Input Resolution and Optional Archival

### 13.1 Build selection

```text
s1atlas extract
```

uses the current Atlas build.

```text
s1atlas extract --build <build-id>
```

selects an existing immutable build record.

### 13.2 Live input resolution order

```text
1. Explicit --game-path
2. Stored installation observations, newest first
3. Conventional Steam discovery
4. Verified archived input snapshot
```

A live candidate is accepted only when the current hashes of `GameAssembly.dll` and `global-metadata.dat` match the selected build.

### 13.3 Live input integrity

Before Cpp2IL starts:

```text
pre-run GameAssembly hash == selected build hash
pre-run metadata hash     == selected build hash
```

After Cpp2IL exits:

```text
post-run GameAssembly hash == selected build hash
post-run metadata hash     == selected build hash
pre-run hashes             == post-run hashes
```

Any mismatch invalidates the attempt as `InputChangedDuringExtraction`.

S1Atlas records file sizes, last-write timestamps, and hashes, but only hashes determine acceptance.

### 13.4 Live game access

Cpp2IL receives the live game root through `--game-path` by default. Its working directory and `--output-to` path are always Atlas-owned staging paths.

S1Atlas does not claim that pointing a third-party executable at the game path is a complete operating-system sandbox. The trust model instead relies on:

- an official pinned executable;
- an expected SHA-256;
- explicit installation;
- no shell invocation;
- controlled arguments;
- Atlas-owned working/output paths;
- pre/post game input verification;
- unchanged-game smoke tests.

### 13.5 Optional input snapshots

`--snapshot-inputs` deliberately retains a replayable minimal game-root structure.

The initial profile archives:

```text
Required content inputs:
  GameAssembly.dll
  Schedule I_Data/il2cpp_data/Metadata/global-metadata.dat

Required executable/support input:
  Schedule I.exe

Required Unity-version source:
  the first existing file in this priority order:
    Schedule I_Data/globalgamemanagers
    Schedule I_Data/data.unity3d
```

Relative paths are preserved under `game-root`.

Each archived file is copied, re-hashed, and compared with its source before the input snapshot is promoted. Every support input receives its own recorded hash even though only `GameAssembly.dll` and metadata define the Atlas build ID.

The local smoke gate must prove that an archived-only extraction works. If Cpp2IL demonstrably requires an additional support file, the committed profile is updated through review to add the smallest additional required input. S1Atlas does not mark a snapshot replayable until an archived-only capability test succeeds.

A successfully verified input snapshot is retained even if the subsequent extraction fails because the user explicitly requested archival.

## 14. Managed Tool Installation

### 14.1 Network boundary

Only this command may download a tool:

```text
s1atlas tools install cpp2il
```

These commands remain offline:

```text
scan
status
env
builds
tools status
extract
extractions ...
```

### 14.2 Installation flow

For the initial single-file pin:

```text
load and validate committed definition
        |
check for an existing verified installation
        |
download to tools/.staging
        |
verify exact byte size and SHA-256
        |
write Cpp2IL.exe in staging
        |
run controlled capability probes
        |
write tool-manifest.json and installation.json
        |
atomically rename to tools/cpp2il/<version>
```

Required capability probes:

```text
Cpp2IL.exe --help
Cpp2IL.exe --list-output-formats
```

The probe must complete successfully and the output-format listing must contain `dll_il_recovery`.

### 14.3 Generic package safety

The tool-definition model supports `singleFile` and `archive` package kinds.

For a future archive definition, the installer rejects:

- absolute archive paths;
- `..` path traversal;
- entries escaping staging after canonicalization;
- case-insensitive destination collisions;
- symbolic-link, hard-link, or reparse-point entries;
- package size or expanded-size limits from the committed definition;
- excessive file counts;
- a missing declared executable.

These checks are tested even though the initial pin is a single file.

### 14.4 Idempotence and repair

A verified installation produces a successful no-op.

A present but invalid installation is not overwritten silently. Normal installation reports that repair is required.

```text
s1atlas tools install cpp2il --repair
```

moves the invalid installation to a dated quarantine directory and installs a fresh verified copy. A failed repair leaves the last verified installation available.

## 15. Tool Trust and Preference

### 15.1 Managed tool

A managed tool is trusted as `ManagedPinned` only when:

- the repository definition is valid;
- package and executable hashes match;
- capability probes pass;
- the executable remains unchanged immediately before execution.

A modified managed executable is classified as corrupt. It is not silently treated as custom.

### 15.2 Custom executable

```text
s1atlas extract --cpp2il-path <path>
```

uses a `CustomOverride` tool instance.

S1Atlas:

- verifies the path exists;
- hashes the executable immediately before running it;
- performs the same capability probe;
- records its exact observed hash and path;
- runs it through the same controlled profile;
- never automatically makes its output preferred.

## 16. Extraction Attempt Lifecycle

Lifecycle states:

```text
Created
Preparing
Running
Validating
Succeeded
Failed
Canceled
Abandoned
```

Terminal states:

```text
Succeeded
Failed
Canceled
Abandoned
```

`Succeeded` means the attempt mapped to a structurally validated extraction. It does not by itself imply automatic preference.

### 16.1 Failure stages

```text
ToolResolution
InputResolution
PreRunInputVerification
InputSnapshotCreation
AttemptPersistence
ProcessStart
ProcessExecution
PostRunInputVerification
OutputContainment
ArtifactValidation
AssemblyValidation
SanityValidation
ReproducibilityValidation
FilesystemPromotion
DatabasePromotion
Recovery
```

### 16.2 Failure codes

Representative stable codes:

```text
ToolNotInstalled
ToolDefinitionInvalid
ToolChecksumMismatch
ToolProbeFailed
BuildNotFound
LiveInputNotFound
BuildInputMismatch
ArchivedInputInvalid
ProcessStartFailed
ProcessTimedOut
ProcessExitNonZero
OperationCanceled
InputChangedDuringExtraction
OutputOutsideStaging
NoArtifactsProduced
NoManagedAssembliesProduced
EmptyArtifact
InvalidManagedAssembly
RequiredAssemblyMissing
DuplicateAssemblyIdentity
CatastrophicSanityDeviation
SameRecipeDifferentOutput
FilesystemPromotionFailed
DatabasePromotionFailed
InterruptedProcess
IntegrityMismatch
```

Failures have a machine-readable stage/code and a concise human message.

### 16.3 Interrupted work

On startup of a tool or extraction command, recovery inspects:

- nonterminal database attempts;
- extraction lock ownership;
- recorded child process IDs;
- staging directories;
- complete extraction directories missing database registration;
- database rows missing required filesystem evidence.

A nonterminal attempt whose process no longer exists becomes `Abandoned`.

Recovery completes a promotion only when complete manifests, marker, hashes, and provenance prove what happened. Otherwise it preserves evidence and quarantines ambiguous files. It never guesses.

## 17. Process Execution

### 17.1 Isolation

Cpp2IL is launched with:

```text
UseShellExecute = false
CreateNoWindow = true
RedirectStandardOutput = true
RedirectStandardError = true
WorkingDirectory = Atlas attempt staging directory
```

Arguments are passed as an argument list, never through `cmd.exe`, PowerShell, or a free-form shell string.

### 17.2 Concurrency

Only one active extraction process is permitted per Atlas data root in this milestone.

The lock records:

```text
attempt ID
owning S1Atlas process ID
child process ID, when started
start timestamp
```

A second extraction reports the active attempt. Read-only commands remain usable.

A stale lock is cleared only after recovery proves the owning process is gone.

### 17.3 Cancellation

`Program` wires `Console.CancelKeyPress` to a cancellation token.

On `Ctrl+C`, S1Atlas:

1. cancels the operation;
2. terminates Cpp2IL and its child process tree;
3. drains available stdout/stderr;
4. marks the attempt `Canceled`;
5. applies failed-output retention policy;
6. returns exit code 2.

### 17.4 Timeout

At the 30-minute profile timeout, S1Atlas terminates the process tree, records `ProcessTimedOut`, retains diagnostic evidence, and rejects all partial output.

### 17.5 Bounded logs

S1Atlas always consumes both redirected streams to avoid deadlock.

After 64 MiB retained per stream, it stops retaining additional bytes but continues consuming them. The log receives a truncation marker, and the attempt records discarded byte counts.

## 18. Validation Pipeline

Validation outcomes:

```text
Valid
ValidWithWarnings
Invalid
```

Warning classes:

```text
Informational
PreferenceBlocking
```

### 18.1 Tool provenance

Verify the tool definition, installation record, executable existence, current executable hash, probe results, platform, and trust level.

### 18.2 Input integrity

Verify selected-build hashes before and after execution. For archived inputs, verify marker, manifest digest, every file hash, and build ownership.

### 18.3 Process result

Hard requirements:

- process started;
- process reached a terminal state under S1Atlas control;
- process was not canceled or timed out;
- exit code is accepted by the profile;
- stdout and stderr were drained;
- output directory exists.

### 18.4 Output containment

For every output entry:

- canonical path remains under attempt output root;
- no reparse point is followed;
- manifest path is relative;
- no traversal segments remain;
- paths are unique case-insensitively on Windows.

Any path escape invalidates the attempt.

### 18.5 Artifact inventory

Hard requirements:

- at least one artifact exists;
- at least one non-empty DLL exists;
- every retained file can be opened and hashed;
- artifacts do not change between manifest creation and promotion;
- the canonical artifact-manifest digest can be reproduced.

Every promoted file is inventoried, not just DLLs.

### 18.6 Managed assembly validation

Use `PEReader` and `MetadataReader`; do not load reconstructed assemblies into the S1Atlas runtime.

For each DLL determine:

```text
readable PE status
managed metadata status
assembly identity
module identity
type definitions
method definitions
field definitions
properties
events
```

Hard failures:

```text
unreadable/truncated PE for an artifact classified as managed
invalid managed metadata
metadata tables cannot be enumerated
required Assembly-CSharp identity absent
zero aggregate type definitions
zero aggregate method definitions
conflicting duplicate assembly identities
```

Native DLLs are inventoried as non-managed artifacts and do not fail the extraction unless a committed policy explicitly requires them to be managed.

Identical duplicate assembly identities are retained exactly as produced and reported as an informational warning. Conflicting duplicate identities with different bytes are invalid. V1 does not rewrite or deduplicate accepted output.

### 18.7 Absolute sanity

Apply the committed minimum requirements from `managed-assemblies-v1`.

The first successful real extraction establishes observed Schedule I statistics for later comparison. Exact game-specific type or method counts are not permanently hard-coded into generic validator code.

### 18.8 Comparative sanity

When a preferred extraction exists for the same build, compare:

```text
managed assembly count
type count
method count
total managed bytes
required assembly identities
per-assembly counts
```

More than an 80% decrease in a major count is invalid.

More than a 25% absolute difference is a warning.

Large increases are warnings unless they violate another hard invariant.

### 18.9 Reproducibility

For an existing recipe:

```text
same artifact-manifest digest
  -> link attempt to existing extraction
  -> discard duplicate reconstructed output

different artifact-manifest digest
  -> create a distinct validated extraction
  -> add preference-blocking SameRecipeDifferentOutput warning
```

### 18.10 Preference eligibility

Automatic preference requires all of:

```text
outcome is Valid or acceptable ValidWithWarnings
tool trust is ManagedPinned
input integrity passed
filesystem integrity passed
no preference-blocking warning
```

A current managed pin may automatically replace an older preferred extraction after a successful toolchain upgrade. The audit reason is `ReplacementAfterToolUpgrade`.

## 19. Promotion and Recovery

SQLite cannot include a directory rename in its transaction. Promotion is a recoverable two-phase operation.

### 19.1 Filesystem finalization

After validation:

1. write immutable extraction, validation, and artifact manifests;
2. move logs into the candidate extraction;
3. calculate the extraction ID;
4. write `complete.marker` last;
5. atomically rename staging to `extractions/<extraction-id>` on the same volume.

### 19.2 SQLite commit

In one transaction:

1. insert or link the validated extraction;
2. insert artifacts and validation issues;
3. mark the attempt `Succeeded`;
4. link the attempt to its extraction;
5. update preferred extraction when eligible;
6. record a preference audit event.

SQLite never intentionally points at a staging directory.

### 19.3 Crash recovery

A crash after filesystem rename but before database commit may leave a complete unregistered directory. Recovery verifies its marker, manifests, hashes, tool/profile provenance, and source attempt before registering it.

A database extraction row whose directory or marker is missing is an integrity failure and is never served downstream.

## 20. SQLite Data Model

### 20.1 `managed_tool_installations`

```text
tool_id
version
platform
definition_digest
package_sha256
executable_sha256
root_path
status
installed_at_utc
last_verified_at_utc
probe_summary
```

Primary key:

```text
(tool_id, version, platform)
```

### 20.2 `tool_instances`

```text
tool_instance_id
tool_name
version_label
platform
trust_level
definition_digest
package_sha256
executable_sha256
observed_path
first_observed_at_utc
last_verified_at_utc
status
```

This preserves provenance for custom tools even if their files later move.

### 20.3 `extraction_attempts`

```text
attempt_id
recipe_id
build_id
tool_instance_id
profile_id
profile_version
profile_digest
validation_policy_id
validation_policy_version
validation_policy_digest
adapter_version
extraction_schema_version
input_source
input_snapshot_id
status
created_at_utc
started_at_utc
completed_at_utc
pre_input_manifest_digest
post_input_manifest_digest
working_path
stdout_path
stderr_path
stdout_truncated
stderr_truncated
stdout_discarded_bytes
stderr_discarded_bytes
process_id
process_exit_code
failure_stage
failure_code
failure_message
keep_failed_artifacts
discarded_file_count
discarded_byte_count
result_extraction_id
```

Indexes support build, recipe, status, creation time, and resulting extraction queries.

### 20.4 `validated_extractions`

```text
extraction_id
recipe_id
build_id
tool_instance_id
source_attempt_id
artifact_manifest_digest
root_path
created_at_utc
trust_level
validation_outcome
assembly_count
managed_assembly_count
type_count
method_count
field_count
property_count
event_count
total_output_bytes
total_managed_bytes
```

### 20.5 `extraction_artifacts`

```text
extraction_id
relative_path
kind
size
sha256
assembly_name
module_name
type_count
method_count
field_count
property_count
event_count
```

Primary key:

```text
(extraction_id, relative_path)
```

Case-insensitive path uniqueness is validated before insertion.

### 20.6 `extraction_validation_issues`

```text
attempt_id
ordinal
severity
code
message
artifact_relative_path
preference_blocking
```

### 20.7 `preferred_extractions`

```text
build_id PRIMARY KEY
extraction_id
selected_at_utc
selection_reason
```

Selection reasons:

```text
ManagedAutomatic
ManualPromotion
ReplacementAfterToolUpgrade
```

### 20.8 `extraction_preference_events`

```text
event_id
build_id
previous_extraction_id
new_extraction_id
selected_at_utc
selection_reason
```

### 20.9 Archived inputs

```text
input_snapshots
  input_snapshot_id
  build_id
  root_path
  manifest_digest
  created_at_utc
  replay_verified
  replay_verified_at_utc

input_snapshot_files
  input_snapshot_id
  relative_path
  role
  size
  sha256
```

## 21. CLI Contract

### 21.1 Commands

```text
s1atlas tools status [cpp2il] [--json]
s1atlas tools install cpp2il [--repair] [--json]

s1atlas extract [--build <id>]
                [--game-path <path>]
                [--cpp2il-path <path>]
                [--profile <profile-id>]
                [--retry]
                [--snapshot-inputs]
                [--keep-failed-artifacts]
                [--json]

s1atlas extractions list [--build <id>]
                          [--include-failed]
                          [--json]

s1atlas extractions show <attempt-or-extraction-id> [--json]
s1atlas extractions promote <extraction-id> [--json]

s1atlas extractions cleanup [--older-than <duration>]
                             [--apply]
                             [--json]
```

Foundation commands gain JSON output while being updated for corrected metadata:

```text
s1atlas status --json
s1atlas env --json
s1atlas builds --json
```

### 21.2 JSON envelope

JSON-mode stdout contains exactly one final document:

```json
{
  "schemaVersion": 1,
  "command": "extract",
  "success": false,
  "exitCode": 1,
  "data": null,
  "error": {
    "attemptId": "...",
    "stage": "PostRunInputVerification",
    "code": "InputChangedDuringExtraction",
    "message": "The Schedule I inputs changed while extraction was running."
  }
}
```

Progress messages in JSON mode go to stderr or are suppressed. Standard output remains parseable JSON.

### 21.3 Exit codes

```text
0  success, including intentional no-op/reuse/preview
1  operational, integrity, or validation failure
2  cancellation
```

### 21.4 Normal no-op behavior

If the exact recipe already has a valid extraction and `--retry` is absent, `extract` returns success without launching Cpp2IL.

### 21.5 Custom preference

A valid custom extraction is preserved but preference remains unchanged. The CLI explains how to promote it explicitly.

### 21.6 Human errors

Normal human output never includes a raw stack trace.

Attempt metadata and diagnostic logs may record exception type and stack details for maintainers.

## 22. Retention and Cleanup

### 22.1 Validated extractions

Retain indefinitely until a future dedicated deletion feature is designed. This milestone’s cleanup command never deletes validated extractions.

### 22.2 Failed attempts

Always retain:

- attempt manifest;
- validation report;
- bounded stdout and stderr;
- failure stage/code/message;
- discarded file/byte statistics.

Delete partial output after terminal failure unless `--keep-failed-artifacts` was selected.

Retained partial output lives under:

```text
attempts/<attempt-id>/retained-output
```

It is permanently quarantined from downstream use.

### 22.3 Cleanup defaults

Without `--older-than`, cleanup uses 30 days.

Without `--apply`, cleanup is preview-only and returns exit code 0.

Eligible data:

- failed, canceled, or abandoned attempts older than the threshold;
- their retained partial output;
- stale staging directories proven not to belong to a live process;
- quarantined failed tool installations older than the threshold.

Not eligible:

- validated extractions;
- preferred state or audit history;
- verified managed tools;
- archived input snapshots;
- active or ambiguous attempts.

## 23. Error Examples

### Missing tool

```text
The pinned Cpp2IL tool is not installed.

Run:
  s1atlas tools install cpp2il
```

### Live installation changed

```text
The live Schedule I installation no longer matches the selected Atlas build.

Selected build:
  6fbd38f8401afa...

Run:
  s1atlas scan
```

### Process failure

```text
Cpp2IL exited unsuccessfully.

Attempt: <attempt-id>
Stage:   ProcessExecution
Code:    ProcessExitNonZero
Exit:    1
Logs:    <Atlas attempt logs path>

No validated extraction was created.
```

### Input changed during execution

```text
The Schedule I inputs changed while extraction was running.
The output was rejected because it may combine different game builds.

Run:
  s1atlas scan
```

### Structural validation failure

```text
Cpp2IL produced output, but a required reconstructed assembly was not valid.

Attempt:  <attempt-id>
Stage:    AssemblyValidation
Code:     InvalidManagedAssembly
Artifact: reconstructed/Assembly-CSharp.dll
```

## 24. Testing Strategy

### 24.1 Core tests

Verify:

- canonical identity encoding;
- tool-instance IDs;
- profile and policy digests;
- recipe IDs;
- extraction IDs;
- timestamps and absolute paths excluded from reproducibility IDs;
- trust and preference rules;
- warning classification;
- lifecycle transition validation;
- cleanup-duration parsing.

### 24.2 Steam metadata tests

Use fixture ACF files to verify:

- installation-directory matching;
- app ID and build ID extraction;
- unrelated manifests ignored;
- quoted values parsed;
- malformed/partial manifests yield unknown metadata;
- no network access.

### 24.3 Migration tests

Start with a real Foundation-shaped SQLite fixture containing a current build, environment snapshot, dependencies, and Atlas state.

Verify:

- exact baseline recognition;
- backup creation;
- transactional migration;
- executable-version relabeling;
- build IDs/hashes unchanged;
- dependencies and current pointer preserved;
- identity-version handling;
- idempotence;
- unknown schemas rejected untouched;
- intentionally failed migration leaves the original database usable.

### 24.4 Tool installer tests

Use injected local byte streams and fixture packages, never live GitHub downloads in CI.

Verify:

- single-file install;
- idempotent reinstall;
- size mismatch;
- checksum mismatch;
- missing executable;
- probe failure;
- interrupted install;
- repair/quarantine;
- failed repair preserves last valid installation;
- archive traversal, absolute paths, case collisions, link entries, size limits, and file-count limits.

### 24.5 Process runner tests

Build a test-only fake executable with modes:

```text
success
nonzero-exit
timeout
large-stdout
large-stderr
spawn-child
partial-output
valid-managed-output
malformed-dll
```

Verify capture, truncation without deadlock, exit codes, timeout, cancellation, child-tree termination, process-start failures, working-directory isolation, exact argument passing, and no shell interpolation.

### 24.6 Input tests

Verify:

- current-build resolution;
- explicit build selection;
- explicit path priority;
- stored observation fallback;
- Steam fallback;
- pre-run mismatch rejection;
- post-run change rejection;
- input snapshot creation and deduplication;
- invalid archived input rejection;
- archived-only replay capability.

### 24.7 Validator tests

Fixtures include:

- valid managed assemblies;
- native DLLs;
- empty files;
- malformed PE files;
- truncated metadata;
- missing required assembly;
- identical duplicate assembly identities;
- conflicting duplicate assembly identities;
- output path escapes;
- case-colliding paths;
- catastrophic count reduction;
- moderate deviation;
- same recipe/same output;
- same recipe/different output.

### 24.8 Storage tests

Verify:

- attempt persisted before process execution;
- legal lifecycle transitions;
- terminal attempt immutability;
- atomic extraction/artifact commit;
- failed validation creates no validated extraction;
- managed auto-preference;
- custom no-auto-preference;
- manual promotion audit;
- no cross-build promotion;
- duplicate output links rather than duplicates;
- transaction failure leaves no false preference.

### 24.9 Recovery tests

Simulate:

```text
staging exists and DB says Running, process gone
filesystem promoted but DB not committed
DB row exists but complete.marker is missing
complete directory contains invalid manifest
stale lock owner is gone
ambiguous live process still exists
```

Recovery either completes a provably valid action or quarantines evidence. It never guesses.

### 24.10 CLI tests

Verify human and JSON modes for all new commands plus `status`, `env`, and `builds`.

Verify:

- `extract` never downloads;
- missing tool gives install instructions;
- changed live build recommends scan;
- existing recipe is a successful no-op;
- cleanup previews by default;
- cleanup never deletes validated output;
- errors contain no raw stack trace;
- exit codes remain 0/1/2.

## 25. Real Schedule I Smoke Gate

Proprietary inputs and reconstructed assemblies remain local and ignored by Git.

Required local sequence:

```cmd
s1atlas tools status cpp2il
s1atlas tools install cpp2il
s1atlas tools status cpp2il

s1atlas extract
s1atlas extractions list
s1atlas extractions show <extraction-id>

s1atlas extract
s1atlas extract --retry
```

Expected:

- managed tool verifies against the committed pin;
- first extraction validates;
- a preferred extraction is selected;
- second normal extraction is a no-op;
- retry creates a new attempt;
- identical retry output links to the existing extraction;
- game hashes are unchanged before/after;
- Schedule I directory contents remain unchanged;
- all generated content is under the Atlas data root;
- repository remains clean.

Also run one deliberate failure using the fake tool or a controlled validation fixture to prove the prior preferred extraction remains intact.

A non-proprietary report is committed under:

```text
docs/smoke-tests/<date>-schedule-i-cpp2il-extraction.md
```

It may contain IDs, hashes, tool version, counts, sizes, timings, warnings, and pass/fail results. It may not contain game binaries, reconstructed DLLs, decompiled code, or long proprietary symbol listings.

## 26. Implementation Phases

This milestone is implemented through five independently reviewable pull requests.

### Phase 1 — Metadata correction and migration

Deliver:

- migration ledger and checksums;
- Foundation schema recognition;
- SQLite backup;
- version-2 metadata migration;
- executable-version terminology;
- offline Steam metadata;
- installation observations;
- Foundation JSON output;
- migration and compatibility tests.

No Cpp2IL process runs in Phase 1.

### Phase 2 — Managed tool supply chain

Deliver:

- committed tool-definition schema and initial pin;
- managed tool cache;
- single-file and safe-archive installation paths;
- size/hash verification;
- capability probes;
- repair and quarantine;
- `tools status`;
- `tools install cpp2il`;
- offline CI tests.

### Phase 3 — Extraction orchestration

Deliver:

- committed extraction profile;
- typed Cpp2IL argument builder;
- process runner;
- bounded logs;
- attempt lifecycle persistence;
- input resolution and pre/post verification;
- optional input snapshots;
- extraction lock;
- timeout/cancellation;
- `extract`;
- failed-attempt retention.

Phase 3 output is not authoritative until Phase 4 validation is present.

### Phase 4 — Validation, immutable promotion, and history

Deliver:

- validation policy;
- containment validation;
- artifact manifests;
- managed PE/metadata validation;
- sanity checks;
- reproducibility comparison;
- extraction identity;
- two-phase promotion;
- recovery;
- preference rules;
- `extractions list`;
- `extractions show`;
- `extractions promote`.

### Phase 5 — Cleanup, documentation, and real smoke test

Deliver:

- cleanup preview/apply;
- stale-attempt recovery hardening;
- command documentation;
- privacy/source-control checks;
- full Windows CI verification;
- real extraction against the reference build;
- archived-only replay verification;
- non-proprietary smoke report;
- final review and QA gate.

Each phase keeps the existing Foundation commands usable.

## 27. Definition of Done

The milestone is complete only when all are true:

```text
[ ] Existing atlas.db migrates without losing current build, dependencies, or current pointer
[ ] A pre-migration SQLite backup is created
[ ] “Game version” is replaced by accurate executable-version terminology
[ ] Local Steam app/build metadata is detected offline when available
[ ] The exact Cpp2IL pin is repository controlled
[ ] Managed installation verifies size, checksum, executable, and required output format
[ ] Tool installation is explicit, staged, idempotent, repairable, and atomic
[ ] extract performs no hidden network access
[ ] Custom executables are fingerprinted and assigned CustomOverride trust
[ ] Raw Cpp2IL arguments cannot be passed through the CLI
[ ] Live inputs are verified before and after execution
[ ] Historical extraction requires matching live inputs or replay-verified archived inputs
[ ] Cpp2IL working and output directories are Atlas owned
[ ] Timeout and Ctrl+C terminate the complete child process tree
[ ] Every attempt has durable metadata and bounded logs
[ ] Failed partial output follows the approved retention policy
[ ] Every promoted artifact has a normalized path, size, and SHA-256
[ ] Reconstructed assemblies pass managed PE and metadata validation
[ ] Required Assembly-CSharp is present
[ ] Catastrophically incomplete output is rejected
[ ] Same-recipe differing output cannot silently replace preferred output
[ ] Validated extraction directories are immutable and integrity checked
[ ] Filesystem/database promotion recovers safely after interruption
[ ] Managed pinned output may become preferred automatically
[ ] Custom or preference-blocked output requires explicit promotion
[ ] Cleanup previews by default and never deletes validated extractions
[ ] Human and JSON output are supported
[ ] Existing Foundation commands continue working
[ ] All automated tests pass with zero build warnings
[ ] A real extraction succeeds for build 6fbd38f8401afa2241a1322afd4b8a8eadc99aa1f1c660ece253da7859d54bdc
[ ] An archived-only retry succeeds for the profile’s declared replay input set
[ ] A repeat extraction is a no-op without --retry
[ ] An identical retry links to existing output rather than duplicating it
[ ] The real Schedule I installation remains unchanged
[ ] No proprietary game data or reconstructed assemblies enter Git or CI artifacts
```

## 28. Hard Invariants

```text
A failed attempt cannot create a validated extraction.
A validated extraction cannot reference changed live inputs.
A validated extraction directory is immutable.
An artifact path cannot escape its extraction root.
A custom tool cannot become preferred automatically.
A preferred extraction must be validated.
A preferred extraction must belong to the same game build.
A database row is not trusted without its complete filesystem marker.
Historical build hashes are never rewritten.
Hashes alone do not claim that historical bytes are available.
A structural validation result does not claim behavioral decompilation accuracy.
Failed or retained partial output never feeds ILSpy, indexing, docs, or MCP.
Cleanup never deletes validated extractions in this milestone.
Only tools install may perform network access.
```

## 29. Follow-On Milestone

The next independent design and implementation cycle consumes only the preferred validated extraction and adds:

```text
ILSpy decompilation
source-file generation
normalized assemblies/namespaces/types/methods/fields/properties/parameters
source locations and fingerprints
initial search/type/method/source CLI commands
capability assessment for meaningful reconstructed method bodies
```

That follow-on must not bypass the provenance, validation, or preference boundaries established here.

## 30. Upstream References

Cpp2IL release and asset metadata:

```text
https://github.com/SamboyCoding/Cpp2IL/releases/tag/2022.1.0-pre-release.21
```

Cpp2IL command-line documentation for `--game-path`, `--output-as`, and `--output-to`:

```text
https://github.com/SamboyCoding/Cpp2IL/blob/2022.1.0-pre-release.21/README.md
```

Cpp2IL output-format implementation identifying `dll_il_recovery`:

```text
https://github.com/SamboyCoding/Cpp2IL/blob/2022.1.0-pre-release.21/Cpp2IL.Core/OutputFormats/AsmResolverDllOutputFormatIlRecovery.cs
```

Cpp2IL license:

```text
https://github.com/SamboyCoding/Cpp2IL/blob/2022.1.0-pre-release.21/LICENSE
```
