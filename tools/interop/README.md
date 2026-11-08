# Interop verification (test-only)

Purpose: prove that what we write can be read by real clients, and that we can read what they write.
**Nothing here ships with the app.**

## `verify.ps1`

Round-trip every supported fixture (the upstream `keepass-rs` corpus **and** the
repository-generated `tests/fixtures/synthetic/` corpus):

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
`KeePassLib.dll`. Build the managed library from the official source release
(pinned release used by CI: **KeePass 2.59**,
`KeePass-2.59-Source.zip`, SHA-256
`7ac2711821836fc0198b6f19ecd787318c8bf06585a4a3aeb2e6bd5319604a7b`, published on
<https://keepass.info/integrity.html>):

```powershell
# official source: KeePassLib\KeePassLib_N48.csproj
# The dummy PFX files that ship with the source are password-protected, so the
# CI build disables strong-name signing (the library is test-only):
msbuild KeePassLib_N48.csproj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /p:SignAssembly=false
```

Then pass the resulting `KeePassLib.dll` (under `Build\KeePassLib\Release\`) to
`-KeePassLibDll`; the script builds `KeePassLibTool` (net48) automatically.
The `interop-keepasslib / windows` CI job does exactly this, so the write closure
is checked by two independent readers (`keepassxc-cli` on Linux and KeePassLib on
Windows).

SourceForge (the official distributor) rejects requests from cloud/datacenter
addresses, including GitHub-hosted runners, so the CI job falls back to a pinned
GitHub mirror of the official source package ([`csware/keepass2`](https://github.com/csware/keepass2),
commit `5aa5bb77`, archive SHA-256 `c518a1ea...`). Both paths must match a pinned
SHA-256 before use: the official archive against the hash published on
<https://keepass.info/integrity.html>, the mirror archive against the hash pinned
in `.github/workflows/ci.yml`. To run the job against a locally downloaded
official package instead, set `KEEPASS_SOURCE_ZIP` in the environment.

Known limitation: the managed KeePassLib build does not include the Twofish
cipher plugin, so Twofish fixtures are skipped for the KeePassLib check and are
covered by `keepassxc-cli` plus the CLI verifier instead.

## Acceptance rule

A fixture passes only if the CLI field comparison passes, a third-party client
opens the output, and (when enabled) KeePassLib's canonical hash is unchanged.
Interop failures are P0: stop feature work and fix the write path first.
