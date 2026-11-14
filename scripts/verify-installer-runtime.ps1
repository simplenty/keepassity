# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Exercise a throwaway installer end to end: install, upgrade, run, uninstall.

.DESCRIPTION
  Builds an Inno Setup package with its own AppId and mutex (TestMode: no
  shortcuts, no file-association registry writes, no Run entry), installs it to a
  temporary folder, installs it again to exercise the upgrade path, runs the
  installed app's --selftest against a temporary data directory, then uninstalls
  it. The checks prove two data-safety properties of the installer:

    * uninstall removes the files the installer created and nothing else;
    * user data (a file a user put next to the executable, and the application
      data directory) survives the uninstall.

  The real installed copy under %LOCALAPPDATA%\Programs\keepassity and the real
  HKCU associations are captured before and after and must be unchanged.

  A workspace directory can carry the Low mandatory integrity label; Windows then
  shows an invisible "Open File - Security Warning" before launching an
  executable there. The script raises the freshly built setup and uninstaller to
  Medium first, the same thing scripts/build-installer.ps1 does for the shipped
  setup.

.PARAMETER Version
  Version compiled into the throwaway installer.

.PARAMETER SourceDir
  Published portable folder to package (scripts/publish-windows.ps1 output).

.PARAMETER WorkDir
  Optional scratch folder; defaults to out/tmp/installer-runtime.

.PARAMETER KeepArtifacts
  Keep the scratch folder instead of deleting it after a successful run.
#>
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [Parameter(Mandatory = $true)][string]$SourceDir,
  [string]$WorkDir,
  [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Test-Path $SourceDir)) {
  throw "publish folder not found: $SourceDir"
}
$SourceDir = (Resolve-Path $SourceDir).Path
if (-not $WorkDir) {
  $WorkDir = Join-Path $root 'out/tmp/installer-runtime'
}
$WorkDir = [IO.Path]::GetFullPath($WorkDir)
if (Test-Path $WorkDir) {
  Remove-Item -Recurse -Force $WorkDir
}
$installerDir = Join-Path $WorkDir 'installer'
$appDir = Join-Path $WorkDir 'app'
$dataDir = Join-Path $WorkDir 'data'
$fixture = Join-Path $WorkDir 'fixture.kdbx'
New-Item -ItemType Directory -Force -Path $installerDir, $dataDir | Out-Null

function Find-Iscc {
  $candidates = @(
    (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe')
  ) | Where-Object { $_ -and (Test-Path $_) }
  if ($candidates) {
    return $candidates | Select-Object -First 1
  }
  $found = Get-ChildItem 'C:\Program Files', 'C:\Program Files (x86)', `
      (Join-Path $env:LOCALAPPDATA 'Programs') -Recurse -Filter 'ISCC.exe' `
      -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($found) {
    return $found.FullName
  }
  throw 'Inno Setup (ISCC.exe) was not found; install it to run this check.'
}

function Set-MediumIntegrity([string]$Path) {
  # Harmless on folders without the Low label; required when the workspace is
  # Low integrity, otherwise ShellExecute hides behind a security-warning dialog.
  try {
    & icacls $Path /setintegritylevel Medium | Out-Null
  }
  catch {
    Write-Warning "could not raise integrity of $Path (continuing): $_"
  }
}

function Invoke-Setup([string]$Path, [string]$Target) {
  $process = Start-Process -FilePath $Path `
      -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', `
                    "/DIR=$Target" -Wait -PassThru
  if ($process.ExitCode -ne 0) {
    throw "installer exited with $($process.ExitCode)"
  }
}

function Invoke-Uninstaller([string]$Path) {
  $process = Start-Process -FilePath $Path `
      -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' `
      -Wait -PassThru
  if ($process.ExitCode -ne 0) {
    throw "uninstaller exited with $($process.ExitCode)"
  }
}

$running = Get-Process Keepassity -ErrorAction SilentlyContinue
if ($running) {
  throw 'Keepassity is running; close it before running the installer check.'
}

$realInstall = Join-Path $env:LOCALAPPDATA 'Programs/keepassity/Keepassity.exe'
$realInstallBefore = Test-Path $realInstall
$realAssocBefore = Test-Path 'HKCU:\Software\Classes\keepassity.kdbx'
$oldSelftestPassword = $env:KEEPASSITY_SELFTEST_PASSWORD
$oldDataDir = $env:KEEPASSITY_DATA_DIR
$oldReport = $env:KEEPASSITY_SELFTEST_REPORT
$installed = $false
try {
  $iscc = Find-Iscc
  $appId = '{{' + [guid]::NewGuid().ToString().ToUpperInvariant() + '}'
  $mutexName = 'Local\keepassity-installer-runtime-' + [guid]::NewGuid().ToString('N')

  & $iscc `
    "/DAppVersion=$Version" `
    "/DSourceDir=$SourceDir" `
    "/DOutputDir=$installerDir" `
    "/DAppId=$appId" `
    "/DAppMutexName=$mutexName" `
    '/DTestMode=1' `
    (Join-Path $root 'installer/keepassity.iss')
  if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE"
  }
  $setup = Join-Path $installerDir "keepassity-$Version-win-x64-setup.exe"
  if (-not (Test-Path $setup)) {
    throw "installer was not produced: $setup"
  }
  Set-MediumIntegrity $setup

  Write-Host 'installer check: installing'
  Invoke-Setup $setup $appDir
  $installed = $true
  $exe = Join-Path $appDir 'Keepassity.exe'
  if (-not (Test-Path $exe)) {
    throw 'install did not place Keepassity.exe'
  }

  Write-Host 'installer check: upgrading'
  Invoke-Setup $setup $appDir
  if (-not (Test-Path $exe)) {
    throw 'upgrade removed the executable'
  }

  Write-Host 'installer check: running the installed app'
  Copy-Item (Join-Path $root 'tests/fixtures/keepass-rs/test_db_few_rounds.kdbx') `
      $fixture -Force
  Set-Content -Path (Join-Path $dataDir 'keepassity-data-marker.txt') -Value 'preserve'
  $reportDirectory = Join-Path $dataDir 'selftest'
  $report = Join-Path $reportDirectory ('keepassity-selftest-' + [guid]::NewGuid().ToString('N') + '.log')
  $env:KEEPASSITY_SELFTEST_PASSWORD = 'demopass'
  $env:KEEPASSITY_DATA_DIR = $dataDir
  $env:KEEPASSITY_SELFTEST_REPORT = $report
  # Start-Process -Wait (not &): the app is a GUI-subsystem executable, so a
  # shell invocation returns before the self-test has written its report.
  Set-MediumIntegrity $exe
  $selftest = Start-Process -FilePath $exe `
      -ArgumentList '--selftest', $fixture, '--data-dir', $dataDir -Wait -PassThru
  if ($selftest.ExitCode -ne 0) {
    throw "the installed app's self-test failed with exit code $($selftest.ExitCode)"
  }
  if (-not (Test-Path $report)) {
    $written = @(Get-ChildItem $reportDirectory -Filter '*.log' -ErrorAction SilentlyContinue)
    throw "the installed app did not write its self-test report ($report); found $($written.Count) report(s)"
  }
  Set-Content -Path (Join-Path $appDir 'keepassity-user-note.txt') -Value 'preserve'

  Write-Host 'installer check: uninstalling'
  $uninstaller = Join-Path $appDir 'unins000.exe'
  if (-not (Test-Path $uninstaller)) {
    throw 'installer did not place an uninstaller'
  }
  Set-MediumIntegrity $uninstaller
  Invoke-Uninstaller $uninstaller
  $installed = $false
  Start-Sleep -Milliseconds 500

  if (Test-Path $exe) {
    throw 'uninstall left Keepassity.exe behind'
  }
  if (-not (Test-Path (Join-Path $appDir 'keepassity-user-note.txt'))) {
    throw 'uninstall deleted a user file next to the executable'
  }
  if (-not (Test-Path (Join-Path $dataDir 'keepassity-data-marker.txt')) -or
      -not (Test-Path $report)) {
    throw 'uninstall deleted user data'
  }
  if ($realInstallBefore -ne (Test-Path $realInstall) -or
      $realAssocBefore -ne (Test-Path 'HKCU:\Software\Classes\keepassity.kdbx')) {
    throw 'the throwaway installer changed the real installation or associations'
  }

  Write-Host 'PASS: install, upgrade, run and uninstall preserve user data'
}
finally {
  if ($installed) {
    $leftover = Join-Path $appDir 'unins000.exe'
    if (Test-Path $leftover) {
      Set-MediumIntegrity $leftover
      try { Invoke-Uninstaller $leftover } catch { Write-Warning $_ }
    }
  }
  $env:KEEPASSITY_SELFTEST_PASSWORD = $oldSelftestPassword
  $env:KEEPASSITY_DATA_DIR = $oldDataDir
  $env:KEEPASSITY_SELFTEST_REPORT = $oldReport
  if (-not $KeepArtifacts) {
    Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue
  }
}
