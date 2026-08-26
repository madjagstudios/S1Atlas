---
name: Bug report
about: Something S1Atlas did wrong, or a wrong/mislabeled result
title: ""
labels: bug
assignees: ""
---

<!--
Do NOT paste proprietary game data: no assembly dumps, global-metadata.dat,
decompiled source, atlas.db contents, or extracted artifacts. Describe the
behavior and use symbol names — never attach game-derived files.
-->

## What happened

A clear description of the bug and what you expected instead.

## Steps to reproduce

1. Command run (e.g. `s1atlas search "..."` / `extract` / `index` / `docs generate`)
2. ...
3. ...

## Provenance / labeling

If a result looked wrong: was it labeled `FACT`, `DERIVED`, or `INTERPRETATION`?
Paste the symbol name or query, not the game-derived content.

## Environment

- S1Atlas commit (`git rev-parse --short HEAD`):
- OS / .NET SDK version:
- Command surface: CLI / MCP / portal

## Additional context

Logs (with any game-derived content removed), screenshots of the tool's own output, etc.
