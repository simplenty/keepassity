# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
    Static localization gate for the Windows shell: catalogs, key usage and
    placeholder parity.

.DESCRIPTION
    The Windows shell localizes by using the English source text as the catalog
    key. Catalog files live in
    apps/windows/Keepassity.Core/Localization/*.zh-CN.json and are embedded
    resources; a key without an entry silently renders English at runtime, which
    is why the mistakes this script looks for do not show up in the UI.

    Errors (always fail the run):
      * every *.zh-CN.json must parse as one JSON object of non-empty string keys
        and non-empty string values; a key defined by two catalog files (or twice
        in one file) is a duplicate;
      * every used key must have a translation:
          - Loc.T("...") / Loc.TLong("...", ...) in apps/windows/**/*.cs,
          - loc:LocMarkup.Text / ToolTip / Name / HelpText attributes in
            apps/windows/**/*.xaml (XML entities such as &amp; and &#xE774; are
            decoded),
          - the string literals in the first argument of SetSyncStatus(...) and of
            CommitSyncedAsync(...) (the sync status is rendered later by
            SetSyncStatus, so its literal is a catalog key too).
        Adjacent C# literals ("a" + "b") are joined, and both "..." and @"..."
        are decoded. A call whose first argument is not a literal (a variable, a
        conditional expression, an interpolated string) cannot be resolved
        statically, so it is reported as a warning instead of being checked;
        two shapes are recognised instead of warned about: a variable forwarded
        by the localization helpers or the sync label, and the tray's
        "_unlocked ? \"locked\" : \"unlocked\"" pair (both literals are checked);
      * composite format placeholders ({0}, {1}, ...) must match between a
        catalog key and its translation, so a translated sentence cannot drop or
        renumber an argument.

    Warnings (reported, but only fatal with -Strict):
      * catalog entries that no source refers to (stale);
      * translations identical to their English key (untranslated), except for
        the brand/acronym allowlist below;
      * C# lines that look like UI text assigned without Loc.T (candidates for
        human review, not proof of a bug). Deliberately untranslated literals -
        brand words, example URLs and secrets, the password/TOTP placeholder
        glyphs and the tray fallback glyph - are allowlisted with their reason;
        keep that list short, since anything on it is never reported again.

    Comments and string contents are lexed out before calls are located, so an
    example inside a DocFX comment (Loc.cs documents Loc.T with a string) never
    counts as a used key, and commented-out code never counts as an unwrapped UI
    literal.

.PARAMETER Root
    Repository root to check. Defaults to the parent directory of this script, so
    the script works from any working directory.

.PARAMETER Strict
    Treat warnings as errors (exit 1). Use this once the translation backlog is
    empty; without it the gate can land before the work is finished.

.PARAMETER MaxPerSection
    How many lines of each error/warning category to print. Defaults to 50; the
    summary always reports the full counts.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-localization.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-localization.ps1 -Strict

.NOTES
    Requires Windows PowerShell 5.1; uses no external modules and only reads
    product files. The report is ASCII-only on purpose: Windows PowerShell 5.1
    encodes redirected stdout with the console code page, so printing raw CJK
    would corrupt CI logs. Non-ASCII characters in keys are shown as \uXXXX
    escapes; in quoted source lines they are replaced with "?".
    Exit code 0 = pass, 1 = error (or warnings when -Strict is given).
#>
[CmdletBinding()]
param(
    [string] $Root,
    [switch] $Strict,
    [int] $MaxPerSection = 50
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- settings

$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:WindowsRelative = 'apps/windows'
$script:CatalogRelative = 'apps/windows/Keepassity.Core/Localization'
$script:CatalogSuffix = '.zh-cn.json'

# Keys whose translation is legitimately identical to the English text: product
# names, acronyms and the tray tooltip state prefix (the brand is not translated;
# the state word after the dash is).
$script:UntranslatedAllowlist = @(
    'English', 'TOTP', 'WebDAV', 'KDBX', 'Keepassity',
    'Keepassity - {0}', 'Keepassity - {0} ({1})'
)

# UI-literal review candidates that are deliberately not localized: example URLs
# and secrets in placeholders, brand words, the tray fallback glyph, password /
# TOTP placeholder glyphs, and the URL shown in a WebDAV heading. These patterns
# are matched against the line; each one carries its reason. The patterns are
# regexes anchored on the literal value: watch out for bare "?" and "." metacharacters,
# which turn an intended exact match into "anything" and silently hide candidates.
$script:UnwrappedAllowlist = @(
    @{ Pattern = 'PlaceholderText\s*=\s*"https://'; Reason = 'example URL in a placeholder' },
    @{ Pattern = 'Text = "Keepassity"'; Reason = 'product name' },
    @{ Pattern = 'Title = "Keepassity"'; Reason = 'product name' },
    @{ Pattern = 'Text = "WebDAV"'; Reason = 'protocol name heading' },
    @{ Pattern = 'Text = "K"'; Reason = 'tray icon fallback glyph' },
    @{ Pattern = 'Text = "JBSWY3DPEHPK3PXP"'; Reason = 'example Base32 secret' },
    @{ Pattern = 'Text = "\?"'; Reason = 'empty TOTP placeholder' },
    @{ Pattern = 'Text = "\?\?\?\?\?\?\?\?\?\?\?\?"'; Reason = 'password mask' },
    @{ Pattern = '[\u2022\u2014]'; Reason = 'password / TOTP placeholder glyph' }
)

# C# lexer: one pass over comments, strings and char literals. Alternation order
# matters: raw strings before plain strings, verbatim interpolated before the
# plain verbatim form, so the longest spelling wins.
$script:CSharpTokenPattern = '(?s)(?<comment>//[^\r\n]*|/\*.*?\*/)' +
    '|(?<raw>"""(?:.*?)""")' +
    '|(?<vinterp>[@$]{2}"(?:[^"]|"")*")' +
    '|(?<verbatim>@"(?:[^"]|"")*")' +
    '|(?<interp>\$"(?:[^"\\\r\n]|\\.)*")' +
    '|(?<regular>"(?:[^"\\\r\n]|\\.)*")' +
    '|(?<chr>''(?:[^''\\\r\n]|\\.)*'')'

# XAML attached properties that carry a catalog key.
$script:XamlAttributePattern = @'
loc:LocMarkup\.(?<prop>Text|ToolTip|Name|HelpText)\s*=\s*(?:"(?<dq>[^"]*)"|'(?<sq>[^']*)')
'@

# Code that assigns or announces user-visible text. A line matching one of these,
# containing a string literal and not using Loc., is a review candidate.
$script:UiLiteralPattern = 'Text\s*=\s*"' +
    '|Content\s*=\s*"' +
    '|Header\s*=\s*"' +
    '|PlaceholderText\s*=\s*"' +
    '|ShowToast\s*\(\s*"' +
    '|Title\s*=\s*"' +
    '|Message\s*=\s*"' +
    '|PrimaryButtonText\s*=\s*"' +
    '|SecondaryButtonText\s*=\s*"' +
    '|CloseButtonText\s*=\s*"' +
    '|AutomationProperties\.SetName\s*\(' +
    '|AutomationProperties\.SetHelpText\s*\(' +
    '|new\s+MenuFlyoutItem'

$script:RegexLocT = [regex]'(?<![A-Za-z0-9_.])(?:Loc\.T|_text\.T)\s*\('
$script:RegexLocTLong = [regex]'(?<![A-Za-z0-9_.])(?:Loc\.TLong|_text\.TLong)\s*\('
$script:RegexSetSyncStatus = [regex]'(?<![A-Za-z0-9_.])SetSyncStatus\s*\('
# Status strings handed to the sync commit helper end up in SetSyncStatus(status)
# through a variable, so their literals are catalog keys too.
$script:RegexCommitSynced = [regex]'(?<![A-Za-z0-9_.])CommitSyncedAsync\s*\('
# A tiny ternary over two catalog keys is intentional and both keys are checked
# as used; the expression itself cannot be one static key.
$script:RegexConditionalKey = [regex]'_unlocked\s*\?\s*"(?<yes>[^"\\]*)"\s*:\s*"(?<no>[^"\\]*)"'
# Unqualified T()/TLong() calls are only scanned in the Localization folder, where
# Loc.cs translates its own language-name constants; elsewhere a bare T() is not
# the catalog API.
$script:RegexBareT = [regex]'(?<![A-Za-z0-9_.])T(Long)?\s*\('

$script:ParenChars = [char[]]@('(', ')')
$script:XmlCommentPattern = [regex]'(?s)<!--.*?-->'

if ($MaxPerSection -lt 1) {
    $MaxPerSection = 1
}

# ---------------------------------------------------------------- helpers

function Format-ReportText {
    <#
      Makes a string safe to print: replaces every non-ASCII and control
      character with "?" (tabs become spaces), trims, and truncates.
    #>
    param(
        [string] $Text,
        [int] $MaxLength = 200
    )
    if ([string]::IsNullOrEmpty($Text)) {
        return ''
    }
    $builder = New-Object System.Text.StringBuilder($Text.Length)
    foreach ($character in $Text.ToCharArray()) {
        $code = [int]$character
        if ($code -eq 9) {
            [void]$builder.Append(' ')
        }
        elseif ($code -ge 32 -and $code -le 126) {
            [void]$builder.Append($character)
        }
        else {
            [void]$builder.Append('?')
        }
    }
    $value = $builder.ToString().Trim()
    if ($value.Length -gt $MaxLength) {
        if ($MaxLength -lt 8) {
            return $value.Substring(0, $MaxLength)
        }
        return $value.Substring(0, $MaxLength - 3) + '...'
    }
    return $value
}

function Format-KeyText {
    <#
      Like Format-ReportText but for catalog keys: non-ASCII characters become
      \uXXXX escapes so the reported key is exact and the line stays ASCII. Keys
      are English, but typographic punctuation (ellipsis, em dash) is common in
      the source text and must not be confused with "?".
    #>
    param(
        [string] $Text,
        [int] $MaxLength = 160
    )
    if ([string]::IsNullOrEmpty($Text)) {
        return ''
    }
    $builder = New-Object System.Text.StringBuilder
    foreach ($character in $Text.ToCharArray()) {
        $code = [int]$character
        if ($code -eq 9) {
            [void]$builder.Append(' ')
        }
        elseif ($code -ge 32 -and $code -le 126) {
            [void]$builder.Append($character)
        }
        else {
            [void]$builder.Append('\u' + $code.ToString('X4', [System.Globalization.CultureInfo]::InvariantCulture))
        }
    }
    $value = $builder.ToString().Trim()
    if ($value.Length -gt $MaxLength) {
        if ($MaxLength -lt 8) {
            return $value.Substring(0, $MaxLength)
        }
        return $value.Substring(0, $MaxLength - 3) + '...'
    }
    return $value
}

function Get-RelativePath {
    param([string] $FullName)
    $prefix = $script:RootPrefix
    if ($FullName.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $FullName.Substring($prefix.Length).Replace('\', '/')
    }
    return $FullName.Replace('\', '/')
}

function Read-SourceText {
    param([string] $Path)
    return [System.IO.File]::ReadAllText($Path, $script:Utf8NoBom)
}

function Get-SourceFiles {
    <#
      Walks a directory without descending into bin/, obj/ or .git/. Recursing
      through the generated build output would take seconds and only produce
      generated sources.
    #>
    param([string] $BaseDirectory)
    $files = New-Object System.Collections.Generic.List[object]
    $pending = New-Object System.Collections.Generic.Stack[string]
    $pending.Push($BaseDirectory)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        $children = @()
        try {
            $children = @(Get-ChildItem -LiteralPath $directory -ErrorAction Stop)
        }
        catch {
            continue
        }
        foreach ($child in $children) {
            if ($child.PSIsContainer) {
                $name = $child.Name
                if ($name -eq 'bin' -or $name -eq 'obj' -or $name -eq '.git') {
                    continue
                }
                $pending.Push($child.FullName)
            }
            else {
                $files.Add($child)
            }
        }
    }
    return $files
}

function Get-LineStarts {
    <#
      Offsets at which each line starts. Newlines are found with regex matches so
      the scan stays fast on large files.
    #>
    param([string] $Text)
    $starts = New-Object System.Collections.Generic.List[int]
    $starts.Add(0)
    foreach ($hit in [regex]::Matches($Text, "\n")) {
        $starts.Add($hit.Index + 1)
    }
    return , $starts.ToArray()
}

function Get-LineNumber {
    param(
        [int[]] $LineStarts,
        [int] $Index
    )
    $low = 0
    $high = $LineStarts.Length - 1
    while ($low -lt $high) {
        $middle = [int][Math]::Floor(($low + $high + 1) / 2)
        if ($LineStarts[$middle] -le $Index) {
            $low = $middle
        }
        else {
            $high = $middle - 1
        }
    }
    return $low + 1
}

function Get-LineText {
    param(
        [string] $Text,
        [int[]] $LineStarts,
        [int] $LineNumber,
        [int] $MaxLength = 200
    )
    if ($LineNumber -lt 1 -or $LineNumber -gt $LineStarts.Length) {
        return ''
    }
    $start = $LineStarts[$LineNumber - 1]
    $end = $Text.IndexOf([char]10, $start)
    if ($end -lt 0) {
        $end = $Text.Length
    }
    $line = $Text.Substring($start, $end - $start).Trim()
    return (Format-ReportText -Text $line -MaxLength $MaxLength)
}

function Format-DynamicWarning {
    <#
      Builds the warning for a key position that cannot be resolved statically.
      Literals that do appear in that position are remembered as referenced so a
      catalog entry behind Loc.T(flag ? "locked" : "unlocked") is not called
      stale, but they are never checked as keys: a fragment of an expression is
      not necessarily a catalog key.
    #>
    param(
        [string] $Relative,
        [int] $Line,
        $Literals,
        [string] $Text,
        [int[]] $LineStarts
    )
    $preview = New-Object System.Collections.Generic.List[string]
    $interpolated = $false
    foreach ($literal in $Literals) {
        if (-not $literal.Ok) {
            $interpolated = $true
            continue
        }
        if ($literal.Value.Length -eq 0) {
            continue
        }
        [void]$script:Referenced.Add($literal.Value)
        if ($preview.Count -lt 4) {
            $preview.Add(('"' + (Format-KeyText -Text $literal.Value -MaxLength 80) + '"'))
        }
    }
    $detail = Get-LineText -Text $Text -LineStarts $LineStarts -LineNumber $Line -MaxLength 160
    if ($interpolated) {
        return ('[{0}] {1}:{2}: key is an interpolated string, not checked -- {3}' -f
            'dynamic', $Relative, $Line, $detail)
    }
    if ($preview.Count -eq 0) {
        return ('[{0}] {1}:{2}: key is a variable, not checked -- {3}' -f
            'dynamic', $Relative, $Line, $detail)
    }
    $suffix = ''
    if ($preview.Count -ge 4) {
        $suffix = ', ...'
    }
    return ('[{0}] {1}:{2}: key expression not checked (literals: {3}{4}) -- {5}' -f
        'dynamic', $Relative, $Line, ($preview -join ', '), $suffix, $detail)
}

function Test-UnwrappedAllowlisted {
    <#
      True when an unwrapped UI-literal candidate is a known intentional literal
      (example URL/secret, brand, placeholder glyph). Keeps the review list focused
      on strings a user could actually read in English after switching.
    #>
    param([string] $LineText)
    foreach ($rule in $script:UnwrappedAllowlist) {
        if ($LineText -match $rule.Pattern) {
            return $true
        }
    }
    return $false
}

function New-BlankToken {
    <#
      Same-length blank copy of a lexed token, keeping \r and \n so line numbers
      in the masked text still line up with the source.
    #>
    param([string] $Token)
    if ($Token.IndexOf([char]10) -lt 0 -and $Token.IndexOf([char]13) -lt 0) {
        return (' ' * $Token.Length)
    }
    return [regex]::Replace($Token, '[^\r\n]', ' ')
}

function ConvertFrom-CSharpEscapes {
    param([string] $Text)
    $builder = New-Object System.Text.StringBuilder($Text.Length)
    $index = 0
    $length = $Text.Length
    while ($index -lt $length) {
        $character = $Text[$index]
        if ([int]$character -ne 92) {
            [void]$builder.Append($character)
            $index++
            continue
        }
        # backslash
        $index++
        if ($index -ge $length) {
            [void]$builder.Append('\')
            break
        }
        $escape = [string]$Text[$index]
        if ($escape -eq 'n') { [void]$builder.Append([char]10); $index++ }
        elseif ($escape -eq 'r') { [void]$builder.Append([char]13); $index++ }
        elseif ($escape -eq 't') { [void]$builder.Append([char]9); $index++ }
        elseif ($escape -eq '0') { [void]$builder.Append([char]0); $index++ }
        elseif ($escape -eq 'a') { [void]$builder.Append([char]7); $index++ }
        elseif ($escape -eq 'b') { [void]$builder.Append([char]8); $index++ }
        elseif ($escape -eq 'f') { [void]$builder.Append([char]12); $index++ }
        elseif ($escape -eq 'v') { [void]$builder.Append([char]11); $index++ }
        elseif ($escape -eq '"') { [void]$builder.Append('"'); $index++ }
        elseif ($escape -eq '\') { [void]$builder.Append('\'); $index++ }
        elseif ($escape -eq "'") { [void]$builder.Append("'"); $index++ }
        elseif ($escape -eq 'u' -or $escape -eq 'U' -or $escape -eq 'x') {
            $digits = 4
            if ($escape -eq 'U') { $digits = 8 }
            if ($escape -eq 'x') { $digits = 4 }
            $available = $length - $index - 1
            if ($available -lt 1) {
                [void]$builder.Append($escape)
                $index++
                continue
            }
            $take = [Math]::Min($digits, $available)
            if ($escape -eq 'x') {
                $take = 0
                while ($take -lt $digits -and ($index + 1 + $take) -lt $length) {
                    $candidate = [string]$Text[$index + 1 + $take]
                    if ($candidate -match '^[0-9A-Fa-f]$') {
                        $take++
                    }
                    else {
                        break
                    }
                }
                if ($take -lt 1) {
                    [void]$builder.Append($escape)
                    $index++
                    continue
                }
            }
            $hex = $Text.Substring($index + 1, $take)
            $codePoint = 0
            $parsed = [int]::TryParse(
                $hex,
                [System.Globalization.NumberStyles]::HexNumber,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [ref] $codePoint)
            if ($parsed -and $codePoint -le 0xFFFF) {
                [void]$builder.Append([char]$codePoint)
                $index = $index + 1 + $take
            }
            else {
                [void]$builder.Append($escape)
                $index++
            }
        }
        else {
            [void]$builder.Append($escape)
            $index++
        }
    }
    return $builder.ToString()
}

function ConvertFrom-CSharpLiteral {
    <#
      Returns @{ Value = decoded text; Ok = $false for interpolated strings }.
    #>
    param(
        [string] $Lexeme,
        [string] $Kind
    )
    if ($Kind -eq 'raw') {
        $inner = $Lexeme
        while ($inner.Length -gt 0 -and $inner.StartsWith('"')) {
            $inner = $inner.Substring(1)
        }
        while ($inner.Length -gt 0 -and $inner.EndsWith('"')) {
            $inner = $inner.Substring(0, $inner.Length - 1)
        }
        return @{ Value = $inner; Ok = $true }
    }
    if ($Kind -eq 'verbatim') {
        if ($Lexeme.Length -lt 3) {
            return @{ Value = ''; Ok = $true }
        }
        $inner = $Lexeme.Substring(2, $Lexeme.Length - 3)
        return @{ Value = $inner.Replace('""', '"'); Ok = $true }
    }
    if ($Kind -eq 'regular') {
        if ($Lexeme.Length -lt 2) {
            return @{ Value = ''; Ok = $true }
        }
        $inner = $Lexeme.Substring(1, $Lexeme.Length - 2)
        return @{ Value = (ConvertFrom-CSharpEscapes -Text $inner); Ok = $true }
    }
    # Interpolated strings are resolved at runtime; their text is not a key.
    return @{ Value = ''; Ok = $false }
}

function New-SourceScan {
    <#
      Returns @{ Code = ...; NoComment = ...; Literals = ... } for one C# file.
      Code has comments and string/char literals blanked (identifier and block
      structure survive); NoComment has only comments blanked, so per-line UI
      checks still see the literals.
    #>
    param([string] $Text)
    $tokens = [regex]::Matches($Text, $script:CSharpTokenPattern)
    $code = New-Object System.Text.StringBuilder($Text.Length)
    $noComment = New-Object System.Text.StringBuilder($Text.Length)
    $literals = New-Object System.Collections.Generic.List[object]
    $position = 0
    foreach ($token in $tokens) {
        if ($token.Index -gt $position) {
            $gap = $Text.Substring($position, $token.Index - $position)
            [void]$code.Append($gap)
            [void]$noComment.Append($gap)
        }
        # The lexer pattern matches exactly one alternative per token, so a group
        # lookup is unnecessary: the spelling of the token identifies the kind.
        # Checking it directly avoids several GroupCollection lookups per token,
        # which dominates the mask build on the large window file.
        $lexeme = $token.Value
        $firstCode = [int]$lexeme[0]
        if ($firstCode -eq 47) {
            $kind = 'comment'
        }
        elseif ($firstCode -eq 39) {
            $kind = 'chr'
        }
        elseif ($lexeme.Length -ge 3 -and $lexeme.StartsWith('"""')) {
            $kind = 'raw'
        }
        elseif ($lexeme.StartsWith('@"')) {
            $kind = 'verbatim'
        }
        elseif ($lexeme.StartsWith('$"')) {
            $kind = 'interp'
        }
        elseif ($lexeme.StartsWith('$@') -or $lexeme.StartsWith('@$')) {
            $kind = 'vinterp'
        }
        else {
            $kind = 'regular'
        }
        $masked = New-BlankToken -Token $lexeme
        [void]$code.Append($masked)
        if ($kind -eq 'comment') {
            [void]$noComment.Append($masked)
        }
        else {
            [void]$noComment.Append($lexeme)
            if ($kind -ne 'chr') {
                $decoded = ConvertFrom-CSharpLiteral -Lexeme $lexeme -Kind $kind
                $literals.Add([pscustomobject]@{
                    Start = $token.Index
                    End = $token.Index + $token.Length
                    Kind = $kind
                    Value = $decoded.Value
                    Ok = $decoded.Ok
                })
            }
        }
        $position = $token.Index + $token.Length
    }
    if ($position -lt $Text.Length) {
        $gap = $Text.Substring($position)
        [void]$code.Append($gap)
        [void]$noComment.Append($gap)
    }
    return @{
        Code = $code.ToString()
        NoComment = $noComment.ToString()
        Literals = $literals
    }
}

function Find-CloseParen {
    param(
        [string] $Code,
        [int] $OpenIndex
    )
    $depth = 0
    $index = $OpenIndex
    $length = $Code.Length
    while ($index -lt $length) {
        $next = $Code.IndexOfAny($script:ParenChars, $index)
        if ($next -lt 0) {
            return -1
        }
        if ($Code[$next] -eq '(') {
            $depth++
        }
        else {
            $depth--
            if ($depth -eq 0) {
                return $next
            }
        }
        $index = $next + 1
    }
    return -1
}

function Get-FirstArgumentEnd {
    <#
      Index of the comma that ends the first argument, or the closing paren.
    #>
    param(
        [string] $Code,
        [int] $OpenIndex,
        [int] $CloseIndex
    )
    $depth = 0
    $index = $OpenIndex + 1
    while ($index -lt $CloseIndex) {
        $character = $Code[$index]
        $number = [int]$character
        if ($number -eq 40 -or $number -eq 91 -or $number -eq 123) {
            $depth++
        }
        elseif ($number -eq 41 -or $number -eq 93 -or $number -eq 125) {
            if ($depth -gt 0) {
                $depth--
            }
        }
        elseif ($number -eq 44 -and $depth -eq 0) {
            return $index
        }
        $index++
    }
    return $CloseIndex
}

function Get-LiteralsInRange {
    <#
      Literals fully inside [Start, End). The literal list is ordered by offset,
      so a binary search finds the window instead of scanning every literal of a
      large file once per call site.
    #>
    param(
        $Literals,
        [int] $Start,
        [int] $End
    )
    $found = New-Object System.Collections.Generic.List[object]
    $count = $Literals.Count
    $low = 0
    $high = $count - 1
    $first = $count
    while ($low -le $high) {
        $middle = [int][Math]::Floor(($low + $high) / 2)
        if ($Literals[$middle].Start -ge $Start) {
            $first = $middle
            $high = $middle - 1
        }
        else {
            $low = $middle + 1
        }
    }
    for ($index = $first; $index -lt $count; $index++) {
        $literal = $Literals[$index]
        if ($literal.End -gt $End) {
            break
        }
        $found.Add($literal)
    }
    return , $found
}

function Get-PlaceholderSet {
    <#
      Composite format placeholders of a string as "{0},{2}" (or "(none)").
      {{ and }} are escapes and are ignored; alignment and format specifiers
      ({0:N2}, {1,6}) contribute just their index.
    #>
    param([string] $Text)
    # Most entries have no placeholder at all; one scan answers that immediately.
    if ($Text.IndexOf('{') -lt 0) {
        return '(none)'
    }
    $indices = New-Object System.Collections.Generic.SortedSet[int]
    $index = 0
    $length = $Text.Length
    while ($index -lt $length) {
        $character = $Text[$index]
        if ($character -eq '{') {
            if (($index + 1) -lt $length -and $Text[$index + 1] -eq '{') {
                $index = $index + 2
                continue
            }
            $close = $Text.IndexOf([char]125, $index + 1)
            if ($close -lt 0) {
                break
            }
            $inner = $Text.Substring($index + 1, $close - $index - 1)
            $match = [regex]::Match($inner, '^\s*(\d+)')
            if ($match.Success) {
                $number = 0
                if ([int]::TryParse($match.Groups[1].Value, [ref] $number)) {
                    [void]$indices.Add($number)
                }
            }
            $index = $close + 1
            continue
        }
        if ($character -eq '}') {
            if (($index + 1) -lt $length -and $Text[$index + 1] -eq '}') {
                $index = $index + 2
                continue
            }
            $index++
            continue
        }
        $index++
    }
    if ($indices.Count -eq 0) {
        return '(none)'
    }
    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($number in $indices) {
        $parts.Add(('{' + $number + '}'))
    }
    return ($parts -join ',')
}

function ConvertFrom-XmlEntity {
    param([string] $Name)
    if ($Name -eq 'amp') { return '&' }
    if ($Name -eq 'lt') { return '<' }
    if ($Name -eq 'gt') { return '>' }
    if ($Name -eq 'quot') { return '"' }
    if ($Name -eq 'apos') { return "'" }
    if ($Name.StartsWith('#x') -or $Name.StartsWith('#X')) {
        $codePoint = 0
        $parsed = [int]::TryParse(
            $Name.Substring(2),
            [System.Globalization.NumberStyles]::HexNumber,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref] $codePoint)
        if ($parsed -and $codePoint -gt 0 -and $codePoint -le 0xFFFF) {
            return ([string][char]$codePoint)
        }
        return $null
    }
    if ($Name.StartsWith('#')) {
        $codePoint = 0
        if ([int]::TryParse($Name.Substring(1), [ref] $codePoint) -and
            $codePoint -gt 0 -and $codePoint -le 0xFFFF) {
            return ([string][char]$codePoint)
        }
        return $null
    }
    return $null
}

function Expand-XmlEntities {
    param([string] $Text)
    if ($Text.IndexOf('&') -lt 0) {
        return $Text
    }
    $builder = New-Object System.Text.StringBuilder($Text.Length)
    $index = 0
    while ($index -lt $Text.Length) {
        $character = $Text[$index]
        if ($character -eq '&') {
            $semicolon = $Text.IndexOf([char]59, $index + 1)
            if ($semicolon -gt $index) {
                $entity = $Text.Substring($index + 1, $semicolon - $index - 1)
                $decoded = ConvertFrom-XmlEntity -Name $entity
                if ($null -ne $decoded) {
                    [void]$builder.Append($decoded)
                    $index = $semicolon + 1
                    continue
                }
            }
        }
        [void]$builder.Append($character)
        $index++
    }
    return $builder.ToString()
}

function Read-JsonString {
    <#
      Reads one JSON string starting at the opening quote. Returns
      @{ Ok; Value; Position; Error }.

      The scan jumps from one backslash to the next with IndexOf and appends whole
      runs, instead of walking character by character: catalog files are mostly
      plain ASCII keys and CJK values without escapes, and Windows PowerShell 5.1
      charges a lot for a per-character loop.
    #>
    param(
        [string] $Text,
        [int] $Start
    )
    if ($Start -ge $Text.Length -or $Text[$Start] -ne '"') {
        return @{ Ok = $false; Value = ''; Position = $Start; Error = 'expected a quoted string' }
    }
    $length = $Text.Length
    $index = $Start + 1
    $builder = $null
    while ($index -lt $length) {
        $quote = $Text.IndexOf('"', $index)
        if ($quote -lt 0) {
            return @{ Ok = $false; Value = ''; Position = $index; Error = 'unterminated string' }
        }
        $backslash = $Text.IndexOf('\', $index)
        if ($backslash -lt 0 -or $backslash -gt $quote) {
            $chunk = $Text.Substring($index, $quote - $index)
            if ($null -eq $builder) {
                return @{ Ok = $true; Value = $chunk; Position = $quote + 1; Error = '' }
            }
            [void]$builder.Append($chunk)
            return @{ Ok = $true; Value = $builder.ToString(); Position = $quote + 1; Error = '' }
        }
        if ($null -eq $builder) {
            $builder = New-Object System.Text.StringBuilder
        }
        [void]$builder.Append($Text.Substring($index, $backslash - $index))
        $index = $backslash + 1
        if ($index -ge $length) {
            break
        }
        $escape = [string]$Text[$index]
        if ($escape -eq '"') { [void]$builder.Append('"'); $index++ }
        elseif ($escape -eq '\') { [void]$builder.Append('\'); $index++ }
        elseif ($escape -eq '/') { [void]$builder.Append('/'); $index++ }
        elseif ($escape -eq 'b') { [void]$builder.Append([char]8); $index++ }
        elseif ($escape -eq 'f') { [void]$builder.Append([char]12); $index++ }
        elseif ($escape -eq 'n') { [void]$builder.Append([char]10); $index++ }
        elseif ($escape -eq 'r') { [void]$builder.Append([char]13); $index++ }
        elseif ($escape -eq 't') { [void]$builder.Append([char]9); $index++ }
        elseif ($escape -eq 'u') {
            if (($index + 4) -lt $length) {
                $hex = $Text.Substring($index + 1, 4)
                $codePoint = 0
                $parsed = [int]::TryParse(
                    $hex,
                    [System.Globalization.NumberStyles]::HexNumber,
                    [System.Globalization.CultureInfo]::InvariantCulture,
                    [ref] $codePoint)
                if (-not $parsed) {
                    return @{ Ok = $false; Value = ''; Position = $index; Error = 'invalid \u escape' }
                }
                [void]$builder.Append([char]$codePoint)
                $index = $index + 5
            }
            else {
                return @{ Ok = $false; Value = ''; Position = $index; Error = 'truncated \u escape' }
            }
        }
        else {
            return @{
                Ok = $false
                Value = ''
                Position = $index
                Error = ('invalid escape "\' + $escape + '"')
            }
        }
    }
    return @{ Ok = $false; Value = ''; Position = $index; Error = 'unterminated string' }
}

function Read-CatalogEntries {
    <#
      Minimal reader for the catalog shape (one JSON object of string -> string).
      Returns @{ Entries = list of @{ Key; Value; IsString }; Errors = list }.
      It is strict on purpose: duplicate keys are valid JSON but a packaging bug,
      and ConvertFrom-Json would silently keep the last value.
    #>
    param([string] $Text)
    $entries = New-Object System.Collections.Generic.List[object]
    $problems = New-Object System.Collections.Generic.List[string]
    $length = $Text.Length
    $position = 0
    if ($length -gt 0 -and [int]$Text[0] -eq 0xFEFF) {
        $position = 1
    }
    while ($position -lt $length -and [char]::IsWhiteSpace($Text[$position])) {
        $position++
    }
    if ($position -ge $length -or $Text[$position] -ne '{') {
        $problems.Add('top-level JSON value is not an object')
        return @{ Entries = $entries; Errors = $problems }
    }
    $position++
    while ($true) {
        while ($position -lt $length -and [char]::IsWhiteSpace($Text[$position])) {
            $position++
        }
        if ($position -ge $length) {
            $problems.Add('unexpected end of file inside the object')
            break
        }
        if ($Text[$position] -eq '}') {
            $position++
            break
        }
        $key = Read-JsonString -Text $Text -Start $position
        if (-not $key.Ok) {
            $problems.Add(('key: ' + $key.Error))
            return @{ Entries = $entries; Errors = $problems }
        }
        $position = $key.Position
        while ($position -lt $length -and [char]::IsWhiteSpace($Text[$position])) {
            $position++
        }
        if ($position -ge $length -or $Text[$position] -ne ':') {
            $problems.Add('expected ":" after a key')
            return @{ Entries = $entries; Errors = $problems }
        }
        $position++
        while ($position -lt $length -and [char]::IsWhiteSpace($Text[$position])) {
            $position++
        }
        if ($position -ge $length) {
            $problems.Add('unexpected end of file after ":"')
            break
        }
        if ($Text[$position] -ne '"') {
            $problems.Add((
                'value for key "' + (Format-KeyText -Text $key.Value -MaxLength 120) +
                '" is not a JSON string'))
            return @{ Entries = $entries; Errors = $problems }
        }
        $value = Read-JsonString -Text $Text -Start $position
        if (-not $value.Ok) {
            $problems.Add(('value: ' + $value.Error))
            return @{ Entries = $entries; Errors = $problems }
        }
        $position = $value.Position
        $entries.Add([pscustomobject]@{
            Key = $key.Value
            Value = $value.Value
            IsString = $true
        })
        while ($position -lt $length -and [char]::IsWhiteSpace($Text[$position])) {
            $position++
        }
        if ($position -ge $length) {
            $problems.Add('unexpected end of file inside the object')
            break
        }
        if ($Text[$position] -eq ',') {
            $position++
            continue
        }
        if ($Text[$position] -eq '}') {
            $position++
            break
        }
        $problems.Add('expected "," or "}" between entries')
        return @{ Entries = $entries; Errors = $problems }
    }
    while ($position -lt $length -and [char]::IsWhiteSpace($Text[$position])) {
        $position++
    }
    if ($position -lt $length) {
        $problems.Add('trailing content after the top-level object')
    }
    return @{ Entries = $entries; Errors = $problems }
}

function Add-UsedKey {
    param(
        [string] $Key,
        [string] $File,
        [int] $Line,
        [string] $Origin
    )
    if ([string]::IsNullOrEmpty($Key)) {
        return
    }
    $location = ('{0}:{1}' -f $File, $Line)
    if ($script:Used.ContainsKey($Key)) {
        $entry = $script:Used[$Key]
        if (-not $entry.Locations.Contains($location)) {
            $entry.Locations.Add($location)
        }
        $entry.Count = $entry.Count + 1
        return
    }
    $item = [pscustomobject]@{
        Key = $Key
        Locations = (New-Object System.Collections.Generic.List[string])
        Count = 1
        Origin = $Origin
    }
    $item.Locations.Add($location)
    $script:Used[$Key] = $item
}

function New-Warning {
    param(
        [string] $Tag,
        [string] $Message
    )
    $script:Warnings[$Tag].Add($Message)
}

function Write-Category {
    param(
        [string] $Title,
        $Items
    )
    if ($null -eq $Items -or $Items.Count -eq 0) {
        return
    }
    Write-Host ('  {0} {1}' -f $Title, $Items.Count)
    $limit = [Math]::Min($Items.Count, $MaxPerSection)
    for ($index = 0; $index -lt $limit; $index++) {
        Write-Host ('    ' + $Items[$index])
    }
    if ($Items.Count -gt $limit) {
        Write-Host ('    ... {0} more' -f ($Items.Count - $limit))
    }
}

# ---------------------------------------------------------------- locate the repo

if ([string]::IsNullOrWhiteSpace($Root)) {
    $scriptRoot = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptRoot)) {
        $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    $Root = Split-Path -Parent $scriptRoot
}
$Root = $Root.TrimEnd('\', '/')
if ([string]::IsNullOrWhiteSpace($Root)) {
    Write-Host 'ERROR: could not determine the repository root; pass -Root <path>.'
    exit 1
}
$script:RootPrefix = $Root + [System.IO.Path]::DirectorySeparatorChar

$windowsRoot = Join-Path $Root $script:WindowsRelative
$catalogDirectory = Join-Path $Root $script:CatalogRelative

if (-not (Test-Path -LiteralPath $windowsRoot)) {
    Write-Host ('ERROR: {0} not found under "{1}".' -f $script:WindowsRelative, $Root)
    Write-Host 'This script must live in <repo>/tools and be run from a keepassity checkout.'
    exit 1
}

Write-Host 'keepassity localization check'
Write-Host ('  repo: {0}' -f $Root.Replace('\', '/'))

# ---------------------------------------------------------------- catalogs

$script:Catalog = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::Ordinal)
$errorsStructural = New-Object System.Collections.Generic.List[string]
$errorsMissing = New-Object System.Collections.Generic.List[string]
$errorsPlaceholder = New-Object System.Collections.Generic.List[string]
$script:Used = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::Ordinal)
# Literals that appear in a key position but inside an expression we cannot
# resolve (Loc.T(flag ? "locked" : "unlocked")). They keep a catalog entry from
# being reported as stale, but they are not checked as keys.
$script:Referenced = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
# Keys already reported as duplicates; they are not also reported as stale, which
# would be noise on top of the error.
$script:DuplicateKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
$script:Warnings = @{
    stale = (New-Object System.Collections.Generic.List[string])
    untranslated = (New-Object System.Collections.Generic.List[string])
    unwrapped = (New-Object System.Collections.Generic.List[string])
    dynamic = (New-Object System.Collections.Generic.List[string])
}

$catalogFiles = @()
if (Test-Path -LiteralPath $catalogDirectory) {
    $catalogFiles = @(
        Get-ChildItem -LiteralPath $catalogDirectory -File -ErrorAction Stop |
        Where-Object { $_.Name.ToLowerInvariant().EndsWith($script:CatalogSuffix) } |
        Sort-Object -Property Name)
}

if ($catalogFiles.Count -eq 0) {
    $errorsStructural.Add((
        '[catalog] no *{0} catalog found in {1}' -f $script:CatalogSuffix, $script:CatalogRelative))
}

foreach ($catalogFile in $catalogFiles) {
    $relative = Get-RelativePath -FullName $catalogFile.FullName
    $text = Read-SourceText -Path $catalogFile.FullName

    # The reader understands this catalog shape (one object of string -> string),
    # so it produces precise messages and can see duplicate keys, which
    # ConvertFrom-Json silently collapses to the last value.
    $read = Read-CatalogEntries -Text $text
    if ($read.Errors.Count -gt 0) {
        foreach ($problem in $read.Errors) {
            $errorsStructural.Add(('[{0}] {1}: {2}' -f 'json', $relative, $problem))
        }
        continue
    }

    # Empty keys and values are rejected first: an empty property name makes
    # ConvertFrom-Json throw an opaque error instead of naming the problem.
    $entryProblems = 0
    foreach ($entry in $read.Entries) {
        if ([string]::IsNullOrEmpty($entry.Key)) {
            $errorsStructural.Add(('[{0}] {1}: a catalog key is empty' -f 'empty-key', $relative))
            $entryProblems++
            continue
        }
        if ([string]::IsNullOrEmpty($entry.Value)) {
            $errorsStructural.Add((
                '[empty-value] {0}: the translation of "{1}" is empty' -f
                $relative, (Format-KeyText -Text $entry.Key -MaxLength 120)))
            $entryProblems++
        }
    }

    # Second opinion on JSON validity when nothing above already explained the
    # problem; ConvertFrom-Json is strict about escapes and edge syntax.
    $jsonValid = $true
    if ($entryProblems -eq 0) {
        try {
            $parsed = ConvertFrom-Json -InputObject $text -ErrorAction Stop
            if ($parsed -isnot [System.Management.Automation.PSCustomObject]) {
                $errorsStructural.Add(('[{0}] {1}: top-level JSON value is not a single object' -f 'json', $relative))
                $jsonValid = $false
            }
        }
        catch {
            $errorsStructural.Add((
                '[json] {0}: {1}' -f $relative, (Format-ReportText -Text $_.Exception.Message)))
            $jsonValid = $false
        }
    }
    if (-not $jsonValid) {
        continue
    }

    foreach ($entry in $read.Entries) {
        if ([string]::IsNullOrEmpty($entry.Key) -or [string]::IsNullOrEmpty($entry.Value)) {
            continue
        }
        if ($script:Catalog.ContainsKey($entry.Key)) {
            $existing = $script:Catalog[$entry.Key]
            [void]$script:DuplicateKeys.Add($entry.Key)
            $errorsStructural.Add((
                '[duplicate] "{0}" is defined in {1} and {2}' -f
                (Format-KeyText -Text $entry.Key -MaxLength 120), $existing.File, $relative))
            continue
        }
        $script:Catalog[$entry.Key] = [pscustomobject]@{
            Key = $entry.Key
            Value = $entry.Value
            File = $relative
        }
    }
}

# ---------------------------------------------------------------- sources

$sourceFiles = @(Get-SourceFiles -BaseDirectory $windowsRoot)
$csFiles = @($sourceFiles | Where-Object { $_.Extension -eq '.cs' } | Sort-Object -Property FullName)
$xamlFiles = @($sourceFiles | Where-Object { $_.Extension -eq '.xaml' } | Sort-Object -Property FullName)

$countLocT = 0
$countLocTLong = 0
$countInternalT = 0
$countXaml = 0
$countSync = 0

foreach ($file in $csFiles) {
    $relative = Get-RelativePath -FullName $file.FullName
    $text = Read-SourceText -Path $file.FullName
    $scan = New-SourceScan -Text $text
    $code = $scan.Code
    $lineStarts = Get-LineStarts -Text $text
    $inLocalizationFolder = $relative.Contains('/Localization/')

    # ---- Loc.T: the first argument carries the catalog key.
    foreach ($hit in $script:RegexLocT.Matches($code)) {
        $line = Get-LineNumber -LineStarts $lineStarts -Index $hit.Index
        $openIndex = $hit.Index + $hit.Length - 1
        $closeIndex = Find-CloseParen -Code $code -OpenIndex $openIndex
        if ($closeIndex -lt 0) {
            New-Warning 'dynamic' ('[{0}] {1}:{2}: Loc.T( call is not closed on this line' -f 'dynamic', $relative, $line)
            continue
        }
        $argumentEnd = Get-FirstArgumentEnd -Code $code -OpenIndex $openIndex -CloseIndex $closeIndex
        $argumentCode = $code.Substring($openIndex + 1, $argumentEnd - $openIndex - 1)
        $remainder = ($argumentCode -replace '\s', '').Replace('+', '')
        $siteLiterals = Get-LiteralsInRange -Literals $scan.Literals -Start ($openIndex + 1) -End $argumentEnd
        if ($remainder.Length -eq 0 -and $siteLiterals.Count -gt 0) {
            $resolved = $true
            $parts = New-Object System.Collections.Generic.List[string]
            foreach ($literal in $siteLiterals) {
                if (-not $literal.Ok) {
                    $resolved = $false
                    break
                }
                $parts.Add($literal.Value)
            }
            if (-not $resolved) {
                New-Warning 'dynamic' (Format-DynamicWarning -Relative $relative -Line $line `
                    -Literals $siteLiterals -Text $text -LineStarts $lineStarts)
                continue
            }
            $key = ($parts -join '')
            if ($key.Length -eq 0) {
                New-Warning 'dynamic' ('[{0}] {1}:{2}: Loc.T("") has an empty key' -f 'dynamic', $relative, $line)
                continue
            }
            Add-UsedKey -Key $key -File $relative -Line $line -Origin 'Loc.T'
            $countLocT++
            continue
        }
        # Loc.cs/LocMarkup.cs intentionally forward a variable key (the attached
        # properties call Loc.T with the element's registered key), and the sync
        # label forwards its stored source string; neither is a missing key.
        if ($inLocalizationFolder -or $argumentCode -match '_syncStatusSource') {
            continue
        }
        $lineText = Get-LineText -Text $text -LineStarts $lineStarts -LineNumber $line -MaxLength 400
        $conditional = $script:RegexConditionalKey.Match($lineText)
        if ($conditional.Success) {
            Add-UsedKey -Key $conditional.Groups['yes'].Value -File $relative -Line $line -Origin 'Loc.T'
            Add-UsedKey -Key $conditional.Groups['no'].Value -File $relative -Line $line -Origin 'Loc.T'
            $countLocT += 2
            continue
        }
        New-Warning 'dynamic' (Format-DynamicWarning -Relative $relative -Line $line `
            -Literals $siteLiterals -Text $text -LineStarts $lineStarts)
    }

    # ---- Loc.TLong: the first argument is a symbolic key.
    foreach ($hit in $script:RegexLocTLong.Matches($code)) {
        $line = Get-LineNumber -LineStarts $lineStarts -Index $hit.Index
        $openIndex = $hit.Index + $hit.Length - 1
        $closeIndex = Find-CloseParen -Code $code -OpenIndex $openIndex
        if ($closeIndex -lt 0) {
            New-Warning 'dynamic' ('[{0}] {1}:{2}: Loc.TLong( call is not closed on this line' -f 'dynamic', $relative, $line)
            continue
        }
        $argumentEnd = Get-FirstArgumentEnd -Code $code -OpenIndex $openIndex -CloseIndex $closeIndex
        $argumentCode = $code.Substring($openIndex + 1, $argumentEnd - $openIndex - 1)
        $remainder = ($argumentCode -replace '\s', '').Replace('+', '')
        $siteLiterals = Get-LiteralsInRange -Literals $scan.Literals -Start ($openIndex + 1) -End $argumentEnd
        if ($remainder.Length -eq 0 -and $siteLiterals.Count -gt 0) {
            $resolved = $true
            $key = ''
            foreach ($literal in $siteLiterals) {
                if (-not $literal.Ok) {
                    $resolved = $false
                    break
                }
                $key = $key + $literal.Value
            }
            if (-not $resolved) {
                New-Warning 'dynamic' (Format-DynamicWarning -Relative $relative -Line $line `
                    -Literals $siteLiterals -Text $text -LineStarts $lineStarts)
                continue
            }
            if ($key.Length -eq 0) {
                New-Warning 'dynamic' ('[{0}] {1}:{2}: Loc.TLong("", ...) has an empty key' -f 'dynamic', $relative, $line)
                continue
            }
            Add-UsedKey -Key $key -File $relative -Line $line -Origin 'Loc.TLong'
            $countLocTLong++
            continue
        }
        # Loc.cs/LocMarkup.cs/LocText.cs intentionally forward a variable key
        # (the shell's view-model text adapter and the XAML attached properties
        # pass a key that was registered by the caller); that is not a missing key.
        if ($inLocalizationFolder) {
            continue
        }
        New-Warning 'dynamic' (Format-DynamicWarning -Relative $relative -Line $line `
            -Literals $siteLiterals -Text $text -LineStarts $lineStarts)
    }

    # ---- SetSyncStatus: every literal in the first argument is a catalog key.
    # The status method stores its source string and renders it later, so the
    # literals can be a direct string, a conditional expression, or nothing at
    # all (a variable passed through from another code path).
    foreach ($hit in $script:RegexSetSyncStatus.Matches($code)) {
        $openIndex = $hit.Index + $hit.Length - 1
        $closeIndex = Find-CloseParen -Code $code -OpenIndex $openIndex
        if ($closeIndex -lt 0) {
            continue
        }
        $line = Get-LineNumber -LineStarts $lineStarts -Index $hit.Index
        $argumentEnd = Get-FirstArgumentEnd -Code $code -OpenIndex $openIndex -CloseIndex $closeIndex
        $argumentCode = $code.Substring($openIndex + 1, $argumentEnd - $openIndex - 1)
        if ($argumentCode -match 'Loc\.') {
            # Wrapped in Loc.T already; that call site was handled above.
            continue
        }
        $siteLiterals = Get-LiteralsInRange -Literals $scan.Literals -Start ($openIndex + 1) -End $argumentEnd
        foreach ($literal in $siteLiterals) {
            if (-not $literal.Ok) {
                New-Warning 'dynamic' ('[{0}] {1}:{2}: interpolated string as the sync status: {3}' -f
                    'dynamic', $relative, $line, (Get-LineText -Text $text -LineStarts $lineStarts -LineNumber $line))
                continue
            }
            if ($literal.Value.Length -eq 0) {
                continue
            }
            Add-UsedKey -Key $literal.Value -File $relative -Line $line -Origin 'SetSyncStatus'
            $countSync++
        }
    }

    # ---- CommitSyncedAsync: its status argument is a catalog key, rendered by
    # SetSyncStatus(status) later. Only literals inside the call are keys (the
    # other arguments are values/variables).
    foreach ($hit in $script:RegexCommitSynced.Matches($code)) {
        $openIndex = $hit.Index + $hit.Length - 1
        $closeIndex = Find-CloseParen -Code $code -OpenIndex $openIndex
        if ($closeIndex -lt 0) {
            continue
        }
        $line = Get-LineNumber -LineStarts $lineStarts -Index $hit.Index
        $siteLiterals = Get-LiteralsInRange -Literals $scan.Literals `
            -Start ($openIndex + 1) -End $closeIndex
        foreach ($literal in $siteLiterals) {
            if (-not $literal.Ok -or $literal.Value.Length -eq 0) {
                continue
            }
            Add-UsedKey -Key $literal.Value -File $relative -Line $line `
                -Origin 'CommitSyncedAsync'
            $countSync++
        }
    }

    # ---- Bare T()/TLong() inside the Localization folder (Loc.cs translates its
    # own language-name constants). Elsewhere a bare T() is not the catalog API.
    if ($inLocalizationFolder) {
        foreach ($hit in $script:RegexBareT.Matches($code)) {
            $openIndex = $hit.Index + $hit.Length - 1
            $closeIndex = Find-CloseParen -Code $code -OpenIndex $openIndex
            if ($closeIndex -lt 0) {
                continue
            }
            $argumentEnd = Get-FirstArgumentEnd -Code $code -OpenIndex $openIndex -CloseIndex $closeIndex
            $argumentCode = $code.Substring($openIndex + 1, $argumentEnd - $openIndex - 1)
            $remainder = ($argumentCode -replace '\s', '').Replace('+', '')
            if ($remainder.Length -ne 0) {
                # Declarations (T(string english)) and variable lookups are not keys.
                continue
            }
            $siteLiterals = Get-LiteralsInRange -Literals $scan.Literals -Start ($openIndex + 1) -End $argumentEnd
            if ($siteLiterals.Count -eq 0) {
                continue
            }
            $resolved = $true
            $parts = New-Object System.Collections.Generic.List[string]
            foreach ($literal in $siteLiterals) {
                if (-not $literal.Ok) {
                    $resolved = $false
                    break
                }
                $parts.Add($literal.Value)
            }
            if (-not $resolved) {
                continue
            }
            $key = ($parts -join '')
            if ($key.Length -eq 0) {
                continue
            }
            $line = Get-LineNumber -LineStarts $lineStarts -Index $hit.Index
            Add-UsedKey -Key $key -File $relative -Line $line -Origin 'T'
            $countInternalT++
        }
    }

    # ---- Unwrapped UI literals: review candidates, not failures. The UI pattern
    # is matched once over the whole file (a single regex pass instead of one per
    # line) and the matching lines are then filtered with cheap ordinal checks.
    $noCommentLines = $scan.NoComment -split "\r?\n"
    $candidateLines = New-Object System.Collections.Generic.SortedSet[int]
    foreach ($patternHit in [regex]::Matches($scan.NoComment, $script:UiLiteralPattern)) {
        $lineNumber = Get-LineNumber -LineStarts $lineStarts -Index $patternHit.Index
        [void]$candidateLines.Add($lineNumber)
    }
    foreach ($lineNumber in $candidateLines) {
        $lineText = $noCommentLines[$lineNumber - 1]
        if ($lineText.IndexOf('"') -lt 0 -or
            $lineText.IndexOf('Loc.') -ge 0 -or
            $lineText.IndexOf('AppLog.') -ge 0 -or
            $lineText.IndexOf('[DllImport') -ge 0 -or
            $lineText.IndexOf('JsonPropertyName') -ge 0) {
            continue
        }
        if (Test-UnwrappedAllowlisted -LineText $lineText) {
            continue
        }
        $display = Format-ReportText -Text $lineText.Trim() -MaxLength 180
        New-Warning 'unwrapped' ('[{0}] {1}:{2}: {3}' -f 'unwrapped', $relative, $lineNumber, $display)
    }
}

# ---- XAML attached properties.
foreach ($file in $xamlFiles) {
    $relative = Get-RelativePath -FullName $file.FullName
    $text = Read-SourceText -Path $file.FullName
    $lineStarts = Get-LineStarts -Text $text
    # Comments are blanked in place so offsets (and therefore line numbers) hold.
    $masked = $text
    $comments = $script:XmlCommentPattern.Matches($text)
    if ($comments.Count -gt 0) {
        $builder = New-Object System.Text.StringBuilder($text.Length)
        $position = 0
        foreach ($comment in $comments) {
            if ($comment.Index -gt $position) {
                [void]$builder.Append($text.Substring($position, $comment.Index - $position))
            }
            [void]$builder.Append((New-BlankToken -Token $comment.Value))
            $position = $comment.Index + $comment.Length
        }
        if ($position -lt $text.Length) {
            [void]$builder.Append($text.Substring($position))
        }
        $masked = $builder.ToString()
    }
    $pattern = [regex]$script:XamlAttributePattern
    foreach ($hit in $pattern.Matches($masked)) {
        $line = Get-LineNumber -LineStarts $lineStarts -Index $hit.Index
        $value = ''
        if ($hit.Groups['dq'].Success) {
            $value = $hit.Groups['dq'].Value
        }
        else {
            $value = $hit.Groups['sq'].Value
        }
        if ($value.StartsWith('{}')) {
            $value = $value.Substring(2)
        }
        elseif ($value.StartsWith('{')) {
            New-Warning 'dynamic' ('[{0}] {1}:{2}: markup extension in loc:{3}, not checked' -f
                'dynamic', $relative, $line, $hit.Groups['prop'].Value)
            continue
        }
        $decoded = Expand-XmlEntities -Text $value
        if ($decoded.Length -eq 0) {
            New-Warning 'dynamic' ('[{0}] {1}:{2}: empty loc:{3} value' -f
                'dynamic', $relative, $line, $hit.Groups['prop'].Value)
            continue
        }
        Add-UsedKey -Key $decoded -File $relative -Line $line -Origin 'XAML'
        $countXaml++
    }
}

# ---------------------------------------------------------------- checks

$catalogKeys = New-Object System.Collections.Generic.List[string]
foreach ($key in $script:Catalog.Keys) {
    $catalogKeys.Add($key)
}
$catalogKeys.Sort([System.StringComparer]::Ordinal)

# Placeholder parity and untranslated entries, in catalog order.
foreach ($key in $catalogKeys) {
    $entry = $script:Catalog[$key]
    $keyPlaceholders = Get-PlaceholderSet -Text $key
    $translationPlaceholders = Get-PlaceholderSet -Text $entry.Value
    if ($keyPlaceholders -ne $translationPlaceholders) {
        $errorsPlaceholder.Add((
            '[placeholder] "{0}" ({1}): key placeholders {2} but translation placeholders {3}' -f
            (Format-KeyText -Text $key -MaxLength 140), $entry.File,
            $keyPlaceholders, $translationPlaceholders))
    }
    if ($entry.Value -ceq $key) {
        $allowed = $false
        foreach ($allowlisted in $script:UntranslatedAllowlist) {
            if ($allowlisted -ceq $key) {
                $allowed = $true
                break
            }
        }
        if (-not $allowed) {
            New-Warning 'untranslated' ('[{0}] "{1}" ({2}): translation is identical to the English key' -f
                'untranslated', (Format-KeyText -Text $key -MaxLength 140), $entry.File)
        }
    }
    # Product-name convention: UI text spells the product "Keepassity"; the
    # lowercase form is reserved for file/folder names, environment variables,
    # identifiers and URLs. Catalog keys and values are UI text.
    if ($key -cmatch 'keepassity') {
        $errorsStructural.Add((
            '[naming] "{0}" ({1}): lowercase product name in a UI catalog key; use "Keepassity"' -f
            (Format-KeyText -Text $key -MaxLength 160), $entry.File))
    }
    if ($entry.Value -cmatch 'keepassity') {
        $errorsStructural.Add((
            '[naming] translation of "{0}" ({1}): lowercase product name in UI text; use "Keepassity"' -f
            (Format-KeyText -Text $key -MaxLength 160), $entry.File))
    }
}

# Used keys without a translation, and catalog entries nobody uses.
$usedKeys = New-Object System.Collections.Generic.List[string]
foreach ($key in $script:Used.Keys) {
    $usedKeys.Add($key)
}
$usedKeys.Sort([System.StringComparer]::Ordinal)

foreach ($key in $usedKeys) {
    if ($script:Catalog.ContainsKey($key)) {
        continue
    }
    $entry = $script:Used[$key]
    $suffix = ''
    if ($entry.Locations.Count -gt 1) {
        $suffix = (' (and {0} more site(s))' -f ($entry.Locations.Count - 1))
    }
    $errorsMissing.Add((
        '[missing] {0}: no zh-CN translation for "{1}"{2}' -f
        $entry.Locations[0], (Format-KeyText -Text $key -MaxLength 160), $suffix))
}

foreach ($key in $catalogKeys) {
    if ($script:Used.ContainsKey($key) -or $script:Referenced.Contains($key)) {
        continue
    }
    if ($script:DuplicateKeys.Contains($key)) {
        continue
    }
    $entry = $script:Catalog[$key]
    New-Warning 'stale' ('[{0}] "{1}" ({2}): no source reference' -f
        'stale', (Format-KeyText -Text $key -MaxLength 140), $entry.File)
}

# ---------------------------------------------------------------- report

Write-Host ('  catalogs: {0} file(s), {1} entries' -f $catalogFiles.Count, $script:Catalog.Count)
Write-Host ('  sources : {0} .cs, {1} .xaml' -f $csFiles.Count, $xamlFiles.Count)
Write-Host ('  usage   : {0} Loc.T, {1} Loc.TLong, {2} T() in Localization, {3} XAML attribute(s), {4} sync-status literal(s)' -f
    $countLocT, $countLocTLong, $countInternalT, $countXaml, $countSync)
Write-Host ''

$errorLists = @(
    @{ Title = 'structural'; Items = $errorsStructural },
    @{ Title = 'missing'; Items = $errorsMissing },
    @{ Title = 'placeholder'; Items = $errorsPlaceholder }
)
$errorCount = $errorsStructural.Count + $errorsMissing.Count + $errorsPlaceholder.Count

if ($errorCount -eq 0) {
    Write-Host 'ERRORS: none'
}
else {
    Write-Host ('ERRORS ({0})' -f $errorCount)
    foreach ($group in $errorLists) {
        if ($group.Items.Count -eq 0) {
            continue
        }
        $limit = [Math]::Min($group.Items.Count, $MaxPerSection)
        for ($index = 0; $index -lt $limit; $index++) {
            Write-Host ('  ' + $group.Items[$index])
        }
        if ($group.Items.Count -gt $limit) {
            Write-Host ('  ... {0} more {1} error(s)' -f ($group.Items.Count - $limit), $group.Title)
        }
    }
}

$warningOrder = @('stale', 'untranslated', 'unwrapped', 'dynamic')
$warningCount = 0
foreach ($tag in $warningOrder) {
    $warningCount = $warningCount + $script:Warnings[$tag].Count
}

Write-Host ''
if ($warningCount -eq 0) {
    Write-Host 'WARNINGS: none'
}
else {
    Write-Host ('WARNINGS ({0})' -f $warningCount)
    foreach ($tag in $warningOrder) {
        Write-Category -Title $tag -Items $script:Warnings[$tag]
    }
}

Write-Host ''
Write-Host ('summary: {0} error(s), {1} warning(s)' -f $errorCount, $warningCount)
if ($errorCount -gt 0) {
    Write-Host 'RESULT: FAIL'
    exit 1
}
if ($Strict -and $warningCount -gt 0) {
    Write-Host 'RESULT: FAIL (-Strict treats warnings as errors)'
    exit 1
}
if ($warningCount -gt 0) {
    Write-Host 'RESULT: PASS (warnings only; rerun with -Strict to fail on them)'
}
else {
    Write-Host 'RESULT: PASS'
}
exit 0
