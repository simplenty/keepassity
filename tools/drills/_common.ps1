# SPDX-License-Identifier: GPL-3.0-or-later
# Shared helpers for the M4-07 drill scripts. Dot-source from a drill script:
#   . "$PSScriptRoot/_common.ps1"

$ErrorActionPreference = 'Stop'
$script:DrillRepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Get-RepoRoot { return $script:DrillRepoRoot }

function Initialize-Drills {
    <#
    .SYNOPSIS
      Build the mock WebDAV server, the drill runner and the native FFI, and
      return the paths the drill scripts need.
    #>
    $repoRoot = $script:DrillRepoRoot
    $mockProject = Join-Path $repoRoot 'tools/drills/mock-webdav/MockWebDav.csproj'
    $runnerProject = Join-Path $repoRoot 'tools/drills/runner/DrillRunner.csproj'
    $runnerBin = Join-Path $repoRoot 'tools/drills/runner/bin/Release/net8.0'
    $mockBin = Join-Path $repoRoot 'tools/drills/mock-webdav/bin/Release/net8.0'
    $fixture = Join-Path $repoRoot 'tests/fixtures/synthetic/synthetic-500.kdbx'

    if (-not (Test-Path -LiteralPath $fixture)) {
        throw "synthetic fixture not found: $fixture (run pwsh -File tools/fixture-gen/regenerate.ps1)"
    }

    Write-Host 'Building drill tools (dotnet build -c Release) ...'
    & dotnet build $mockProject -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'mock-webdav build failed' }
    # The runner references Keepassity.Core (owned by another workstream); retry
    # once in case a concurrent edit made the first build see a partial tree.
    & dotnet build $runnerProject -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) {
        Start-Sleep -Seconds 2
        & dotnet build $runnerProject -c Release --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'drill runner build failed' }
    }

    $cargoTarget = if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { Join-Path $repoRoot 'target' }
    Write-Host 'Building keepassity-ffi (cargo build --release) ...'
    Push-Location $repoRoot
    try {
        & cargo build -p keepassity-ffi --release --quiet
        if ($LASTEXITCODE -ne 0) { throw 'cargo build -p keepassity-ffi failed' }
    }
    finally {
        Pop-Location
    }
    $ffiDll = Join-Path $cargoTarget 'release/keepassity_ffi.dll'
    if (-not (Test-Path -LiteralPath $ffiDll)) {
        throw "native FFI DLL not found: $ffiDll (is CARGO_TARGET_DIR set for both build and drill?)"
    }
    Copy-Item -LiteralPath $ffiDll -Destination (Join-Path $runnerBin 'keepassity_ffi.dll') -Force

    $mockExe = Join-Path $mockBin 'MockWebDav.exe'
    $runnerExe = Join-Path $runnerBin 'DrillRunner.exe'
    if (-not (Test-Path -LiteralPath $mockExe)) { throw "mock server executable not found: $mockExe" }
    if (-not (Test-Path -LiteralPath $runnerExe)) { throw "drill runner executable not found: $runnerExe" }

    return [pscustomobject]@{
        RepoRoot  = $repoRoot
        MockExe   = $mockExe
        RunnerExe = $runnerExe
        FfiDll    = $ffiDll
        Fixture   = $fixture
    }
}

function New-DrillWorkDir {
    param([Parameter(Mandatory)][string]$Name)
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $path = Join-Path ([IO.Path]::GetTempPath()) ("keepassity-drill-$Name-$stamp-" + [guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    return $path
}

function Start-MockWebDav {
    <#
    .SYNOPSIS
      Start MockWebDav on a free port (or a requested port) and wait until it
      has written its port file.
    #>
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Log,
        [int]$Port = 0,
        [Parameter(Mandatory)][string]$MockExe
    )
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    $portFile = Join-Path ([IO.Path]::GetTempPath()) ('keepassity-mock-' + [guid]::NewGuid().ToString('N') + '.port')
    $arguments = @(
        '--root', ('"' + $Root + '"'),
        '--port', "$Port",
        '--port-file', ('"' + $portFile + '"'),
        '--log', ('"' + $Log + '"')
    )
    # -NoNewWindow (CreateProcess) instead of -WindowStyle Hidden (ShellExecuteEx):
    # ShellExecuteEx blocks in headless/sandboxed sessions before the server is
    # even created, so the drill would hang waiting for its port file. The mock
    # server only writes one READY line, so sharing the drill console is harmless.
    $process = Start-Process -FilePath $MockExe -ArgumentList $arguments -PassThru -NoNewWindow
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path -LiteralPath $portFile)) {
        if ($process.HasExited) {
            throw "MockWebDav exited before opening a port (exit code $($process.ExitCode))"
        }
        if ((Get-Date) -gt $deadline) {
            try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { }
            throw 'timed out waiting for the MockWebDav port file'
        }
        Start-Sleep -Milliseconds 100
    }
    $actualPort = [int]((Get-Content -LiteralPath $portFile -Raw).Trim())
    return [pscustomobject]@{
        Process  = $process
        Port     = $actualPort
        PortFile = $portFile
        UrlBase  = "http://127.0.0.1:$actualPort"
        Log      = $Log
    }
}

function Start-MockWebDavWithRetry {
    <#
    .SYNOPSIS
      Restart the mock server on the same port (used by the offline replay drill
      so the configured remote URL stays identical).
    #>
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Log,
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][string]$MockExe
    )
    $lastError = $null
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            return Start-MockWebDav -Root $Root -Log $Log -Port $Port -MockExe $MockExe
        }
        catch {
            $lastError = $_
            Start-Sleep -Milliseconds 400
        }
    }
    throw "could not restart MockWebDav on port $Port after 5 attempts: $lastError"
}

function Stop-MockWebDav {
    param($Mock)
    if ($null -eq $Mock) { return }
    try {
        if (-not $Mock.Process.HasExited) {
            Stop-Process -Id $Mock.Process.Id -Force -ErrorAction SilentlyContinue
        }
        $deadline = (Get-Date).AddSeconds(10)
        while (-not $Mock.Process.HasExited -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 100
        }
    }
    catch {
        Write-Host "WARN  could not stop MockWebDav: $($_.Exception.Message)"
    }
    if ($Mock.PortFile -and (Test-Path -LiteralPath $Mock.PortFile)) {
        Remove-Item -LiteralPath $Mock.PortFile -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-DrillRunner {
    param(
        [Parameter(Mandatory)][string]$RunnerExe,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    # Route the native stdout to the host explicitly: a bare `& $RunnerExe` would
    # put every PASS/INFO line into the function's output stream, so callers that
    # do `$exitCode = Invoke-DrillRunner ...` would receive an array (the lines
    # plus the exit code) and treat a successful drill as a failure.
    & $RunnerExe @Arguments | Out-Host
    return $LASTEXITCODE
}

function Remove-DrillWorkDir {
    param([string]$Path, [switch]$Keep)
    if ($Keep -or [string]::IsNullOrWhiteSpace($Path)) {
        Write-Host "INFO  drill workspace kept at $Path"
        return
    }
    try { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop }
    catch { Write-Host "WARN  could not remove drill workspace ${Path}: $($_.Exception.Message)" }
}
