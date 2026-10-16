# AT-69 attribute search: real-build acceptance check

Date: 2026-10-16. Status: **acceptance passed on a real build, in a sandbox copy of a
local atlas.** The live atlas was never written.

## Inputs

| Fact | Value |
|---|---|
| Schedule I build | `a9add121f99f9f00a6f23054e2f4ea4403c980e2e6ae1cf4e8df05d5620aa116` (Steam build 25796331) |
| Preferred extraction | `36f7132d89cf89b54d27246667be3ab92e2438b9ecc624c1ec7a1253eeae2b26`, validated, reused (no new `extract`) |
| Game index (with attributes) | `d932347e51cf2e739c75105cb72fd478fae51acdb136b85fdd1da70f55b7e4d6`, 63,422 symbols |
| Installed S1API | 3.2.1.0, installed index 14,650 symbols |
| S1MAPI | not installed |

## Procedure

The atlas data root was copied, without its backup folders, to a short sandbox
path, and every command ran with `S1ATLAS_HOME` set to that copy (`<data-root>`).
The commands used the branch build:

```powershell
$env:S1ATLAS_HOME = "<data-root>"
s1atlas doctor
s1atlas status        # migrates the copy to schema 21
s1atlas scan          # records the installed DLL hashes (schema 20)
s1atlas index --json  # game index plus the installed S1API index
s1atlas doctor --json
```

Run `scan` before `index`. In this run, `index` first ran before the scan
completed, so the installed S1API index was bound to the earlier environment
snapshot and the S1API query reported `StaleApiIndex` until `index` ran again
after the scan (the game index was reused, and the S1API index rebuilt). The
documented upgrade order (`doctor`, `status`, `scan`, `index`) avoids this.

That re-index also exercised the AT-142 fix on real data: the sandbox already
held an installed S1API snapshot from the previous index format, and the
rebuild completed without a `source_files` constraint error.

## Results

| Query | Exit | Total |
|---|---|---|
| `attributes "FishNet.Object.ServerRpcAttribute" --json` | 0 | 325 |
| `attributes "FishNet.Object.ServerRpcAttribute" --arg RequireOwnership=false --json` | 0 | 297 |
| `attributes "UnityEngine.SerializeField" --json` | 0 | 2,129 |
| `attributes "System.ObsoleteAttribute" --codebase s1api --json` | 0 | 44 |
| `attributes "Cpp2ILInjected.AddressAttribute" --json` | 0 | 0 |
| `attributes "Cpp2ILInjected.TokenAttribute" --json` | 0 | 0 |
| `attributes "Cpp2ILInjected.CallsAttribute" --json` | 0 | 0 |
| MCP `find_by_attribute` (`attribute: FishNet.Object.ServerRpcAttribute`, `limit: 3`) | resolved | 325 |

`doctor --json` after the run: the schema, scan, extraction and Schedule I index
items were `ok`. The optional API item was `stale` only because a separate S1API
Preview index is older than the last upstream sync. That predates this change and
is unrelated to the installed index.

## Excerpts

The first hit each query returned in game code, abridged to the symbol and the
matching attribute.

Game RPC methods (`ServerRpcAttribute`, 325 hits):

```json
{
  "success": true, "exitCode": 0, "totalCount": 325, "returnedCount": 50,
  "hit": {
    "symbol": "ScheduleOne.Cartel.Cartel::SetStatus_Server(ECartelStatus,System.Boolean):System.Void",
    "kind": "Method",
    "attribute": "FishNet.Object.ServerRpcAttribute",
    "target": "Member",
    "namedArguments": [ { "name": "RequireOwnership", "value": false } ]
  }
}
```

The same query with `--arg RequireOwnership=false` narrows to 297 hits, and its
first hit is the same method.

Serialized fields (`SerializeField`, 2,129 hits):

```json
{
  "success": true, "exitCode": 0, "totalCount": 2129, "returnedCount": 50,
  "hit": {
    "symbol": "BurnoutMovement::System.Boolean useSineWave",
    "kind": "Field",
    "attribute": "UnityEngine.SerializeField",
    "target": "Member"
  }
}
```

Obsolete S1API members (`ObsoleteAttribute`, installed channel, 44 hits):

```json
{
  "success": true, "exitCode": 0, "totalCount": 44, "returnedCount": 44,
  "hit": {
    "symbol": "S1API.Casino.SlotMachineHelper::FindNearestSlotMachine(UnityEngine.Vector3,System.Single):Il2CppScheduleOne.Casino.SlotMachine",
    "kind": "Method",
    "attribute": "System.ObsoleteAttribute"
  }
}
```

Cpp2IL-injected attributes never appear (the same empty envelope for `Address`,
`Token` and `Calls`):

```json
{ "success": true, "exitCode": 0, "totalCount": 0, "returnedCount": 0, "attributeHits": [] }
```

MCP `find_by_attribute` over stdio:

```json
{
  "status": "resolved",
  "indexId": "d932347e51cf2e739c75105cb72fd478fae51acdb136b85fdd1da70f55b7e4d6",
  "totalCount": 325,
  "returnedCount": 3
}
```

The sandbox copy was deleted afterwards.
