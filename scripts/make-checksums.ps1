# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Write a portable, deterministic SHA256SUMS file for release artifacts.

.DESCRIPTION
  Every regular artifact except SHA256SUMS itself is hashed, including the SBOM.
  Lines use LF endings and lowercase hex so the file works with GNU coreutils
  (sha256sum -c) as well as Windows tooling.
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
  Where-Object { $_.Name -ne 'SHA256SUMS' } |
  Sort-Object Name
if (@($files).Count -eq 0) {
  throw "no release artifacts found in $Directory"
}

$lines = foreach ($file in $files) {
  $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  "$hash  $($file.Name)"
}
$path = Join-Path $Directory 'SHA256SUMS'
[System.IO.File]::WriteAllText($path, (($lines -join "`n") + "`n"),
  (New-Object System.Text.UTF8Encoding($false)))
Write-Host "checksums: $path"
$lines | ForEach-Object { Write-Host "  $_" }
