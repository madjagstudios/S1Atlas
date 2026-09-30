<!--
Thanks for contributing to S1Atlas. Keep the diff scoped to one change.
Never commit proprietary or generated artifacts — the repository-hygiene CI gate
blocks GameAssembly.dll, global-metadata.dat, atlas.db, decompiled output, etc.
-->

## What & why

Describe the change and the motivation. Link the tracking issue if there is one.

## Public-repo checklist

- [ ] No AI attribution (no Co-Authored-By trailers, "Generated with" lines, or AI tool names)
- [ ] No machine-specific paths (no absolute user paths, hostnames, or local data roots)
- [ ] No agent-process residue (no plans, worknotes, task reports, or prompt references)
- [ ] No internal/private references (no private project names, non-AT ticket keys, or tracker URLs)
- [ ] No game content or game-derived data (generated fakes only, no network)

## How it was verified

- [ ] `dotnet build S1Atlas.sln --configuration Release`
- [ ] `dotnet test S1Atlas.sln --configuration Release`
- [ ] `dotnet format S1Atlas.sln --verify-no-changes`
- [ ] `./scripts/verify-repository-hygiene.ps1`

## Notes

Provenance impact (FACT / DERIVED / INTERPRETATION), docs updated, anything reviewers
should look at closely.
