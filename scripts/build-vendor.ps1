#requires -Version 7
# Builds the vendored vs-chromium binaries (Server.exe, Core.dll, NativeInterop.dll, Native.dll)
# needed by VsChromiumMcp.Daemon. Idempotent.
#
# Usage:
#   ./scripts/build-vendor.ps1
#
# Patches Common.Build.settings to retarget v4.5 -> v4.7.1 (4.5 dev pack is no longer
# redistributable; 4.7.1 is the highest installed targeting pack that doesn't pull in
# System.Linq.Enumerable.ToHashSet, which collides with vs-chromium's own extension).

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$vendor     = Join-Path $repoRoot 'vendor\vs-chromium'
$srcDir     = Join-Path $vendor 'src'
$settings   = Join-Path $vendor 'Build\Common.Build.settings'
$outDir     = Join-Path $vendor 'Binaries\Release'

if (-not (Test-Path $vendor)) {
    throw "vendor submodule not initialized. Run: git submodule update --init"
}

# 1. Apply the v4.5 -> v4.7.1 retarget patch idempotently.
$content = Get-Content $settings -Raw
if ($content -match "'\`$\(TargetFrameworkVersion\)' == ''\`">v4\.5<") {
    Write-Host "[build-vendor] retargeting default TargetFrameworkVersion v4.5 -> v4.7.1"
    $patched = $content -replace "(<TargetFrameworkVersion Condition=`"'\`$\(TargetFrameworkVersion\)' == ''`">)v4\.5(</TargetFrameworkVersion>)", '$1v4.7.1$2'
    Set-Content -Path $settings -Value $patched -Encoding UTF8
} else {
    Write-Host "[build-vendor] target framework already patched (or upstream has changed)"
}

# 2. Locate MSBuild from VS installer.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found; install Visual Studio 2022 or newer" }
$msbuild = & $vswhere -latest -products '*' -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild not found; install the .NET desktop workload in VS" }
Write-Host "[build-vendor] msbuild: $msbuild"

# 3. NuGet restore (vs-chromium uses legacy packages.config).
$nuget = (Get-Command nuget -ErrorAction SilentlyContinue)?.Source
if (-not $nuget) { throw "nuget.exe not on PATH. Install via 'scoop install nuget'." }
Write-Host "[build-vendor] restoring NuGet packages"
& $nuget restore (Join-Path $srcDir 'vs-chromium.sln') | Out-Null

# 4. Build the C# Server (pulls in Core + ServerNativeInterop).
Write-Host "[build-vendor] building managed Server (Release, AnyCPU)"
& $msbuild (Join-Path $srcDir 'vs-chromium.sln') /t:Server /p:Configuration=Release /p:Platform="Any CPU" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "managed Server build failed" }

# 5. Build the C++ Native search engine (depends on bundled re2). v143 toolset works
#    against vs-chromium's bundled re2 source.
Write-Host "[build-vendor] building Native search engine (Release, x64, v143)"
& $msbuild (Join-Path $srcDir 'Native\Native.vcxproj') /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v143 /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Native build failed" }

# 6. Confirm critical outputs.
$required = @(
    'VsChromium.Server.exe',
    'VsChromium.Core.dll',
    'VsChromium.Server.NativeInterop.dll',
    'VsChromium.Native.dll',
    'NLog.dll',
    'protobuf-net.dll'
)
foreach ($f in $required) {
    $path = Join-Path $outDir $f
    if (-not (Test-Path $path)) { throw "missing build output: $path" }
}
Write-Host "[build-vendor] OK. Binaries at $outDir"
