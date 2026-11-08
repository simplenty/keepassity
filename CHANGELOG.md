# Changelog

All notable changes are documented here. Format: Keep a Changelog. Versioning: SemVer.
KDBX write-path changes must state the compatibility impact.

## [Unreleased]

Changes after the `v0.1.0` tag (2026-10-28). The Windows UI was reworked through
2026-11-08; nothing in this section is released yet.

### Added

- Entry list: sorting, filtering and empty states; context menu actions; scrollbar and
  hover/selection styling aligned with WinUI.
- Detail pane rebuilt around a new spacing rhythm; entry forms open in the detail column
  and empty fields are hidden; TOTP setup moved into the edit form.
- Entry editing from the context menu, entry drag-and-drop onto groups, and whole-group
  create/rename/delete with tombstones so deletes survive sync.
- Collapsible NavigationView workspace with a title-bar back button and hamburger pane,
  page/panel/detail transitions, and a unified first-run/unlock start page that separates
  unlocking from choosing a database source.
- Inline settings panel replacing the settings dialogs; settings are cancellable and keep a
  single navigation selection.

### Changed

- Theme is no longer forced to light by launchers and shortcuts; it follows the system
  setting.
- Recycle-bin and template groups are pinned in the navigation, and recycle-bin entries are
  hidden from the main list.
- The tray menu is a native WinUI `MenuFlyout`: it opens from the notification icon
  (below-left/right of the icon, flipped at screen edges), follows the app theme, and
  disables Lock while the vault is locked.
- Tray tooltip reports locked/unlocked state and the open database name.
- Tray Lock locks in place instead of raising the window first, so decrypted content is
  never flashed on screen by the Lock action.

### Fixed

- The tray menu no longer renders as a framed window with caption buttons, a near-black
  background, a fixed 240x156 dead strip, or a clipped first-open menu with a scrollbar.
- Closing the tray menu with its own window close button no longer leaves a dead menu for
  the rest of the session.
- Release executables are kept at medium integrity so installs and runs work from
  low-integrity workspace folders.

### Fixed

- Deleted groups stay deleted across syncs, and no-op sync reloads are skipped.
- Entry list scroll position is kept after drag/move operations and drag targets are
  marked.

### Notes

- No version bump yet: publishing `0.1.0` (or the next version) is a pending human
  decision. The GitHub Release for `v0.1.0` is still a draft.
- Known gaps at this point: unsigned binaries (SmartScreen), WebDAV verified against a
  local mock server but not yet against a real Jianguoyun account, graphical
  install/uninstall not yet through manual acceptance, no code signing, single database,
  English UI, master-password credentials only (no key file / challenge-response).
- KDBX compatibility: the save format is unchanged, but whole-group deletion now writes a
  tombstone (deleted-object record) so a deleted group stays deleted across merges and
  WebDAV syncs.

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
