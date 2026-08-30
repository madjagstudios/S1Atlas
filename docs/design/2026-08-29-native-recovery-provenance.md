# Native Recovery Provenance and Feasibility Boundary

## Decision

Task 3 defines a bounded, local provider contract and a deterministic evidence record. It does not configure or execute a production native-body recovery provider. A request made without a configured `INativeBodyRecoveryProvider` returns `Unsupported` with the exact reason:

> No native body recovery provider is configured.

This is a valid negative feasibility result. The workflow does not substitute a reconstructed managed body, an interop wrapper, or an unverified disassembly result.

## Local tool inventory

The only repository-pinned local reverse-engineering executable at this boundary is:

| Field | Reviewed value |
| --- | --- |
| Tool | Cpp2IL |
| Version | `2022.1.0-pre-release.21` |
| Asset | `Cpp2IL-2022.1.0-pre-release.21-Windows.exe` |
| SHA-256 | `663fb432433b4371fd1ee0ebc321a8fff2a9aac5ac4230c843f9e03ddee4e04c` |
| License | MIT |
| Configured capability | `dll_il_recovery` |

That pin produces reconstructed managed assemblies for the existing extraction workflow. It is deliberately **not selected as a native-body recovery provider**: the reviewed profile does not expose the bounded method-pointer mapping, direct native edges, field accesses, and completeness contract required here. Task 3 never launches it. A future provider must identify its exact executable name, version, and executable SHA-256 in the `NativeRecoveryExecutionContext`; the returned record is rejected if the provider reports a different identity.

## Accepted inputs and bounds

The workflow accepts only:

- one nonblank Schedule I build ID;
- one nonblank completed S1Atlas index ID;
- the current lower-case, 64-character `GameAssembly.dll` SHA-256;
- one or more unique, explicitly selected symbol IDs; and
- a traversal edge budget from 1 through 500.

The caller also supplies the currently observed build, index, and `GameAssembly.dll` identities plus the configured provider tool name, version, and executable SHA-256. A mismatch between current and requested build, index, or binary identity returns `InputChanged` before provider execution. The provider receives the canonical symbol ordering and the same bounded request. Returned edges are deterministically ordered and truncated to the requested budget; truncation makes the record incomplete.

## Evidence output

The record stores provenance and normalized facts only:

- build ID, index ID, `GameAssembly.dll` SHA-256, selected symbol IDs, and traversal budget;
- provider tool name, version, and executable SHA-256;
- managed-wrapper-to-native pointer mapping evidence;
- bounded direct native edges and field-access descriptions;
- explicit status, completeness, output SHA-256, deterministic recovery ID, and observation timestamp; and
- an explicit failure reason when applicable.

It has no field for a game binary, proprietary method body, raw disassembly, reference-mod content, or a local filesystem path. Those artifacts must remain in operator-controlled local storage and are never copied into the repository or into a native recovery record.

The output SHA-256 is derived from a versioned, length-prefixed canonical stream containing status, sorted mapping evidence, sorted bounded edges, sorted field accesses, completeness, and the failure reason. Edge IDs are derived from the normalized edge evidence; provider-supplied IDs, hashes, ordering, and timestamps are not trusted. Provider-controlled summaries and failure messages are bounded and rejected when they contain local paths, URLs, binary-artifact markers, or raw-disassembly markers. The recovery ID hashes the canonical request, provider identity, and output hash. Identical inputs and equivalent evidence therefore reproduce the same output hash and recovery ID; `CreatedAtUtc` remains the time S1Atlas normalized the observation.

## Failure and UNKNOWN semantics

`NoBody`, `AmbiguousMapping`, `InputChanged`, `Failed`, and `Unsupported` remain first-class results. Non-recovered statuses cannot carry native edges or field-access claims. Mapping evidence may remain on `NoBody` or `AmbiguousMapping` so the negative result is auditable.

Only a directly evidenced `DirectCall` edge with a target method pointer can remain a direct native edge. `IndirectDispatch`, `RuntimeDispatch`, `CrossThreadDispatch`, unrecognized kinds, and targetless edges are normalized as `UNKNOWN`, marked incomplete, and make the enclosing record incomplete. They do not establish a direct native callee, execution order, thread affinity, authority, or lifecycle ownership. Provider exceptions become sanitized `Failed` records with the configured tool provenance; cancellation requested by the caller still propagates.

## Licensing and distribution boundary

The Cpp2IL identity above is an inventory reference to the existing reviewed MIT-licensed pin, not authorization to redistribute it or claim native recovery capability. This workflow does not download tools. Any future provider executable remains locally installed and hash-verified under a separately reviewed tool definition and license. Schedule I binaries and derived native artifacts remain outside source control and distribution.
