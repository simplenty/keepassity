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
│ application services (apps/windows/Keepassity.Core)        │
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
- **`crates/ffi` is a contract.** Commands and JSON payloads are versioned by the
  DTO contract (kept with the project process documentation); changes need an ADR.
- **The WinUI project owns platform integration.** It creates windows and dialogs, hosts the
  tray icon, clears the clipboard and stores credentials with DPAPI. File and WebDAV IO goes
  through the application-services layer; `crates/core` stays pure.
- **Application services live in `apps/windows/Keepassity.Core`.** Settings, sync state, the
  WebDAV client, backups and sync orchestration are already platform-neutral C# and covered
  by the headless `tests/app-core` suite, so the future Linux shell can reuse them instead of
  duplicating the logic.

## Data safety

- Saves use a temp file, a timestamped backup and a verified atomic replace; the save
  protocol is exercised by the core save/sync tests.
- The real database, credentials and WebDAV working copies never enter a git repository.
- Test fixtures are synthetic or public samples; see `tests/fixtures/README.md`.

## Compatibility

KDBX read/write compatibility notes and the write-backend decision live in
`docs/compatibility.md`.
