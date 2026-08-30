# MCP launch baseline — 2026-08-30 (AT-8)

This benchmark compares the old project-run launch shape with the direct
compiled-DLL launch recommended for registered Codex and Claude MCP clients.
It measures a process-cold start to the MCP initialize/tool-list handshake; it
does not claim to be a cold filesystem or OS-cache benchmark.

## Environment

- Windows 11 Home 64-bit (`10.0.26200`)
- .NET SDK `8.0.422`
- Release build from the AT-8 worktree
- Three runs per launch mode
- Measurement helper: `scripts/benchmark-mcp-launch.ps1`

## Commands

Build once before measuring:

```powershell
dotnet build src/S1Atlas.Mcp/S1Atlas.Mcp.csproj --configuration Release --no-restore
```

Direct-DLL launch:

```powershell
dotnet src/S1Atlas.Mcp/bin/Release/net8.0/S1Atlas.Mcp.dll mcp serve
```

Comparison control, with build and restore disabled:

```powershell
dotnet run --project src/S1Atlas.Mcp/S1Atlas.Mcp.csproj --configuration Release --no-build --no-restore -- mcp serve
```

Run the complete comparison with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\benchmark-mcp-launch.ps1 -Runs 3
```

## Results

| Launch mode | Wall time (ms) | Process count | Working set (MiB) |
| --- | ---: | ---: | ---: |
| Direct DLL — run 1 | 365.5 | 2 | 70.6 |
| Direct DLL — run 2 | 333.1 | 2 | 70.6 |
| Direct DLL — run 3 | 331.2 | 2 | 70.5 |
| `dotnet run` control — run 1 | 577.7 | 3 | 144.4 |
| `dotnet run` control — run 2 | 574.0 | 3 | 144.4 |
| `dotnet run` control — run 3 | 596.3 | 3 | 144.0 |

The direct-DLL path averaged 343.3 ms, 2 processes, and 70.6 MiB; the control
averaged 582.7 ms, 3 processes, and 144.3 MiB. It used one fewer process and
roughly half the startup working set in this measurement, while completing the
same initialize/tool-list handshake. The process and memory values are
machine-specific; the durable conclusion is that registered hosts should launch
the already-built Release DLL so startup does not involve the project-run
wrapper or build/restore path.

## Lifecycle boundary

Each independent stdio client still owns one MCP server process. Multiple
processes are expected when multiple Codex or Claude sessions are connected;
the benchmark does not justify or introduce a shared singleton. MCP stdout
remains protocol-only, and any diagnostics belong on standard error.
