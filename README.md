# vs-chromium-mcp

A Model Context Protocol server that exposes Google's [vs-chromium](https://github.com/chromium/vs-chromium) indexed code search to Claude Code (and any other MCP client). Indexed full-text search returns matches in <100ms on 100k+ file trees.

## Architecture

```
Claude Code   ─stdio─>   VsChromiumMcp.exe   ─named pipe─>   VsChromiumMcp.Daemon.exe   ─TCP/protobuf─>   VsChromium.Server.exe
```

Three Windows-only processes:
- **`VsChromiumMcp.exe`** — thin stdio MCP server spawned per Claude session. Forwards tool calls to the daemon.
- **`VsChromiumMcp.Daemon.exe`** — persistent background process that owns the indexer state. Auto-spawned on first MCP call, survives Claude restarts via a global mutex.
- **`VsChromium.Server.exe`** — the unmodified vs-chromium indexer. Spawned and managed by the daemon. Communicates over TCP loopback using protobuf-net.

The MCP wraps vs-chromium **without forking it**. We just vendor it as a submodule and consume its already-typed protobuf messages by referencing its `Core.dll` directly from .NET 8.

## Requirements

- Windows 10 / 11
- Visual Studio 2022+ with the **.NET desktop** and **C++ desktop** workloads
- .NET SDK 8.0+
- `nuget` CLI on PATH (e.g. `scoop install nuget`)
- Claude Code CLI on PATH (for `scripts/install.ps1`)

## Build

```powershell
git clone <this-repo> X:\dev\vs-chromium-mcp
cd X:\dev\vs-chromium-mcp
git submodule update --init

pwsh scripts/build.ps1
```

`build.ps1` is idempotent — it skips vendor work when the binaries are already present.

## Install (register with Claude Code at user scope)

```powershell
pwsh scripts/install.ps1
claude mcp list   # should show "vs-chromium ... ✓ Connected"
```

`install.ps1` builds, copies the MCP server, the daemon and the vendor indexer into `%LOCALAPPDATA%\VsChromiumMcp\app`, and registers that copy. Claude never runs the server from the repo's `bin` folders, which builds overwrite and build-output cleaners delete. Re-run it after every change to take the change live; it stops a running daemon first.

## Tools exposed

| Tool | Purpose |
|------|---------|
| `vs_chromium_ensure_indexed(path, wait, wait_timeout_seconds)` | Register a directory with the indexer. Drops a `vs-chromium-project.txt` if missing. Waits for scan and search-engine warmup. |
| `vs_chromium_search_text(pattern, match_case, whole_word, regex, file_filter, max_results)` | Indexed full-text search. Returns file/line/column/snippet. |
| `vs_chromium_search_files(pattern, match_case, regex, max_results)` | Filename search. |
| `vs_chromium_list_indexed` | Show registered project roots. |
| `vs_chromium_status` | Daemon uptime + indexer state. |

The companion user-level Claude skill `/vs-chromium-index` automates per-repo setup (gitignore-aware `vs-chromium-project.txt` generation + first-time registration).

## State on disk

- `%LOCALAPPDATA%\VsChromiumMcp\projects.json` — persisted list of indexed roots
- `%LOCALAPPDATA%\VsChromiumMcp\daemon.pid` / `daemon.ready` — daemon liveness markers
- `%LOCALAPPDATA%\VsChromiumMcp\logs\daemon-YYYYMMDD.log` — operational log

## Project layout

```
src/
  VsChromiumMcp.Shared/      .NET 8 class lib: pipe protocol types, paths
  VsChromiumMcp.Daemon/      .NET 8 console: supervisor + named pipe server
  VsChromiumMcp/             .NET 8 console: stdio MCP server (uses ModelContextProtocol SDK)
  Prototype/                 Smoke test for the protobuf/TCP round-trip
vendor/
  vs-chromium/               Submodule, pinned to v0.9.37
scripts/
  build-vendor.ps1           Build Server.exe + Core.dll + Native.dll
  build.ps1                  Build everything
  install.ps1                Register with Claude Code
```

## Vendor build notes

A small, idempotent in-place patch is applied to the submodule during `build-vendor.ps1`:

- `vendor/vs-chromium/Build/Common.Build.settings`: default `TargetFrameworkVersion` retargeted from `v4.5` to `v4.7.1` (Microsoft no longer redistributes the 4.5 dev pack; 4.7.1 is the highest version that avoids the `System.Linq.Enumerable.ToHashSet` collision with vs-chromium's own extension).

The C++ Native search engine builds with PlatformToolset `v143` (upstream's `v140` requires VS 2015 build tools that aren't installed by default on modern VS).

## License

vs-chromium itself is BSD-licensed by Google. This wrapper is provided as-is for personal use.
