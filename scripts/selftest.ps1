# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Run the WinUI application's non-UI self-test and wait for its report.

.DESCRIPTION
  The app is a GUI process; --selftest writes a report file and exits. This
  wrapper locates the built executable, starts the self-test, polls the report
  and fails on missing/timeout/FAIL. Secrets are passed through environment
  variables and never written to the report.

.PARAMETER Fixture
  KDBX fixture path (relative to the repository root or absolute).

.PARAMETER Password
  Fixture password (copied to KEEPASSITY_SELFTEST_PASSWORD for the child process).

.PARAMETER Exe
  Optional path to Keepassity.exe. Defaults to the Release x64 build output.

.PARAMETER DataDir
  Optional application data directory passed to the app as --data-dir. Use this
  when %LOCALAPPDATA%\keepassity is not writable for the calling process (for
  example when the app runs from a low-integrity workspace folder).

.PARAMETER TimeoutSeconds
  Maximum time to wait for the report.
#>
param(
  [Parameter(Mandatory = $true)][string]$Fixture,
  [Parameter(Mandatory = $true)][string]$Password,
  [string]$Exe,
  [string]$DataDir,
  [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Exe) {
  # Pick the newest Release build: builds made with -p:Platform=x64 and a plain
  # `dotnet build` land in different bin subdirectories, and testing a stale
  # binary silently hides regressions.
  $candidate = Get-ChildItem -Path (Join-Path $root 'apps/windows/Keepassity.App/bin') -Recurse -Filter 'Keepassity.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\Release\\' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
  if ($candidate) {
    $Exe = $candidate.FullName
  } else {
    $Exe = Join-Path $root 'apps/windows/Keepassity.App/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/Keepassity.exe'
  }
}
if (-not (Test-Path $Exe)) {
  throw "application executable not found: $Exe"
}
if (-not (Test-Path $Fixture)) {
  $relative = Join-Path $root $Fixture
  if (Test-Path $relative) { $Fixture = $relative } else { throw "fixture not found: $Fixture" }
}

$arguments = '--selftest "' + (Resolve-Path $Fixture).Path + '"'
$resolvedDataDir = $null
if ($DataDir) {
  $resolvedDataDir = [System.IO.Path]::GetFullPath($DataDir)
  New-Item -ItemType Directory -Force -Path $resolvedDataDir | Out-Null
  $env:KEEPASSITY_DATA_DIR = $resolvedDataDir
  $arguments += ' --data-dir "' + $resolvedDataDir + '"'
}

# Keep the report next to the data directory when one is given: a portable or
# low-integrity run may not be allowed to write under %LOCALAPPDATA%.
$reportDirectory = if ($resolvedDataDir) {
  Join-Path $resolvedDataDir 'selftest'
} else {
  Join-Path $env:LOCALAPPDATA 'keepassity\selftest'
}
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
$report = Join-Path $reportDirectory ("keepassity-selftest-" + [guid]::NewGuid().ToString('N') + ".log")
$env:KEEPASSITY_SELFTEST_PASSWORD = $Password
$env:KEEPASSITY_SELFTEST_REPORT = $report

Write-Host "selftest: $Exe $arguments"
# Start the GUI process directly (CreateProcess) instead of ShellExecute: the app
# lives in a low-integrity workspace folder, and Explorer's ShellExecute path can
# show a Windows security warning dialog that a headless test cannot answer.
$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = (Resolve-Path $Exe).Path
$startInfo.Arguments = $arguments
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$process = [System.Diagnostics.Process]::Start($startInfo)
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$content = $null
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Milliseconds 300
  if (Test-Path $report) {
    $content = Get-Content $report -Raw -ErrorAction SilentlyContinue
    # Match the verdict lines only: a report may legitimately contain words like
    # "password" before the self-test finishes, and matching on the substring
    # would kill the process mid-run.
    if ($content -match '(?m)^(PASS|FAIL)\s*$') { break }
  }
  if ($process.HasExited -and -not (Test-Path $report)) { break }
}
if (-not $process.HasExited) {
  try { $process.Kill() } catch { }
}
if (-not $content) {
  throw "selftest produced no report (timeout ${TimeoutSeconds}s)"
}
Write-Host $content.Trim()
if ($content -match '(?m)^PASS\s*$') {
  exit 0
}
throw 'selftest did not report PASS'
