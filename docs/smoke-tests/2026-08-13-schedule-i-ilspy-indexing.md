# Schedule I ILSpy Indexing Smoke — 2026-08-13

This smoke measured the pinned ILSpy adapter and typed relationship index against the
preferred, integrity-verified Schedule I extraction and one existing divergent validated
output. It records aggregate results only; no reconstructed source, DLLs, local runtime
data, or proprietary bytes are committed.

## Environment

| Fact | Value |
|---|---|
| Branch / tested commit | `feature/cpp2il-phase5-hardening-replay-finalization` @ `aeb8d9c` |
| OS | Windows 11 |
| Toolchain | .NET 8 SDK, Release configuration |
| Content-derived build ID | `6fbd38f8401afa2241a1322afd4b8a8eadc99aa1f1c660ece253da7859d54bdc` |
| Decompiler package | `ICSharpCode.Decompiler` `10.1.1.8388` |
| Preferred extraction ID | `1950abaf5cac2ab4e54436193efa4030c1cd887e431ff18893056c54e3850fbc` |
| Comparison extraction ID | `41027a0230a63f1dc4d277f1bd26a0a3c5b10b2586fc10d89b21f8f894aa6e7c` |

## Preferred extraction measurement

`Assembly-CSharp.dll` was found in the preferred reconstructed artifact and produced
readable whole-module C# text. Aggregate adapter output:

| Measurement | Result |
|---|---:|
| Source characters | 3,179,118 |
| Types | 3,564 |
| Members | 48,727 |
| Members with recoverable bodies | 26,423 |
| Canonical symbols persisted | 52,291 |
| Extracted and persisted relationship facts | 50,028 |

Metadata inheritance, interface, and typed member relationships are emitted when their
targets can be bound to indexed symbols. Recovered IL contributes conservative call and
field edges; the reconstructed sample does not provide complete body semantics.

## Divergent-output measurement

The same representative assembly from the existing divergent output also produced readable
text and the same aggregate declaration/body counts:

| Measurement | Preferred | Divergent |
|---|---:|---:|
| Source characters | 3,179,118 | 3,179,118 |
| Types | 3,564 | 3,564 |
| Members | 48,727 | 48,727 |
| Members with recoverable bodies | 26,423 | 26,423 |
| Canonical symbols persisted | 52,291 | 52,291 |
| Extracted and persisted relationship facts | 50,028 | 50,028 |

The raw `Assembly-CSharp.dll` hashes differed (`9abdfc53…67803e4d` versus
`1bc776c3…11934b77`), while the measured structural counts matched. This supports using
normalized declarations and structural facts for a later stability comparison, but does
not establish semantic equivalence.

## Fingerprint stability measurement

The preferred and divergent indexes each contained 52,291 canonical symbols, with
52,291 common canonical identities. Comparing normalized evidence for those common
symbols produced:

| Fingerprint layer | Common symbols | Equal | Equality rate |
|---|---:|---:|---:|
| Declaration | 52,291 | 52,291 | 100% |
| Structural | 52,291 | 52,291 | 100% |
| Method body | 26,423 | 26,423 | 100% |

These are normalized metadata/evidence comparisons, not proof of semantic equivalence.

## Integrity and mutation checks

The adapter hashes of `Assembly-CSharp.dll` were identical before and after each run:

- preferred: `9abdfc53b91b003151c30809233c3a83a8bd4f824451b918df82608167803e4d`;
- divergent: `1bc776c30411a43e0e3f589deddb233be1062d220f3c039d6c71b7ca11934b77`.

The validated extraction records do not contain `GameAssembly.dll` or
`global-metadata.dat`; those authoritative inputs remain outside the reconstructed
artifact. The existing extraction validation gate had already verified the authoritative
input set, and this measurement did not modify or rerun extraction.

## Gate result

The focused adapter tests and this live measurement passed. The result is sufficient to
continue with normalized symbols and explicit relationship-quality handling; it is not a
license to infer missing method relationships.

## Full CLI indexing/query smoke

The Phase 5 CLI smoke ran against the same existing preferred extraction without rerunning
Cpp2IL, at commit `aeb8d9c`:

| Fact | Value |
|---|---|
| Index ID | `bbc5418ef2c91664bac697ee039af017a3165cd6625382033463122da36309f5` |
| Schedule I Installed symbols | 52,291 |
| Generated source files | 1 |
| Persisted relationships | 50,028 |
| Source locations returned for `Dealer` | 4,219 |
| Metadata relationships returned for `refs Dealer` | 412 |
| Repeated `index` invocation | reused the completed index |
| Representative search/type/method/source commands | succeeded |
| `refs`, `callers`, `callees` commands | succeeded; no matching edges for the sampled method |
| Installed query channel | Schedule I / Installed only |

The index command's first run reported `reused: false`; the repeated run reported
`reused: true` with the same symbol, source, and relationship counts. A forced rebuild
created a distinct index and snapshot. The sampled type was `ScheduleOne.Economy.Dealer`,
and the sampled method was `ScheduleOne.Economy.Dealer::Awake`. Search and source lookup
returned canonical methods and source locations. `refs`, `callers`, and `callees` for the
sampled method had no matching body edges; this is a limitation of the Cpp2IL-reconstructed
input, while metadata relationships remain queryable.

## Upstream network smoke

The official upstream identities used for configuration are `KaBooMa/S1API` and
`ifBars/S1MAPI`. An explicit manual sync fetched S1API commit
`d9665e9bd95b76b033fb53d5c1698afa82fe53ac` and cached 162 C# files. Ordinary `upstream
status` performed no network access and reported the cached commit and exact source match.
The first
transport attempt returned GitHub HTTP 403; adding the required explicit User-Agent fixed
the transport, and the retry completed successfully.

No installed S1API/S1MAPI binary snapshot or Release/Preview repository provenance was
available in the existing Atlas environment during this smoke, so those measurements remain
unavailable rather than inferred. Cached upstream files and generated Schedule I source
remain under ignored local runtime roots and are not tracked by Git.
