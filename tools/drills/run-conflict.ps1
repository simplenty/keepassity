# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Drill (c): remote changed + local dirty -> conflict decision and safe merge.

.DESCRIPTION
  The drill scripts the parts that do not need the WinUI dialog:
    1. one client ("another device") changes and uploads the remote database;
    2. the local device edits a disjoint entry while its baseline is stale;
    3. the FFI sync decision reports `download_and_merge` (never a silent
       upload);
    4. a naive upload with the stale If-Match baseline is rejected with HTTP 412
       and both local and remote bytes stay untouched;
    5. the FFI merge combines both edits, the merged result is uploaded with the
       current ETag, and the remote reopens with both edits.

  The same-entry same-mtime conflict dialog and the conflict-copy files created
  by the WinUI shell (MainWindow.MergeRemoteAsync) are NOT automated here; see
  tools/drills/README.md for the exact manual steps.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\drills\run-conflict.ps1
#>
[CmdletBinding()]
param([switch]$Keep)

. "$PSScriptRoot/_common.ps1"

$paths = Initialize-Drills
$workdir = New-DrillWorkDir -Name 'conflict'
$remoteRoot = Join-Path $workdir 'remote'
$logPath = Join-Path $workdir 'mock-webdav.log'
$mock = $null
try {
    Write-Host ''
    Write-Host '== M4-07 drill (c): remote changed + local dirty conflict =='
    Write-Host "workspace: $workdir"
    $mock = Start-MockWebDav -Root $remoteRoot -Log $logPath -MockExe $paths.MockExe
    $url = "$($mock.UrlBase)/vault.kdbx"
    $exitCode = Invoke-DrillRunner -RunnerExe $paths.RunnerExe -Arguments @(
        'conflict', '--url', $url, '--fixture', $paths.Fixture,
        '--workdir', $workdir
    )
    if ($exitCode -ne 0) { throw "conflict drill failed (exit $exitCode)" }
    if (-not (Test-Path -LiteralPath (Join-Path $workdir 'merged.kdbx'))) {
        throw 'conflict drill did not produce merged.kdbx'
    }
    Write-Host "INFO  merged database kept for inspection: $(Join-Path $workdir 'merged.kdbx')"
    Write-Host "INFO  mock request log: $logPath"
    Write-Host '== CONFLICT DRILL PASS (protocol + core decision/merge; dialog remains manual) =='
    exit 0
}
finally {
    Stop-MockWebDav -Mock $mock
    Remove-DrillWorkDir -Path $workdir -Keep:$Keep
}
