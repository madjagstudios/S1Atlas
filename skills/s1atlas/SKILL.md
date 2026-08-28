---
name: s1atlas
description: Use when an agent answers questions about Schedule I internals, chooses a mod implementation, inspects a game or dependency update, or decides between S1API/S1MAPI and a direct game patch.
---

# S1Atlas evidence-first Schedule I work

S1Atlas is an evidence source, not a permission to guess. Use it before making
claims about Schedule I internals, and keep every claim tied to the exact indexed
scope that supports it. MCP is a faster read-only interface; the CLI is the
always-available fallback and the only S1API/S1MAPI query surface.

## Install and prerequisites

The versioned source of truth is `skills/s1atlas/SKILL.md` in the S1Atlas repo.
Install the directory using the skill mechanism supported by your agent host;
prefer a junction or symlink so the source stays versioned. Verify that the
installed `SKILL.md` has identical bytes to the repository copy and that its
frontmatter description matches the task before relying on it.

MCP is optional. To register the read-only stdio server with Claude Code, add
this launch command to the S1Atlas MCP server entry:

```text
dotnet run --project src/S1Atlas.Mcp -- mcp serve
```

Use MCP tools only after the server is registered and the tool is actually
available. If it is not registered, use the CLI commands below; do not invent
MCP results or silently treat an unavailable server as an empty index. Neither
interface writes Atlas data as part of querying, and “cite” means cite in your
own answer or decision record, never write a citation back to Atlas.

## The evidence loop

1. **Pin the scope.** Prefer the current indexed Schedule I build. Use CLI
   `status --json` and `builds --json`, or MCP `list_builds`, to identify it.
   An explicitly targeted build is allowed: pass `--build <id>` to Schedule I
   CLI queries or `buildId` to MCP. An omitted MCP `buildId` resolves the current
   build; an explicit one is never silently replaced. S1API and S1MAPI have no
   game-build dimension: query their selected `--codebase` and `--channel`, and
   cite the completed index’s commit SHA and index ID.
2. **Find the exact symbol.** Resolve names before reasoning from them. Use
   `search`, `type`, `method`, or `callable` on the CLI; use `search_symbols`,
   `get_type`, `get_method`, or `get_callable_surface` through MCP. Qualify
   ambiguous matches instead of choosing one.
3. **Inspect behavior.** When behavior, side effects, ownership, lifetime, or
   persistence matters, inspect the decompiled span first: CLI `source` or MCP
   `get_source`. A method name or an old mod guide is not behavior evidence.
4. **Trace relationships.** Use CLI `refs`, `callers`, and `callees`, or MCP
   `find_references`, `find_callers`, and `find_related_types`. Preserve the
   reported direction, resolution status, and completeness boundary.
5. **Check higher-level evidence.** For scene questions use CLI `scenes`,
   `scene`, `gameobject`, `prefab`, and `component`, or MCP `list_scenes`,
   `get_scene`, `get_gameobject`, `get_prefab`, and `get_component`. For
   environment/dependency facts use CLI `env --json` or MCP `get_environment`.
6. **Recheck after change.** After a Schedule I game update, use CLI `builds`
   and `diff <build-before> <build-after>`, or MCP `list_builds` and
   `compare_symbol` (which requires two explicit build IDs). Then repeat the
   affected source and relationship queries. After an S1API/S1MAPI or dependency
   update, re-query the selected API `--codebase`/`--channel` with `search`,
   `type`, `method`, `source`, and the relevant relationship commands. For a
   cached upstream snapshot cite its commit SHA and index ID; for an installed
   API snapshot cite its binary SHA, environment identity, and index ID. The
   CLI `diff` command is for installed build IDs and is not the API update path.
   Pre-update guides are historical evidence, not current proof.

### Quick reference

| Evidence need | CLI, always available | MCP, when registered |
|---|---|---|
| Locate a symbol | `search`, `type`, `method` with `--json` and, for Schedule I, `--build` | `search_symbols`, `get_type`, `get_method` |
| Callable surface | `callable <game-member>` | `get_callable_surface` |
| Behavior/source | `source <query> --context <n> --json` | `get_source` |
| Callers/references | `callers`, `callees`, `refs` | `find_callers`, `find_references`, `find_related_types` |
| Builds/history | `status`, `builds`, `diff <a> <b>` | `list_builds`, `compare_symbol` |
| Environment | `env --json` | `get_environment` |
| Scenes | `scenes`, `scene`, `gameobject`, `prefab`, `component` | `list_scenes`, `get_scene`, `get_gameobject`, `get_prefab`, `get_component` |
| S1API/S1MAPI | `search`/`type`/`method`/`source`/`refs`/`callers`/`callees --codebase <s1api-or-s1mapi> --channel <channel>` | Not exposed by the V1 MCP server |

Use `upstream status --codebase s1api|s1mapi` to inspect cached upstream state.
`upstream sync` and `index --codebase ... --commit ...` prepare data; they are
not evidence until a completed index exists and its query result reports the
commit and index provenance.

## Authority boundary and provenance contract

Only a completed, matching index is evidence. For Schedule I, the selected
index must be over the preferred, integrity-verified extraction resolved by the
shared authority path. For S1API/S1MAPI, use the latest completed index for the
chosen codebase/channel and its real source identity. Running or failed runs,
retained failure output, Phase 3 candidates, unverified extractions, and
unchecked rows are never authoritative and must never support a claim.

Label every statement:

- **FACT** — directly returned from indexed metadata, source, or a resolved
  relationship. Cite the exact symbol/signature and its source span when useful.
- **DERIVED** — computed from returned facts, such as a relationship count,
  selection, or coverage statement. State the inputs and true denominator.
- **INTERPRETATION** — a human explanation or recommendation. Label it plainly,
  keep it separate from evidence, and show the FACT/DERIVED evidence it uses.

For Schedule I, copy the returned MCP build context or CLI JSON identifiers into
the citation: requested/resolved build ID, extraction ID, index ID, and integrity
state. For S1API/S1MAPI, cite codebase, channel, source commit SHA, and index ID.
Do not claim more than the result’s completeness boundary permits. “Zero callers
in this completed index” is a measured DERIVED result; unavailable, not indexed,
integrity-failed, ambiguous, and zero are different states and must remain so.

A useful decision note has this shape:

```text
Claim [FACT | DERIVED | INTERPRETATION]: <one claim>
Evidence: <exact symbol(s), source span or relationship result>
Scope: <resolved build or codebase/channel>
Provenance: <extraction/index IDs, or API commit/index IDs; integrity state>
Limitations: <zero, unavailable, partial, or not-indexed state if applicable>
```

## API-before-patch rule

Before recommending a direct game patch, check the relevant S1API and S1MAPI
indexed surfaces with the CLI. Search the API codebase/channel, inspect its
source, and trace its references when the abstraction may already expose the
needed operation. If the API snapshot is absent, stale, ambiguous, or not
completed, say that explicitly; do not replace it with memory or documentation
from an unrelated commit. Then inspect the current Schedule I symbol and source
before discussing any direct patch. This is a decision discipline, not a license
to edit game files or add new Atlas capabilities.

## C# learning rule

Explain what the exact decompiled C# span shows: signatures, control flow,
nullability checks, calls, collections, and types. Teach the syntax without
turning it into a behavioral certainty the source does not prove. For example:

> **FACT:** `Employee.Fire()` calls `EmployeeManager.RemoveEmployee()` in build
> `<build-id>`; source `<symbol/signature>`, index `<index-id>`.
> **DERIVED:** the indexed call graph has one incoming call from `<caller>`.
> **INTERPRETATION:** this may be the employment-state transition; persistence
> still needs evidence from the surrounding save/load symbols.

## Common mistakes — stop and correct them

| Temptation | Correction |
|---|---|
| Answer from a symbol name or old guide | Resolve the current symbol and inspect `get_source`/`source`. |
| Use MCP when it is not registered | Fall back to the CLI and report the unavailable server. |
| Treat zero or missing data as proof of absence | State the exact zero/unavailable/not-indexed boundary. |
| Recommend a direct patch before checking APIs | Query S1API/S1MAPI first, then inspect Schedule I evidence. |
| Cite “the current build” without IDs | Include the returned build, extraction, index, or API commit/index identifiers. |
| Reuse pre-update modding knowledge after an update | Run `diff`/`compare_symbol` and re-query affected source and relationships. |
| Write findings into Atlas | Keep the skill read-only; cite results in your own output only. |

If any of these red flags appears, stop the recommendation and return to the
evidence loop.
