# SPDX-License-Identifier: GPL-3.0-or-later
# Build a clean self-contained Windows x64 folder (and optional zip).
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "",
    [string]$OutputRoot = "",
    [string]$FfiDir = "",
    [switch]$Zip
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "apps/windows/Keepassity.App/Keepassity.App.csproj"
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $root "dist"
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    # Read the single version source with the XML DOM instead of the PowerShell
    # adapter: Windows PowerShell 5.1 returns an XmlElement for
    # $props.Project.PropertyGroup.KeepassityVersion, which turned the version
    # property into "System.Xml.XmlElement" and made restore fail.
    [xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
    $versionNode = $props.SelectSingleNode('/Project/PropertyGroup/KeepassityVersion')
    $Version = if ($versionNode) { $versionNode.InnerText.Trim() } else { "" }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = "1.0.0-dev"
    }
}

$output = Join-Path $OutputRoot "keepassity-$Version-$Runtime"
if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $output | Out-Null

if (-not [string]::IsNullOrWhiteSpace($FfiDir)) {
    $FfiDir = $FfiDir.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
}

$publishArgs = @(
    "publish", $project, "-c", $Configuration, "-r", $Runtime, "--self-contained", "true",
    "-p:KeepassityVersion=$Version", "-o", $output
)
if (-not [string]::IsNullOrWhiteSpace($FfiDir)) {
    $publishArgs += "-p:KeepassityFfiDir=$FfiDir"
}
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$ffi = Join-Path $output "keepassity_ffi.dll"
if (-not (Test-Path $ffi)) {
    throw "Native FFI DLL is missing from the publish output: $ffi. Build it first: cargo build -p keepassity-ffi --release"
}
$xbf = @(Get-ChildItem $output -Filter "*.xbf" -File)
if ($xbf.Count -eq 0) {
    throw "XAML resources (*.xbf) are missing from the publish output: $output. The app would not start."
}

foreach ($name in @("README.md", "LICENSE", "NOTICE", "THIRD-PARTY-LICENSES.md")) {
    $source = Join-Path $root $name
    if (Test-Path $source) {
        Copy-Item $source (Join-Path $output $name)
    }
}
$icon = Join-Path $output "Assets/app.ico"
if (-not (Test-Path $icon)) {
    throw "Application icon is missing from the publish output: $icon. The window/tray icon and the installer icon come from this file."
}

$exe = Join-Path $output "Keepassity.exe"
if (-not (Test-Path $exe)) {
    throw "Published executable is missing: $exe"
}
# The workspace may carry a Low integrity label. Windows starts executables from
# Low-labeled folders at Low integrity, which prevents the app and the installer
# from writing normal user data and the setup TEMP directory. Release binaries
# must run at Medium. Relabeling needs SeRelabelPrivilege, so an unprivileged
# build (developer machine, app in a Low-integrity folder) can refuse here;
# publish from a normal folder in that case instead of failing the build.
& icacls $exe /setintegritylevel Medium | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Could not relabel $exe to Medium integrity. Publish into a normal (Medium) folder before shipping or installing; artifacts inside a Low-integrity workspace are not installable."
} else {
    # The launcher and the setup run the same executable.
    & icacls (Join-Path $output "Keepassity.exe") /setintegritylevel Medium | Out-Null
}
$launcher = Join-Path $output "Run keepassity.cmd"
Set-Content -LiteralPath $launcher -Encoding Ascii -Value @(
    "@echo off",
    'powershell -NoProfile -ExecutionPolicy Bypass -Command "Unblock-File -LiteralPath ''%~dp0Keepassity.exe'' -ErrorAction SilentlyContinue" >nul 2>nul',
    'powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "& ''%~dp0Keepassity.exe''"'
)
try {
    Unblock-File -LiteralPath $exe -ErrorAction SilentlyContinue
}
catch {
    # A local build normally has no Mark-of-the-Web; ignore failures.
}
Write-Host "published: $output"
Write-Host "executable: $exe"
Write-Host "fast launcher: $launcher"

if ($Zip) {
    $archive = Join-Path $OutputRoot "keepassity-$Version-$Runtime.zip"
    if (Test-Path $archive) {
        Remove-Item $archive -Force
    }
    Compress-Archive -Path (Join-Path $output "*") -DestinationPath $archive
    Write-Host "archive: $archive"
}
