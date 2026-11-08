# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Build the drill tools without running a drill.

.DESCRIPTION
  Builds tools/drills/mock-webdav, tools/drills/runner and the native
  keepassity-ffi release library, then copies keepassity_ffi.dll next to the
  runner executable. Drill scripts call the same initialisation automatically;
  this script is for CI/caching or for checking the build in isolation.
#>
[CmdletBinding()]
param()

. "$PSScriptRoot/_common.ps1"

$paths = Initialize-Drills
Write-Host ''
Write-Host 'Drill tools built:'
Write-Host "  mock WebDAV server : $($paths.MockExe)"
Write-Host "  drill runner      : $($paths.RunnerExe)"
Write-Host "  native FFI        : $($paths.FfiDll)"
Write-Host "  synthetic fixture : $($paths.Fixture)"
exit 0
