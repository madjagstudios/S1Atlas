# Build & Symbol Diffing Smoke

**Date:** 2026-08-14
**Scope:** Offline fixture and SQLite integration smoke for the V1 diff layer

## Automated smoke

- Compared two completed `S1Api` indexes with `Installed` and `Release` channels.
- Verified channel stripping matches the same logical symbol across snapshots.
- Verified `BodyChanged`, `Added`, relationship deltas, bounded output, counts, human output, and JSON output.
- Verified a missing `Preview` index returns `NoCompletedIndex`.
- Verified selector resolution is read-only and cross-codebase selectors return `NotComparable`.
- Verified the default view excludes unchanged symbols and standalone unchanged stub-body classifications; `--all` restores those classifications when auditing fidelity.
- The focused diff tests pass in `S1Atlas.Indexing.Tests` and `S1Atlas.IntegrationTests`.

## Fidelity limitation

This workspace does not contain two real Schedule I indexed builds or installed S1API/S1MAPI binaries. No proprietary or fabricated real-game smoke is claimed here. Real Schedule I Cpp2IL indexes remain structural/metadata/source fidelity when method bodies are stubs; the engine retains `BodyUnavailable` classification, while the default changed view suppresses standalone unchanged-stub rows and `--all` exposes them. Real installed managed binaries can provide body-level diffs when both sides record `Recovered` bodies.
