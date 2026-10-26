# keepassity Windows app (test branch)

Functional WinUI 3 shell on top of the Rust core. It opens a real KDBX database
through the FFI v1 C ABI, shows its groups/entries, reads and writes entries,
generates TOTP codes, and saves the database back to disk.

## Build

The shell requires the native FFI DLL:

```powershell
cargo build -p keepassity-ffi --release
dotnet build apps/windows/Keepassity.Mock/Keepassity.Mock.csproj -c Debug
```

The project copies `target/release/keepassity_ffi.dll` next to the app on build.

## Run

```powershell
apps/windows/Keepassity.Mock/bin/Debug/net8.0-windows10.0.19041.0/win-x64/Keepassity.Mock.exe
```

1. Choose a `.kdbx` file, or use **New database…** to create an empty KDBX 4.0
   database (name, path, master password, confirmation).
2. Enter its master password.
3. Browse groups/entries; use New, Edit, Move, Delete.
4. `Edit TOTP` sets/removes TOTP; `Copy` works on passwords and codes.
5. Changes are saved atomically to the same `.kdbx`; a timestamped backup is
   copied to `.keepassity\backups\<db>\` before every save. Lock saves and closes
   the handle.

For local UI testing only, `--db <path>` pre-fills the database, and the
`KEEPASSITY_MOCK_PASSWORD` environment variable auto-unlocks the app. Do not put
real passwords in arguments.

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
- Lock/unlock, light/dark theme, Mica/titlebar layout.
- Settings persisted to `%LOCALAPPDATA%\keepassity\settings.json`: theme, clipboard
  clearing, auto-lock enable/minutes, backup retention, and the last opened
  database path.
- Clipboard secrets are cleared after 30 seconds if they are still the current
  clipboard text.
- Auto-lock after the configured inactivity period (default 15 minutes); activity
  from pointer or keyboard resets the timer.
- **WebDAV sync**: configure a file URL and username in Settings; the password is
  stored with Windows DPAPI. The app checks the remote on unlock, syncs on save
  (toggle), and supports manual sync from the footer. It implements PROPFIND
  depth 0 metadata, conditional GET/PUT with ETag/If-Match/If-None-Match, download,
  upload, and local merge. Non-overlapping changes merge automatically; when the
  same item changed on both sides, a three-choice dialog offers Keep local, Keep
  remote, or Keep both (recommended), and local/remote conflict copies are written
  before any choice.
  Baseline ETag/hash/dirty state is kept under `%LOCALAPPDATA%\keepassity\state\`.
- **Backups**: every save (local or sync write) first copies the current database
  to `.keepassity\backups\<db>\<timestamp>.kdbx`; the newest `BackupRetention`
  copies are kept (default 20). The footer's Backups item lists them and restores
  a selected backup after validating that it opens with the current password and
  backing up the current file first.

## Not implemented yet

- Keyfile/challenge-response credentials.
- Tray behaviour, history/attachment UI.
- Window position/size persistence.

## Notes

- The project is still named `Keepassity.Mock` because it began as the UI prototype;
  the UI is the intended shell and now talks to the real core.
- Fixture passwords are public test values from `tests/fixtures`; no real vault is
  committed. `.spike/` and `docs/spike/` are local review artifacts only.
