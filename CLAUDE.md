# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

An MCP server (Windows-only) that wraps Google's vs-chromium indexer and exposes its `<100ms` indexed full-text and filename search to MCP clients (Claude Code etc.). vs-chromium is vendored as a git submodule at `vendor/vs-chromium` (pinned to v0.9.37) and consumed **unmodified** by referencing its `VsChromium.Core.dll` from .NET 8 — we do not fork it.

## Build / install / run

All scripts require PowerShell 7+ (`pwsh`), are idempotent, and live in `scripts/`.

```powershell
git submodule update --init        # one-time after clone
pwsh scripts/build.ps1             # build vendor + MCP solution (skips vendor if already built)
pwsh scripts/install.ps1           # build, copy into %LOCALAPPDATA%\VsChromiumMcp\app, register that copy at user scope
pwsh scripts/install.ps1 -Uninstall
```

Claude runs the installed copy, never the repo's `bin` output (a daily clean of build folders on D:\ deletes it). A change goes live only after `install.ps1` runs again.

To force a vendor rebuild, delete `vendor/vs-chromium/Binaries/Release/VsChromium.Native.dll` and rerun `build.ps1`. `build-vendor.ps1` requires `nuget.exe` and `vswhere.exe` on PATH, plus VS 2022 with .NET-desktop + C++-desktop workloads.

Direct dotnet workflow during dev:

```powershell
dotnet build VsChromiumMcp.slnx -c Release
# Solution uses .slnx (XML), not .sln. DaemonBootstrap and Daemon's Program both look for either at the repo root.
```

There is **no test suite** (it's on the backlog in `docs/plans/MAIN.md`). When changing daemon or pipe behavior, the manual smoke test is `src/Prototype/Program.cs` for the raw protobuf round-trip, plus `claude mcp list` followed by exercising the tools from a Claude session.

## Architecture

Three Windows-only processes, in order of lifetime:

```
Claude Code  ─stdio─>  VsChromiumMcp.exe  ─named pipe─>  VsChromiumMcp.Daemon.exe  ─TCP/protobuf─>  VsChromium.Server.exe
   (per-session)         (per-session)                    (persistent, user-mode)                   (managed by daemon)
```

- **`src/VsChromiumMcp/`** — stdio MCP server (ModelContextProtocol 1.0.0). Spawned per Claude session. `Tools.cs` declares the five MCP tools; `DaemonHandle` is a session-scoped lazy singleton that on first call invokes `DaemonBootstrap.EnsureRunningAsync` (try-connect; if it fails, `cmd /c start /b` the daemon and poll for the pipe up to 20s).
- **`src/VsChromiumMcp.Daemon/`** — persistent supervisor. `Program.cs` holds a `Global\VsChromiumMcp.Daemon.SingleInstance` mutex so only one ever runs. `VsChromiumServerHost` spawns `VsChromium.Server.exe`, accepts its TCP loopback connect-back, handles the protobuf hello handshake, and correlates requests/responses by `RequestId` over a background reader thread. `Operations` is the high-level orchestrator. `PipeServer` exposes a line-delimited JSON request/response on `\\.\pipe\VsChromiumMcp.Daemon`.
- **`src/VsChromiumMcp.Shared/`** — DTOs for the named-pipe protocol (`PipeProtocol.cs`) and well-known paths (`Paths.cs`). Shared by MCP and daemon, no other deps.
- **`src/Prototype/`** — standalone smoke test for the raw vs-chromium protobuf round-trip. Not referenced by anything else; kept for diagnostics.

### Key cross-file flows

**`vs_chromium_ensure_indexed` is a two-phase wait:**

1. Drop `vs-chromium-project.txt` if absent → send `RegisterFileRequest` pointing at it (vs-chromium discovers projects by walking up from any registered file).
2. **Phase 1 (scan):** wait for the `FileSystemScanFinished` typed event (subscribed via `VsChromiumServerHost.TypedEventReceived` → `Operations.OnTypedEvent`), then `GetFileSystemTreeRequest` to confirm the project root appears in `resp.Tree.Projects`.
3. **Phase 2 (search-engine warmup):** the search engine loads file contents asynchronously after the scan and emits no typed event we can hook. `WaitForSearchEngineReadyAsync` polls a cheap dummy `SearchCodeRequest` every 500 ms until `SearchedFileCount > 0`.

This two-phase design is the reason `EnsureIndexedAsync` looks more complex than a single wait — if you only wait for the scan event, the very next `search_text` returns "0 hits in 0 of N files" and the user is confused.

**Line/column numbering:** vs-chromium is 0-based. `Operations.SearchTextAsync` converts to 1-based (`ex.LineNumber + 1`, `ex.ColumnNumber + 1`) on the way out. Don't reintroduce 0-based output without checking every consumer.

**Snippet extraction:** `SearchCodeRequest` returns matches as `FilePositionSpan`s only; we then issue a follow-up `GetFileExtractsRequest` per file to materialize the snippet text. Trim long extracts on the way out.

**Daemon state on disk** (`%LOCALAPPDATA%\VsChromiumMcp\`):
- `projects.json` — persisted registered roots (re-indexed at daemon startup by `Operations.ReindexKnownProjectsAsync`)
- `daemon.pid`, `daemon.ready` — liveness markers (written by `Program.Main`)
- `logs\daemon-YYYYMMDD.log`

## Critical constraints

- **MCP stdout is protocol; logs go to stderr.** `src/VsChromiumMcp/Program.cs` sets `LogToStandardErrorThreshold = LogLevel.Trace`. Never `Console.WriteLine` from the MCP process — it'll corrupt the JSON-RPC stream.
- **Both MCP and Daemon target `net8.0-windows`.** Named pipes, file-system specifics, and the vendor's `Core.dll` (NET Framework 4.7.1) only work on Windows. Don't switch to `net8.0`.
- **`VsChromium.Core.dll` is .NET Framework 4.7.1**, referenced directly via `<Reference HintPath=...>` from the daemon csproj. This works because protobuf-net DTOs serialize identically across the runtime boundary. The retarget from upstream's `v4.5` → `v4.7.1` is applied as an idempotent in-place patch by `scripts/build-vendor.ps1` to `vendor/vs-chromium/Build/Common.Build.settings` — that's the one line of upstream we touch. Do not commit the patched file from `vendor/`; the script reapplies it.
- **C++ Native uses PlatformToolset `v143`**, not upstream's `v140` (VS 2015 build tools are not installed by default on modern VS).
- **`ensure_indexed` writes a `vs-chromium-project.txt` to the user's repo** if one doesn't exist. Treat that as part of the contract.
- **Detached daemon spawn is `cmd /c start /b`** — known brittle (dies if the desktop session ends abnormally). Listed as an open item in `docs/plans/MAIN.md`; if you change spawn semantics, update both.

## Plan workflow (project-specific)

`docs/plans/MAIN.md` is the single entry point for ongoing work — Done, Open, Backlog, and design rationale. Every new task lands there as an unchecked checkbox before being worked. Per global instructions, capture user steers as tasks in `MAIN.md` (or an active subplan) immediately, before responding in prose.

## Conventions specific to this repo

- C# style follows global CLAUDE.md (file-scoped namespaces, `var`, braces always, `static` where possible).
- `Log.Info` / `Log.Warn` / `Log.Error` (custom in `Daemon/Log.cs`) — not `Console`, not `ILogger`. The daemon has no DI container.
- All daemon → vs-chromium calls go through `VsChromiumServerHost.SendAsync<TResponse>` so the request/response correlation and disconnect handling stay centralized. Don't bypass it.
- The pipe wire format is line-delimited JSON (UTF-8, no BOM, `WriteLineAsync` + flush per message). Keep it that way; both sides assume one message per line.
