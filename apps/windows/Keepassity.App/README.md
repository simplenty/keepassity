# keepassity Windows app

Functional WinUI 3 shell on top of the Rust core. It opens a real KDBX database
through the FFI v1 C ABI, shows its groups/entries, reads and writes entries,
generates TOTP codes, and saves the database back to disk.

## Build

The shell requires the native FFI DLL:

```powershell
cargo build -p keepassity-ffi --release
dotnet build apps/windows/Keepassity.App/Keepassity.App.csproj -c Debug
```

The project copies `target/release/keepassity_ffi.dll` next to the app on build
and builds self-contained for `win-x64` (the .NET and Windows App SDK runtimes are
included in the output).

## Run

```powershell
apps/windows/Keepassity.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/Keepassity.exe
```

1. Choose a `.kdbx` file, or use **New database…** to create an empty KDBX 4.0
   database (name, path, master password, confirmation).
2. Enter its master password.
3. Browse groups/entries; use New, Edit, Move, Delete.
4. `Edit TOTP` sets/removes TOTP; import from a QR image or clipboard otpauth
   URI, or paste a Base32 secret manually. `Copy` works on passwords and codes.
5. Changes are saved atomically to the same `.kdbx`; a timestamped backup is
   copied to `.keepassity\backups\<db>\` before every save. Lock saves and closes
   the handle.

`--db <path>` pre-fills the database, and a bare `.kdbx` path (file association /
"Open with") is handed to the running instance. The diagnostic overrides are
release-safe: `KEEPASSITY_MOCK_PASSWORD`, `--allow-multiple`, `--no-tray` and the
tray-menu hooks only work in a debug build or together with `--enable-test-hooks`.
Do not put real passwords in arguments. A second normal launch activates the
existing window instead of starting a second copy. `--data-dir <path>` (or
`KEEPASSITY_DATA_DIR`) keeps settings, logs and backups away from the real profile.

The tray menu can be opened without pointer input for visual checks (requires
`--enable-test-hooks`):
`KEEPASSITY_TRAY_MENU_AT="x,y"` (physical screen pixels) opens it 1.5 s after the
window loads, `KEEPASSITY_TRAY_MENU_SCHEDULE="2000,x,y;8000,x,y"` lists exact
delay/position toggles, `KEEPASSITY_TRAY_MENU_CYCLE_MS=12000` keeps toggling the
menu for interactive capture, and `KEEPASSITY_TRAY_MENU_ANCHOR=icon` uses the real
notification-icon rectangle instead of the point. Combine with
`KEEPASSITY_DATA_DIR` to isolate settings/logs.

## Self-test

The app can run a non-UI smoke test against a fixture copy:

```powershell
$env:KEEPASSITY_SELFTEST_PASSWORD = "fixture-password"
$env:KEEPASSITY_SELFTEST_REPORT = "$PWD\selftest.log"
& <app.exe> --selftest <fixture.kdbx>
```

It opens/list/saves/reopens a temporary copy, reads TOTP when present, and exits
non-zero on failure. The report contains counts/status only, never secrets.

## Implemented in this slice

- Real KDBX open/read (AES/Argon2/Argon2id fixtures; 3.1/4.0/4.1).
- Create a new empty KDBX 4.0 database from the unlock page (master password,
  save path, confirmation) with a `General` root group; open it immediately and
  keep using the normal save/backup path.
- Groups + virtual All entries view, entry list, detail pane.
- Search via core.
- Create/edit/move/delete entries; save via core writer.
- Copy username/password/URL/custom field; password generation via core.
- TOTP code display, period progress, set/remove through core dual-write fields.
  Import supports a selected QR image, a clipboard QR screenshot, or an
  `otpauth://` clipboard URI; manual Base32 remains available. QR decoding stays
  in the UI layer (ZXing.Net) and only the resulting URI/secret reaches core.
- Lock/unlock, light/dark theme, Mica/titlebar layout.
- Single instance (a second launch restores and focuses the existing window, and a
  `.kdbx` path passed to it is forwarded to the running copy) and persisted window
  position/size; `--allow-multiple` is a gated test-only bypass.
- Notification-area icon: left/double click shows the window, right click offers
  Show / Lock / Exit, and closing the window hides it to the tray with a one-time
  hint. An optional setting starts the app minimized; `--no-tray` is a test-only
  bypass.
- Settings persisted to `%LOCALAPPDATA%\keepassity\settings.json`: theme, UI language,
  clipboard clearing, auto-lock enable/minutes, backup retention, window bounds, start
  minimized, and the last opened database path.
- **UI language**: English and Simplified Chinese, switchable at runtime in Settings or on
  the unlock page (plus "use the system language"), applied without restarting and persisted
  in `settings.json`. English source strings are the catalog keys; translations live in
  `Keepassity.Core/Localization/*.zh-CN.json` (see `docs/testing.md`, UI strings). The tray
  menu, tooltips, dialogs, toasts and the About/GPL section follow the selected language.
- Clipboard secrets are cleared after 30 seconds if they are still the current
  clipboard text, and are marked not to enter Win+V history or cloud clipboard
  (`ClipboardContentOptions`).
- Auto-lock after the configured inactivity period (default 15 minutes); activity
  from pointer or keyboard resets the timer.
- **WebDAV sync**: configure an https file URL and username in Settings (the Open
  dialog offers presets for Nextcloud/ownCloud, Synology and Jianguoyun); the
  password is stored with Windows DPAPI. Self-signed certificates require an
  explicit fingerprint confirmation and are then pinned per host. The app checks
  the remote on unlock, syncs on save (toggle), and supports manual sync from the
  footer. It implements PROPFIND depth 0 metadata, conditional GET/PUT with
  ETag/If-Match/If-None-Match, download, upload, local merge, and 412 back-off
  retries. Non-overlapping changes merge automatically; when the same item changed
  on both sides, a three-choice dialog offers Keep local, Keep remote, or Keep both
  (recommended), and local/remote conflict copies are written before any choice.
  Baseline ETag/hash/dirty state is kept under `%LOCALAPPDATA%\keepassity\state\`.
- **Backups**: every save (local or sync write) first copies the current database
  to `.keepassity\backups\<db>\<timestamp>.kdbx`; the newest `BackupRetention`
  copies are kept (default 20). The footer's Backups item lists them and restores
  a selected backup after validating that it opens with the current password and
  backing up the current file first.

## Not implemented in v1.0

- Keyfile/challenge-response credentials (master password only).
- History/attachment/recycle-bin management UI (the data is preserved).
- Windows Hello / quick unlock, auto-type, browser integration, multi-database.

## Notes

- The project is `Keepassity.App` (renamed from the original `Keepassity.Mock`
  prototype in the v1.1 line) and ships `Keepassity.exe`.
- Fixture passwords are public test values from `tests/fixtures`; no real vault is
  committed. `.spike/` and `docs/spike/` are local review artifacts only.
