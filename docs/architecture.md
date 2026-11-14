# Architecture

keepassity is a KeePass-compatible password manager built as a platform-neutral Rust
core plus a thin platform shell. The Windows shell is WinUI 3; a Linux GTK4 shell is
planned for v1.x and is expected to consume the same core directly.

## Layers

```
┌─────────────────────────────────────────────────────────────┐
│ shell (apps/windows/Keepassity.App)                        │
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
- **The sync protocol is an application service.** `SyncCoordinator` runs one WebDAV sync
  (decide → upload with back-off → download → three-way merge → conflict choice → baseline
  commit) against three narrow ports: `ISyncRemote` (implemented by `WebDavClient`),
  `IVaultDocument` (the open window: durable write, reopen, merge through a second core handle)
  and the expanded `ICoreClient`. The window keeps only the connection, the conflict dialog and
  the toasts. This is the part of the sync path where ordering decides whether data survives a
  crash, so it is tested headlessly with fakes (`SyncCoordinatorTests`) instead of living in the
  6k-line window.

## Data safety

- Saves are verified and durable in two layers: `kp_save` decrypts and parses the fresh
  ciphertext with the cached KDF key before returning it, then `AtomicFile` writes a
  `<database>.keepassity-tmp-<nonce>` sibling with write-through and an explicit flush, keeps a
  timestamped backup and replaces the live file atomically. The same temp infix is used by the
  core's IO-free `save_atomic` protocol; an unlock removes leftover temps before opening. The
  Windows path therefore matches the core protocol's ordering (verify → temp → backup → replace),
  with the backup hook delegated to `BackupService`. The final placement uses `File.Replace`
  (metadata errors ignored) and falls back to a same-volume rename on volumes without that
  primitive (FAT/exFAT, some network shares); both are renames, so a crash still leaves the old or
  new complete file, and the backup taken just before is the recovery point.
- A damaged database can be replaced from the unlock screen by the newest local backup that the
  entered master password opens (`VaultRecovery`); the damaged file is kept as a before-restore
  backup, and the normal in-app restore list still requires the restored vault to be open.
- A newly created database goes through the same protocol from its first byte:
  `AtomicFile.WriteNew` flushes a temporary and performs a create-only move, so it never replaces
  a file another program created in the race, and `NewDatabasePreparer` writes a WebDAV working
  copy before recording the clean sync baseline.
- Remote downloads are decrypted with the current master password before they replace the
  live database; download and upload redirects are rejected rather than followed, and a sync
  baseline without a content hash is refused. A corrupt sync sidecar makes the vault dirty
  instead of clean. Opening a database from WebDAV uses the same rule: the downloaded bytes are
  opened first and only a database that decrypted is promoted to the local working copy
  (`RemoteVaultOpener`), so a bad download cannot overwrite the previous copy.
- A sync run only records a clean baseline after the local write, the verified upload and the
  core's baseline commit succeeded, in that order: `SyncCoordinator` writes the database first,
  uploads second, asks the core to accept the new etag/hash third, and only then saves the
  sidecar. A failure at any step leaves the vault dirty, so the next run retries instead of
  trusting a half-finished sync.
- One process writes a vault: opening it takes a cross-process lock, and a save compares the
  file's SHA-256 with the bytes the in-memory model came from. An external change blocks the
  save instead of being overwritten. The lock has two layers: the account-scoped file under the
  application data folder, plus (for a writable vault folder) an exclusive `.keepassity/locks`
  file next to the database, which also coordinates different Windows accounts or machines
  opening the same shared folder; a read-only folder degrades to the account-scoped layer.
  `VaultSession` owns those three guards (lock, in-process
  write gate, fingerprint) and is the only path for saves, sync replacements and backup
  restores, so the window cannot compose them differently per call site. The new ciphertext is
  decrypted and parsed with the cached KDF key before it can replace the file; the write gate
  serializes save, sync-copy and restore replacements so an older async snapshot cannot land
  last. `VaultSession` also reports whether a write is queued or running; the window drains that
  work (bounded) before it disposes the session and core on close, and locks the core only after a
  running save finished. Sync reads the live database through the same gate (`ReadBytesAsync`), so a
  read cannot collide with a replace. A sync that arrives at its local replace phase after the
  vault locked is refused instead of replacing the database under a locked core.
- The real database, credentials and WebDAV working copies never enter a git repository.
- Test fixtures are synthetic or public samples; see `tests/fixtures/README.md`.

## Compatibility

KDBX read/write compatibility notes and the write-backend decision live in
`docs/compatibility.md`.
