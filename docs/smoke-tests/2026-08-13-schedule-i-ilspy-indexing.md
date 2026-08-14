# Schedule I ILSpy Indexing Smoke — 2026-08-13

This smoke measured the pinned ILSpy adapter against the preferred, integrity-verified
Schedule I extraction and one existing divergent validated output. It records aggregate
results only; no reconstructed source, DLLs, local runtime data, or proprietary bytes are
committed.

## Environment

| Fact | Value |
|---|---|
| Branch / tested commit | `feature/cpp2il-phase5-hardening-replay-finalization` @ `a6c6a84` |
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
| Extracted relationship facts | 0 |

The zero relationship-fact result is recorded as an adapter limitation for this
reconstructed sample, not treated as evidence that relationships are absent. The indexing
implementation must not invent fallback relationships from declarations alone.

## Divergent-output measurement

The same representative assembly from the existing divergent output also produced readable
text and the same aggregate declaration/body counts:

| Measurement | Preferred | Divergent |
|---|---:|---:|
| Source characters | 3,179,118 | 3,179,118 |
| Types | 3,564 | 3,564 |
| Members | 48,727 | 48,727 |
| Members with recoverable bodies | 26,423 | 26,423 |
| Extracted relationship facts | 0 | 0 |

The raw `Assembly-CSharp.dll` hashes differed (`9abdfc53…67803e4d` versus
`1bc776c3…11934b77`), while the measured structural counts matched. This supports using
normalized declarations and structural facts for a later stability comparison, but does
not establish semantic equivalence.

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
