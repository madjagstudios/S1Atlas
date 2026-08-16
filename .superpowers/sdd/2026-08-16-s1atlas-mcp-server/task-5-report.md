# Task 5 Report

Date: 2026-08-16
Branch: `codex/s1atlas-mcp`
Task: MCP-neutral response envelope, provenance, status, and error types
Status: DONE

## Summary

Implemented the neutral MCP response envelope types in `S1Atlas.Application` and added a small authority-to-envelope mapper with regression coverage.

The new envelope layer now provides:

- `ToolStatus` for `Resolved`, `NotFound`, `Ambiguous`, `Unavailable`, and `Invalid`
- `ProvenanceClassification` for `Fact`, `Derived`, and `Interpretation`
- `BuildContext` for requested/resolved build identity and channel metadata
- `ProvenanceEntry` for source classification and traceability
- `ToolError` for stable error codes and messages
- `ToolEnvelope<T>` with factory helpers for the supported statuses

The authority mapper translates `InstalledBuildAuthority` outcomes into the neutral envelope shape without introducing any MCP SDK dependency or host/tool wiring.

## Files Changed

- Created `src/S1Atlas.Application/Envelope/ToolEnvelope.cs`
- Created `src/S1Atlas.Application/Envelope/AuthorityEnvelope.cs`
- Created `tests/S1Atlas.Mcp.Tests/S1Atlas.Mcp.Tests.csproj`
- Created `tests/S1Atlas.Mcp.Tests/EnvelopeTests.cs`
- Modified `S1Atlas.sln`

## Implementation Notes

- `ToolEnvelope<T>` is a sealed record with factory helpers for the five requested statuses.
- The envelope carries `Build`, `Data`, `Candidates`, `Provenance`, and `Error` fields exactly as requested.
- `Resolved` uses the supplied data payload and preserves any provenance entries.
- `NotFound`, `Unavailable`, and `Invalid` keep `Data` empty unless explicitly supplied by the factory call path.
- `AuthorityEnvelope.From(InstalledBuildAuthority)` maps each `InstalledBuildAuthorityStatus` to the corresponding neutral status and stable error code:
  - `NoCurrentBuild` -> `Unavailable` / `NoCurrentBuild`
  - `BuildNotFound` -> `Invalid` / `BuildNotFound`
  - `NoPreferredVerifiedExtraction` -> `NotFound` / `NoPreferredVerifiedExtraction`
  - `ExtractionIntegrityFailure` -> `Unavailable` / `ExtractionIntegrityFailure`
  - `NoCompletedIndex` -> `NotFound` / `NoCompletedIndex`
  - `IndexBuildMismatch` -> `Invalid` / `IndexBuildMismatch`
- The mapper stays independent of the MCP SDK and does not add any server/host implementation.

## TDD / Verification Log

1. Added the initial failing `EnvelopeTests` coverage for the neutral envelope and authority mapping surface.
2. Verified the expected compile failure with `dotnet test tests/S1Atlas.Mcp.Tests --filter EnvelopeTests`.
3. Implemented `ToolEnvelope<T>` and `AuthorityEnvelope`.
4. Fixed the factory signatures so `NotFound` supports both simple provenance-only calls and error-bearing mapping calls.
5. Re-ran the focused test slice successfully with `dotnet test tests/S1Atlas.Mcp.Tests --filter EnvelopeTests`.
6. Built the focused test project successfully with `dotnet build tests/S1Atlas.Mcp.Tests --no-restore`.
7. Built the full solution successfully with `dotnet build S1Atlas.sln --no-restore`.
8. Committed the implementation in `fd5383d`.

## Commands Run

```powershell
dotnet test tests/S1Atlas.Mcp.Tests --filter EnvelopeTests
dotnet build tests/S1Atlas.Mcp.Tests --no-restore
dotnet build S1Atlas.sln --no-restore
```

## Test Results

- `dotnet test tests/S1Atlas.Mcp.Tests --filter EnvelopeTests` -> PASS (9 tests)
- `dotnet build tests/S1Atlas.Mcp.Tests --no-restore` -> PASS
- `dotnet build S1Atlas.sln --no-restore` -> PASS

## Concerns

- None.

## Review Fix Round 1

Addressed the two review findings:

- successful `AuthorityEnvelope.From` / `ToolEnvelope.Resolved` now emit at least one `Fact` provenance entry tied to the resolved build, extraction, and index authority
- regression tests now assert successful fact provenance plus candidate preservation for `Ambiguous` and explicit empty candidates for `Resolved`

### Commands Run

```powershell
dotnet test tests/S1Atlas.Mcp.Tests --filter EnvelopeTests
dotnet build tests/S1Atlas.Mcp.Tests --no-restore
```

### Output

- `dotnet test tests/S1Atlas.Mcp.Tests --filter EnvelopeTests` -> PASS (10 tests)
- `dotnet build tests/S1Atlas.Mcp.Tests --no-restore` -> PASS
