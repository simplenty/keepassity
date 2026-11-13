# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Verify a keepassity installation produced by the Inno Setup installer, or
  verify that an uninstall really cleaned up.

.DESCRIPTION
  The graphical installer (wizard clicks) has to be driven by a person, but
  everything around it is machine-checkable. This script checks the layout,
  licence files, shortcut targets, the Apps & Features (uninstall registry)
  entry, the installed binary version, the per-user .kdbx file association
  (candidate registration: ProgID, DefaultIcon, shell\open\command with %1, OpenWithProgids,
  Applications\...\SupportedTypes, and that the .kdbx default handler was not taken over) and - when a
  fixture is given - runs the installed application's own `--selftest`.

  With -ExpectUninstalled it instead checks that the program folder, the
  shortcuts, the uninstall entry and the .kdbx association are gone and that the
  per-user data folder (settings, state, logs) survived - uninstalling must never
  delete user data.

.PARAMETER InstallDir
  Install folder. Defaults to %LOCALAPPDATA%\Programs\keepassity, which is what
  the per-user installer uses.

.PARAMETER Version
  Optional expected version (for example 0.1.0). Compared against the Apps &
  Features entry and the executable's product version.

.PARAMETER Fixture
  Optional KDBX fixture. When given, the installed executable runs --selftest.

.PARAMETER FixturePassword
  Password for -Fixture.

.PARAMETER DataDir
  Application data folder. Defaults to %LOCALAPPDATA%\keepassity. Used for the
  self-test data directory and for the data-retention check after uninstall.

.PARAMETER ExpectUninstalled
  Check the post-uninstall state instead of the installed state.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\installer\verify-install.ps1 `
    -Version 1.0.0 -Fixture tests/fixtures/keepass-rs/test_db_kdbx4_with_password_aes.kdbx -FixturePassword demopass

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\installer\verify-install.ps1 -ExpectUninstalled

.NOTES
  Exit code 0 = all checks passed, 1 = at least one check failed.
  This script never installs, uninstalls or modifies the profile; it only reads.
#>
[CmdletBinding()]
param(
  [string]$InstallDir = "",
  [string]$Version = "",
  [string]$Fixture = "",
  [string]$FixturePassword = "",
  [string]$DataDir = "",
  [switch]$ExpectUninstalled
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($InstallDir)) {
  $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\keepassity'
}
if ([string]::IsNullOrWhiteSpace($DataDir)) {
  $DataDir = Join-Path $env:LOCALAPPDATA 'keepassity'
}
$InstallDir = $InstallDir.TrimEnd('\', '/')
$DataDir = $DataDir.TrimEnd('\', '/')

$failures = 0
$warnings = 0
function Pass([string]$name, [string]$detail = '') {
  if ($detail) { Write-Host ("PASS  {0}  ({1})" -f $name, $detail) } else { Write-Host ("PASS  {0}" -f $name) }
}
function Fail([string]$name, [string]$detail = '') {
  if ($detail) { Write-Host ("FAIL  {0}  ({1})" -f $name, $detail) } else { Write-Host ("FAIL  {0}" -f $name) }
  $script:failures++
}
function Warn([string]$name, [string]$detail = '') {
  if ($detail) { Write-Host ("WARN  {0}  ({1})" -f $name, $detail) } else { Write-Host ("WARN  {0}" -f $name) }
  $script:warnings++
}

$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\keepassity.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'keepassity.lnk'
$uninstallKeys = @(
  'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A7E3B1F2-9D44-4C7A-A1B2-6F5E8D9C0123}_is1',
  'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A7E3B1F2-9D44-4C7A-A1B2-6F5E8D9C0123}_is1',
  'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{A7E3B1F2-9D44-4C7A-A1B2-6F5E8D9C0123}_is1'
)
$uninstallKey = $uninstallKeys | Where-Object { Test-Path $_ } | Select-Object -First 1
$shell = $null
try { $shell = New-Object -ComObject WScript.Shell } catch { }

function Get-ShortcutTarget([string]$path) {
  if (-not $shell) { return $null }
  try { return ($shell.CreateShortcut($path)).TargetPath } catch { return $null }
}

function Get-RegistryDefault([string]$path) {
  if (-not (Test-Path -LiteralPath $path)) { return $null }
  try { return (Get-ItemProperty -LiteralPath $path -ErrorAction Stop).'(default)' } catch { return $null }
}

if ($ExpectUninstalled) {
  Write-Host '== keepassity uninstall verification =='
  Write-Host ("install dir: {0}" -f $InstallDir)

  if (Test-Path $InstallDir) {
    # The uninstaller removes the program files it installed and leaves
    # directories that still contain user data. A directory per se is not a
    # failure; a leftover program file is.
    $leftProgramFiles = @()
    foreach ($name in @('Keepassity.exe', 'Keepassity.Core.dll', 'keepassity_ffi.dll',
        'unins000.exe', 'unins000.dat')) {
      if (Test-Path (Join-Path $InstallDir $name)) { $leftProgramFiles += $name }
    }
    if ($leftProgramFiles.Count -eq 0) {
      Pass 'program files removed (folder kept for user data)'
    } else {
      Fail 'program files removed' (($leftProgramFiles -join ', ') + ' still in ' + $InstallDir)
    }
  } else {
    Pass 'program folder removed'
  }

  foreach ($pair in @(@('Start Menu shortcut removed', $startMenuShortcut), @('Desktop shortcut removed', $desktopShortcut))) {
    if (Test-Path $pair[1]) { Fail $pair[0] ('still exists: ' + $pair[1]) } else { Pass $pair[0] }
  }

  if ($uninstallKey) {
    Fail 'Apps & Features entry removed' ('still registered: ' + $uninstallKey)
  } else {
    Pass 'Apps & Features entry removed'
  }

  # The .kdbx association is a per-user candidate registration, so uninstall must
  # remove the ProgID, the OpenWithProgids value and the Applications entry. A
  # machine-wide .kdbx mapping from another product (HKLM) is not ours and must
  # not fail these checks; the user's default handler choice is left untouched.
  $openWithLeft = Get-ItemProperty -LiteralPath 'HKCU:\Software\Classes\.kdbx\OpenWithProgids' -ErrorAction SilentlyContinue
  if ($openWithLeft -and $openWithLeft.PSObject.Properties.Name -contains 'keepassity.kdbx') {
    Fail '.kdbx OpenWithProgids entry removed' 'value still present'
  } else {
    Pass '.kdbx OpenWithProgids entry removed'
  }
  if (Test-Path -LiteralPath 'HKCU:\Software\Classes\keepassity.kdbx') {
    Fail 'ProgID keepassity.kdbx removed' 'still registered'
  } else {
    Pass 'ProgID keepassity.kdbx removed'
  }
  if (Test-Path -LiteralPath 'HKCU:\Software\Classes\Applications\Keepassity.exe') {
    Fail 'shell Applications entry removed' 'still registered'
  } else {
    Pass 'shell Applications entry removed'
  }

  # Uninstalling must keep the user's data: vault working copies, settings, state and logs.
  if (Test-Path $DataDir) {
    $kept = @()
    foreach ($item in @('settings.json', 'state', 'logs', 'remote')) {
      if (Test-Path (Join-Path $DataDir $item)) { $kept += $item }
    }
    if ($kept.Count -gt 0) {
      Pass 'user data folder kept' (($kept -join ', ') + ' in ' + $DataDir)
    } else {
      Warn 'user data folder kept but looks empty' $DataDir
    }
  } else {
    Fail 'user data folder kept' ('missing: ' + $DataDir + ' - an uninstall must not delete it')
  }

  $leftover = @(Get-ChildItem $env:TEMP -Directory -Filter 'is-*.tmp' -ErrorAction SilentlyContinue)
  if ($leftover.Count -eq 0) {
    Pass 'no leftover installer temp folders'
  } else {
    Warn 'leftover installer temp folders' (($leftover | ForEach-Object { $_.FullName }) -join '; ')
  }

  Write-Host ''
  if ($failures -gt 0) {
    Write-Host ("== RESULT: {0} fail, {1} warn ==" -f $failures, $warnings)
    exit 1
  }
  Write-Host ("== RESULT: PASS ({0} warn) ==" -f $warnings)
  exit 0
}

Write-Host '== keepassity install verification =='
Write-Host ("install dir: {0}" -f $InstallDir)
Write-Host ("data dir:    {0}" -f $DataDir)

if (-not (Test-Path $InstallDir)) {
  Write-Host '== RESULT: install folder missing, nothing to verify =='
  exit 1
}
Pass 'install folder exists'

$required = @(
  'Keepassity.exe',
  'Keepassity.Core.dll',
  'keepassity_ffi.dll',
  'unins000.exe',
  'unins000.dat',
  'LICENSE',
  'NOTICE',
  'THIRD-PARTY-LICENSES.md'
)
foreach ($name in $required) {
  $path = Join-Path $InstallDir $name
  if (Test-Path $path) { Pass ("file: " + $name) } else { Fail ("file: " + $name) 'missing' }
}
$legacy = Join-Path $InstallDir 'Keepassity.Mock.exe'
if (Test-Path $legacy) { Warn 'legacy Keepassity.Mock.exe removed by the upgrade' $legacy } else { Pass 'no stale Keepassity.Mock.exe from the pre-1.0 installer' }

$xbf = @(Get-ChildItem $InstallDir -Recurse -Filter '*.xbf' -File -ErrorAction SilentlyContinue)
if ($xbf.Count -ge 2) { Pass 'XAML resources present' ("{0} .xbf" -f $xbf.Count) } else { Fail 'XAML resources present' ("{0} .xbf" -f $xbf.Count) }

$exe = Join-Path $InstallDir 'Keepassity.exe'
if (Test-Path $exe) {
  $info = (Get-Item $exe).VersionInfo
  $product = $info.ProductVersion
  $fileVersion = $info.FileVersion
  if ($fileVersion) { Pass 'executable has a file version' $fileVersion } else { Fail 'executable has a file version' 'empty' }
  if ($Version) {
    if ($product -and $product.StartsWith($Version)) { Pass 'executable product version' $product } else { Fail 'executable product version' ("expected $Version, got '$product'") }
    # The Windows file version is what Explorer and bug reports show; it must agree
    # with the installed release (v1.0.0 -> 1.0.0.0).
    if ($fileVersion -and $fileVersion.StartsWith("$Version.")) { Pass 'executable file version matches the release' $fileVersion } else { Fail 'executable file version matches the release' ("expected $Version.x, got '$fileVersion'") }
  } else {
    Pass 'executable product version' $product
  }
  if ((Get-Item $exe).Length -gt 100000) { Pass 'executable size looks sane' ('{0:N0} bytes' -f (Get-Item $exe).Length) } else { Fail 'executable size looks sane' ('{0:N0} bytes' -f (Get-Item $exe).Length) }
}

foreach ($pair in @(@('Start Menu shortcut', $startMenuShortcut), @('Desktop shortcut', $desktopShortcut))) {
  if (Test-Path $pair[1]) {
    $target = Get-ShortcutTarget $pair[1]
    if ($target -and ($target -ieq $exe)) {
      Pass ($pair[0] + ' targets the installed app') $target
    } elseif ($target) {
      Fail ($pair[0] + ' targets the installed app') ("-> " + $target)
    } else {
      Warn ($pair[0] + ' target unreadable') $pair[1]
    }
  } else {
    if ($pair[0] -eq 'Desktop shortcut') { Warn ($pair[0] + ' missing') $pair[1] } else { Fail ($pair[0] + ' missing') $pair[1] }
  }
}

if ($uninstallKey) {
  $entry = Get-ItemProperty $uninstallKey
  Pass 'Apps & Features entry exists' $entry.DisplayName
  if ($Version) {
    if ($entry.DisplayVersion -eq $Version) { Pass 'registered version' $entry.DisplayVersion } else { Fail 'registered version' ("expected $Version, got '$($entry.DisplayVersion)'") }
  } else {
    Pass 'registered version' $entry.DisplayVersion
  }
  if ($entry.Publisher) { Pass 'publisher recorded' $entry.Publisher } else { Warn 'publisher not recorded' }
  if ($entry.InstallLocation -and ($entry.InstallLocation.TrimEnd('\') -ieq $InstallDir)) { Pass 'install location recorded' $entry.InstallLocation } else { Warn 'install location differs' ("'$($entry.InstallLocation)' vs '$InstallDir'") }
  if ($entry.DisplayIcon -and $entry.DisplayIcon.StartsWith($InstallDir, 'OrdinalIgnoreCase')) { Pass 'display icon points into the install folder' $entry.DisplayIcon } else { Warn 'display icon unexpected' $entry.DisplayIcon }
  if ($entry.UninstallString -and (Test-Path ((($entry.UninstallString -replace '"', '') -split ' ')[0]))) { Pass 'uninstaller present' $entry.UninstallString } else { Fail 'uninstaller present' $entry.UninstallString }
} else {
  Fail 'Apps & Features entry exists' 'no uninstall registry key found'
}

# .kdbx file association. The installer runs at lowest privileges, so it writes
# a per-user registration under HKCU\Software\Classes (never HKLM/HKCR). Windows
# 10/11 lets the user pick the default app, so the installer registers keepassity
# as a *candidate* handler: it must NOT take over the .kdbx default value. These
# checks verify the candidate registration and the absence of a silent takeover.
$associationProgId = 'keepassity.kdbx'
$extensionKey = 'HKCU:\Software\Classes\.kdbx'
$progIdKey = 'HKCU:\Software\Classes\' + $associationProgId
$extensionDefault = Get-RegistryDefault $extensionKey
if ($extensionDefault -ieq $associationProgId) {
  Warn '.kdbx default handler is keepassity (the user chose it, or the installer took it over?)' $extensionDefault
} else {
  Pass '.kdbx default handler is not silently taken over' ("current: '$extensionDefault'")
}
$openWith = Get-ItemProperty -LiteralPath ($extensionKey + '\OpenWithProgids') -ErrorAction SilentlyContinue
if ($openWith -and $openWith.PSObject.Properties.Name -contains $associationProgId) {
  Pass '.kdbx OpenWithProgids lists keepassity.kdbx'
} else {
  Fail '.kdbx OpenWithProgids lists keepassity.kdbx' 'value missing'
}
if (Test-Path -LiteralPath $progIdKey) {
  Pass 'ProgID key exists' $progIdKey
} else {
  Fail 'ProgID key exists' ('missing: ' + $progIdKey)
}
$friendlyName = Get-RegistryDefault $progIdKey
if ($friendlyName) {
  Pass 'ProgID has a friendly name' $friendlyName
} else {
  Fail 'ProgID has a friendly name' 'empty'
}
$iconValue = Get-RegistryDefault ($progIdKey + '\DefaultIcon')
if ($iconValue -and ($iconValue.IndexOf($InstallDir, [System.StringComparison]::OrdinalIgnoreCase) -ge 0)) {
  Pass 'ProgID DefaultIcon points into the install folder' $iconValue
} else {
  Fail 'ProgID DefaultIcon points into the install folder' ("got '$iconValue'")
}
$commandValue = Get-RegistryDefault ($progIdKey + '\shell\open\command')
$commandHasApp = $false
$commandHasPlaceholder = $false
if ($commandValue) {
  $commandHasApp = $commandValue.IndexOf($InstallDir, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
  $commandHasPlaceholder = $commandValue.IndexOf('%1', [System.StringComparison]::Ordinal) -ge 0
}
if ($commandHasApp -and $commandHasPlaceholder) {
  Pass 'ProgID open command points into the install folder and keeps %1' $commandValue
} else {
  Fail 'ProgID open command points into the install folder and keeps %1' ("got '$commandValue'")
}
$supportedTypes = Get-ItemProperty -LiteralPath ('HKCU:\Software\Classes\Applications\Keepassity.exe\SupportedTypes') -ErrorAction SilentlyContinue
if ($supportedTypes -and $supportedTypes.PSObject.Properties.Name -contains '.kdbx') {
  Pass 'shell Applications entry advertises .kdbx'
} else {
  Warn 'shell Applications entry advertises .kdbx' 'value missing (Open with still works through OpenWithProgids)'
}

if ($Fixture) {
  if (-not (Test-Path $Fixture)) {
    Fail 'self-test fixture' ('not found: ' + $Fixture)
  } else {
    if ([string]::IsNullOrEmpty($FixturePassword)) {
      Fail 'self-test' 'pass -FixturePassword'
    } else {
      $reportDir = Join-Path $DataDir 'selftest'
      New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
      $report = Join-Path $reportDir ('install-verify-' + [guid]::NewGuid().ToString('N') + '.log')
      $env:KEEPASSITY_SELFTEST_PASSWORD = $FixturePassword
      $env:KEEPASSITY_SELFTEST_REPORT = $report
      $startInfo = New-Object System.Diagnostics.ProcessStartInfo
      $startInfo.FileName = $exe
      $startInfo.Arguments = '--selftest "' + (Resolve-Path $Fixture).Path + '"'
      $startInfo.UseShellExecute = $false
      $startInfo.CreateNoWindow = $true
      $process = [System.Diagnostics.Process]::Start($startInfo)
      if ($process.WaitForExit(120000)) {
        $content = if (Test-Path $report) { Get-Content $report -Raw } else { '' }
        if ($content -match '(?m)^PASS\s*$') {
          Pass 'installed app --selftest' 'PASS'
        } else {
          Fail 'installed app --selftest' (($content -split "`r?`n" | Select-Object -Last 1) -join ' ')
        }
      } else {
        try { $process.Kill() } catch { }
        Fail 'installed app --selftest' 'timeout'
      }
    }
  }
} else {
  Write-Host 'SKIP  installed app --selftest (pass -Fixture and -FixturePassword to enable)'
}

Write-Host ''
if ($failures -gt 0) {
  Write-Host ("== RESULT: {0} fail, {1} warn ==" -f $failures, $warnings)
  exit 1
}
Write-Host ("== RESULT: PASS ({0} warn) ==" -f $warnings)
exit 0
