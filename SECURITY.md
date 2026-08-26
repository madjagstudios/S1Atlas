# Security Policy

S1Atlas runs entirely locally and offline. It does not phone home, open network
listeners, or transmit data — the MCP server speaks over stdio to a local client,
and all generated data stays on your machine. That limits the attack surface, but
we still take security reports seriously.

## Supported versions

S1Atlas is developed on a rolling basis; only the current `main` is supported.
Please reproduce any issue against the latest `main` before reporting.

## Reporting a vulnerability

**Do not open a public issue for a security problem.**

Use GitHub's private vulnerability reporting instead:

1. Go to the repository's **Security** tab.
2. Click **Report a vulnerability**.
3. Describe the issue, the affected version/commit, and reproduction steps.

We aim to acknowledge a report within a few days. Once a fix is available and
released, we're happy to credit you unless you prefer to remain anonymous.

## Scope notes

- **Do not include proprietary game data** (assembly dumps, `global-metadata.dat`,
  decompiled output, `atlas.db`, etc.) in a report. Describe the behavior; never
  attach game-derived artifacts. See [CONTRIBUTING.md](CONTRIBUTING.md).
- Reports that depend on an attacker already having arbitrary local code execution,
  or on a maliciously crafted game installation the user chose to point the tool at,
  are generally out of scope — but tell us anyway if you find something surprising.
