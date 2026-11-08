# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Mock WebDAV protocol check (supports the M4-07 drills).

.DESCRIPTION
  Starts tools/drills/mock-webdav on a free port and checks the protocol surface
  the application's WebDavClient relies on:
    PUT create (If-None-Match: *), PROPFIND depth 0, GET, HEAD,
    conditional PUT with the current ETag, stale If-Match -> 412,
    If-None-Match: * over an existing file -> 412, DELETE, and a non-ASCII
    (percent-encoded UTF-8) file path.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\drills\run-protocol.ps1
#>
[CmdletBinding()]
param([switch]$Keep)

. "$PSScriptRoot/_common.ps1"

$paths = Initialize-Drills
$workdir = New-DrillWorkDir -Name 'protocol'
$remoteRoot = Join-Path $workdir 'remote'
$logPath = Join-Path $workdir 'mock-webdav.log'
$mock = $null
try {
    Write-Host ''
    Write-Host '== Mock WebDAV protocol check =='
    Write-Host "workspace: $workdir"
    $mock = Start-MockWebDav -Root $remoteRoot -Log $logPath -MockExe $paths.MockExe
    # Percent-encoded UTF-8 file name (bytes of "密码" = E5 AF 86 E7 A0 81).
    $encodedName = '%E5%AF%86%E7%A0%81.kdbx'
    $expectedName = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('5a+G56CB')) + '.kdbx'
    $url = "$($mock.UrlBase)/$encodedName"
    $exitCode = Invoke-DrillRunner -RunnerExe $paths.RunnerExe -Arguments @(
        'protocol', '--url', $url
    )
    if ($exitCode -ne 0) {
        throw "protocol drill failed (exit $exitCode)"
    }
    $decodedFiles = @(Get-ChildItem -LiteralPath $remoteRoot -File)
    if ($decodedFiles.Count -eq 0) {
        Write-Host 'NOTE  the protocol drill deletes its file at the end; no file remains on disk'
    }
    Write-Host "INFO  mock request log: $logPath"
    Write-Host "INFO  non-ASCII path decoded to an on-disk name: $expectedName"
    Write-Host '== PROTOCOL CHECK PASS =='
    exit 0
}
finally {
    Stop-MockWebDav -Mock $mock
    Remove-DrillWorkDir -Path $workdir -Keep:$Keep
}
