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
  Optional path to Keepassity.Mock.exe. Defaults to the Release x64 build output.

.PARAMETER TimeoutSeconds
  Maximum time to wait for the report.
#>
param(
  [Parameter(Mandatory = $true)][string]$Fixture,
  [Parameter(Mandatory = $true)][string]$Password,
  [string]$Exe,
  [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Exe) {
  $Exe = Join-Path $root 'apps/windows/Keepassity.Mock/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/Keepassity.Mock.exe'
}
if (-not (Test-Path $Exe)) {
  $candidate = Get-ChildItem $root 'apps/windows/Keepassity.Mock/bin' -Recurse -Filter 'Keepassity.Mock.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\Release\\' } | Select-Object -First 1
  if ($candidate) { $Exe = $candidate.FullName }
}
if (-not (Test-Path $Exe)) {
  throw "application executable not found: $Exe"
}
if (-not (Test-Path $Fixture)) {
  $relative = Join-Path $root $Fixture
  if (Test-Path $relative) { $Fixture = $relative } else { throw "fixture not found: $Fixture" }
}

$reportDirectory = Join-Path $env:LOCALAPPDATA 'keepassity\selftest'
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
$report = Join-Path $reportDirectory ("keepassity-selftest-" + [guid]::NewGuid().ToString('N') + ".log")
$env:KEEPASSITY_SELFTEST_PASSWORD = $Password
$env:KEEPASSITY_SELFTEST_REPORT = $report

Write-Host "selftest: $Exe --selftest $Fixture"
# Start the GUI process directly (CreateProcess) instead of ShellExecute: the app
# lives in a low-integrity workspace folder, and Explorer's ShellExecute path can
# show a Windows security warning dialog that a headless test cannot answer.
$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = (Resolve-Path $Exe).Path
$startInfo.Arguments = '--selftest "' + (Resolve-Path $Fixture).Path + '"'
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$process = [System.Diagnostics.Process]::Start($startInfo)
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$content = $null
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Milliseconds 300
  if (Test-Path $report) {
    $content = Get-Content $report -Raw -ErrorAction SilentlyContinue
    if ($content -match 'PASS|FAIL') { break }
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
