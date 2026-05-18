#requires -Version 7
# Top-level build: vendor binaries + MCP solution. Idempotent.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

# 1. Vendor binaries (skip if already present).
$vendorOut = Join-Path $repoRoot 'vendor\vs-chromium\Binaries\Release\VsChromium.Native.dll'
if (-not (Test-Path $vendorOut)) {
    Write-Host "[build] building vendor binaries (one-time)"
    & (Join-Path $PSScriptRoot 'build-vendor.ps1')
} else {
    Write-Host "[build] vendor binaries up-to-date ($vendorOut)"
}

# 2. MCP solution.
Write-Host "[build] dotnet build VsChromiumMcp.slnx -c Release"
& dotnet build (Join-Path $repoRoot 'VsChromiumMcp.slnx') -c Release -v minimal
if ($LASTEXITCODE -ne 0) { throw "MCP build failed" }

$mcpExe    = Join-Path $repoRoot 'src\VsChromiumMcp\bin\Release\net8.0-windows\VsChromiumMcp.exe'
$daemonExe = Join-Path $repoRoot 'src\VsChromiumMcp.Daemon\bin\Release\net8.0-windows\VsChromiumMcp.Daemon.exe'
Write-Host ""
Write-Host "[build] OK"
Write-Host "  MCP    : $mcpExe"
Write-Host "  Daemon : $daemonExe"
