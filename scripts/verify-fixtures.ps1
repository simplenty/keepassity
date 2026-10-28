# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Verify that every KDBX fixture matches tests/fixtures/manifest.json and that
  every fixture file is registered.

.DESCRIPTION
  The manifest records the SHA-256, size and test password of each synthetic or
  public fixture. This script fails if a fixture changed without a manifest update
  or if an unregistered .kdbx appeared under tests/fixtures.
#>
param(
  [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fixturesRoot = Join-Path $root 'tests/fixtures'
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
  $ManifestPath = Join-Path $fixturesRoot 'manifest.json'
}
if (-not (Test-Path $ManifestPath)) {
  throw "fixture manifest not found: $ManifestPath"
}

$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
$fail = 0
$seen = @{}
foreach ($fixture in $manifest.fixtures) {
  $path = Join-Path $fixturesRoot $fixture.path
  if (-not (Test-Path $path)) {
    Write-Host "FAIL  missing fixture: $($fixture.path)"
    $fail++
    continue
  }
  $seen[$fixture.path] = $true
  $hash = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
  $size = (Get-Item $path).Length
  if ($hash -ne $fixture.sha256) {
    Write-Host "FAIL  hash mismatch: $($fixture.path)"
    $fail++
  }
  elseif ($size -ne $fixture.size) {
    Write-Host "FAIL  size mismatch: $($fixture.path)"
    $fail++
  }
  else {
    Write-Host "PASS  $($fixture.path)"
  }
}

$files = Get-ChildItem $fixturesRoot -Recurse -File -Filter '*.kdbx'
foreach ($file in $files) {
  $relative = $file.FullName.Substring($fixturesRoot.Length + 1) -replace '\\', '/'
  if (-not $seen.ContainsKey($relative)) {
    Write-Host "FAIL  fixture is not in the manifest: $relative"
    $fail++
  }
}

if ($fail -gt 0) {
  Write-Host "== fixtures: $fail failure(s) =="
  exit 1
}
Write-Host "== fixtures: $($files.Count) files verified against the manifest =="
exit 0
