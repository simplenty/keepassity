# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Regenerate the synthetic fixtures under tests/fixtures/synthetic/ and refresh
  their SHA-256/size in tests/fixtures/manifest.json.

.DESCRIPTION
  The generator is an ignored integration test
  (crates/core/tests/synthetic_fixture.rs::generate_synthetic_500) so it uses the
  exact write backend the application uses. It draws fresh crypto material on
  every run, which means the file bytes change every time; this script keeps the
  manifest in sync and then runs the fixture verifier.

  Run from anywhere; paths are resolved against the repository root.

.PARAMETER SkipGenerate
  Only refresh the manifest hashes from the existing files.

.PARAMETER CargoTargetDir
  Cargo target directory (use a shared directory to avoid rebuilding).

.EXAMPLE
  pwsh -File tools/fixture-gen/regenerate.ps1
#>
[CmdletBinding()]
param(
  [switch]$SkipGenerate,
  [string]$CargoTargetDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root

$fixtureRelativePath = 'synthetic/synthetic-500.kdbx'
$fixturePath = Join-Path (Join-Path $root 'tests/fixtures') $fixtureRelativePath
$manifestPath = Join-Path $root 'tests/fixtures/manifest.json'

if (-not $SkipGenerate) {
  $env:KEEPASSITY_SYNTHETIC_OUT = Split-Path -Parent $fixturePath
  if ($CargoTargetDir) {
    $env:CARGO_TARGET_DIR = $CargoTargetDir
  }
  Write-Host 'Generating tests/fixtures/synthetic/synthetic-500.kdbx ...'
  & cargo test -p keepassity-core --test synthetic_fixture generate_synthetic_500 -- --ignored --nocapture
  if ($LASTEXITCODE -ne 0) {
    throw 'fixture generation failed (see cargo output above)'
  }
}

if (-not (Test-Path -LiteralPath $fixturePath)) {
  throw "fixture not found: $fixturePath (run without -SkipGenerate)"
}

$hash = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $fixturePath).Length

# Patch only the hash/size of the registered synthetic fixture so the rest of the
# manifest keeps its formatting and the 25 upstream fixtures stay untouched.
$lines = Get-Content -LiteralPath $manifestPath
$index = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
  if ($lines[$i] -match [regex]::Escape('"path":') -and
      $lines[$i] -match [regex]::Escape($fixtureRelativePath)) {
    $index = $i
    break
  }
}
if ($index -lt 0) {
  throw "manifest has no fixture entry for $fixtureRelativePath"
}

$hashPattern = '^(\s*"sha256":\s*")[0-9a-fA-F]{64}(".*)$'
$sizePattern = '^(\s*"size":\s*)\d+(.*)$'
$replacedHash = $false
$replacedSize = $false
for ($offset = 1; $offset -le 15 -and $index + $offset -lt $lines.Count; $offset++) {
  $line = $lines[$index + $offset]
  if (-not $replacedHash -and $line -match $hashPattern) {
    $lines[$index + $offset] = $Matches[1] + $hash + $Matches[2]
    $replacedHash = $true
    continue
  }
  if (-not $replacedSize -and $line -match $sizePattern) {
    $lines[$index + $offset] = $Matches[1] + $size + $Matches[2]
    $replacedSize = $true
    if ($replacedHash) { break }
  }
}
if (-not ($replacedHash -and $replacedSize)) {
  throw 'could not patch sha256/size in the manifest entry'
}
Set-Content -LiteralPath $manifestPath -Value $lines

Write-Host "Updated manifest: $fixtureRelativePath sha256=$hash size=$size bytes"
$verifyScript = Join-Path $root 'scripts/verify-fixtures.ps1'
$shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }
& $shell -NoProfile -ExecutionPolicy Bypass -File $verifyScript
if ($LASTEXITCODE -ne 0) {
  throw 'scripts/verify-fixtures.ps1 failed after regeneration'
}
