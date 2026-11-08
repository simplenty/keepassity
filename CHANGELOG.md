# Changelog

All notable changes are documented here. Format: Keep a Changelog. Versioning: SemVer.
KDBX write-path changes must state the compatibility impact.

## [Unreleased]

- **Security (pre-release hardening):** the KDBX reader now validates the key-derivation
  parameters from the file header before deriving any key. A file declaring an extreme KDF
  (for example billions of AES-KDF rounds, or multi-GiB Argon2 memory) is rejected with an
  explainable error instead of making the app spend minutes opening it; the daily fuzzing
  job found this shape. Normal KeePass/KeePassXC databases are unaffected. See
  `docs/compatibility.md` for the limits.
- The entry list keeps its scroll position and selection when an entry is moved or when a
  WebDAV merge reloads the database: unchanged lists are no longer re-rendered, and moved
  rows are updated in place when they stay visible.

The `v1.0.0` tag and GitHub Release are created only after the 30-day acceptance passes; see
`docs/release.md`.

## [1.0.0] - 2026-11-09

First v1.0.0 build. The source tree is at version 1.0.0; the tag and GitHub Release are
pending the 30-day real-use acceptance described in `docs/release.md`.

### Added

- **WinUI 3 Windows shell**: single-window English UI with system/light/dark theme (a Mica
  backdrop when following the system), single-instance behaviour, a notification-area tray
  menu (native WinUI flyout) with Show / Lock / Exit, locked/unlocked tooltip state, lock
  disabled while the vault is locked, close-to-tray and optional start-minimised mode.
- **Databases and entries**: open local KDBX files or databases served over WebDAV; create
  new databases (KDBX 4.0); browse groups and entries with search, sorting, filtering and
  empty states; create/edit/move/delete entries and groups (group deletes are recorded as
  tombstones); drag entries onto groups; entry and group context menus; detail pane with
  inline edit forms; start page with recent databases; inline settings panel; About page with
  version and the GPL-3.0-or-later notice.
- **WebDAV**: open a `.kdbx` directly from a WebDAV URL (encrypted working copy downloaded,
  unlocked locally, saves written back), remote check on unlock, sync on save and manual
  sync, three-way merge for non-overlapping changes, conflict copies plus a Keep both /
  Keep local / Keep remote choice for the same item changed on both sides, offline state and
  automatic replay when connectivity returns. WebDAV passwords are stored with Windows DPAPI.
- **Backups**: a timestamped backup before every save (default retention 20 copies), list and
  restore with the current master password, rejection of corrupted backups, and an extra
  backup of the live file before a restore.
- **TOTP**: display, edit and generate codes for both KeePass `TimeOtp-*` and `otpauth`/`otp`
  fields (dual-write on edit); import from a QR image, a clipboard QR screenshot or an
  `otpauth://` URI, or paste a Base32 secret manually; next-window prefetch when fewer than
  3 seconds remain.
- **Security**: auto-lock after inactivity and on Windows session lock, logoff, session end
  and system suspend; clipboard secrets cleared after 30 seconds and marked not to enter
  Win+V history or cloud clipboard; DPAPI credential store; diagnostics export (environment
  summary, non-sensitive settings snapshot and recent logs, no vault data); password
  generator with length/character-class options and an entropy display.
- **Packaging**: self-contained portable publish
  (`dist/keepassity-<version>-win-x64/` and `.zip`) and an Inno Setup per-user installer
  (no administrator rights, install under `%LOCALAPPDATA%\Programs\keepassity`); a non-UI
  `--selftest` mode; `.kdbx` file association: the installer registers a per-user candidate
  association (no default-app override); double-click opens keepassity after the user
  confirms/chooses it in Windows.
- **Desktop conveniences**: keyboard shortcuts (search, lock, new entry, sync, close panel),
  dropping a `.kdbx` file onto the window, opening a database from Explorer (first launch and
  while an instance is already running), and an explicit AppUserModelID so taskbar grouping
  and pinned shortcuts behave.
- **Branding**: the application ships its own icon (window, taskbar, notification area, About
  page and installer) instead of a default framework icon.
- **Engineering**: English-UI CI check (`ui-strings`), headless application-service tests
  (WebDAV client against an in-process mock including non-ASCII paths, backup
  retention/restore, DPAPI credentials, settings), a generated 500-entry synthetic
  database with performance evidence, mock-WebDAV drills (protocol, restore, offline replay,
  conflict), fuzzing of the KDBX/TOTP/merge input surfaces with a nightly job, a second
  independent interop reader (official KeePassLib built in CI), and install/uninstall
  verification against an installed copy (per-user install, shortcuts, Apps & Features entry,
  uninstall keeps user data).
- **Hardening**: malformed KDBX input is rejected with a read error instead of aborting - the
  upstream reader is guarded against its known panic paths, and the fuzzer-found inputs are
  regression tests.

### Changed

- The tray menu is a native WinUI `MenuFlyout`: it opens from the notification icon
  (below-left/right of the icon, flipped at screen edges), follows the app theme, and
  disables Lock while the vault is locked.
- Tray tooltip reports locked/unlocked state and the open database name.
- Tray Lock locks in place instead of raising the window first, so decrypted content is
  never flashed on screen by the Lock action.
- Theme is no longer forced to light by launchers and shortcuts; it follows the system
  setting.
- Recycle-bin and template groups are pinned in the navigation, and recycle-bin entries are
  hidden from the main list.

### Fixed

- The tray menu no longer renders as a framed window with caption buttons, a near-black
  background, a fixed 240x156 dead strip, or a clipped first-open menu with a scrollbar.
- Closing the tray menu with its own window close button no longer leaves a dead menu for
  the rest of the session.
- Deleted groups stay deleted across syncs, and no-op sync reloads are skipped.
- Entry list scroll position is kept after drag/move operations and drag targets are marked.
- Release executables are kept at medium integrity so installs and runs work from
  low-integrity workspace folders.

### KDBX compatibility impact

**Read:** KDBX 3.1, 4.0 and 4.1 databases with a master password, covering AES-KDF and
Argon2/Argon2d/Argon2id KDFs and AES-256, ChaCha20 and Twofish ciphers; attachments,
protected fields, TOTP fields and deleted-object records are parsed (and preserved even
where v1.0 has no UI for them).

**Write:** edits are saved through the vendored `keepassxcr` 0.3.1 backend (ADR-0002),
which writes **KDBX 4.0** files with its defaults (AES-256, Argon2id at 128 MiB / 6 iterations / 4 lanes, ChaCha20 protected
stream, gzip); a database opened as KDBX 3.1 or 4.1 is therefore written back as KDBX 4.0.
Saves write a temporary file, take a timestamped backup and atomically replace the database;
the core save protocol additionally verifies the rewritten bytes against the data model and
is covered by `crates/core/tests/save_protocol.rs`. The CLI interop/`rewrite` path used for
evidence preserves the input version instead.

**Interop evidence:** fixture round trips run against `keepassxc-cli` in CI; the official
KeePassLib is checked locally (canonical structural hash unchanged); the app-edited fixture
was reopened in KeePass 2.x during the G3 manual gate (2026-11-08), and a real KDBX 4.1
database was rewritten and reopened by KeePass 2.x at G0 on 2026-10-25 (10 entries
including 3 TOTP entries spot-checked, signed off).

**Limitations:** master password only (no key file, no challenge-response, no Windows
Hello); no attachments/history/recycle-bin management UI (data preserved); single database;
English UI. The full matrix, including untested states, is in `docs/compatibility.md`.

### Notes

- The binaries are unsigned, so Windows SmartScreen may warn; releases ship with SHA-256
  checksums and build-provenance attestations only (no code signing, no MSIX, no
  auto-update).
- `v1.0.0` is not published yet: the 30-day acceptance runs first, then the tag/release is
  created and the obsolete `v0.1.0` draft is removed (see `docs/release.md`).
- Windows 11 x64 only; Linux is planned for v1.x.

## [0.1.0] - 2026-10-28

First tagged build. The portable package and the Inno Setup installer were produced by the
release workflow and attached to a GitHub Release **draft**; the release has not been
published.

### Added

- Rust core: KDBX 3.1 / 4.0 / 4.1 read and write through the vendored `keepassxcr` 0.3.1
  backend (ADR-0002) with the save protocol (temp file, timestamped backup, verified atomic
  replace); merge/sync decision model with an 18-scenario matrix; TOTP parsing and code
  generation for KeePass `TimeOtp-*` and `otpauth` fields.
- C ABI + JSON FFI (commands for open/list/save/TOTP) and a CLI harness used by CI, interop
  and acceptance checks.
- WinUI 3 Windows shell: open a local file or a database from a WebDAV URL, unlock, browse
  groups and entries, create/edit/move/delete entries, save, display and edit TOTP (QR image
  / clipboard / `otpauth` import), timestamped local backups with restore, WebDAV sync with
  conflict copies and Keep local / Keep remote / Keep both handling, tray icon, settings,
  auto-lock, clipboard auto-clear, theme, single instance, and a non-UI `--selftest` mode.
- Test corpus: 25 public KDBX samples from keepass-rs (MIT) with a hash manifest; interop
  harness that round-trips fixtures and verifies them with `keepassxc-cli` (CI) and the
  official KeePassLib (local only, test-only).
- CI (`.github/workflows/ci.yml`): fmt, clippy and tests on Linux/Windows; `keepassxc-cli`
  interop; the 18-scenario sync matrix; C harness + C# P/Invoke smoke; Windows app build,
  headless service tests and application self-tests; secret scan; `cargo deny`; public
  allowlist check.
- Release pipeline (`.github/workflows/release.yml`): tagged `v*` builds publish the
  portable folder and zip, build the Inno Setup installer, write `SHA256SUMS`, generate an
  SPDX SBOM, attest build provenance and upload everything to a GitHub Release draft.
- Public documentation set: README, architecture, compatibility, testing and release notes,
  third-party licenses, contributing and security policy.

### Notes

- KDBX compatibility: the write path rewrites databases through `keepassxcr`; the manual G0
  gate on 2026-10-25 rewrote a real KDBX 4.1 database and verified it with the official
  KeePassLib (canonical hash unchanged), `keepassxc-cli` and KeePass 2.x (10 entries,
  including 3 TOTP entries, spot-checked by the maintainer).
- Credential support: master password only (no key file, no challenge-response).
- Binaries are unsigned; the installer is an Inno Setup `setup.exe` (per-user).
- The GitHub Release is a draft pending manual acceptance; no release decision has been
  made yet.
