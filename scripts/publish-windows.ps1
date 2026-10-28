# SPDX-License-Identifier: GPL-3.0-or-later
# Build a clean self-contained Windows x64 folder (and optional zip).
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "",
    [switch]$Zip
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "apps/windows/Keepassity.Mock/Keepassity.Mock.csproj"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content $project
    $Version = ($projectXml.Project.PropertyGroup | Where-Object { $_.Version } |
        Select-Object -First 1).Version
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "Cannot determine the app version from $project."
    }
}

$output = Join-Path $root "dist/keepassity-$Version-$Runtime"
if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $output | Out-Null

& dotnet publish $project -c $Configuration -r $Runtime --self-contained true `
    -p:Version=$Version -p:FileVersion="$Version.0" -p:AssemblyVersion="$Version.0" `
    -o $output
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

$exe = Join-Path $output "Keepassity.Mock.exe"
if (-not (Test-Path $exe)) {
    throw "Published executable is missing: $exe"
}
$launcher = Join-Path $output "Run keepassity.cmd"
Set-Content -LiteralPath $launcher -Encoding Ascii -Value @(
    "@echo off",
    'start "" "%~dp0Keepassity.Mock.exe" --light'
)
Write-Host "published: $output"
Write-Host "executable: $exe"
Write-Host "fast launcher: $launcher"

if ($Zip) {
    $archive = Join-Path $root "dist/keepassity-$Version-$Runtime.zip"
    if (Test-Path $archive) {
        Remove-Item $archive -Force
    }
    Compress-Archive -Path (Join-Path $output "*") -DestinationPath $archive
    Write-Host "archive: $archive"
}
