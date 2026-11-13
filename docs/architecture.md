# Architecture

keepassity is a KeePass-compatible password manager built as a platform-neutral Rust
core plus a thin platform shell. The Windows shell is WinUI 3; a Linux GTK4 shell is
planned for v1.x and is expected to consume the same core directly.

## Layers

```
┌─────────────────────────────────────────────────────────────┐
│ shell (apps/windows/Keepassity.Mock)                       │
│   views/XAML, platform adapters: DPAPI, tray, clipboard,   │
│   idle/lock detection, file pickers, window lifetime       │
├─────────────────────────────────────────────────────────────┤
│ view models (apps/windows/Keepassity.ViewModels)           │
│   presentation state and commands; no WinUI types; tested  │
│   headless with fake core/text ports (ADR-0032)            │
├─────────────────────────────────────────────────────────────┤
│ application services (apps/windows/Keepassity.Core)        │
│   settings, sync state, WebDAV client, backups, app paths  │
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
- **The presentation layer is MVVM.** `Keepassity.ViewModels` holds observable state and
  commands and depends on `ICoreClient` plus injected platform ports; it must not reference
  WinUI. Views bind with `x:Bind` and keep only view mechanics (focus, pickers, window
  lifetime). `CommunityToolkit.Mvvm` is the source-generator baseline (ADR-0032).
- **Application services live in `apps/windows/Keepassity.Core`.** Settings, sync state, the
  WebDAV client, backups, paths and the `ICoreClient` port are platform-neutral C# and covered
  by the headless `tests/app-core` suite, so the future Linux shell can reuse them instead of
  duplicating the logic. The WinUI shell also carries the FFI adapter (`CoreClient`).

## Data safety

- Saves write a flushed sibling temporary file, keep a timestamped backup and replace the live
  database atomically (`AtomicFile`). The core also implements a verified write/read-back protocol;
  the Windows save path is being migrated onto it (see the compatibility notes and ADR-0032
  follow-up work).
- Remote downloads are decrypted with the current master password before they replace the
  live database; upload redirects are rejected rather than followed, and a sync baseline
  without a content hash is refused. A corrupt sync sidecar makes the vault dirty instead of
  clean.
- One process writes a vault: opening it takes a cross-process lock, and a save compares the
  file's SHA-256 with the bytes the in-memory model came from. An external change blocks the
  save instead of being overwritten.
- The real database, credentials and WebDAV working copies never enter a git repository.
- Test fixtures are synthetic or public samples; see `tests/fixtures/README.md`.

## Compatibility

KDBX read/write compatibility notes and the write-backend decision live in
`docs/compatibility.md`.
