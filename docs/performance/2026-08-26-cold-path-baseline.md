# Cold-path performance baseline — 2026-08-26 (AT-4)

First measured baseline of the first-time S1Atlas workflow, using the opt-in
`--performance` diagnostics shipped in AT-4 (PR #31). Goal: rank the top wall-time
and I/O / memory contributors so AT-5…AT-9 can be prioritized against facts.

All figures are **DERIVED** metrics (elapsed time, counts, byte sizes) — no
game-derived content. This is **real-install** evidence, not fixture data.

## Runtime context

| | |
|---|---|
| CPU | AMD Ryzen 7 9800X3D — 8 cores / 16 threads |
| RAM | 31.2 GB |
| Disk | Samsung SSD 990 PRO (NVMe) |
| OS | Windows 11 Home 10.0.26200 |
| .NET SDK | 8.0.422, `Release` build |
| Game build | `1f1e5669…0990c` (exe 2022.3.62.7762112, Steam build 24705572) |
| Inputs | GameAssembly.dll 65.9 MB · global-metadata.dat 18.4 MB |

### Cache-state caveat

This machine was **not a clean-cold environment**. `%LOCALAPPDATA%\S1Atlas` already
held a prior authoritative extraction for build `1f1e5669…` (plus an older build
`6fbd38f8…` with several extractions and indexes). Consequently:

- **scan** and **index** ran fresh (`reused: 0`) and are trustworthy, though the OS
  file cache was warm.
- **extract** reported `reused: 0 / process.wasRun: 1`, but the underlying Cpp2IL
  candidate output for this build most likely already existed, so **7.3 s reflects a
  warm Cpp2IL cache** (re-validate/re-promote + ILSpy over existing output), **not a
  from-scratch decompile**. A true cold extract is expected to be substantially
  larger. Treat the extract number as a lower bound. (Not re-measured here because it
  would mean deleting the user's local extraction data.)

## Measured results (single cold run each)

| Phase | Wall (ms) | CPU (ms) | Allocated | Peak WS | Key counters |
|---|---:|---:|---:|---:|---|
| **scan** | 1,980 | 453 | 8.8 MB | 49 MB | 4 deps; hashes 84.3 MB of inputs |
| **extract** ⚠️ | 7,309 | 1,406 | 39.6 MB | 94 MB | authoritative; **warm cache** |
| **index** | 29,464 | 24,484 | **5.44 GB** | 375 MB | 52,297 symbols · 50,034 relationships |
| **Combined first run** | **≈ 38,750** | | | | |

Sub-phase timings, where the instrumentation reports them:

- **scan** → `environment.discovery` **1,640 ms (83%)**, `snapshot.persisted` 18 ms, `repository.initialize` 40 ms.
- **extract** → `extraction.workflow` 7,162 ms *(single aggregate phase — no breakdown)*.
- **index** → `index.workflow` 29,286 ms *(single aggregate phase — no breakdown)*, `repository.initialize` 27 ms.

## Ranking

**By wall time (whole pipeline):**
1. **`index` — 29.5 s (~76% of the first run).** The dominant cost by far.
2. **`extract` — 7.3 s (~19%)** — and this is a *warm-cache lower bound*; true cold is higher.
3. **`scan` — 2.0 s (~5%)**, almost entirely `environment.discovery` (1.64 s).

**By memory / allocation pressure:**
1. **`index` — 5.44 GB allocated**, peak working set 375 MB. Enormous transient
   allocation for 52 k symbols + 50 k relationships → heavy GC pressure.
2. `extract` — 39.6 MB allocated (+10.2 MB written to disk).
3. `scan` — 8.8 MB allocated; hashes 84.3 MB of inputs (GameAssembly + global-metadata).

**CPU-bound check:** `index` spends 24.5 s CPU of 29.5 s wall (~83% on-CPU,
single-threaded-ish given 16 threads available) → real headroom for parallelism
and/or algorithmic/allocation reduction.

## What this means for AT-5…AT-9

- **AT-7 (batch first-index persistence, control memory pressure) — highest value.**
  `index` owns ~76% of first-run wall time and allocates 5.44 GB. Both the time and
  the memory-pressure signals point straight here. Start prioritization with AT-7.
- **`index` is CPU-bound and largely serial** on a 16-thread box — parallelizing
  symbol/relationship construction and cutting per-symbol allocation are the levers.
- **AT-9 / AT-5 (scan hashing + single-pass dependency discovery)** — `environment.discovery`
  is 1.64 s. Real but small in absolute terms (~4% of the pipeline); lower priority
  than index unless it regresses at scale.
- **AT-6 (reduce first-run input hashing/copying)** — extract. **Not yet measurable**
  here due to the warm Cpp2IL cache; needs a true cold run before it can be ranked.
- **AT-8 (MCP launch lifecycle)** — a separate path, not exercised by this pipeline;
  measure independently.

## Instrumentation gaps found (feed back into AT-4 tooling)

The `--performance` output is sufficient to rank **commands**, but not **sub-phases
within** the two expensive commands:

1. **`extract` and `index` each emit a single aggregate workflow phase.** To rank
   hotspots *inside* them, add sub-phase timers:
   - extract → `cpp2il`, `ilspy-decompile`, `validate`, `promote`
   - index → `load-inputs`, `parse`, `build-symbols`, `build-relationships`, `persist`
2. **No cold/warm marker for the Cpp2IL candidate cache**, which is exactly what made
   this extract number ambiguous. Emit whether Cpp2IL actually ran vs. reused output.
3. **Single-run only.** AT-4 asks for median + spread over ≥3 runs; a `--runs N` option
   or a documented repeat harness would satisfy it. `index` at ~30 s makes repeats cheap
   enough; a true-cold `extract` is the expensive one.

## Repeatable procedure

```powershell
dotnet build S1Atlas.sln --configuration Release
$dll = "src/S1Atlas.Cli/bin/Release/net8.0/S1Atlas.Cli.dll"
# --performance writes ONE diagnostics JSON object to stderr; results go to stdout.
dotnet $dll scan    --performance 2> scan.perf.json
dotnet $dll extract --performance 2> extract.perf.json   # for a TRUE cold run, clear the build's extraction/candidate output first
dotnet $dll index   --performance 2> index.perf.json
```

Data root is `%LOCALAPPDATA%\S1Atlas` (not the repo). Generated data is never committed.

## Follow-ups

- Add extract/index sub-phase instrumentation + a Cpp2IL cold/warm marker (extends AT-4).
- Re-run a **true cold extract** (cleared candidate output) to rank AT-6.
- Capture median/spread over ≥3 runs once sub-phases land.
- Prioritize **AT-7** first based on this baseline.
