#Requires -Version 5.1
<#
.SYNOPSIS
    Builds and deploys KdpPublish to C:\Apps\KdpPublish\ and optionally runs it. The one
    script for publishing and launching KdpPublish.

.DESCRIPTION
    Publishes a framework-dependent single-file win-x64 KdpPublish.exe, plus a generated
    deploy.bat next to it; the desktop shortcut points at that. This script never prompts
    (agents and /deploy run it non-interactively); the choice lives in deploy.bat, which
    opens a menu first:
      1) Deploy & Run - runs this script with -Force (always republishes, so a double-click
                        can get a fresh build - see the RESIST incident: a 3-day-stale
                        deployed exe silently missing an entire feature), then starts the exe.
      2) Run          - starts the already-deployed exe without calling this script at all
                        (falls through to Deploy & Run if nothing is deployed yet).
    Either way the exe gets deploy.bat's arguments passed through. If the repo path baked
    into deploy.bat is gone, or the redeploy fails, it launches the existing build instead.

    Every run (published or not) rewrites deploy.bat when its text has changed, so an
    existing install picks up launcher changes without a republish.

    Without -Force it only republishes when the exe is missing, or a source file is newer
    than the exe. Publishing stops any running KdpPublish first (dotnet publish can't
    overwrite it).

    Only the build cache is ever cleaned (bin/obj, and only with -Force). The WebView2
    user-data folders under %LocalAppData%\MindAttic\KdpPublish\ hold the KDP login
    session and are never touched, so the Amazon login survives redeploys. Prose.Core is
    a vendored package (lib/local-packages) since the 2026-10-04 split from Prose, not a
    sibling source tree, so there is nothing else to clean.

.PARAMETER Force
    Republish with a clean bin/obj rebuild even if nothing looks changed.

.PARAMETER LauncherOnly
    Only (re)write the generated deploy.bat and exit: no build, nothing stopped.

.PARAMETER Launch
    After deploying, start the deployed exe with the remaining arguments.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy.ps1 -Force
    powershell -ExecutionPolicy Bypass -File deploy.ps1 -Launch --scan-bookshelf
    powershell -ExecutionPolicy Bypass -File deploy.ps1 -LauncherOnly
#>
param(
    [switch]$Force,
    [switch]$LauncherOnly,
    [switch]$Launch,
    [Parameter(ValueFromRemainingArguments)]
    [string[]]$LaunchArgs = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo      = $PSScriptRoot
$projDir   = Join-Path $repo 'KdpPublish'
$proj      = Join-Path $projDir 'KdpPublish.csproj'
$deployDir = 'C:\Apps\KdpPublish'
$exeName   = 'KdpPublish.exe'
$exe       = Join-Path $deployDir $exeName
$deployBat = Join-Path $deployDir 'deploy.bat'

# Source extensions whose change means a republish. wwwroot's .html/.css/.js are copied
# into the deploy folder, so they count too.
$sourceExt = @('.cs', '.csproj', '.xaml', '.razor', '.json', '.props', '.targets', '.resx',
               '.html', '.css', '.js')

function Test-NeedsPublish {
    if (-not (Test-Path $exe)) { return $true }
    $exeTime = (Get-Item $exe).LastWriteTimeUtc

    foreach ($p in 'Directory.Build.props', 'Directory.Packages.props') {
        $f = Join-Path $repo $p
        if ((Test-Path $f) -and (Get-Item $f).LastWriteTimeUtc -gt $exeTime) { return $true }
    }

    $newer = Get-ChildItem -Path $repo -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -notmatch '\\(bin|obj|\.git)\\' -and
            $sourceExt -contains $_.Extension.ToLowerInvariant() -and
            $_.LastWriteTimeUtc -gt $exeTime
        } |
        Select-Object -First 1
    return [bool]$newer
}

function Stop-Running {
    $procs = Get-Process 'KdpPublish' -ErrorAction SilentlyContinue
    if ($procs) {
        Write-Host "  Stopping running KdpPublish ($(@($procs).Count) process(es))..." -ForegroundColor Yellow
        $procs | Stop-Process -Force
        Start-Sleep -Milliseconds 800
    }
}

function Publish {
    Stop-Running

    if ($Force) {
        # bin/obj only - never the WebView2 user-data folders (KDP login session).
        foreach ($d in @((Join-Path $projDir 'bin'), (Join-Path $projDir 'obj'))) {
            if (Test-Path $d) {
                try { Remove-Item -Recurse -Force $d -ErrorAction Stop }
                catch { Write-Host "  (could not fully remove $d : $($_.Exception.Message))" -ForegroundColor DarkYellow }
            }
        }
    }

    Write-Host "Publishing KdpPublish -> $exe" -ForegroundColor Cyan
    dotnet publish $proj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained false `
        /p:PublishSingleFile=true `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        --output $deployDir | Out-Host
    if ($LASTEXITCODE -ne 0) { return $false }
    if (-not (Test-Path $exe)) { return $false }

    Write-Host "Deployed $exe" -ForegroundColor Green
    return $true
}

# Writes the double-click launcher (only when its text changed, so a launcher that is running
# this script is not rewritten under itself). The repo path is baked in as an absolute path so
# the launcher finds this script wherever it is started from. The menu lives there, not in this
# script, so the script never blocks.
function Write-Launcher {
    $text = @"
@echo off
title KdpPublish
rem Generated by $repo\deploy.ps1 - edits here are overwritten.
set "DEPLOY_PS1=$repo\deploy.ps1"
echo.
echo   KdpPublish
echo     1^) Deploy ^& Run
echo     2^) Run
echo.
choice /c 12 /n /m "  Choose 1 or 2: "
if errorlevel 2 goto run
:deploy
if exist "%DEPLOY_PS1%" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%DEPLOY_PS1%" -Force
    if errorlevel 1 echo Redeploy failed - launching whatever is already in this folder instead.
) else (
    echo Source repo not found at "%DEPLOY_PS1%" - launching existing build without redeploying.
)
goto launch
:run
if exist "%~dp0$exeName" goto launch
echo Nothing is deployed yet - deploying first.
goto deploy
:launch
if not exist "%~dp0$exeName" (
    echo Nothing deployed at "%~dp0$exeName".
    pause
    exit /b 1
)
start "" "%~dp0$exeName" %*
"@
    # CRLF throughout: cmd mis-finds labels (goto) in LF-only batch files.
    $text = ($text -replace "`r?`n", "`r`n") + "`r`n"
    New-Item -ItemType Directory -Force -Path $deployDir | Out-Null
    $current = if (Test-Path $deployBat) { [IO.File]::ReadAllText($deployBat) } else { $null }
    if ($current -ne $text) { [IO.File]::WriteAllText($deployBat, $text, [Text.Encoding]::ASCII) }
}

if ($LauncherOnly) {
    Write-Launcher
    Write-Host "Wrote $deployBat" -ForegroundColor Gray
    exit 0
}

# -- Deploy ------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $deployDir | Out-Null

$ok = $true
if ($Force -or (Test-NeedsPublish)) { $ok = Publish }

Write-Launcher

if (-not $ok) {
    Write-Host 'Deploy FAILED.' -ForegroundColor Red
    if (-not $Launch) { exit 1 }
    Write-Host 'Launching whatever is already deployed instead.' -ForegroundColor Yellow
}

if (-not $Launch) { exit 0 }
if (-not (Test-Path $exe)) {
    Write-Host "Nothing deployed at $exe." -ForegroundColor Red
    exit 1
}
if ($LaunchArgs.Count -gt 0) { Start-Process -FilePath $exe -ArgumentList $LaunchArgs }
else { Start-Process -FilePath $exe }
exit 0
