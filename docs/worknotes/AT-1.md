# AT-1 working notes

- Goal: build the deterministic, offline S1Atlas.Docs static human portal and `docs generate [--build <id>] [--output <dir>]` CLI command.
- Ticket: AT-1; branch: `feat/at-1-s1atlas-docs`.
- Scope: Schedule I Installed plus S1API/S1MAPI; scene HTML is explicitly deferred from V1.
- Schedule I policy: use `InstalledBuildAuthorityResolver`; only preferred, integrity-verified authorities are navigable.
- API policy: latest completed index per `(codebase, channel)`, independent of `--build`; expose commit SHA and index ID.
- `--build` pins only Schedule I; APIs remain latest-completed and build-independent.
- Site model: pre-render all pages; tiny vanilla-JS search over a deterministically sorted prebuilt index.
- URL policy: relative links; filesystem-safe readable slugs plus a 12-hex SHA-256 suffix of the exact key; LF output.
- History/diff policy: all known builds remain visible with status; only verified builds navigate; only adjacent verified Schedule I pairs get diff pages.
- Scene seam: reserve `code/schedule-i/installed/scenes/` without generating pages; show the explicit CLI/MCP availability note on the Schedule I landing page.
- Content policy: FACT and DERIVED labels are mandatory; no V1 INTERPRETATION; learning concepts come from Roslyn syntax analysis over the exact rendered span.
- Verification: generated/repository-owned fixtures only; run format verify, Release build, and Release no-build tests before completion.
- Starting-work finding: the repo had no code-map artifacts; generated and verified the branch-local map before planning.
