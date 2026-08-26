# Contributing to S1Atlas

Thanks for your interest in S1Atlas. This is a local, offline developer-intelligence
tool for Schedule I mod development. Contributions are welcome via pull request.

## Ground rules

- **Never commit game content.** No game binaries (`GameAssembly.dll`,
  `global-metadata.dat`), no extracted/decompiled game source or output, and no
  third-party tool binaries. All generated and extracted data stays local and is
  gitignored. Tests use generated fake bytes and source-built fakes only — never a
  proprietary fixture, and never a network call.
- **You supply your own game.** S1Atlas requires a legitimately obtained local copy of
  Schedule I for real scans. It is unofficial and not affiliated with the game's
  developers or publishers (see the [disclaimer](README.md) and [LICENSE](LICENSE)).
- **Local-first and read-only toward the game.** The Schedule I installation and Steam
  manifest are treated as read-only input.

## Development

Requirements: Windows 10+, .NET 8 SDK. From the repository root:

```powershell
dotnet restore S1Atlas.sln
dotnet build S1Atlas.sln --configuration Release
dotnet test S1Atlas.sln --configuration Release --no-build
```

## Before you open a pull request

CI runs — and merging requires — the full gate. Run it locally first; a green
build and passing tests are **not** sufficient on their own, because the format
check is separate:

```powershell
dotnet format S1Atlas.sln --verify-no-changes --no-restore
dotnet build S1Atlas.sln --configuration Release
dotnet test S1Atlas.sln --configuration Release --no-build
```

- Branch off `main`, keep the change focused, and open a PR against `main`.
- Add tests for new behavior; follow the existing TDD style.
- Match the surrounding code's conventions and keep files focused.

## Commit messages

Keep commit messages and PR descriptions clean. Do **not** include AI-assistant
attribution trailers such as `Co-Authored-By: Claude ...` or "Generated with ...".
A local `commit-msg` hook enforces this — enable it once after cloning:

```powershell
git config core.hooksPath .githooks
```

The hook lives at [`.githooks/commit-msg`](.githooks/commit-msg) and is local to your
clone (it does not affect web-UI commits or anyone who hasn't enabled it).

## Provenance and honesty

S1Atlas reports only what it can prove from the indexed build, and labels every claim
as FACT, DERIVED, or INTERPRETATION. Contributions must preserve that boundary — never
substitute a guess for measured evidence, and keep missing, unavailable,
integrity-failed, and zero-result states explicit and distinct.
