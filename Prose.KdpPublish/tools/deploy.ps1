#Requires -Version 5.1
<#
.SYNOPSIS
    Shuts down any running KdpPublish, clears build cache, rebuilds, and publishes a
    standalone copy to C:\Apps\KdpPublish\ — same process IdiotProof uses
    (tools/publish-all.ps1) to always have a fresh, independent deployed copy.

.DESCRIPTION
    1. Stops any running Prose.KdpPublish.exe process.
    2. Removes this project's own bin/obj — a clean, timestamp-independent rebuild, not an
       incremental one. Prose.Core is a vendored package (lib/local-packages), not a
       sibling source tree, since this app was decoupled from the Prose monorepo
       2026-10-04 — nothing else to clean. This is the BUILD cache only; the WebView2
       user-data folders under %LocalAppData%\MindAttic\KdpPublish\ (which hold the KDP
       login session) are never touched, so the Amazon login survives redeploys.
    3. dotnet publish (Release, win-x64, framework-dependent single file) -> C:\Apps\KdpPublish\.
    4. Writes C:\Apps\KdpPublish\launch.bat. NOTE (changed from the original "fully
       standalone" design): launch.bat now calls this same deploy.ps1 (by its baked-in
       source path) EVERY time before launching, so a double-click always runs the
       current source rather than risking a stale deployed copy (see the RESIST incident
       memory — a 3-day-stale exe silently missing an entire feature). This means
       launch.bat now DEPENDS on the source repo existing at the path baked in at deploy
       time. If that path is missing (repo moved/deleted), it logs a warning and falls
       back to launching whatever's already in this folder rather than refusing to start.

.PARAMETER Launch
    After publishing, immediately run C:\Apps\KdpPublish\launch.bat.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\deploy.ps1
    powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -Launch
#>
param([switch]$Launch)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projDir = Split-Path $PSScriptRoot   # tools\ -> Prose.KdpPublish\
$proj    = Join-Path $projDir 'Prose.KdpPublish.csproj'
$out     = 'C:\Apps\KdpPublish'
$exeName = 'Prose.KdpPublish.exe'

# ── Stop running instance ──────────────────────────────────────────────────
Write-Host ''
Write-Host '  Stopping running instance...' -ForegroundColor Yellow
$procs = Get-Process 'Prose.KdpPublish' -ErrorAction SilentlyContinue
if ($procs) {
    $procs | Stop-Process -Force
    Write-Host "    Stopped ($(@($procs).Count) process(es))" -ForegroundColor DarkYellow
    Start-Sleep -Milliseconds 800
} else {
    Write-Host '    Nothing running.' -ForegroundColor DarkGray
}

# ── Clear build cache ──────────────────────────────────────────────────────
# bin/obj only — never the WebView2 user-data folders, which hold the KDP login
# session and must survive a redeploy exactly as they survive a normal rebuild.
# Only this project's own bin/obj: decoupled from Prose 2026-10-04, so Prose.Core is a
# vendored package now (lib/local-packages), not a sibling source tree to also clean.
Write-Host ''
Write-Host '  Clearing build cache (bin/obj)...' -ForegroundColor Cyan
foreach ($d in @((Join-Path $projDir 'bin'), (Join-Path $projDir 'obj'))) {
    if (Test-Path $d) {
        try { Remove-Item -Recurse -Force $d -ErrorAction Stop }
        catch { Write-Host "    (could not fully remove $d : $($_.Exception.Message))" -ForegroundColor DarkYellow }
    }
}

# ── Publish ─────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host "  Publishing $proj" -ForegroundColor Cyan
Write-Host "    -> $out\$exeName"

dotnet publish $proj `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    --output $out | Out-Host

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $out $exeName
if (-not (Test-Path $exe)) {
    Write-Host "Publish completed but $exeName not found at $exe." -ForegroundColor Red
    exit 1
}

# ── Write self-redeploying launcher ─────────────────────────────────────────
# Always redeploys from source before launching, so a double-click can never run a
# stale build (see the RESIST incident: a 3-day-stale deployed exe silently missing an
# entire feature). $PSScriptRoot here is THIS script's own folder — baked into the
# batch file as an absolute path so it still resolves no matter where launch.bat itself
# is invoked from. Falls back to launching the existing exe (with a warning) if that
# path is missing, rather than refusing to start.
$deployPs1Path = Join-Path $PSScriptRoot 'deploy.ps1'
$launchBatPath = Join-Path $out 'launch.bat'
@(
    '@echo off',
    'title KdpPublish',
    "set `"DEPLOY_PS1=$deployPs1Path`"",
    'if exist "%DEPLOY_PS1%" (',
    '    echo Redeploying latest build from source...',
    '    powershell -NoProfile -ExecutionPolicy Bypass -File "%DEPLOY_PS1%"',
    '    if errorlevel 1 echo Redeploy failed - launching whatever is already in this folder instead.',
    ') else (',
    '    echo Source repo not found at "%DEPLOY_PS1%" - launching existing build without redeploying.',
    ')',
    "start `"`" `"%~dp0$exeName`""
) | Set-Content $launchBatPath -Encoding ascii

# ── Summary ────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '  Published successfully.' -ForegroundColor Green
Write-Host "    Exe    : $exe ($([math]::Round(((Get-Item $exe).Length / 1MB), 1)) MB)" -ForegroundColor Gray
Write-Host "    Launch : $launchBatPath" -ForegroundColor Gray
Write-Host ''

if ($Launch) {
    Write-Host '  Launching...' -ForegroundColor Cyan
    Start-Process $launchBatPath
}
