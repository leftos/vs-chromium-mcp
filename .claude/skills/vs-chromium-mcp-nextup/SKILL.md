---
name: vs-chromium-mcp-nextup
description: Profile for the user-level `nextup` skill in the vs-chromium-mcp repo — loaded by `nextup` at its step 0 for this project's plan order, gates and landing path. Not a loop of its own; invoke `/nextup`.
---

# vs-chromium-mcp profile for `nextup`

The generic loop is the user-level `nextup` skill; this file supplies only what is vs-chromium-mcp-specific.

siblings: none
linear: vs-chromium-mcp

## Plan and tracker

- The plan lives in Linear: every task is a Linear issue in team VSCM, per `~/.claude/docs/plan-operations.md`; `docs/plans/MAIN.md` is its generated snapshot, never edited by hand. Project order, which is the order the queue is worked: `Build scripts`, `Daemon and MCP tools`, `Backlog`.
- A steer or a finding the item does not fix gets an **add**, in the project whose files it shares, else in `Backlog`.
- Tracker: **triage** as plan-operations says (GitHub issues reach the team through Linear's sync; an untriaged one is top-level with no project), each placed in the project that shares its files, else in `Backlog`.
- The repo is public: every issue title and description is public through the GitHub sync.

## Gates

- `dotnet build VsChromiumMcp.slnx -c Release`, through the user-level gate (`pwsh ~/.claude/tools/gate/gate.ps1 -Log .tmp/build.log -TimeoutSeconds 600 -Slot heavy -- dotnet build VsChromiumMcp.slnx -c Release`), with no warnings.
- No test project exists; until one does, a daemon or pipe change is checked with the manual smoke in `CLAUDE.md` (`src/Prototype/Program.cs`, then `pwsh scripts/install.ps1` and the tools exercised from a Claude session), which needs the user's session.

## Traps

- `vendor/vs-chromium` is a submodule pinned to v0.9.37 and is never committed from: `scripts/build-vendor.ps1` patches `Build/Common.Build.settings` in place, so the submodule shows as modified after a build.

## Landing

Commit on `main` and push, with a ≤4-char type tag, an imperative ≤72-char subject and the session's attribution trailers.
