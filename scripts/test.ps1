# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Run the local verification layers.

.DESCRIPTION
  -Fast (default): Rust formatting, clippy and tests, the Windows build and the
  headless core service tests. Use -Full to also run the application self-tests.
#>
param(
  [switch]$Full
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
# Prefer a user-local SDK when it is new enough for global.json: the
# machine-wide SDK can be broken (missing workload resolvers) while a
# dotnet-install copy works. An older user-local SDK must not shadow the
# machine-wide one after a framework upgrade, so compare versions first.
$localDotnet = Join-Path $env:USERPROFILE '.dotnet'
if (Test-Path (Join-Path $localDotnet 'dotnet.exe')) {
  $required = $null
  try {
    $required = [version](Get-Content (Join-Path $root 'global.json') -Raw |
      ConvertFrom-Json).sdk.version
  } catch {
    $required = $null
  }
  $localCompatible = $null
  $sdkDirectory = Join-Path $localDotnet 'sdk'
  if (Test-Path $sdkDirectory) {
    $localCompatible = Get-ChildItem $sdkDirectory -Directory |
      ForEach-Object {
        try { [version]$_.Name } catch { $null }
      } |
      Where-Object { $_ -ne $null -and ($required -eq $null -or $_ -ge $required) } |
      Sort-Object -Descending |
      Select-Object -First 1
  }
  if ($localCompatible) {
    $env:PATH = $localDotnet + ';' + $env:PATH
    $env:DOTNET_ROOT = $localDotnet
  } else {
    Write-Host ("user-local .NET SDK is older than global.json requires ({0}); using the machine-wide SDK" -f $required)
  }
}
$failures = 0
function Run([string]$name, [scriptblock]$action) {
  Write-Host "== $name =="
  & $action
  if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: $name (exit $LASTEXITCODE)"
    $script:failures++
  }
}

Run 'cargo fmt' { cargo fmt --all -- --check }
Run 'cargo clippy' { cargo clippy --workspace --all-targets -- -D warnings }
Run 'cargo test' { cargo test --workspace }
Run 'fixtures' { powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-fixtures.ps1 }
Run 'ui strings' { powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-ui-strings.ps1 }
Run 'localization' { powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-localization.ps1 -Strict }
Run 'app build' { dotnet build apps/windows/Keepassity.sln -c Debug }
$coreTestsDir = Join-Path $root 'out/tmp/core-tests'
New-Item -ItemType Directory -Force -Path $coreTestsDir | Out-Null
$env:KEEPASSITY_CORE_TESTS_DIR = $coreTestsDir
Run 'core service tests' { dotnet run --project tests/app-core/Keepassity.Core.Tests -c Release }

if ($Full) {
  Run 'selftest aes' {
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/selftest.ps1 `
      -Fixture tests/fixtures/keepass-rs/test_db_kdbx4_with_password_aes.kdbx -Password demopass
  }
  Run 'selftest totp' {
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/selftest.ps1 `
      -Fixture tests/fixtures/keepass-rs/test_db_kdbx4_with_totp_entry.kdbx -Password test
  }
}

Pop-Location
if ($failures -gt 0) {
  Write-Host "== verification: $failures failure(s) =="
  exit 1
}
Write-Host '== verification: PASS =='
exit 0
