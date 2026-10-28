# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Write a SHA256SUMS file for the release artifacts in a directory.
#>
param(
  [string]$Directory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Directory)) {
  $Directory = Join-Path $root 'dist'
}
if (-not (Test-Path $Directory)) {
  throw "artifact directory not found: $Directory"
}

$files = Get-ChildItem $Directory -File |
  Where-Object { $_.Name -notlike 'SHA256SUMS*' -and $_.Name -notlike '*.spdx.json' } |
  Sort-Object Name
$lines = foreach ($file in $files) {
  $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  "$hash  $($file.Name)"
}
$path = Join-Path $Directory 'SHA256SUMS'
$lines = @($lines)
[System.IO.File]::WriteAllLines($path, $lines, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "checksums: $path"
$lines | ForEach-Object { Write-Host "  $_" }
