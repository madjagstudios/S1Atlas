# V1 Milestone 1 usability/API smoke

This report records sanitized metadata and counts only. No proprietary game source, generated source, Atlas database, or absolute machine paths are included.

## Environment

| Check | Result |
| --- | --- |
| Schedule I installation discovery | PASS |
| Schedule I build identity | `6fbd38f8…9d54bdc` |
| S1API Installed | PASS — `3.1.12.0` present |
| S1MAPI Installed | Not present |
| Normal query network access | PASS — local-only |

## Schedule I usability

| Operation | Result |
| --- | --- |
| Installed index schema 8 build | PASS — reused after verified rebuild |
| `search Property --limit 10` | PASS — total `991`, returned `10` |
| Focused `source` query | PASS — verified file `Assembly-CSharp.cs`, `3,179,118` bytes, span `34357:3–34360:4`, body status `StubOrUnavailable` |
| `refs` query | PASS — `127` relationships |
| `callers` query | PASS — zero results with stub/unavailable completeness notice |
| `callees` query | PASS — zero results with stub/unavailable completeness notice |

The Schedule I index observed `26,423` `StubOrUnavailable` methods and `356` `NoBodyByDesign` methods. `Recovered` and `Unknown` were both `0` in this snapshot. A further `3,777` non-method symbols had no body-classification value, as expected for fields/events/properties.

## API channel matrix

Release and Preview were indexed from exact cached commits after explicit upstream sync. Each channel received a distinct index identity even where the source commit was the same.

| Codebase/channel | Result | Source identity | Index ID prefix | Symbols | Source files |
| --- | --- | --- | --- | ---: | ---: |
| S1API Installed | PASS | binary `3.1.12.0` | `30457300…` | 13,197 | 1 |
| S1API Release | PASS | `d9665e9b…fe53ac` | `3f40e0df…` | 723 | 162 |
| S1API Preview | PASS | `d9665e9b…fe53ac` | `d8d31f14…` | 723 | 162 |
| S1MAPI Installed | Not present | — | — | — | — |
| S1MAPI Release | PASS | `616d8686…40dc61` (`v2.0.1`) | `1ce5da84…` | 1,658 | 65 |
| S1MAPI Preview | PASS | `52ac066d…c94294` (`stable`) | `ef72b4c7…` | 1,658 | 65 |

Representative Release/Preview searches and integrity-checked source queries returned the requested codebase and channel labels. Cached Release/Preview indexing used no upstream network access; network access occurred only during the explicit `upstream sync` commands.

## Known limitation

Cpp2IL-derived Schedule I method bodies are conservative: zero caller/callee results for a `StubOrUnavailable` body are not evidence that no runtime callers exist. The CLI exposes that completeness notice.
