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
- **Keep the tree public-safe.** This repo is public: no AI attribution in
  commits or content, no machine-specific paths, no agent-process residue,
  no internal or private references, and no game content. CI enforces these
  rules on every pull request.
- **No plan-step references in commit messages.** Subjects and bodies must
  not cite work-plan steps ("Task N", "step N", "phase N"); describe the
  change instead. The public-content gate enforces this.

## Development

Requirements: Windows 10+, .NET 8 SDK. From the repository root:

```powershell
dotnet restore S1Atlas.sln
dotnet build S1Atlas.sln --configuration Release
dotnet test S1Atlas.sln --configuration Release --no-build
```

Tests marked `LocalGameRequired` need a real game install plus a local atlas, so
plain `dotnet test` skips them without touching your live atlas. To run them
explicitly (they copy the live atlas into temp first and never write the live
files, but the copy can take a while for large atlases):

```powershell
$env:S1ATLAS_RUN_LOCAL_GAME_TESTS = '1'
dotnet test S1Atlas.sln --configuration Release --no-build --filter "Category=LocalGameRequired"
Remove-Item Env:\S1ATLAS_RUN_LOCAL_GAME_TESTS
```

The `GoldenFacts` subset additionally reads expected values from a gitignored local file; see [docs/USAGE.md](docs/USAGE.md#real-game-golden-facts).

## Tool development loop

To exercise the packaged tools exactly as users install them, pack and install
to a scratch tool-path (never the global store) from the repository root:

```powershell
dotnet pack S1Atlas.sln --configuration Release --output ./.tool-dev/packages
dotnet tool install S1Atlas.Cli --tool-path ./.tool-dev/tools --add-source ./.tool-dev/packages
dotnet tool install S1Atlas.Mcp --tool-path ./.tool-dev/tools --add-source ./.tool-dev/packages
./.tool-dev/tools/s1atlas.exe --version
```

After further source changes, re-pack and update by package ID — `update` takes
the package ID (`S1Atlas.Cli`, `S1Atlas.Mcp`), not the command name, and
reinstalls even when the version is unchanged:

```powershell
dotnet pack S1Atlas.sln --configuration Release --output ./.tool-dev/packages
dotnet tool update S1Atlas.Cli --tool-path ./.tool-dev/tools --add-source ./.tool-dev/packages
dotnet tool update S1Atlas.Mcp --tool-path ./.tool-dev/tools --add-source ./.tool-dev/packages
```

`./.tool-dev/` is a scratch directory; keep it out of the repository.

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

## Provenance and honesty

S1Atlas reports only what it can prove from the indexed build, and labels every claim
as FACT, DERIVED, or INTERPRETATION. Contributions must preserve that boundary — never
substitute a guess for measured evidence, and keep missing, unavailable,
integrity-failed, and zero-result states explicit and distinct.
