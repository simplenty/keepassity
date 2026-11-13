# Changelog

All notable changes are documented here. Format: Keep a Changelog. Versioning: SemVer.
KDBX write-path changes must state the compatibility impact.

## [Unreleased]

- **Save temporary files use the core protocol name and are cleaned on unlock:** `AtomicFile`
  now writes `<database>.keepassity-tmp-<nonce>` (the same infix as the core `save_atomic`
  protocol) and removes crash-left siblings/legacy dot-prefixed temps before a vault opens.
  Other databases' temps and the live file are never touched.
- **Backup files are created atomically:** the recovery copy is written to a flushed temporary
  sibling and moved into place, so a crash mid-copy cannot leave a truncated file with a real
  backup name. A name collision retries with the next suffix instead of overwriting.
- **Release artifacts are checksum-verified:** `SHA256SUMS` now includes the SBOM, uses LF
  endings for `sha256sum -c` on any platform, and the release workflow recomputes every digest
  before upload. The release gate also runs `fmt`, `clippy` and the public-file allowlist.
- **Settings can no longer be silently downgraded:** a settings file from a newer schema is
  loaded but treated as read-only, so an older build cannot overwrite fields it does not
  understand. An unreadable settings file is copied to `settings.json.corrupt-<timestamp>`
  before defaults are used, preserving the evidence instead of destroying it.
- **TOTP countdown rules moved into a view model:** `TotpTiming` owns the remaining-second
  clamp, 6/8-digit grouping, the "request a new code" decision, the three-second pre-fetch
  window and the near-boundary timer interval. The shell keeps the timer and core calls; the
  off-by-one window behavior now has headless tests.
- **Group navigation ordering moved into a projection:** `GroupNavigationProjection` now builds
  the "All entries / normal groups / Templates / Recycle Bin" order outside the shell, with
  counts and root/blank filtering, and is covered by headless tests.
- **Entry editor normalization moved into a view model:** `EntryEditorViewModel` owns the
  untitled-entry fallback, trimmed fields, comma-separated tag parsing/deduplication and TOTP
  defaults/clamping (SHA1/SHA256/SHA512, 6/8 digits, 1–300 s). Create and edit now use the same
  rules; the "empty secret removes TOTP" decision is tested instead of living in two dialogs.
- **Detail panel state moved into the view model:** `EntryDetailViewModel` owns the title/group,
  field visibility, tag formatting, custom-field selection and the password mask/reveal state;
  the XAML detail panel binds the text and visibility through `x:Bind`. Password reveal no longer
  writes UI controls directly, and the mask/divider rules now have headless tests.
- **Entry list presentation moved out of the shell:** row display logic (title/initial/avatar color,
  username/URL/group fallback, TOTP badge), sorting, the TOTP filter and the row fingerprint now
  live in `Keepassity.ViewModels` and run under the headless test harness. The shell keeps only a
  thin `EntryRowPresenter` that turns the view model's color string and boolean into WinUI types;
  the group navigation item is also a pure ViewModel type.
- **Uninstall no longer deletes user files:** the uninstaller used to remove the whole
  installation directory (`filesandordirs {app}`), which would also delete a vault, backup or
  note a user put next to the executable. Inno Setup now removes only the files it installed;
  a directory containing user data is kept. A broken single-instance mutex is also logged and
  ignored instead of crashing startup; the per-vault lock remains the writer guard.
- **Deleting a group is recoverable:** a group and everything inside it is moved to the Recycle
  Bin (the whole subtree stays intact) instead of being permanently removed; when the Recycle
  Bin is disabled the old tombstone-based permanent path is used. The confirmation dialog and
  toast now say which happened.
- **Every save is verified before it can reach disk:** after serializing, the core decrypts and
  parses the new ciphertext with the cached KDF key; `kp_save` and `kp_sync_merge` return an
  error instead of bytes if the output is not a readable database. The check costs a decrypt,
  not another KDF run, and the live file is only replaced after it succeeds.
- **Saving reuses the transformed KDF key:** after the first save of an unlocked vault, later
  saves reuse the KDF salt and transformed key and only draw a fresh master seed, IV and
  protected-stream key. The KDF cost/type and format version still match the source file, so the
  compatibility guarantees are unchanged, but routine saves no longer spend hundreds of
  milliseconds on Argon2/AES-KDF. The cache is dropped when the vault locks or closes.
- **One writer per vault, and no silent external overwrite:** opening a vault takes a cross-process
  lock, so a second Keepassity window cannot save over the first. Before every save (and before a
  remote download or merge replaces the local working copy) the app compares the file's SHA-256
  with the bytes its model was opened from; if another program changed the file, the save is
  refused and the user is told to reopen, instead of overwriting their work. Settings, recent
  databases, sync state and DPAPI credential files now use flushed, atomic writes too.
- **Saving keeps the KDBX 4 outer format:** the writer now carries the source version (4.0/4.1),
  outer cipher, compression, KDF type and cost, and the `PublicCustomData` header field through a
  save. A KDBX 4.1 vault is no longer downgraded to 4.0, and an AES-KDF vault is no longer
  silently rewritten with the writer's Argon2id defaults. KDBX 3.1 sources still follow the
  documented upgrade to KDBX 4.0. This also makes the core's verified rewrite path accept the
  model writer's output.
- **Durable saves and deletes:** the save path now writes a flushed sibling temporary file, keeps the
  previous database as a timestamped backup, and replaces the live file atomically; failed or
  interrupted saves leave either the old or the new database, never a partial file. Deleting an
  entry now creates the Recycle Bin group when the database enables recycling but has no usable
  group UUID, so a new database no longer silently turns a delete into an unrecoverable tombstone
  while the UI promises a recycle bin.
- **Settings is now an MVVM panel:** the Settings/About page is a bound `SettingsPanel`
  backed by `SettingsViewModel`; appearance, language, clipboard, auto-lock, backup retention
  and sync-on-save still apply immediately, while the WebDAV source is committed on Save.
  Stored-credential state, HTTPS validation, DPAPI storage, diagnostics export and the
  About/license text moved out of the window code-behind without changing the user-visible
  flow.
- **Sync and restore fail closed:** a WebDAV upload that receives a redirect (for example a 303)
  is rejected instead of being replayed as a body-less GET whose 200 response looked like a
  successful database upload; non-loopback plain HTTP is refused because Basic authentication
  would expose the WebDAV password. Remote bytes applied to an unlocked vault are decrypted
  with the current master password before the local database is replaced, a corrupt sync
  sidecar is treated as dirty instead of clean, and a sync baseline without a content hash is
  rejected. Backups are ordered by their creation name rather than the copied file's timestamp,
  are flushed before use, and an empty restore payload is rejected.
- **Password generator page:** Generate in the entry form now opens the generator in the right pane
  instead of a flyout, so the result, entropy, hint, options and actions use the full column. Back
  returns to the form with everything already typed; Done returns to the form with the generated
  password in the field, and Save stores the entry. The generated password is shown in a read-only
  preview with a Copy button, and changing an option regenerates immediately. This also removes the
  old flyout's clipping: the page cannot flip above the field or hide its buttons.
- **Detail pane refreshes after a password-only edit:** saving an entry whose visible list fields did
  not change (for example right after using the password generator) used to leave the detail pane on
  the previous entry content, so revealing or copying the password returned the old secret until
  another entry was selected and reselected. The save now refreshes the selected entry's detail even
  when the middle column keeps its rows, and that refresh does not replay the entry animation.
- **Saving keeps the vault metadata:** a save now writes back the fields the reader parses but the
  writer used to drop — entry and group icons (built-in and custom icon references), entry colors,
  override URLs, Auto-Type settings, custom data, group notes and auto-type/search flags, the meta
  memory-protection, default-user-name, custom-data and custom-icon elements, per-field protection
  and the recycle-bin timestamp. The merge compares semantic content (ignoring field order, tag
  order, empty fields, protection flags and unknown XML) and treats a side that lost optional
  metadata as the same content, so a vault saved by an earlier build no longer produces conflict
  dialogs for entries that were never edited, and an unresolved conflict keeps its single local /
  remote copy pair instead of writing another one on every sync attempt. Compatibility: the saved
  file stays KDBX 4.0, upgraded to 4.1 only when a custom icon carries a name or modification time;
  files written by other KeePass clients remain readable and mergeable.
- **Product-name casing:** product text in the app UI, the installer/Apps & Features display
  name and the `.kdbx` file-association label now read **Keepassity**. File and folder names,
  environment variables, the AUMID/ProgID and URLs keep the lowercase `keepassity` spelling.
  The localization gate rejects a lowercase product name in a UI catalog key or translation, so
  the convention cannot silently regress.
- **Smooth navigation pane toggle:** collapsing or expanding the navigation pane resizes the
  workspace columns frame by frame instead of snapping them to their final width while only
  the pane slides. The pane length is animated in layout (200 ms opening, 180 ms closing,
  eased out), the compact icon rail is unchanged, and the system "show animations" setting
  falls back to the plain toggle. This removes the visible jump between the pane and the
  entry list / detail columns.
- **UI language switch (English / Simplified Chinese):** the Windows shell is now localized.
  Settings (and the unlock page) offer "use the system language", English and 简体中文; the
  choice is persisted in `settings.json` and applies immediately without restarting or
  re-unlocking — XAML labels, code-built panels, dialogs, toasts, tooltips, accessibility
  names and the tray menu all follow the selected language. Translations are embedded JSON
  catalogs (`Keepassity.Core/Localization/*.zh-CN.json`) keyed by the English source text, so
  an untranslated string falls back to readable English. New gates: `tools/check-localization.ps1`
  and catalog tests in `tests/app-core` (see `docs/testing.md`).
- **Entries can leave a folder again:** the virtual "All entries" row is a drop target that moves
  an entry to the vault root, and the Move dialog offers `Top level (no folder)`. Dropping an
  entry on the group (or top level) it already lives in is refused with a hint instead of a
  pointless save and a row that disappears until the next reload.
- **WebDAV 412 back-off retry (SY-08):** when the remote file changes while an upload is being prepared (HTTP 412), the app now retries three times with the same 0.5 s / 1.5 s / 4 s back-off schedule as the core sync state machine, then resolves the change through the three-way merge/conflict path - the remote is never overwritten silently.
- **WebDAV server presets and app-password guidance:** the Open-from-WebDAV dialog offers URL presets for Nextcloud/ownCloud, Synology WebDAV and Jianguoyun, and every WebDAV entry point (open, new database, settings, sync) refuses plain `http://` URLs and reminds two-factor accounts to use an app password.
- **WebDAV TLS and self-signed certificates:** WebDAV URLs now require `https://` (Basic
  authentication must not travel over plain HTTP). A server certificate Windows does not trust
  is shown to the user with subject, issuer, expiry and SHA-256 fingerprint; only an explicit
  confirmation pins that exact certificate for the host, and a changed certificate is
  re-confirmed. Certificate validation is never disabled globally. Covered by settings,
  client and certificate-probe checks in `tests/app-core`, plus a manual check against a real
  TLS server.
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
  created. No GitHub Release exists today; the obsolete `v0.1.0` draft and tag were removed
  on 2026-11-09 (see `docs/release.md`).
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
