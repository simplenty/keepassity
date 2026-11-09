# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Fail when hard-coded CJK text appears in the Windows shell sources.

.DESCRIPTION
  UI text lives in the embedded language catalogs, not in the WinUI sources (see
  docs/testing.md). This check scans apps/windows/**/*.cs and *.xaml for hard-coded
  CJK characters and fails with "file:line" for every hit, so a translation can
  never bypass the catalogs or drift from them.

  Two lines in MainWindow.xaml.cs are allowed: they are the data-matching
  predicates for the localized KeePass group names of the recycle-bin and
  templates groups. They compare group names read from a database and never
  render as UI text. The allowlist below is built from Unicode code points so
  this script stays ASCII-only and decodes identically under Windows PowerShell
  5.1 and PowerShell 7.

  Generated build output (bin/, obj/) and test/fixture directories are skipped.

.PARAMETER Root
  Repository root to scan. Defaults to the parent directory of this script.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-ui-strings.ps1

.NOTES
  Exit code 0 = clean, 1 = hard-coded CJK found (or the scan could not run).
#>
[CmdletBinding()]
param(
    [string]$Root
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $PSScriptRoot
}
$Root = $Root.TrimEnd('\', '/')
$scanRoot = Join-Path $Root 'apps/windows'
if (-not (Test-Path -LiteralPath $scanRoot)) {
    Write-Host "FAIL: scan directory not found: $scanRoot"
    exit 1
}

# CJK ranges: symbols/punctuation, hiragana/katakana, unified ideographs (+ extension A),
# compatibility ideographs, compatibility forms, fullwidth/halfwidth forms.
# Typographic characters used by the English UI (em dash, quotes, bullet, ellipsis,
# middle dot) are deliberately outside these ranges.
$cjkPattern = '[\u3000-\u303F\u3040-\u30FF\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF\uFE30-\uFE4F\uFF00-\uFFEF]'

# Allowed lines, as Unicode code points:
#   recycle bin: U+56DE U+6536 U+7AD9
#   templates:   U+6A21 U+677F
$recycleBin = [string][char]0x56DE + [char]0x6536 + [char]0x7AD9
$templates = [string][char]0x6A21 + [char]0x677F
$allowedLines = @{}
$allowedLines['apps/windows/Keepassity.Mock/MainWindow.xaml.cs'] = @(
    ('return value.Equals("' + $recycleBin + '", StringComparison.OrdinalIgnoreCase) ||'),
    ('return value.Equals("' + $templates + '", StringComparison.OrdinalIgnoreCase) ||')
)

$utf8 = New-Object System.Text.UTF8Encoding($false)
$sourceExtensions = @('.cs', '.xaml')
$skipPathPattern = '[\\/](bin|obj|tests?|fixtures)[\\/]'

$files = @(Get-ChildItem -LiteralPath $scanRoot -Recurse -File -ErrorAction Stop |
    Where-Object {
        ($sourceExtensions -contains $_.Extension.ToLowerInvariant()) -and
        ($_.FullName -notmatch $skipPathPattern)
    } |
    Sort-Object FullName)

if ($files.Count -eq 0) {
    Write-Host "FAIL: no .cs/.xaml sources found under $scanRoot"
    exit 1
}

$violations = New-Object System.Collections.Generic.List[string]
$allowedHits = 0

foreach ($file in $files) {
    $relative = $file.FullName.Substring($Root.Length + 1).Replace('\', '/')
    $allowedForFile = $null
    if ($allowedLines.ContainsKey($relative)) {
        $allowedForFile = $allowedLines[$relative]
    }

    $lines = [System.IO.File]::ReadAllLines($file.FullName, $utf8)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ($line -notmatch $cjkPattern) {
            continue
        }
        if (($null -ne $allowedForFile) -and ($allowedForFile -contains $line.Trim())) {
            $allowedHits++
            continue
        }
        $display = $line.Trim()
        if ($display.Length -gt 160) {
            $display = $display.Substring(0, 157) + '...'
        }
        $violations.Add(('{0}:{1}: {2}' -f $relative, ($index + 1), $display))
    }
}

if ($violations.Count -gt 0) {
    Write-Host "FAIL: $($violations.Count) hard-coded CJK line(s) in the shell sources:"
    foreach ($violation in $violations) {
        Write-Host "  $violation"
    }
    Write-Host 'Add the translation to the language catalog and reference the English key instead;'
    Write-Host 'only the documented data-matching literals in MainWindow.xaml.cs are allowed.'
    Write-Host 'See docs/testing.md (UI strings).'
    exit 1
}

$summary = 'OK: {0} source file(s) scanned under apps/windows; no hard-coded CJK text ' +
    '({1} documented data-matching line(s) allowed)'
Write-Host ($summary -f $files.Count, $allowedHits)
exit 0
