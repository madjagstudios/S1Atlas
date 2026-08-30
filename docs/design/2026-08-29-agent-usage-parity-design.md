# Agent Usage and MCP Parity Design

**Status:** Implemented

## Goal

Make S1Atlas equally usable from Codex and Claude while keeping reference-mod selection explicit, local, provenance-preserving, and efficient for agent-driven mod development.

## Problem

S1Atlas v1.1.0 already provides a shared agent skill, read-only MCP tools, CLI fallbacks, and explicitly selected reference-mod collections. The remaining usability gap is operational: an agent can follow the evidence methodology only if its host has the MCP server registered, and a registered server does not by itself ensure narrow, well-scoped queries.

The two concerns are therefore related but separate:

1. **Availability:** Codex and Claude must expose the same read-only S1Atlas MCP server.
2. **Method:** both agents must use the same evidence-first query sequence and collection-selection rules.

## Scope

### Shared agent contract

`skills/s1atlas/SKILL.md` remains the single versioned usage contract. It applies to both Codex and Claude and documents:

- MCP-first usage when the server is actually available.
- CLI JSON as the supported fallback when MCP is unavailable.
- The prohibition on inventing MCP results or treating an unavailable server as an empty index.
- Explicit selection of a completed reference collection before prior-art queries.
- A narrow query sequence: identify scope, resolve the exact symbol, inspect focused source, then request only the relationship evidence needed.
- Carrying build, extraction, index, collection, mod, and content-hash provenance into the resulting decision.
- The distinction between static recovered evidence, callable-surface evidence, body-recovery evidence, and runtime behavior.

The contract must not require a machine-specific path, credentials, or a host-private manifest.

### Host registration

Each agent host gets a local, user-level registration for the same read-only stdio server:

```text
command = "dotnet"
args = ["<local-S1Atlas-root>/src/S1Atlas.Mcp/bin/Release/net8.0/S1Atlas.Mcp.dll", "mcp", "serve"]
```

The Release project is built separately so registration does not invoke restore or build work during MCP startup. The registration is host configuration, not repository content. It must use the operator's local checkout path, enable the server, and apply bounded startup and tool timeouts. No host registration may add write, network, extraction, indexing, or game-execution capabilities.

The verification target is tool parity, not configuration-file identity: both hosts must expose the same supported read-only S1Atlas tools, even if their configuration formats differ.

### Curated reference collections

Reference-mod indexing remains manifest-driven. An operator chooses the mods, roots, metadata, and selected file globs in a local manifest, validates it, and indexes it against a completed Schedule I base index. S1Atlas does not download mods, rank internet mods by similarity, discover unlisted local roots, or redistribute selected source.

This supports a focused collection such as `qol` without claiming that its members are safe, compatible, licensed for redistribution, or behaviorally equivalent. Similarity remains an agent/operator selection decision supported by the resulting indexed prior art.

### Efficient evidence loop

For a mod implementation question, both agents follow this sequence:

1. Identify the current completed game build and list completed reference collections only when prior art is relevant.
2. Select one explicit collection and retain its recorded Schedule I base-index binding.
3. Resolve the exact symbol and qualify ambiguity instead of guessing.
4. Read the focused source span before requesting a containing type or broad file output.
5. Request callers, callees, call sites, field references, or related types only when they answer the current question; use bounded limits.
6. Label the conclusion as FACT, DERIVED, or INTERPRETATION and preserve the result's completeness boundary.
7. Use runtime testing for claims that static evidence cannot establish.

Repeated collection listing, broad type dumps, duplicate MCP and CLI queries, and unscoped cross-collection searches are explicitly discouraged.

### Failure and fallback behavior

- If the MCP server is not registered or the tool is not available, the agent uses the CLI JSON surface.
- If the CLI or MCP result says an index, collection, relationship, or source is unavailable, the agent reports that state rather than substituting memory or an unrelated build.
- If a query is ambiguous or bounded, the agent preserves that limitation in the answer.
- Querying remains read-only; indexing and manifest validation remain explicit CLI operations.

## Verification

The implementation is complete when:

- Codex and Claude both have a local `s1atlas` MCP registration using the same server entry point.
- Each host can list the available S1Atlas tools and successfully perform a representative collection-list and exact-symbol read query.
- The shared skill clearly describes MCP-first/CLI-fallback selection, curated collection use, narrow query sequencing, and provenance requirements.
- The CLI and MCP paths return the same evidence scope and trust boundaries for the representative query.
- Host configuration, local manifests, mod paths, credentials, and generated indexes remain outside the public repository.
- The public repository passes its normal build, test, format, hygiene, and publication-audit gates.

## Non-goals

- Automatic mod downloading or internet search.
- Automatic similarity scoring or recommendation ranking.
- A second agent-specific copy of the S1Atlas skill.
- MCP write operations or automatic indexing.
- Rewriting existing public history.
