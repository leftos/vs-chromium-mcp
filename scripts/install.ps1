#requires -Version 7
# Registers VsChromiumMcp with Claude Code at user scope so every Claude session can use it.
#
# Usage:
#   ./scripts/install.ps1            # register
#   ./scripts/install.ps1 -Force     # re-register (remove + add)
#   ./scripts/install.ps1 -Uninstall  # remove
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$Uninstall
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$mcpExe   = Join-Path $repoRoot 'src\VsChromiumMcp\bin\Release\net8.0-windows\VsChromiumMcp.exe'
$serverName = 'vs-chromium'

if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
    throw "claude CLI not found on PATH. Install Claude Code first: https://claude.com/claude-code"
}

if ($Uninstall -or $Force) {
    Write-Host "[install] removing existing $serverName registration (if any)"
    & claude mcp remove $serverName --scope user 2>&1 | Out-Null
    if ($Uninstall) { Write-Host "[install] uninstalled"; return }
}

if (-not (Test-Path $mcpExe)) {
    Write-Host "[install] MCP not built. Running scripts/build.ps1 ..."
    & (Join-Path $PSScriptRoot 'build.ps1')
}

Write-Host "[install] registering $serverName at user scope -> $mcpExe"
& claude mcp add $serverName --scope user -- $mcpExe
if ($LASTEXITCODE -ne 0) { throw "claude mcp add failed" }

Write-Host ""
Write-Host "[install] OK. Verify with: claude mcp list"
