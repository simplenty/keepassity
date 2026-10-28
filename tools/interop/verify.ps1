# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Interop verification for the KDBX write closure.

.DESCRIPTION
  For every supported fixture:
    1. `keepassity-cli rewrite` re-saves it with the ADR-0002 write backend;
    2. `keepassity-cli verify` compares source and output field by field;
    3. optionally `keepassxc-cli` opens the output and reports the same entry count;
    4. optionally KeePassLib (via tools/interop/KeePassLibTool) re-opens both files
       and compares a canonical structural hash.

  The optional real-database check rewrites a copy (never the input) and prints
  no entry content. Pass the password through an environment variable.

.PARAMETER FixturesOnly
  Skip the real-database round trip.

.PARAMETER KeepassXcPath
  Path/name of keepassxc-cli. When omitted the script looks on PATH.

.PARAMETER RealDbPath
  Path to a real KDBX to exercise read -> rewrite -> verify. Never modified.

.PARAMETER RealDbPasswordEnv
  Environment variable holding the real database password.

.PARAMETER KeePassLibDll
  Path to a managed KeePassLib.dll. When given (Windows + dotnet SDK), the
  KeePassLibTool is built and used as an independent KeePassLib reader.

.PARAMETER WorkDir
  Working directory for outputs. Defaults to <repo>/target/interop-tmp.
#>
[CmdletBinding()]
param(
    [switch]$FixturesOnly,
    [string]$KeepassXcPath,
    [string]$RealDbPath,
    [string]$RealDbPasswordEnv = 'KEEPASSITY_REAL_PASSWORD',
    [string]$KeePassLibDll,
    [string]$WorkDir
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root

$isWindows = $true
if ($PSVersionTable.PSVersion.Major -ge 6) { $isWindows = $IsWindows }
$exeSuffix = if ($isWindows) { '.exe' } else { '' }

if (-not $WorkDir) { $WorkDir = [IO.Path]::Combine($root, 'target', 'interop-tmp') }
if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

$failures = New-Object System.Collections.Generic.List[string]
$passes = 0
$skipped = 0

function Add-Failure([string]$message) {
    Write-Host "FAIL  $message"
    $script:failures.Add($message) | Out-Null
}

function Invoke-Cli([string[]]$arguments) {
    $output = & $script:cli @arguments 2>&1 | Out-String
    return [pscustomobject]@{ Exit = $LASTEXITCODE; Output = $output }
}

function Resolve-KeepassXc([string]$candidate) {
    if ($candidate) {
        if (Test-Path -LiteralPath $candidate) { return (Resolve-Path -LiteralPath $candidate).Path }
        $command = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($command) { return $command.Source }
        throw "keepassxc-cli not found: $candidate"
    }
    $command = Get-Command 'keepassxc-cli' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    return $null
}

function Get-KpxcLineCount([string]$password, [string]$path) {
    $output = $password | & $script:kpxc 'ls' '-R' '-f' $path 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { return -1 }
    $lines = @($output -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })
    return $lines.Count
}

function Invoke-KeePassLibTool([string]$password, [string]$path) {
    $previous = $env:KEEPASSITY_INTEROP_PW
    try {
        $env:KEEPASSITY_INTEROP_PW = $password
        $output = & $script:keepassLibTool $path '--password-env' 'KEEPASSITY_INTEROP_PW' '--allow-empty-standard-fields' 2>&1 | Out-String
        $code = $LASTEXITCODE
    } finally {
        if ($null -eq $previous) { Remove-Item Env:\KEEPASSITY_INTEROP_PW -ErrorAction SilentlyContinue }
        else { $env:KEEPASSITY_INTEROP_PW = $previous }
    }
    if ($code -ne 0) { return $null }
    try { return ($output | ConvertFrom-Json) } catch { return $null }
}

# --- locate/build the CLI -------------------------------------------------
$cargo = Get-Command cargo -ErrorAction SilentlyContinue
if (-not $cargo) { throw 'cargo not found on PATH' }
Write-Host 'Building keepassity-cli ...'
$build = & $cargo build -p keepassity-cli --quiet 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Host $build
    throw 'cargo build -p keepassity-cli failed'
}
$cli = [IO.Path]::Combine($root, 'target', 'debug', "keepassity-cli$exeSuffix")
if (-not (Test-Path -LiteralPath $cli)) { throw "CLI binary not found: $cli" }

# --- optional third-party tools ------------------------------------------
$kpxc = $null
try { $kpxc = Resolve-KeepassXc $KeepassXcPath } catch { throw }
if ($kpxc) { Write-Host "keepassxc-cli: enabled ($kpxc)" } else { Write-Host 'keepassxc-cli: SKIPPED (not found)' }

$keepassLibTool = $null
if ($KeePassLibDll) {
    if (-not $isWindows) {
        Write-Host 'KeePassLibTool: SKIPPED (Windows/.NET Framework only)'
    } else {
        $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $dotnet) { throw 'dotnet not found (needed for KeePassLibTool)' }
        $project = [IO.Path]::Combine($root, 'tools', 'interop', 'KeePassLibTool', 'KeePassLibTool.csproj')
        Write-Host 'Building KeePassLibTool ...'
        $toolBuild = & dotnet build $project -c Release --nologo -p:KeePassLibPath=$KeePassLibDll 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            Write-Host $toolBuild
            throw 'KeePassLibTool build failed'
        }
        $keepassLibTool = [IO.Path]::Combine($root, 'tools', 'interop', 'KeePassLibTool', 'bin', 'Release', 'net48', 'KeePassLibTool.exe')
        if (-not (Test-Path -LiteralPath $keepassLibTool)) { throw "KeePassLibTool not found: $keepassLibTool" }
        Write-Host 'KeePassLibTool: enabled'
    }
} else {
    Write-Host 'KeePassLibTool: SKIPPED (pass -KeePassLibDll to enable)'
}

# --- fixtures -------------------------------------------------------------
$fixtureDir = [IO.Path]::Combine($root, 'tests', 'fixtures', 'keepass-rs')
$fixtures = @(Get-ChildItem -LiteralPath $fixtureDir -Filter '*.kdbx' | Sort-Object Name)
Write-Host "Fixtures: $($fixtures.Count) files"
foreach ($fixture in $fixtures) {
    $name = $fixture.Name
    if ($name -match 'keyfile|challenge_response') {
        $skipped++
        continue
    }
    $password = if ($name -match 'totp') { 'test' }
                elseif ($name -match 'chacha20_protected_fields') { 'password' }
                else { 'demopass' }
    $output = [IO.Path]::Combine($WorkDir, "out-$name")

    $rewrite = Invoke-Cli @('rewrite', $fixture.FullName, $output, '--password', $password)
    if ($name -match '^broken_') {
        if ($rewrite.Exit -eq 0) { Add-Failure "${name}: broken fixture was rewritten unexpectedly" }
        else { Write-Host "PASS  $name (rejected as expected)"; $passes++ }
        continue
    }
    if ($rewrite.Exit -ne 0) {
        Add-Failure "${name}: rewrite failed (exit $($rewrite.Exit))"
        continue
    }

    $verify = Invoke-Cli @('verify', $fixture.FullName, $output, '--password', $password, '--allow-empty-standard-fields')
    if ($verify.Exit -ne 0) {
        Add-Failure "${name}: verify failed"
        $verify.Output -split "`r?`n" | Where-Object { $_ -match '^FAIL:' } | ForEach-Object { Write-Host "      $_" }
        continue
    }

    $thirdPartyOk = $true
    if ($kpxc) {
        $inputCount = Get-KpxcLineCount $password $fixture.FullName
        $outputCount = Get-KpxcLineCount $password $output
        if ($inputCount -lt 0 -or $outputCount -lt 0) {
            Add-Failure "${name}: keepassxc-cli could not list a file"; $thirdPartyOk = $false
        } elseif ($inputCount -ne $outputCount) {
            Add-Failure "${name}: keepassxc-cli count differs ($inputCount -> $outputCount)"; $thirdPartyOk = $false
        }
    }

    if ($thirdPartyOk -and $keepassLibTool) {
        # KeePassLib 2.x managed build does not ship the Twofish cipher plugin.
        # keepassxc-cli and the CLI verifier still cover these fixtures.
        if ($name -match 'twofish') {
            Write-Host "SKIP  ${name}: KeePassLib does not support Twofish (keepassxc-cli covered it)"
        } else {
            $before = Invoke-KeePassLibTool $password $fixture.FullName
            $after = Invoke-KeePassLibTool $password $output
            if ($null -eq $before -or $null -eq $after) {
                Add-Failure "${name}: KeePassLibTool could not open original/output"; $thirdPartyOk = $false
            } elseif ($before.canonical_sha256 -ne $after.canonical_sha256) {
                Add-Failure "${name}: KeePassLib canonical hash changed"; $thirdPartyOk = $false
            } elseif ([int]$before.entries -ne [int]$after.entries -or [int]$before.groups -ne [int]$after.groups) {
                Add-Failure "${name}: KeePassLib entry/group count changed"; $thirdPartyOk = $false
            } elseif ($before.kdbx -ne $after.kdbx) {
                Add-Failure "${name}: KeePassLib version changed ($($before.kdbx) -> $($after.kdbx))"; $thirdPartyOk = $false
            }
        }
    }

    if ($thirdPartyOk) {
        Write-Host "PASS  $name"
        $passes++
    }
}

# --- optional real database -------------------------------------------------
if (-not $FixturesOnly -and $RealDbPath) {
    $realPassword = [Environment]::GetEnvironmentVariable($RealDbPasswordEnv)
    if ([string]::IsNullOrEmpty($realPassword)) {
        Add-Failure "real database: environment variable $RealDbPasswordEnv is not set"
    } elseif (-not (Test-Path -LiteralPath $RealDbPath)) {
        Add-Failure 'real database: path not found'
    } else {
        $realOut = [IO.Path]::Combine($WorkDir, 'real-roundtrip.kdbx')
        $rewrite = Invoke-Cli @('rewrite', $RealDbPath, $realOut, '--password-env', $RealDbPasswordEnv)
        if ($rewrite.Exit -ne 0) {
            Add-Failure 'real database: rewrite failed'
        } else {
            $verify = Invoke-Cli @('verify', $RealDbPath, $realOut, '--password-env', $RealDbPasswordEnv, '--allow-empty-standard-fields')
            if ($verify.Exit -ne 0) {
                $detail = [IO.Path]::Combine($WorkDir, 'real-verify.txt')
                Set-Content -LiteralPath $detail -Value $verify.Output -Encoding UTF8
                Add-Failure "real database: verify failed (details: $detail)"
            } else {
                $thirdPartyOk = $true
                if ($kpxc) {
                    $inputCount = Get-KpxcLineCount $realPassword $RealDbPath
                    $outputCount = Get-KpxcLineCount $realPassword $realOut
                    if ($inputCount -lt 0 -or $outputCount -lt 0 -or $inputCount -ne $outputCount) {
                        Add-Failure 'real database: keepassxc-cli count differs'; $thirdPartyOk = $false
                    }
                }
                if ($thirdPartyOk -and $keepassLibTool) {
                    $before = Invoke-KeePassLibTool $realPassword $RealDbPath
                    $after = Invoke-KeePassLibTool $realPassword $realOut
                    if ($null -eq $before -or $null -eq $after) {
                        Add-Failure 'real database: KeePassLibTool could not open original/output'; $thirdPartyOk = $false
                    } elseif ($before.canonical_sha256 -ne $after.canonical_sha256) {
                        Add-Failure 'real database: KeePassLib canonical hash changed'; $thirdPartyOk = $false
                    }
                }
                if ($thirdPartyOk) { Write-Host 'PASS  real database (rewrite -> verify -> third-party readers)'; $passes++ }
            }
        }
    }
}

Write-Host ''
Write-Host "SUMMARY: pass=$passes fail=$($failures.Count) skipped=$skipped keepassxc=$([bool]$kpxc) keepasslib=$([bool]$keepassLibTool)"
if ($failures.Count -gt 0) { exit 1 }
exit 0
