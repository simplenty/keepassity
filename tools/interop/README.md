# Interop verification (test-only)

Purpose: prove that what we write can be read by real clients, and that we can read what they write.
**Nothing here ships with the app.**

## `verify.ps1`

Round-trip every supported fixture:

1. `keepassity-cli rewrite` re-saves the fixture with the ADR-0002 write backend
   (`keepassxcr`) into `target/interop-tmp/`;
2. `keepassity-cli verify` compares source and output field by field with the
   established `keepass 0.17` read path;
3. `keepassxc-cli` opens the output and must report the same group+entry count;
4. KeePassLib (official C# library, test-only) re-opens both files and must
   produce the same canonical structural hash.

```powershell
# fixtures only, if keepassxc-cli is on PATH
pwsh -File tools/interop/verify.ps1 -FixturesOnly

# explicit paths + KeePassLib coverage
pwsh -File tools/interop/verify.ps1 -FixturesOnly `
  -KeepassXcPath C:\path\to\keepassxc-cli.exe `
  -KeePassLibDll C:\path\to\KeePassLib.dll

# real database: rewrite a copy; the input is never modified
$env:KEEPASSITY_REAL_PASSWORD = '...'   # read from the environment, never printed
pwsh -File tools/interop/verify.ps1 -RealDbPath C:\path\to\real.kdbx
```

The script writes outputs under `target/interop-tmp/` (gitignored) and never
prints entry titles, usernames, passwords or TOTP secrets.

## KeePassLib

KeePass 2.61.1 official binaries are NativeAOT and do **not** ship a managed
`KeePassLib.dll`. Build the managed library from the official source release:

```powershell
# official source: KeePassLib\KeePassLib_N48.csproj
msbuild KeePassLib_N48.csproj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU
```

Then pass the resulting `KeePassLib.dll` to `-KeePassLibDll`; the script builds
`KeePassLibTool` (net48) automatically. Tested with the KeePass 2.59 managed
source release; newer official binaries may differ.

Known limitation: the managed KeePassLib build does not include the Twofish
cipher plugin, so Twofish fixtures are skipped for the KeePassLib check and are
covered by `keepassxc-cli` plus the CLI verifier instead.

## Acceptance rule

A fixture passes only if the CLI field comparison passes, a third-party client
opens the output, and (when enabled) KeePassLib's canonical hash is unchanged.
Interop failures are P0: stop feature work and fix the write path first.
