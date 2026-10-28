# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Build the Inno Setup installer for an already published portable folder.
#>
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [Parameter(Mandatory = $true)][string]$SourceDir,
  [Parameter(Mandatory = $true)][string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path $SourceDir)) {
  throw "publish folder not found: $SourceDir"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$candidates = @(
  (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe'),
  (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
  (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) }
$iscc = $candidates | Select-Object -First 1
if (-not $iscc) {
  $found = Get-ChildItem 'C:\Program Files', 'C:\Program Files (x86)', (Join-Path $env:LOCALAPPDATA 'Programs') `
      -Recurse -Filter 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($found) { $iscc = $found.FullName }
}
if (-not $iscc) {
  throw 'Inno Setup (ISCC.exe) was not found; install it or skip the installer step.'
}

& $iscc `
  "/DAppVersion=$Version" `
  "/DSourceDir=$((Resolve-Path $SourceDir).Path)" `
  "/DOutputDir=$((Resolve-Path $OutputDir).Path)" `
  (Join-Path $root 'installer/keepassity.iss')
if ($LASTEXITCODE -ne 0) {
  throw "ISCC failed with exit code $LASTEXITCODE"
}
$installer = Join-Path $OutputDir "keepassity-$Version-win-x64-setup.exe"
if (-not (Test-Path $installer)) {
  throw "installer was not produced: $installer"
}
Write-Host "installer: $installer"
