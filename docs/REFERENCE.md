# S1Atlas reference

Deep internals: on-disk data layout, build/environment identity, the managed
Cpp2IL pin, and the validation policy. For usage and commands, see
[USAGE.md](USAGE.md); for an overview, see the [README](../README.md).

## Local data location

By default, Atlas data is stored at:

```text
%LOCALAPPDATA%\S1Atlas
```

Override that location with the `S1ATLAS_HOME` environment variable:

```powershell
$env:S1ATLAS_HOME = "C:\S1Atlas Data"
dotnet run --project src/S1Atlas.Cli -- status
```

When an existing recognized Foundation database requires migration, S1Atlas creates one recoverable SQLite backup under:

```text
%LOCALAPPDATA%\S1Atlas\backups
```

An existing schema-version-2 database can produce one
`atlas-before-schema-3-*.db` backup when managed-tool provenance tables are
added, and a schema-version-4 database produces one `atlas-before-schema-5-*.db`
backup when the validated-extraction, artifact, validation-result, and preference
tables are added. Migrations 1–4 remain byte-for-byte unchanged and Phase 4
appends migration 5 only. New databases apply all migrations without a backup.

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

Extraction data is stored only below the Atlas data root:

```text
%LOCALAPPDATA%\S1Atlas\extraction.lock
%LOCALAPPDATA%\S1Atlas\builds\<build-id>\attempts\<attempt-id>\attempt.json
%LOCALAPPDATA%\S1Atlas\builds\<build-id>\attempts\<attempt-id>\logs\stdout.log
%LOCALAPPDATA%\S1Atlas\builds\<build-id>\attempts\<attempt-id>\logs\stderr.log
%LOCALAPPDATA%\S1Atlas\builds\<build-id>\attempts\<attempt-id>\candidate-output
%LOCALAPPDATA%\S1Atlas\builds\<build-id>\attempts\<attempt-id>\retained-output
%LOCALAPPDATA%\S1Atlas\builds\<build-id>\inputs\<input-snapshot-id>
%LOCALAPPDATA%\S1Atlas\builds\<build-id>\extractions\.staging\<attempt-id>
%LOCALAPPDATA%\S1Atlas\builds\<build-id>\extractions\.staging\<attempt-id>.promotion.json
%LOCALAPPDATA%\S1Atlas\builds\<build-id>\extractions\<extraction-id>\reconstructed
%LOCALAPPDATA%\S1Atlas\builds\<build-id>\extractions\<extraction-id>\complete.marker
%LOCALAPPDATA%\S1Atlas\builds\<build-id>\extractions\quarantine
```

`ProcessCompleted` remains a non-authoritative Phase 3 status: the Cpp2IL
candidate under `candidate-output` has no `complete.marker`, no validated
extraction ID, and cannot feed a downstream consumer. Phase 4 promotes a valid
candidate into an immutable `extractions\<extraction-id>` directory whose
`artifact-manifest.json`, `validation.json`, and `extraction.json` are written
before a `complete.marker` is written last. The promotion journal is a sibling of
the staging directory (never copied into the final output) and survives a
database failure after the final rename so a complete-but-unregistered extraction
can be recovered on the next run. A validated extraction directory is immutable —
S1Atlas never edits its artifacts or manifests in place — and only an extraction
whose database row, marker, manifests, artifact rows, and current hashes all
agree is returned as authoritative. Failed partial output is deleted by default
or moved to `retained-output` only when `--keep-failed-artifacts` is explicit.
Phase 5 `extractions cleanup` can remove only proven Atlas-owned, age-eligible
failure, staging, and quarantine data, and never deletes a validated extraction,
an input snapshot, a preferred or `ProcessCompleted` output, or any active or
ambiguous evidence.

Unknown nonempty schemas are rejected without a migration ledger, schema mutation, or backup because S1Atlas cannot safely infer their origin.

Generated data, databases, backups, extraction artifacts, decompiled output, and logs are intentionally excluded from Git.

## Build and environment identity

The build ID remains derived only from the `GameAssembly.dll` and `global-metadata.dat` content hashes. Executable version, Steam app/build IDs, installation paths, dependency versions, and Atlas version describe an environment snapshot; they do not redefine the game build.

After a Foundation-v1 database is migrated, its existing snapshot remains identity version 1 with the same build ID, snapshot ID, dependencies, and current pointer. The first subsequent scan intentionally creates and promotes an identity-version 2 environment snapshot even when the observed installation is otherwise unchanged. The migrated v1 snapshot remains as history; this one-time transition is expected and is not duplicate-build churn.

## Managed Cpp2IL pin

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

The production pin above remains byte-for-byte unchanged. Phase 3 may point a
freshly verified tool at Schedule I only through the explicit `extract`
command. The Schedule I installation remains read-only; live input hashes are
required to match before and after execution. Automated integration tests use
generated fake game bytes, a source-built fake executable, and a rejecting HTTP
handler. They use no proprietary fixture and make no network request.

## Validation policy

The committed `managed-assemblies-v1` policy is reviewed with the repository and
is provenance, not production identity — it can never change a recipe, manifest
digest, or extraction ID, and a policy-only revalidation never reruns Cpp2IL:

```text
Policy ID:                         managed-assemblies-v1
Required assembly identity:        Assembly-CSharp
Minimum managed assembly count:    1
Minimum type-definition count:     1
Minimum method-definition count:   1
Minimum total managed bytes:       1,048,576
Comparative warning threshold:     relative change > 0.25
Catastrophic decrease threshold:   relative decrease > 0.80
```

Absolute checks enforce those floors; comparative checks flag large deviations
from the preferred baseline and hard-fail a catastrophic decrease; reproducibility
comparison links a byte-identical same-recipe result and blocks automatic
preference when the same recipe produces different bytes. Automated tests use a
test policy with a tiny managed-byte floor and never modify the production
`config/validation/*.json`.

## Design documents

- [V1 design specification](design/2026-08-12-s1atlas-design.md)
- [Validated Cpp2IL extraction design](design/2026-08-12-cpp2il-extraction-design.md)
