# Task 2 Report

Date: 2026-08-16
Branch: `codex/s1atlas-mcp`
Task: `S1Atlas.Application` library + shared Schedule I Installed build-authority resolver
Status: DONE

## Summary

Implemented the new `S1Atlas.Application` project and added the shared `InstalledBuildAuthorityResolver` plus its authority status/result records. Added a real-storage `AuthorityHarness` and resolver tests covering all requested branches:

- `NoCurrentBuild`
- `BuildNotFound`
- `NoPreferredVerifiedExtraction`
- `ExtractionIntegrityFailure`
- `NoCompletedIndex`
- `Resolved`

The CLI's existing S1API/S1MAPI resolution scope was left untouched.

## Files Changed

- Created `src/S1Atlas.Application/S1Atlas.Application.csproj`
- Created `src/S1Atlas.Application/Authority/InstalledBuildAuthority.cs`
- Created `src/S1Atlas.Application/Authority/InstalledBuildAuthorityResolver.cs`
- Modified `S1Atlas.sln`
- Modified `tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj`
- Created `tests/S1Atlas.Indexing.Tests/AuthorityHarness.cs`
- Created `tests/S1Atlas.Indexing.Tests/InstalledBuildAuthorityResolverTests.cs`
- Modified `src/S1Atlas.Extraction/Properties/AssemblyInfo.cs`

## Implementation Notes

- `InstalledBuildAuthorityResolver` follows the brief exactly:
  - falls back to the current environment snapshot when `requestedBuildId` is null/blank
  - validates explicit build IDs via `IAtlasRepository.ListBuildsAsync`
  - resolves authoritative extractions through `PreferredVerifiedExtractionResolver`
  - distinguishes missing preference rows from integrity-failed preferences
  - resolves completed Schedule I Installed indexes by extraction/source identity
- Added `S1Atlas.Indexing.Tests` access to `ValidatedExtractionIntegrityVerifier`'s internal constructor so the test harness can use the real verifier as required by the brief.
- The test harness uses a writable `SqliteAtlasRepository`, writes real validated-extraction final documents, and seeds completed index runs through the existing repository APIs.

## TDD / Verification Log

1. Added the first `Resolve_NoCurrentSnapshot_ReturnsNoCurrentBuild` test and harness scaffold.
2. Ran `dotnet test tests\S1Atlas.Indexing.Tests --filter InstalledBuildAuthorityResolverTests` and confirmed the expected missing-application failure.
3. Implemented `S1Atlas.Application` and reran the single no-current-build test until it passed.
4. Added the remaining branch tests.
5. Fixed harness attempt IDs to satisfy repository lifecycle validation (`lower-case GUID N` format requirement).
6. Re-ran the full resolver test class successfully.
7. Built the full solution successfully to verify project/solution wiring.

## Commands Run

```powershell
dotnet test tests\S1Atlas.Indexing.Tests --filter InstalledBuildAuthorityResolverTests
dotnet test tests\S1Atlas.Indexing.Tests --filter InstalledBuildAuthorityResolverTests.Resolve_NoCurrentSnapshot_ReturnsNoCurrentBuild
dotnet test tests\S1Atlas.Indexing.Tests --filter InstalledBuildAuthorityResolverTests
dotnet build S1Atlas.sln
```

## Test Results

- `dotnet test tests\S1Atlas.Indexing.Tests --filter InstalledBuildAuthorityResolverTests` -> PASS (6 tests)
- `dotnet build S1Atlas.sln` -> PASS

## Concerns

- None.
