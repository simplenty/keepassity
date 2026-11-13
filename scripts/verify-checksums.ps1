# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Verify every entry in a SHA256SUMS file against the files in its directory.

.DESCRIPTION
  Recomputes each digest and fails on a missing file, malformed line or mismatch.
  This is the release step that proves the checksum file belongs to the exact
  artifacts being uploaded.
#>
param(
  [string]$Directory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Directory)) {
  $Directory = Join-Path $root 'dist'
}
$sums = Join-Path $Directory 'SHA256SUMS'
if (-not (Test-Path $sums)) {
  throw "checksum file not found: $sums"
}

$lines = @(Get-Content $sums)
$checked = 0
foreach ($line in $lines) {
  if ([string]::IsNullOrWhiteSpace($line)) { continue }
  $match = [regex]::Match($line, '^([0-9a-fA-F]{64})  (.+)$')
  if (-not $match.Success) {
    throw "malformed checksum line: $line"
  }
  $expected = $match.Groups[1].Value.ToLowerInvariant()
  $name = $match.Groups[2].Value
  $path = Join-Path $Directory $name
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "checksummed artifact is missing: $name"
  }
  $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $expected) {
    throw "checksum mismatch for $name (expected $expected, got $actual)"
  }
  $checked++
}
if ($checked -eq 0) {
  throw "checksum file contains no artifacts: $sums"
}
Write-Host "checksums verified: $checked artifact(s)"
