# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Run all M4-07 headless drills and summarise the result.

.DESCRIPTION
  Runs the protocol check and the three drills in separate PowerShell processes
  so one failure cannot mask another. Returns 0 only if every drill passed.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\drills\run-all.ps1
#>
[CmdletBinding()]
param([switch]$Keep)

$ErrorActionPreference = 'Stop'
$scripts = @(
    'run-protocol.ps1',
    'run-restore.ps1',
    'run-offline-replay.ps1',
    'run-conflict.ps1'
)
$shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }
$results = @()
foreach ($script in $scripts) {
    $path = Join-Path $PSScriptRoot $script
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $path)
    if ($Keep) { $arguments += '-Keep' }
    Write-Host ''
    Write-Host "### $script"
    & $shell @arguments
    $code = $LASTEXITCODE
    $results += [pscustomobject]@{ Script = $script; ExitCode = $code }
    if ($code -ne 0) {
        Write-Host "### $script FAILED (exit $code)"
    }
}

Write-Host ''
Write-Host '== drill summary =='
$failed = 0
foreach ($result in $results) {
    $status = if ($result.ExitCode -eq 0) { 'PASS' } else { 'FAIL'; }
    if ($result.ExitCode -ne 0) { $failed++ }
    Write-Host ("{0,-22} {1}" -f $result.Script, $status)
}
if ($failed -gt 0) {
    Write-Host "$failed drill(s) failed"
    exit 1
}
Write-Host 'all drills passed'
exit 0
