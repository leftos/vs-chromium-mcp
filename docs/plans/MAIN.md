# vs-chromium-mcp — Main plan

Entry point for ongoing work on this repo. New tasks land here as unchecked checkboxes; completed items get archived periodically to `docs/plans/archive/`.

## Status

**v1 (2026-05-18): SHIPPED.** Architecture (Path C — unmodified Server.exe, persistent user-mode daemon, stdio MCP) validated end-to-end. MCP registered with Claude Code at user scope as `vs-chromium`. User-level skill `/vs-chromium-index` available for per-repo setup.

## Done

- [x] Validate that the protobuf-over-TCP IPC works between .NET 8 and vs-chromium's .NET Framework 4.7.1 Core.dll
- [x] Vendor vs-chromium @ v0.9.37; patch `Common.Build.settings` v4.5 → v4.7.1; build C# + C++ Native projects
- [x] Implement `VsChromiumServerHost` — TCP listener, Server.exe lifecycle, protobuf request/response correlation by RequestId, typed-event dispatch
- [x] Implement `Operations` — `ensure_indexed` (with two-phase wait: scan + search-engine warmup), `search_text` (with `GetFileExtractsRequest` for snippets), `search_files`, `list_indexed`, `status`
- [x] Implement `PipeServer` — named pipe (`\\.\pipe\VsChromiumMcp.Daemon`), JSON request/response framing
- [x] Implement `DaemonBootstrap` — connect-or-spawn-detached via `cmd /c start /b`, 20s pipe-ready polling
- [x] Stdio MCP server using ModelContextProtocol 1.0.0 with `WithStdioServerTransport` and `WithTools<VsChromiumTools>`
- [x] Register MCP at user scope: `claude mcp add vs-chromium --scope user -- ...VsChromiumMcp.exe`
- [x] User-level skill `~/.claude/skills/vs-chromium-index/SKILL.md` with create + update modes
- [x] Memory entries: project reference, search-preference feedback, skill reference

## Open

- [ ] **Build-script regex bug**: `scripts/build-vendor.ps1` line 22's `$content -match` regex contains an unescaped `>` inside a double-quoted string that PowerShell may misinterpret. Verify the script runs cleanly from a fresh checkout and fix.
- [ ] **Multi-repo testing**: exercise indexing >1 directory at the same time. Confirm `ensure_indexed` correctly merges, and that `search_text` returns results from all registered projects in a single call.
- [ ] **Daemon detached-spawn brittleness**: `cmd /c start /b` works but the daemon dies if the desktop session ends abnormally. Consider Win32 `CreateProcess` with `DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP` for a cleaner detach, or a Scheduled-Task-on-logon option for users who want it survive logoff.
- [ ] **Tests**: write a tiny test harness for the daemon's pipe surface — ping/ensure_indexed/search_text against a fixture dir. Optional `dotnet test` project.
- [ ] **Self-update**: the build script currently requires `nuget` and `vswhere` on PATH; we silently fail otherwise. Add precondition checks with clear instructions.
- [ ] **Error UX**: when the index isn't ready yet, `vs_chromium_search_text` returns "0 hits in 0 of N files" — confusing. Detect this and surface "Search engine still warming, retry in ~10s" instead.
- [ ] **`search_files` path filter**: today `search_text` has `file_filter` but `search_files` doesn't. Plumb the same parameter through if useful for narrowing.

## Backlog

- [ ] Persistent-daemon hardening: crash recovery (auto-respawn Server.exe on disconnect), idle shutdown (release RAM after N hours of inactivity)
- [ ] Linux/macOS support: vs-chromium is Windows-only (named pipes, .NET Framework). A long-term port via cross-platform alternative (zoekt? Sourcegraph local?) is a separate project.
- [ ] CI build: GitHub Actions on `windows-latest` that runs `scripts/build.ps1` and uploads installable artifacts.
- [ ] Telemetry: optional local metrics file (query count, p50/p95 search latency) for the user to see whether the indexer is paying off vs. their old grep workflow.

## Notes / design rationale

- **Why Path C (unmodified upstream + in-process Core.dll types)** over Path B (fork + extract indexer): vs-chromium's IPC is documented protobuf, not opaque; we get all message types for free by referencing `Core.dll`. Maintaining a fork is more work than vendor-patching one line of MSBuild config.
- **Why a persistent daemon**: an MCP launched per Claude session over stdio would cold-index every time. The user explicitly chose warm indices over per-session simplicity.
- **Why named pipes between MCP and daemon**: stdio MCP processes are short-lived and per-session; the daemon needs to be addressable by any future MCP instance. Named pipes are the Windows-native, no-port-conflicts choice.
