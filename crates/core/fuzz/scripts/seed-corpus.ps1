# SPDX-License-Identifier: GPL-3.0-or-later
# Seeds the cargo-fuzz corpus for the keepassity-core fuzz targets.
#
# IMPORTANT: the corpus is written OUTSIDE the repository (never into
# crates/core/fuzz/corpus). The repository is only allowed to contain `.kdbx`
# files under tests/fixtures, and fuzz corpora are disposable generated state,
# so they must not live in the repo tree.
#
# Default corpus root, first match wins:
#   1. -CorpusDir <path> argument
#   2. $env:KEEPASSITY_FUZZ_CORPUS
#   3. <CARGO_TARGET_DIR>/../fuzz-corpus  (i.e. out/fuzz-corpus)
#   4. <workspace>/out/fuzz-corpus
#
# Run it before `cargo fuzz run` (Linux/macOS/CI use `pwsh`; Windows PowerShell
# 5.1 uses `powershell`):
#
#   pwsh -File scripts/seed-corpus.ps1
#   pwsh -File scripts/seed-corpus.ps1 -CorpusDir /tmp/keepassity-fuzz-corpus
#
# It only copies public test fixtures and writes small synthetic JSON/text
# seeds; it never touches a real vault.

param(
    [string]$CorpusDir = ''
)

$ErrorActionPreference = 'Stop'

$fuzzDir = Split-Path -Parent $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $fuzzDir '../../../')).Path
$workspaceRoot = Split-Path -Parent $repoRoot
$fixtures = Join-Path $repoRoot 'tests/fixtures/keepass-rs'

if ([string]::IsNullOrWhiteSpace($CorpusDir)) {
    if (-not [string]::IsNullOrWhiteSpace($env:KEEPASSITY_FUZZ_CORPUS)) {
        $CorpusDir = $env:KEEPASSITY_FUZZ_CORPUS
    } elseif (-not [string]::IsNullOrWhiteSpace($env:CARGO_TARGET_DIR)) {
        $CorpusDir = Join-Path (Split-Path -Parent $env:CARGO_TARGET_DIR) 'fuzz-corpus'
    } else {
        $CorpusDir = Join-Path $workspaceRoot 'out/fuzz-corpus'
    }
}
$CorpusDir = [System.IO.Path]::GetFullPath($CorpusDir)
if ($CorpusDir.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "refusing to write the fuzz corpus inside the repository: $CorpusDir (pass -CorpusDir with a path outside repo/)"
}

$kdbxCorpus = Join-Path $CorpusDir 'kdbx_parse'
$totpCorpus = Join-Path $CorpusDir 'totp_uri'
$mergeCorpus = Join-Path $CorpusDir 'merge_json'
foreach ($dir in @($kdbxCorpus, $totpCorpus, $mergeCorpus)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

function Write-TextSeed([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

# --- kdbx_parse: valid KDBX containers openable with the fixed password ------
# `demopass` is the documented public test password (tests/fixtures/README.md).
# Small AES-KDF samples are preferred so each execution stays fast.
$kdbxSeeds = @(
    'test_db_few_rounds.kdbx',
    'test_db_kdbx3_with_attachments.kdbx',
    'test_db_kdbx4_with_password_aes.kdbx',
    'test_db_kdbx41_with_password_aes.kdbx',
    'test_db_kdbx4_with_password_deleted_entry.kdbx'
)
foreach ($seed in $kdbxSeeds) {
    $source = Join-Path $fixtures $seed
    if (-not (Test-Path -LiteralPath $source)) {
        throw "missing fixture for fuzz corpus: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $kdbxCorpus $seed) -Force
}

# --- totp_uri: otpauth/steam URIs and Base32 secrets ------------------------
Write-TextSeed (Join-Path $totpCorpus 'otpauth_basic.txt') `
    'otpauth://totp/KeePassXC:alice?secret=JBSWY3DPEHPK3PXP&period=30&digits=6&issuer=KeePassXC'
Write-TextSeed (Join-Path $totpCorpus 'otpauth_sha512.txt') `
    'otpauth://totp/Corp:bob?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&algorithm=SHA512&digits=8&period=60&issuer=Corp'
Write-TextSeed (Join-Path $totpCorpus 'otpauth_padded.txt') `
    'otpauth://totp/Example:user%40example.com?secret=MZXW6%3D%3D%3D&issuer=Example'
Write-TextSeed (Join-Path $totpCorpus 'steam.txt') `
    'steam://Steam:alice?secret=JBSWY3DPEHPK3PXP'
Write-TextSeed (Join-Path $totpCorpus 'base32.txt') `
    'JBSWY3DPEHPK3PXP'
Write-TextSeed (Join-Path $totpCorpus 'base32_padded.txt') `
    'MZXW6==='
Write-TextSeed (Join-Path $totpCorpus 'timeotp_fields.txt') `
    'JBSWY3DPEHPK3PXP'
Write-TextSeed (Join-Path $totpCorpus 'invalid.txt') `
    'not-a-uri'

# --- merge_json: two `Database` JSON documents separated by a NUL byte ------
# Required fields follow crates/core/src/model/mod.rs. Both sides share the
# root/entry UUIDs so merge compares LastModificationTime and history; the
# remote side has a newer entry, one extra entry and one deleted object.
$meta = '"meta":{"generator":"fuzz-seed","database_name":"seed","database_description":"","default_username":"","memory_protection":{"protect_title":false,"protect_username":false,"protect_password":true,"protect_url":false,"protect_notes":false},"recycle_bin_enabled":false,"history_max_items":10,"history_max_size":0,"custom_data":{}}'

$timesJan1 = '"times":{"creation":"2026-01-01T00:00:00Z","last_modification":"2026-01-01T00:00:00Z","last_access":"2026-01-01T00:00:00Z","expiry":"2026-01-01T00:00:00Z","expires":false,"usage_count":0,"location_changed":"2026-01-01T00:00:00Z"}'
$timesJan2 = '"times":{"creation":"2026-01-01T00:00:00Z","last_modification":"2026-01-02T00:00:00Z","last_access":"2026-01-02T00:00:00Z","expiry":"2026-01-01T00:00:00Z","expires":false,"usage_count":0,"location_changed":"2026-01-01T00:00:00Z"}'
$timesJan3 = '"times":{"creation":"2026-01-01T00:00:00Z","last_modification":"2026-01-03T00:00:00Z","last_access":"2026-01-03T00:00:00Z","expiry":"2026-01-01T00:00:00Z","expires":false,"usage_count":0,"location_changed":"2026-01-01T00:00:00Z"}'

$localEntry = '{"uuid":"22222222222222222222222222222222","icon_id":0,"foreground_color":"","background_color":"","override_url":"",' + $timesJan2 + ',"strings":[{"key":"Title","value":"Local title","protected":false},{"key":"Password","value":"local-secret","protected":true}],"custom_data":{}}'
$remoteEntrySame = '{"uuid":"22222222222222222222222222222222","icon_id":0,"foreground_color":"","background_color":"","override_url":"",' + $timesJan3 + ',"strings":[{"key":"Title","value":"Remote title","protected":false},{"key":"Password","value":"remote-secret","protected":true}],"custom_data":{}}'
$remoteEntryNew = '{"uuid":"33333333333333333333333333333333","icon_id":0,"foreground_color":"","background_color":"","override_url":"",' + $timesJan2 + ',"strings":[{"key":"Title","value":"Only remote","protected":false}],"custom_data":{}}'

$rootLocal = '{"uuid":"11111111111111111111111111111111","name":"Root","notes":"","icon_id":0,' + $timesJan1 + ',"is_expanded":true,"default_auto_type":false,"default_autotype_sequence":"","enable_auto_type":false,"enable_searching":true,"custom_data":{},"entries":[' + $localEntry + ']}'
$rootRemote = '{"uuid":"11111111111111111111111111111111","name":"Root","notes":"","icon_id":0,' + $timesJan2 + ',"is_expanded":true,"default_auto_type":false,"default_autotype_sequence":"","enable_auto_type":false,"enable_searching":true,"custom_data":{},"entries":[' + $remoteEntrySame + ',' + $remoteEntryNew + ']}'

$localDoc = '{"format_version":"4.0",' + $meta + ',"root":' + $rootLocal + '}'
$remoteDoc = '{"format_version":"4.0",' + $meta + ',"root":' + $rootRemote + ',"deleted_objects":[{"uuid":"44444444444444444444444444444444","deletion_time":"2026-01-03T00:00:00Z"}]}'

Write-TextSeed (Join-Path $mergeCorpus 'merge_pair.seed') ($localDoc + [char]0 + $remoteDoc)
Write-TextSeed (Join-Path $mergeCorpus 'merge_reverse.seed') ($remoteDoc + [char]0 + $localDoc)
Write-TextSeed (Join-Path $mergeCorpus 'single_doc.seed') $localDoc

Write-Host "Seeded corpus outside the repository: $CorpusDir"
Write-Host "  kdbx_parse: $((Get-ChildItem $kdbxCorpus -File).Count) files"
Write-Host "  totp_uri:   $((Get-ChildItem $totpCorpus -File).Count) files"
Write-Host "  merge_json: $((Get-ChildItem $mergeCorpus -File).Count) files"
Write-Host ''
Write-Host "Run a target with, e.g.:"
Write-Host "  cargo +nightly fuzz run kdbx_parse `"$kdbxCorpus`" -- -max_total_time=180"
