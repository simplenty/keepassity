# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Drill (a): backup restore through the application's BackupService.

.DESCRIPTION
  1. copy the synthetic 500-entry fixture to a temp workspace;
  2. create a backup with BackupService.CreateBackup (the app's service);
  3. edit + save the database through the real FFI (update_entry + kp_save);
  4. restore with BackupService.Restore;
  5. prove the restored bytes equal the backup, that a before-restore safety
     copy exists, and that the restored KDBX reopens with 500 entries without
     the drill edit.

  No real vault or password is involved: the fixture's master password is the
  public `demopass` value.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\drills\run-restore.ps1
#>
[CmdletBinding()]
param([switch]$Keep)

. "$PSScriptRoot/_common.ps1"

$paths = Initialize-Drills
$workdir = New-DrillWorkDir -Name 'restore'
try {
    Write-Host ''
    Write-Host '== M4-07 drill (a): backup restore =='
    Write-Host "workspace: $workdir"
    $exitCode = Invoke-DrillRunner -RunnerExe $paths.RunnerExe -Arguments @(
        'restore', '--fixture', $paths.Fixture, '--workdir', $workdir
    )
    if ($exitCode -ne 0) {
        throw "restore drill failed (exit $exitCode)"
    }
    Write-Host '== RESTORE DRILL PASS =='
    exit 0
}
finally {
    Remove-DrillWorkDir -Path $workdir -Keep:$Keep
}
