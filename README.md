# keepassity

A Windows-first, open-source password manager for KeePass **KDBX** databases, with native
**WebDAV** sync and built-in **TOTP**. Rust core + WinUI 3 shell, GPL-3.0-or-later.

> **Unofficial.** Not affiliated with, endorsed by, or sponsored by the KeePass project.
> "KeePass" is a trademark of its owner; this project only reads and writes `.kdbx` files.

## Status

**Pre-implementation (v0.1.0 scaffolding).** No usable build yet — do not use this for real
passwords. The v1.0 scope is frozen by the maintainer.

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
| `apps/windows` | WinUI 3 shell (planned) |
| `tools/interop` | Test-only interop verification harness (KeePassLib + keepassxc-cli) |
| `tests/fixtures` | Public KDBX test corpus (MIT, from keepass-rs) |
| `docs/compatibility.md` | Compatibility matrix (only actually-tested states are listed) |

## Build

Toolchain: Rust stable, pinned in `rust-toolchain.toml`.

```powershell
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
```

## Testing

`cargo test --workspace` covers the write closure on the public fixtures. For
third-party interop (keepassxc-cli, optional KeePassLib):

```powershell
pwsh -File tools/interop/verify.ps1 -FixturesOnly
```

The Windows shell (WinUI 3 / .NET) and installer are not buildable yet; their toolchain
requirements will be documented when that work starts.

## Contributing and security

See `CONTRIBUTING.md` and `SECURITY.md`.

## License

GPL-3.0-or-later. See `LICENSE`, `NOTICE`, and `THIRD-PARTY-LICENSES.md`.
