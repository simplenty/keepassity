# drills — headless M4-07 drill automation

This directory automates the parts of the M4-07 drills that can run without a UI:
a mock WebDAV server, a headless drill runner, and PowerShell scripts that build
them and run protocol / backup-restore / offline-replay / conflict scenarios
against a temporary copy of the 500-entry synthetic fixture.

Everything here runs headless and uses **public test constants only** (fixture
master password `demopass`). Never point these scripts at a real vault.

## Prerequisites

- Windows with **.NET SDK 10 or newer** (both projects target `net10.0`) and the
  matching .NET 10 runtime.
- **Rust/cargo** for the native core; run commands from the repository root.
- The synthetic fixture `tests/fixtures/synthetic/synthetic-500.kdbx` (committed;
  regenerate with `tools/fixture-gen/regenerate.ps1` if needed).
- Optional: set `CARGO_TARGET_DIR` to keep cargo output out of `repo\target`, e.g.
  `$env:CARGO_TARGET_DIR='..\out\cargo-target'`. The scripts honour this variable
  and copy `keepassity_ffi.dll` from `<CARGO_TARGET_DIR>\release\` next to the
  runner. If it is unset, cargo's default `repo\target` is used.

## Build

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\drills\build-drills.ps1
```

This builds `mock-webdav`, `runner` (Release), builds `keepassity-ffi` in release
and copies the FFI DLL into `tools\drills\runner\bin\Release\net10.0\`. Every drill
script calls the same initialisation, so running this first is optional (useful
for CI or to check the build in isolation). Equivalent manual steps:

```powershell
dotnet build tools\drills\mock-webdav\MockWebDav.csproj -c Release --nologo -v minimal
dotnet build tools\drills\runner\DrillRunner.csproj   -c Release --nologo -v minimal
cargo build -p keepassity-ffi --release
```

## Run

Whole suite (each drill runs in its own PowerShell process; prints a summary and
returns 0 only if every drill passed):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\drills\run-all.ps1
```

One drill at a time:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\drills\run-protocol.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\drills\run-restore.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\drills\run-offline-replay.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\drills\run-conflict.ps1
```

Add `-Keep` to any script (or to `run-all.ps1`) to keep the temporary workspace
and printed paths for inspection; without it the workspace is deleted on exit.
Exit code 0 = PASS, non-zero = FAIL; each drill prints `PASS`/`FAIL`/`INFO` lines.

## What each drill asserts

- **protocol** (`run-protocol.ps1`): starts the mock WebDAV server on a free
  loopback port and checks the contract the app's `WebDavClient` relies on —
  `PUT` create with `If-None-Match: *`, `PROPFIND` depth 0 ETag/size, `GET`,
  `HEAD`, stale `If-Match` → 412, `If-None-Match: *` over an existing file → 412,
  conditional `PUT` with the current ETag, `DELETE`, and a percent-encoded UTF-8 path.
- **restore** (`run-restore.ps1`): backup restore through the real
  `Keepassity.Core.BackupService` — CreateBackup → edit/save through the FFI →
  Restore → byte-for-byte equality, before-restore safety copy, and a reopen of
  the restored KDBX with 500 entries and without the drill edit.
- **offline-replay** (`run-offline-replay.ps1`): seeds the mock remote, downloads
  the app's remote working copy (`RemoteDatabaseStore`), edits offline through the
  FFI, records dirty state (`SyncStateStore`), stops the server (transport
  failures must not lose the edit or dirty flag), restarts it on the same port,
  checks the remote is unchanged, expects `kp_sync_decide = upload`, replays the
  upload with `If-Match`, and commits a clean baseline.
- **conflict** (`run-conflict.ps1`): another client changes the remote while the
  local copy has a disjoint edit. Expects `kp_sync_decide = download_and_merge`,
  rejects a stale `If-Match` upload with 412 (no silent overwrite) while both
  sides stay intact, merges via `kp_sync_merge` (0 conflicts for disjoint edits),
  uploads the merge with the current ETag and reopens the remote with both edits.

The runner uses the real app services (`Keepassity.Core`) and the real native core
through the FFI v1 C ABI (`kp_open`, `kp_call`, `kp_save`, `kp_sync_decide`,
`kp_sync_merge`, `kp_sync_commit`) — the same seam the WinUI client uses.

## Temporary state

Each drill creates `%TEMP%\keepassity-drill-<drill>-<timestamp>-<random>\` and
sets `KEEPASSITY_DATA_DIR=<workdir>\appdata` for the runner, so no data is written
to the real `%LOCALAPPDATA%\keepassity` profile. The mock server's remote root,
request log and port file also live there (`%TEMP%` for the port file).

## What remains manual / UI

These drills are service- and protocol-level automation; they do **not** test the
WinUI experience. Still manual:

- the WinUI three-choice conflict dialog ("keep local / keep remote / keep both")
  for same-entry, same-mtime conflicts, and the conflict-copy files produced by
  `MainWindow.MergeRemoteAsync` (the scripted drill stops before that path);
- a real Jianguoyun (坚果云) WebDAV account: TLS/auth, provider-specific
  PROPFIND/ETag/412 behaviour, rate limits and real-network outages. The mock
  server is loopback-only, unauthenticated, single-connection, and its ETag is the
  content SHA-256;
- clean-machine install, first run, tray/autostart and credential-store checks, and
  visual checks such as G3 "500 entries scroll smoothly" in the app.

## Notes

- The mock server is started with `Start-Process -NoNewWindow`. Do not switch it
  to `-WindowStyle Hidden`: in headless/sandboxed sessions that path uses
  ShellExecuteEx and can block before the server exists, hanging the drill while
  it waits for the port file.
- `run-all.ps1` uses `pwsh` when available, otherwise Windows PowerShell 5.1; both
  are supported. Direct runner usage is possible for debugging (sub-commands:
  `protocol`, `restore`, `offline-prepare`, `offline-unreachable`, `offline-replay`,
  `conflict`; run `DrillRunner.exe --help` for the argument surface).
