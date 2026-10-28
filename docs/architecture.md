# Architecture

keepassity is a KeePass-compatible password manager built as a platform-neutral Rust
core plus a thin platform shell. The Windows shell is WinUI 3; a Linux GTK4 shell is
planned for v1.x and is expected to consume the same core directly.

## Layers

```
┌─────────────────────────────────────────────────────────────┐
│ shell (apps/windows)                                       │
│   UI, dialogs, platform adapters: DPAPI, tray, clipboard,  │
│   idle/lock detection, file pickers                        │
├─────────────────────────────────────────────────────────────┤
│ application services (currently inside the Windows shell)  │
│   settings, sync state, WebDAV client, backups, sync       │
│   orchestration/conflict flow                              │
├─────────────────────────────────────────────────────────────┤
│ FFI contract (crates/ffi)                                  │
│   C ABI + JSON commands; the only cross-language boundary  │
├─────────────────────────────────────────────────────────────┤
│ core (crates/core)                                         │
│   KDBX read/write, crypto, model/DTO, merge, sync decisions│
│   TOTP. No file IO, no network, no environment access.     │
└─────────────────────────────────────────────────────────────┘
```

## Key rules

- **`crates/core` stays platform-neutral.** No `cfg(windows)`, file IO, HTTP, environment
  variables or UI types. Platform work belongs in the shell.
- **`crates/ffi` is a contract.** Commands and JSON payloads are versioned in
  `.agents/contracts/` in the private process repository; changes need an ADR.
- **The shell owns IO and networking.** It opens/saves files, performs WebDAV transfers,
  stores credentials with DPAPI and owns platform integration.
- **Application services are the next extraction target** so the future Linux shell can
  reuse sync, backup and settings logic instead of duplicating it.

## Data safety

- Saves use a temp file, timestamped backup and atomic replace (see `docs/compatibility.md`).
- The real database, credentials and WebDAV working copies never enter a git repository.
- Test fixtures are synthetic or public samples; see `tests/fixtures/README.md`.

## Compatibility

KDBX read/write compatibility notes and the write-backend decision live in
`docs/compatibility.md`.
