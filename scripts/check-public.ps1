<#
.SYNOPSIS
  Enforce the public-repository allowlist.

.DESCRIPTION
  Fails (exit 1) if any git-tracked file:
    * does not match a pattern in public.allowlist, or
    * looks like a secret / real database / local process artefact.
  Exit 0 means the tracked tree is safe to publish.

  Run from CI (public repo) or locally before pushing:
      pwsh -File scripts/check-public.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$allowlistPath = Join-Path $root 'public.allowlist'
if (-not (Test-Path -LiteralPath $allowlistPath)) {
    Write-Host 'FAIL: public.allowlist is missing'
    exit 1
}

$patterns = @(
    Get-Content -LiteralPath $allowlistPath |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -ne '' -and -not $_.StartsWith('#') }
)
if ($patterns.Count -eq 0) {
    Write-Host 'FAIL: public.allowlist has no patterns'
    exit 1
}

function Test-Allowlisted([string]$Path) {
    foreach ($pattern in $patterns) {
        $regex = '^' + [regex]::Escape($pattern).Replace('\*\*', '.*').Replace('\*', '[^/]*').Replace('\?', '[^/]') + '$'
        if ($Path -match $regex) { return $true }
    }
    return $false
}

$tracked = @(& git -c core.quotepath=false ls-files 2>$null)
if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) {
    Write-Host 'FAIL: git ls-files failed or returned nothing (is git installed? is this a repo?)'
    exit 1
}

$violations = New-Object System.Collections.Generic.List[string]
$forbidden = @(
    '(?i)\.kdbx$', '(?i)\.key$', '(?i)\.pem$', '(?i)\.pfx$', '(?i)\.p12$',
    '(?i)(^|/)\.env$', '(?i)(^|/)secrets/',
    '(?i)(-pw\.txt|\.pw\.txt|\.password|\.passwd)$',
    '(?i)^AGENTS\.md$', '(?i)^REQUIREMENTS\.md$', '(?i)^V1\.0-',
    '(?i)^\.agents/', '(?i)^\.spike/', '(?i)^target/',
    '(?i)^docs/(STATUS\.md|acceptance/|checklists/|env-setup\.md|gate-logs/|spike/|system-tour\.md|v1\.x-ideas\.md|verify/|adr/)',
    '(?i)^scripts/(check-system|gate|new-task-card|sync-board)\.ps1$'
)

foreach ($file in $tracked) {
    if (-not (Test-Allowlisted $file)) {
        $violations.Add("not on public.allowlist: $file")
    }
    foreach ($pattern in $forbidden) {
        if ($file -match $pattern -and -not ($file -match '(?i)^tests/fixtures/.*\.kdbx$')) {
            $violations.Add("forbidden tracked path: $file")
            break
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "FAIL: $($violations.Count) public-repository violation(s):"
    $violations | ForEach-Object { Write-Host "  - $_" }
    exit 1
}

Write-Host "OK: $($tracked.Count) tracked files all match public.allowlist"
exit 0
