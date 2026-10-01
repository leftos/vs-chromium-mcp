#requires -Version 7
# Installs VsChromiumMcp into %LOCALAPPDATA%\VsChromiumMcp\app and registers it with Claude Code at user scope.
#
# The server runs from that installed copy, never from the repo's bin folders: those are build output, which
# clean-builds and every rebuild may delete or overwrite while Claude sessions hold the exe open.
#
# Usage:
#   ./scripts/install.ps1             # build, copy into the install folder, register
#   ./scripts/install.ps1 -Force      # the same, re-registering an existing entry
#   ./scripts/install.ps1 -Uninstall  # unregister and delete the install folder
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA 'VsChromiumMcp\app'
$mcpExe     = Join-Path $installDir 'VsChromiumMcp.exe'
$serverName = 'vs-chromium'

$sources = @(
    (Join-Path $repoRoot 'src\VsChromiumMcp\bin\Release\net8.0-windows'),
    (Join-Path $repoRoot 'src\VsChromiumMcp.Daemon\bin\Release\net8.0-windows'),
    (Join-Path $repoRoot 'vendor\vs-chromium\Binaries\Release')
)

function Stop-InstalledProcess {
    # The daemon and the indexer outlive Claude sessions and lock the files being replaced.
    $running = Get-Process VsChromiumMcp, VsChromiumMcp.Daemon, VsChromium.Server -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) }
    foreach ($process in $running) {
        Write-Host "[install] stopping $($process.Name) ($($process.Id))"
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit(10000) | Out-Null
    }
}

if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
    throw "claude CLI not found on PATH. Install Claude Code first: https://claude.com/claude-code"
}

if ($Uninstall -or $Force) {
    Write-Host "[install] removing existing $serverName registration (if any)"
    & claude mcp remove $serverName --scope user 2>&1 | Out-Null
}
if ($Uninstall) {
    Stop-InstalledProcess
    if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
    Write-Host "[install] uninstalled"
    return
}

Write-Host "[install] building"
& (Join-Path $PSScriptRoot 'build.ps1')

foreach ($source in $sources) {
    if (-not (Test-Path $source)) { throw "Build output missing: $source. Run scripts/build.ps1 and check its output." }
}

Stop-InstalledProcess
if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
$null = New-Item -ItemType Directory -Force $installDir
foreach ($source in $sources) {
    # The vendor folder also holds link-time files (.lib, .exp) and docs (.xml) the runtime never loads.
    Copy-Item (Join-Path $source '*') $installDir -Recurse -Force -Exclude '*.lib', '*.exp', '*.xml'
}
Write-Host "[install] copied the build into $installDir"

$registered = (& claude mcp get $serverName 2>&1 | Out-String) -match [regex]::Escape($mcpExe)
if (-not $registered) {
    & claude mcp remove $serverName --scope user 2>&1 | Out-Null
    Write-Host "[install] registering $serverName at user scope -> $mcpExe"
    & claude mcp add $serverName --scope user -- $mcpExe
    if ($LASTEXITCODE -ne 0) { throw "claude mcp add failed" }
}

Write-Host ""
Write-Host "[install] OK. Restart Claude sessions to pick it up; verify with: claude mcp list"
