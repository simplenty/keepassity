# keepassity

A Windows-first, open-source password manager for KeePass **KDBX** databases, with native
**WebDAV** sync and built-in **TOTP**. Rust core + WinUI 3 shell, GPL-3.0-or-later.

> **Unofficial.** Not affiliated with, endorsed by, or sponsored by the KeePass project.
> "KeePass" is a trademark of its owner; this project only reads and writes `.kdbx` files.

## Status

**Early development (v0.1.0).** The Windows app is usable against real KDBX databases today:
it unlocks, browses, edits and saves entries, shows TOTP codes, keeps timestamped local
backups, and syncs over WebDAV with conflict copies. It is **not ready for daily use as the
only copy of your passwords** — there is no installer yet, the v1.0 acceptance gates have not
been run, and keyfile/challenge-response credentials are not supported. The v1.0 scope is
frozen by the maintainer.

## What v1.0 aims to be

- Opens real KeePass 2.x `.kdbx` files (KDBX 3.1 / 4 / 4.1), writes back without losing data.
- Native WebDAV sync (e.g. Jianguoyun / Nextcloud) with three-way conflict handling and conflict copies.
- Built-in editable TOTP, compatible with both KeePass 2.x (`TimeOtp-*`) and otpauth (`otp`) fields.
- Automatic local backups, atomic saves, offline queue, auto-lock, clipboard auto-clear.
- Unsigned Inno Setup installer for Windows 11.

Not in v1.0: browser extension, auto-type, Windows Hello, attachments/history/recycle-bin UI,
multi-database, mobile, macOS, Linux (Linux is planned for v1.x).

## Repository layout

| Path | What |
|---|---|
| `crates/core` | Rust core: KDBX parsing/writing, crypto, merge/sync decisions, TOTP (no IO, no network) |
| `crates/ffi` | C ABI + JSON bridge |
| `crates/cli` | CLI harness for CI, interop and acceptance checks (not shipped with the app) |
| `apps/windows` | WinUI 3 shell (`Keepassity.Mock`); talks to the core through the FFI v1 C ABI |
| `tools/interop` | Test-only interop verification harness (KeePassLib + keepassxc-cli) |
| `tests/fixtures` | Public KDBX test corpus (MIT, from keepass-rs) |
| `docs/compatibility.md` | Compatibility matrix (only actually-tested states are listed) |

## Build

Toolchain: Rust stable, pinned in `rust-toolchain.toml`; .NET SDK 8 or newer plus the Windows
App SDK for the shell.

Core and interop checks (no UI needed):

```powershell
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
```

Windows app (WinUI 3, self-contained portable build):

```powershell
cargo build -p keepassity-ffi --release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish-windows.ps1 -Zip
```

The portable build lands in `dist/keepassity-<version>-win-x64/` (and a `.zip` with `-Zip`).
Run `Keepassity.Mock.exe` or the faster `Run keepassity.cmd`; the project name is historical
and the UI is the intended shell. The native `keepassity_ffi.dll` and the WinUI XAML resources
are copied into the publish output by the app project, and the publish script fails if either
is missing.

## Testing

`cargo test --workspace` covers the write closure on the public fixtures. For third-party
interop (keepassxc-cli, optional KeePassLib):

```powershell
pwsh -File tools/interop/verify.ps1 -FixturesOnly
```

The Windows shell has a non-UI smoke test (open fixture copy → list → TOTP → save → reopen):

```powershell
$env:KEEPASSITY_SELFTEST_PASSWORD = "demopass"
$env:KEEPASSITY_SELFTEST_REPORT = "$PWD\selftest.log"
Start-Process dist\keepassity-0.1.0-win-x64\Keepassity.Mock.exe -Wait -PassThru -ArgumentList `
  '--selftest', (Resolve-Path tests\fixtures\keepass-rs\test_db_kdbx4_with_password_aes.kdbx).Path
```

It exits non-zero on failure and never writes secrets to the report. Installer packaging and
the manual acceptance checklist are not part of this build yet.

## Contributing and security

See `CONTRIBUTING.md` and `SECURITY.md`.

## License

GPL-3.0-or-later. See `LICENSE`, `NOTICE`, and `THIRD-PARTY-LICENSES.md`.
