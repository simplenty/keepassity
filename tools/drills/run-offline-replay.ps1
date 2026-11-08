# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Drill (b): offline edit, then replay the upload when the remote returns.

.DESCRIPTION
  Phase 1 (mock server up): seed the mock WebDAV remote with the synthetic
  500-entry fixture, download the app's remote working copy
  (RemoteDatabaseStore), save the sync baseline (SyncStateStore), edit the
  working copy offline through the real FFI, mark the sidecar dirty.

  Phase 2 (mock server stopped): the remote is unreachable. The drill proves the
  PROPFIND/PUT fail without changing local state, and that the dirty flag
  survives.

  Phase 3 (mock server restarted on the same port): PROPFIND shows the remote
  is unchanged, the FFI sync decision returns `upload`, the local bytes are
  replayed with If-Match, the remote reopens with the offline edit, and the
  baseline is committed clean.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\drills\run-offline-replay.ps1
#>
[CmdletBinding()]
param([switch]$Keep)

. "$PSScriptRoot/_common.ps1"

$paths = Initialize-Drills
$workdir = New-DrillWorkDir -Name 'offline'
$remoteRoot = Join-Path $workdir 'remote'
$logPath = Join-Path $workdir 'mock-webdav.log'
$statePath = Join-Path $workdir 'offline-state.json'
$mock = $null
try {
    Write-Host ''
    Write-Host '== M4-07 drill (b): offline edit then replay =='
    Write-Host "workspace: $workdir"
    $mock = Start-MockWebDav -Root $remoteRoot -Log $logPath -MockExe $paths.MockExe
    $url = "$($mock.UrlBase)/vault.kdbx"

    Write-Host ''
    Write-Host '-- phase 1: seed remote, download working copy, edit offline --'
    $exitCode = Invoke-DrillRunner -RunnerExe $paths.RunnerExe -Arguments @(
        'offline-prepare', '--url', $url, '--fixture', $paths.Fixture,
        '--workdir', $workdir
    )
    if ($exitCode -ne 0) { throw "offline-prepare failed (exit $exitCode)" }

    Write-Host ''
    Write-Host '-- phase 2: stop the mock server (simulated outage) --'
    $port = $mock.Port
    Stop-MockWebDav -Mock $mock
    $mock = $null
    Start-Sleep -Milliseconds 500
    $exitCode = Invoke-DrillRunner -RunnerExe $paths.RunnerExe -Arguments @(
        'offline-unreachable', '--workdir', $workdir
    )
    if ($exitCode -ne 0) { throw "offline-unreachable failed (exit $exitCode)" }

    Write-Host ''
    Write-Host "-- phase 3: restart the mock server on port $port --"
    $mock = Start-MockWebDavWithRetry -Root $remoteRoot -Log $logPath -Port $port -MockExe $paths.MockExe
    $restartedUrl = "$($mock.UrlBase)/vault.kdbx"
    if ($restartedUrl -ne $url) {
        throw "mock server URL changed after restart ($url -> $restartedUrl); the drill needs the same URL"
    }
    $exitCode = Invoke-DrillRunner -RunnerExe $paths.RunnerExe -Arguments @(
        'offline-replay', '--workdir', $workdir
    )
    if ($exitCode -ne 0) { throw "offline-replay failed (exit $exitCode)" }

    if (-not (Test-Path -LiteralPath $statePath)) {
        throw 'offline drill state file is missing'
    }
    Write-Host "INFO  mock request log: $logPath"
    Write-Host '== OFFLINE REPLAY DRILL PASS =='
    exit 0
}
finally {
    Stop-MockWebDav -Mock $mock
    Remove-DrillWorkDir -Path $workdir -Keep:$Keep
}
