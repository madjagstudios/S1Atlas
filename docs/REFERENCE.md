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

## Seam investigation contract

`investigate_seam` is a read-only ownership-analysis surface shared by the CLI
JSON result and the MCP tool payload. Valid conclusion values are
`SupportableSeam`, `NoSupportableSeam`, and `InsufficientCoverage`.

`InsufficientCoverage` is the service-gate outcome whenever mandatory evidence
is `Incomplete` or `Unavailable`, including incomplete or unavailable caller
coverage. `InsufficientCoverage` and `NoSupportableSeam` are both resolved
research outcomes; CLI may return `success: true` and MCP may return
`status: resolved` while preserving either conclusion.

Coverage is categorical, never probabilistic. Valid coverage states are
`Complete`, `Bounded`, `Incomplete`, `Unavailable`, and `NotApplicable`.
S1Atlas does not emit a confidence score; it preserves `FACT`/`DERIVED` claims
and separate `unknownDimensions` so unsupported gaps remain explicit instead of
being collapsed into a confidence number.
Treat every entry in `unknownDimensions` as a literal `UNKNOWN`
classification, not as a confidence score.

Owner candidates are ordered deterministically by the traversal/path rules used
by the investigation service. The same seeded request therefore preserves the
same selected candidate symbol ID, ordered owner candidate symbol IDs,
coverage warnings, unknown dimensions, and next-action kinds across the CLI and
MCP surfaces.

Once mandatory evidence is complete, `NoSupportableSeam` is reserved for
complete evidence that establishes no supportable owner, such as no candidate,
competing candidates, generic-only ownership coverage, or a remaining literal
`UNKNOWN` dimension. Example: if complete evidence leaves
`Game.Seams.CompleteEvidenceTarget` with competing owner candidates, the
investigation remains a successful resolved `NoSupportableSeam` result.

MCP provenance entries carry `source`, `buildId`, `extractionId`, and
`indexId`. The shared CLI/MCP data packet carries `pinnedProvenance`,
`authorityEntityAttribution`, `alternateGenericCallersAndExclusivity`,
`lifecyclePositionAndBeforeAfterState`, and `apiBeforePatchResult` in both
detail modes. These are the five mandatory gate records: pinned authority,
authority/entity attribution, alternate or generic callers and exclusivity,
lifecycle position and before/after state, and API-before-patch result.

CLI seam results additionally expose nullable
`referenceCollectionBaseProvenance`. For `scope: reference`, it identifies the
installed Schedule I build/extraction/index that the selected reference
collection pins as its base authority; `pinnedProvenance` identifies the
selected reference index. The field is `null` for game-only results. MCP places
the same base authority in the envelope's `build` and `provenance` metadata.

`details: false` keeps `claims` and `evidenceSections` empty without removing
any decision, coverage, provenance, authority, or gate record; `details: true`
populates those two arrays. The remaining shared packet must be identical
between detail modes and between CLI JSON and MCP for the same seeded request.

The CLI reports resolved research outcomes as `success: true` with exit code
`0`. MCP adapter statuses are `resolved`, `not_found`, `ambiguous`,
`unavailable`, and `invalid`; only `resolved` carries the successful seam data
packet. CLI failures remain nonzero error envelopes and never become resolved
research packets. These adapter statuses are transport outcomes and do not
replace the packet's `SupportableSeam`, `NoSupportableSeam`, or
`InsufficientCoverage` conclusion.

Candidate and ordered owner records preserve their symbol/index identifiers,
and claims, evidence sections, gate records, and MCP provenance preserve their
evidence or authority identifiers. Treat those identifiers as provenance, not
as proof that the unknown dimensions have been closed.

Native recovery and runtime proof are next actions only; S1Atlas never executes
either automatically. Seam investigation does not patch binaries, does not run
automatic native recovery, and does not claim runtime proof from static source,
relationship, or callable evidence alone. An explicit native lookup uses
`nativeSymbolIds` plus a `nativeTraversalBudget` from `0` to `500`; zero means
no lookup. A matching stored result is exposed as `nativeEvidence` with
`status`, `isComplete`, mapping evidence, direct native edges, field accesses,
tool provenance, an output SHA-256, and an optional failure message. The lookup
also reports `Matched`, `NoMatch`, or `InputChanged` separately from recovery
status, so a missing record is not conflated with `Unsupported`. Records are matched by build ID, index
ID, GameAssembly SHA-256, selected native symbols, and traversal budget. Negative
statuses such as `NoBody`, `Failed`, `InputChanged`, and `Unsupported` remain
visible and do not imply a recovered body. Native persistence is read-only at
query time and stores no proprietary body, disassembly, path, or binary artifact.

The API parity MCP surface includes `find_api_callers`, `find_api_callees`,
`find_api_references`, `find_api_related_types`, `find_api_call_sites`, and
`find_api_field_references` in addition to API index, symbol, and source
queries. These remain read-only and use the same completed-index and exact
environment-snapshot authority rules as the CLI query services.

`plan_runtime_proof` is a bounded planning surface, not a game runner. Its
`executionBoundary` is one of `singlePlayer`, `listenHost`, `dedicatedServer`,
or `client`; the planner keeps observability and authority evidence inside that
boundary. It returns competing hypotheses, controls, lifecycle checks,
declared-observable limitations, cleanup, and `Pass`, `Inconclusive`, or `Stop`
outcomes. A missing policy gate or authority starts at `Stop`; otherwise the
initial decision is `Inconclusive` until runtime observations satisfy the
declared controls.

## Design documents

- [V1 design specification](design/2026-08-12-s1atlas-design.md)
- [Validated Cpp2IL extraction design](design/2026-08-12-cpp2il-extraction-design.md)
