# AT-136 manual patch targets: Organized Crime acceptance check

Date: 2026-10-10. Status: **current-DLL acceptance passed after review fixes.**
Both runs used a pinned copy of the installed DLL and the same two completed
game indexes.
All seven Harmony targets in the current acceptance list have resolved patch
sources. The eighth named dependency is a reflection invocation in this DLL.
The older AT-122 DLL has not been remeasured.

## Summary

- The branch finds 27 unchanged patch targets, compared with 0 on `main`.
  Unresolved patch-target rows drop from 4 to 3. These counts compare identical
  bytes, pinned after the installed DLL changed during verification.
- Arrest has both Prefix and Postfix sources. The curfew minute pass has its
  Postfix source at `OnUncappedMinPass`, with no unresolved row at `TryEngage`.
- The installed input differs from AT-122. Its `set_IsCurrentlyActive` lookup
  feeds `MethodInfo.Invoke`, not `Harmony.Patch`. For this input, the acceptance
  list is seven Harmony targets plus an unchanged reflection setter dependency.
  Requiring a Harmony source on that setter would manufacture a registration.
- A real `reference index` run on a writable copy of the existing atlas rebuilt
  the same collection after the resolver identity changed, then reused it.
  CLI `patched-by` queries read back all eight resolved patch edges on the seven
  game targets and no patch edge on the reflection-only setter.

## Revisions and inputs

- Baseline: `main` at `f65d4a9d9a83c586342f643ca8770dc1c1db3e31`, reference
  resolver version 3.
- After: the AT-136 branch at `b97878b4` plus these uncommitted review fixes,
  reference resolver version 4. Podium owns the task commit.
- Both commands used `dotnet run --project <source>/src/S1Atlas.Cli -- check-mod
  <OrganizedCrime.dll> --json`, built from their respective source trees.
  Both returned command exit code 0. The globally installed tool was not used.

| DLL name | SHA-256 |
| --- | --- |
| `OrganizedCrime.dll` | `0140805f50b259e85d4dbe87bedd12353f9f95258bf378d84527ce4d799722b6` |

The pinned copy's hash was identical before and after the runs. The installed
DLL was replaced during an earlier attempt in this session, so those runs were
discarded and both commands and reference indexes were rerun on the pinned
copy. The rejected Task 4 note measured
`e81c8a5608cf121e963f73da4a9f3e923dd5ad80d1182bec48ec85db8760c414`
(18 unchanged, 10 unresolved); its counts are not a same-input comparison with
this rerun. The AT-122 hash was
`816ae5d8dfab0bb0bf457249a51829b45eeca660de231c8ec66d6ae6f218b1f0`.
ILSpy inspection of the measured input established the registration shapes below.

## Build and index scope

The branch's `builds --json` returned eight valid builds. Both `check-mod`
results have `singleBuild: false` and identify the same two completed indexes.
No single-build fallback was used. The default `--from` selection picked the
most recent older indexed build.

| Context identity | Before and after |
| --- | --- |
| From build | `da2c5b326f4aa5dbfabc8faf526cb736a30bac6e7a1369c38bb408bb5e41d78a` |
| From completed game index | `7f5a456fa3cc18bf85ce43ed51193f9e86d583cf6d51677f36ba110b535c8d57` |
| To build | `a9add121f99f9f00a6f23054e2f4ea4403c980e2e6ae1cf4e8df05d5620aa116` |
| To completed game index | `23bf6d58ced7091b57a9ec06c81ca50c1078dce798a0e3aa6a3c58f844381c94` |

## Results

Every count comes from `data.summary.patchTargets` in the saved JSON. Counts
are dependency rows, not patch-source counts, and unresolved rows can be named
after a source method.

| Status | Before (`main`) | After review fixes | Delta |
| --- | --- | --- | --- |
| `removed` | 0 | 0 | 0 |
| `signature_changed` | 0 | 0 | 0 |
| `unresolved` | 4 | 3 | -1 |
| `moved` | 0 | 0 | 0 |
| `unchanged` | 0 | 27 | +27 |
| `resolved` | 0 | 0 | 0 |

Names use the normalized game view without the `Il2Cpp` prefix. Patch kinds
come only from `harmony_patch` sources. The setter row is explicitly checked as
reflection evidence for this DLL, rather than counted as a Harmony target.

| Current acceptance dependency | After status | Patch kinds | Source kinds | Acceptance |
| --- | --- | --- | --- | --- |
| `ScheduleOne.PlayerScripts.Player::RpcLogic___Arrest_Server_2166136261` | `unchanged` | Prefix, Postfix | `harmony_patch`, `reflection` | Pass |
| `ScheduleOne.Law.CurfewManager::OnUncappedMinPass` | `unchanged` | Postfix | `harmony_patch`, `reflection` | Pass |
| `ScheduleOne.Law.CurfewManager::set_IsCurrentlyActive` | `unchanged` | None (reflection invocation) | `reflection` | Pass for current input; not a Harmony target |
| `ScheduleOne.NPCs.NPC::ReceiveImpact` | `unchanged` | Prefix | `harmony_patch` | Pass |
| `ScheduleOne.NPCs.Responses.NPCResponses::ImpactReceived` | `unchanged` | Prefix | `harmony_patch` | Pass |
| `ScheduleOne.NPCs.NPCHealth::TakeDamage` | `unchanged` | Prefix | `harmony_patch` | Pass |
| `ScheduleOne.NPCs.NPCHealth::KnockOut` | `unchanged` | Prefix | `harmony_patch` | Pass |
| `ScheduleOne.NPCs.NPCHealth::Die` | `unchanged` | Prefix | `harmony_patch` | Pass |

## Registering shapes and remaining unresolved rows

- `PoliceCustodyPatch.Apply` stores the result of `ResolveTarget()` in a local,
  then registers Prefix and Postfix methods with constant type/name arguments.
  The static, parameterless helper performs one constant `AccessTools.Method`
  lookup with `Type.EmptyTypes`, checks null, return type and declaring type,
  throws on mismatch, and returns that lookup. It now resolves one level deep.
  The helper classifier checks these constant returns before deciding whether
  to inline a registration, so an outer caller no longer suppresses `Apply`'s
  resolved facts.
- `Release1LockdownGatePatch.EnsurePatched` assigns constant target and patch
  method lookups with simple `?? throw new MissingMethodException(...)` guards.
  The IL stack value is preserved on the sole surviving path. The method now
  resolves independently, so its patches are not attributed to `TryEngage`.
- `Release1LockdownGatePatch.OnUncappedMinPassPostfix` obtains
  `IsCurrentlyActive` with `AccessTools.PropertySetter` and uses a null-conditional
  `Invoke`. Its source member is
  `OrganizedCrime.Runtime.Release1LockdownGatePatch::OnUncappedMinPassPostfix(Il2CppScheduleOne.Law.CurfewManager):System.Void`.
  This lookup is an unchanged reflection dependency, not a Harmony registration.

All four unresolved baseline rows (two FishWarehouse Postfix rows and Arrest's
Prefix/Postfix) now resolve. The three rows below were absent from the baseline
patch report. Retaining unresolved registrations exposes these additional gaps;
no previously resolved target became unresolved. None is on the current
acceptance list. The reasons and source members are copied from the after JSON.

| Patch kind | Reason | Reported source member |
| --- | --- | --- |
| Prefix | `unrecognized-manual-shape` | `OrganizedCrime.Runtime.OcCheckpointSwapPatch::SwapPrefix(Il2CppScheduleOne.NPCs.Behaviour.CheckpointBehaviour,OrganizedCrime.Runtime.OcCheckpointSwap&):System.Boolean` |
| Postfix | `unrecognized-manual-shape` | `OrganizedCrime.Runtime.OcCheckpointSwapPatch::SwapPostfix(OrganizedCrime.Runtime.OcCheckpointSwap):System.Void` |
| Finalizer | `unrecognized-manual-shape` | `OrganizedCrime.Runtime.OcCheckpointSwapPatch::SwapFinalizer(OrganizedCrime.Runtime.OcCheckpointSwap):System.Void` |

## Real reference-index rebuild and read-back

The same local manifest selected only the measured `OrganizedCrime.dll`, in
collection `organized-crime-at136-pinned`, against the completed To game index
above.
`reference index <manifest> --json` ran first from `main`, then from the branch,
then again from the branch. No `--force` was used.

| Run | Reference index ID | Reused | Symbols | Relationships |
| --- | --- | --- | --- | --- |
| Baseline, resolver 3 | `ef790f2d9b44f68c7e555899629bd52bf41ac09c08b9fc7a60792892f60ae308` | false | 30,723 | 80,509 |
| Branch, resolver 4 | `4df35cfcd3a4b723cc4311b15c44ee470eaa1fad385e4690545a99f77147608d` | false | 30,723 | 80,536 |
| Branch repeat | `4df35cfcd3a4b723cc4311b15c44ee470eaa1fad385e4690545a99f77147608d` | true | 30,723 | 80,536 |

All three runs exited 0 with one mod, one source file, no documents and no
warnings. `patched-by <selector> --scope reference --collection
organized-crime-at136-pinned --json` then read the stored edges through the CLI.
Arrest returned two resolved edges (Prefix and Postfix); each of the other six
Harmony targets returned one resolved edge of the kind in the acceptance table.
The setter returned no patch edge. Every query exited 0.

## Isolation

The verification used a writable copy of the existing atlas database and the
two builds' validated extraction directories. Both copied extractions passed
the production integrity verifier. The live atlas was not modified, and no
game index was rebuilt. The baseline source was a Git archive of the exact
`main` SHA above; this rerun did not require a scratch worktree or bare copy.
The earlier run's isolated bare-copy worktree method remains valid for its
recorded SHA, but is not the isolation method for this rerun.

No DLL, decompiled source, raw JSON, atlas copy or local path is checked in.

## Regression verification

| Command | Result |
| --- | --- |
| `dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --no-restore --filter "FullyQualifiedName~ManualPatchShapeTests\|FullyQualifiedName~ReferenceResolverVersionTests"` | Exit 0: 6 passed, 0 failed, 0 skipped |
| `dotnet format S1Atlas.sln --verify-no-changes --no-restore` | Exit 0: no formatting changes required |
| `dotnet test S1Atlas.sln --no-restore` | Exit 0: 3,209 passed, 0 failed, 16 skipped across 8 test projects |
| `git diff --check` | Exit 0: no whitespace errors |

The two added shape tests failed before their fixes. They also check that
ambiguous helper returns, nested helpers and null fallbacks stay unresolved,
that an outer caller does not suppress a self-contained registration, and that
a patch inside a throwing arm is retained. Resolver-version tests verify
rebuilding followed by reuse with resolver version 4; the real index and CLI
read-back above independently verify that behavior on the existing atlas.
