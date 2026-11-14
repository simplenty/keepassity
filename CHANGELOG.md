# Changelog

All notable changes are documented here. Format: Keep a Changelog. Versioning: SemVer.
KDBX write-path changes must state the compatibility impact.

## [Unreleased]

- **Creating a database uses the durable write protocol, and creating one on WebDAV works
  again:** a new local database is written through a flushed temporary file and a create-only
  move, so a file another program creates in the same moment is never replaced; a WebDAV create
  claims the local working copy before the remote file exists, then writes the working copy
  durably and records the clean sync baseline through `NewDatabasePreparer`. The flow had been
  routed through the sync write guard with a path that was not the open vault (introduced with
  the `VaultSession` consolidation), which made a WebDAV create fail after the upload.
- **Release builds no longer leave the repository dirty:** the two library projects stopped
  generating their own `packages.lock.json`; their package graph is already pinned by the
  application and test lock files, and the duplicated library locks flipped between a RID and a
  non-RID restore, so every `scripts/publish-windows.ps1` run modified tracked files (and a plain
  restore reported them as modified again because of line endings).
- **An unreadable remote copy can be repaired:** when the server-side database cannot be opened
  with the master password (a failed upload from another client, a server-side truncation, a
  different encryption), a sync used to fail and every later sync failed at the same point — the
  local vault stayed safe but the remote copy remained broken with no way to fix it from the app.
  The run now pauses without touching the local vault and offers an explicit repair dialog that
  replaces the unreadable remote with the verified local copy; nothing is uploaded unless the user
  confirms.
- **Large vaults no longer time out during sync:** every WebDAV request shared the HTTP client's
  fixed 45-second timeout, so a database with large attachments could not be downloaded or
  uploaded on a slow link and the save failed on every attempt. Metadata requests keep the
  45-second bound, whole-file transfers now get thirty minutes, a stalled transfer still aborts
  with a clear "timed out" message, and caller cancellation is still reported as cancellation.
- **Conflict copies no longer pile up across restarts:** the local/remote copies for an unresolved
  conflict were named with the current timestamp, so every sync against the same unresolved pair
  (or every launch after a cancelled dialog) wrote another pair into the vault folder, and a new
  process could never match the older ones. Names now derive from the local and remote content
  hashes and a sync locates and reuses the matching pair, including in a fresh process with no
  in-memory state. A same-named file with different bytes is never overwritten; the new copy gets
  a numeric suffix.
- **The last write is guarded too:** a save, sync replacement or backup restore checked that
  the file had not changed outside Keepassity, then wrote it; another program changing the file in
  the gap (core serialization, KDF, temporary write, backup copy — seconds for a large vault) could
  still be overwritten. The replace step now takes a last-moment check after the backup is taken
  and immediately before the rename: a changed database aborts with `VaultChanged`, the temporary
  is removed, and the live file plus the fresh recovery backup are kept.
- **Logs are bounded and no longer lose lines to sharing:** one day's log could grow without
  limit (an error loop could fill the disk), a second instance's append failed on a sharing
  violation, and the diagnostics exporter could fail to read the newest log while the app was
  writing it. Daily logs now rotate to a single `.1` sibling at 5 MB, pruning enforces both the
  fourteen-day window and a 50 MB folder ceiling, and every handle shares read/write/delete so
  appends and diagnostics exports always see the current file.
- **Crash residue is cleaned up:** a crash during a backup copy, a backup restore or a sidecar
  write could leave `.tmp-` / `.keepassity-tmp-` files behind forever; only the save temporaries
  next to the database were cleaned when a vault was opened. Opening a vault now also removes
  crash-left restore and backup temporaries, and the first launch of the day removes stale
  protocol temporaries anywhere under the application data folder (fresh files are kept, so a
  concurrent test instance is never disturbed).
- **A save during a sync is no longer overwritten:** a background sync captured the local file
  when it started and then replaced it with downloaded or merged bytes after the network round
  trip, so a save that landed in between could be silently undone and the reloaded window would
  show the reverted data. The coordinator now remembers the run's local file hash (updated when it
  writes the file itself) and checks it before every replace and before writing an upload
  snapshot: the newer local save is kept, the vault stays dirty and the next sync merges both
  sides. Fake-network scenarios cover the download, merge and upload paths.
- **Diagnostics exports are durable, unique and bounded:** two exports in the same second collided
  on the archive name (the second one failed), the finished zip was moved into place without a
  disk flush, and every export stayed in the data folder forever. The archive now carries a
  millisecond timestamp with a unique fallback suffix, is written to a flushed temporary and moved
  atomically, crash-left `.tmp` files are removed, and only the newest ten archives are kept.
- **The WebDAV cache stays bounded:** every remote database ever opened left its working copy and
  up to twenty backups under the application data folder, and nothing removed them. A startup pass
  now drops clean working copies that have not been touched for ninety days (they can be downloaded
  again), plus backups, metadata and sync state whose working copy already vanished. Copies with
  unsynced edits, copies whose hash does not match the recorded baseline, copies without source
  metadata and the current target are never removed, and the recent-databases list forgets a
  working copy that the pass deleted instead of offering that now-missing file on the unlock
  screen. The pass only runs when this process owns the single-instance mutex before any window
  opens.
- **A crash is noticed on the next launch:** a small run marker is written when the window
  starts and removed on a clean close. When it is still there and its process is gone, the unlock
  surface reports that the previous session ended unexpectedly and points at the backups, so a
  crash or power loss no longer looks like a normal start. A marker from a still-running instance
  is ignored, a malformed marker counts as an unclean run, and writing the marker is atomic and
  best effort (the `--selftest` path never creates one).
- **Saves work on FAT/exFAT and network shares:** the atomic replace used `File.Replace`, which
  is unavailable on volumes without that primitive, so every save of a vault on such a volume
  failed. The placement now falls back to a same-volume rename (still a rename, so a crash leaves
  either the old or the new complete database) with the backup taken just before as the recovery
  point; the backup-restore path uses the same fallback.
- **Shared-folder vaults coordinate across machines and Windows accounts:** the lock lived only
  under the per-account application data folder, so two machines or two Windows users opening the
  same database on a network share could both hold it and silently overwrite each other's saves.
  A writable vault folder now also holds an exclusive `<database>.lock` inside its
  `.keepassity/locks` folder; a lock held there refuses the open, releasing removes it, and the
  OS drops it on a crash. A read-only folder keeps the account-only lock with a log entry.
- **A damaged database can be recovered from the unlock screen:** opening a corrupt database used
  to leave only the error banner and an external backup-folder button, because the in-app backup
  list needs an open vault. The unlock failure now offers the newest local backup that the entered
  master password still opens; its bytes are verified with the core before anything is replaced,
  the damaged file is kept as a before-restore backup, and the unlock is retried. Corrupt,
  unreadable and wrong-password candidates are skipped (`VaultRecovery`).
- **Backup restore verifies the exact bytes it writes, and the self-test covers the round trip:**
  the restore flow verified a backup by opening its path in a probe, so an external change between
  the read and the probe could make it validate a different snapshot than the bytes it replaced.
  It now runs `kp_verify` on the byte array that will be restored, before the pre-restore save and
  the atomic replacement. The `--selftest` smoke test now creates a backup, makes and saves a
  change, restores the backup, reopens and asserts that the change is gone and the overwritten
  bytes survive as a recovery point.
- **A sync read can no longer make a save fail:** the sync coordinator read the live database file
  directly, so its read handle could collide with a concurrent atomic replace and make the save
  fail with a sharing violation. Reads now go through the vault session's write gate
  (`ReadBytesAsync`), so a sync always hashes a complete database, a save never races the reader,
  and the shutdown drain counts the read like any other in-flight operation.
- **A failing TOTP refresh cannot crash the window:** the display timer pulls the code from the
  core, and a locked vault, a deleted entry or a rejected secret could throw out of the async
  timer callback. The view model now catches the failed request, marks the code as unavailable
  (the shell shows `TOTP code unavailable` instead of claiming no TOTP is configured) and lets the
  next tick retry; the timer callback is also guarded as a last resort.
- **Locking waits for an in-flight save:** lock (manual button, tray menu, inactivity timer or a
  Windows session event) used to lock the core immediately; a save that was already queued or
  running then failed, leaving the previous complete file and losing the edit being written. Lock
  now waits up to ten seconds for the vault session to finish writing; a manual or inactivity
  lock stops with an informational message if the write is still stuck, while a session lock
  proceeds anyway so a locked workstation never keeps an unlocked vault. A sync that reaches its
  local replace phase after the vault locked now fails without touching the file instead of
  replacing the database under a locked core.
- **A failed save keeps the session alive:** a disk or permission error no longer escapes the
  save path (from an `async void` caller it could take the process down and with it the only
  in-memory copy of the edit). The failure is logged and shown as an error toast, the in-memory
  model keeps the change, the write gate is released, and the next successful save persists it.
  Callers check the result so a success toast is not shown after a failed write, and the
  backup-restore flow aborts instead of replacing the file when its pre-restore save fails.
  Recording the dirty sync marker is best-effort now, so a sidecar IO error cannot turn a
  successful save into a reported failure.
- **Closing the window waits for an in-flight vault write:** `VaultSession` now tracks queued and
  running saves, sync replacements and restores, and the shell drains them (bounded to ten
  seconds) before the closed handler releases the lock, disposes the write gate and the core. An
  edit whose save was already running is therefore not the one cut off by exit; `AtomicFile` still
  guarantees the file itself is always a complete database, and a write that outlives the drain no
  longer has its gate disposed underneath it.
- **The WinUI shell project is `Keepassity.App`:** the prototype name `Keepassity.Mock` is gone
  from the project folder, namespace, XAML classes, solution, publish/self-test scripts,
  installer icon path, CI and docs; the shipped binary stays `Keepassity.exe`, and the pre-1.0
  `Keepassity.Mock.exe` cleanup in the installer/verify script is kept deliberately for upgrade
  semantics. `Keepassity.Core` also gets its real root namespace. A clean self-contained
  publish and an installer compile were run against the renamed project.
- **The publish script works under Windows PowerShell 5.1 again:** its version fallback read
  `Directory.Build.props` through the PowerShell XML adapter, which returns an `XmlElement`
  there, so the version property became `System.Xml.XmlElement`, restore failed with a
  swallowed MSBuild error and the output folder was misnamed. The script now reads the DOM
  `InnerText`; a clean self-contained publish and installer compile were verified afterwards.
- **Vault writes share one guard:** `VaultSession` in `Keepassity.Core` now owns the
  cross-process lock, the in-process write gate and the content fingerprint, and exposes save,
  sync-replace and backup-restore as the only write paths. The window no longer composes the
  three guards by hand at each call site, so a save, a merge download and a restore cannot drift
  into different safety rules. A locked vault also skips background sync instead of failing
  against a locked core. The headless suite covers lock ownership, external-change refusal,
  save serialization, atomic replacement with backup, restore ordering and release semantics
  (29 checks).
- **A WebDAV download never replaces the vault before it is verified:** opening a database from
  WebDAV now downloads the bytes into memory and keeps them pending; the master password opens
  those exact bytes, and only then does `RemoteVaultOpener` write them to the local working copy
  (durable atomic replace with a backup) and record a clean sync baseline. A wrong password, a
  corrupted download or an HTML error page therefore leaves the previous working copy and its
  baseline untouched, instead of clobbering the vault and requiring a backup restore. The
  headless suite covers the ordering with fakes and adds an end-to-end check with the real core
  and a loopback WebDAV server.
- **The WebDAV sync protocol is a tested application service:** `SyncCoordinator` in
  `Keepassity.Core` now owns the whole run (decision, conditional upload with the 0.5/1.5/4 s
  back-off, verified download, three-way merge, conflict copies and the three conflict choices,
  then the baseline commit) behind `ISyncRemote`, `IVaultDocument` and the expanded `ICoreClient`
  ports. The window keeps the WebDAV connection, the conflict dialog and the toast texts, which
  shrank it by ~500 lines. Because the ordering rules are now outside WinUI, 38 headless
  assertions pin them down: verify before replace, local write before upload, core baseline
  commit before the sidecar says clean, 412 retries and the merge fallback, conflict-copy reuse
  and dirty-on-failure. A clean sidecar is also cross-checked against the local file, so a crash
  between the save and its baseline write cannot turn the next sync into a silent no-op. The FFI
  wire DTOs moved to Core with the coordinator they describe, and the localization gate learned
  the coordinator's status strings.
- **Sync baseline corruption is preserved:** an unreadable `state/*.json` sidecar is copied to `.corrupt-<timestamp>` before the vault is treated as dirty. `SyncStateStore` accepts an explicit directory for tests, and the headless suite covers round trip, missing-baseline clean state, malformed hash and corrupt-file preservation.
- **WebDAV downloads also reject redirects:** the download/metadata HTTP client no longer
  follows redirects (the upload client already did not). A 3xx response fails the sync instead
  of sending Basic credentials to the redirect target or replacing the working copy with a
  different resource. The mock-server test asserts one request and a 302 error.
- **The application runs on .NET 10:** the shell targets `net10.0-windows10.0.19041.0`; Core, ViewModels, the app-core harness, FFI smoke test and maintenance drills target `net10.0`. Windows App SDK 1.8 and the existing packages build and run unchanged; self-contained publish, installer compilation and `--selftest` were verified on the installed .NET 10 runtime. CI/CodeQL/release use 10.0.x and `global.json` pins SDK 10.0.100 (`latestFeature`).
- **Conflict copies are written atomically:** both the local and remote conflict copies now go
  through a flushed temporary file and atomic move (`AtomicFile.Copy` / `AtomicFile.Write`)
  instead of a direct copy, so a crash cannot leave a truncated "conflict" file that a user
  might later restore.
- **TOTP display is a view-model state machine:** `TotpDisplayViewModel` owns the selected entry,
  generation guard, displayed code, countdown, window rollover and the three-second pre-fetch
  (including adopting a pre-fetched code at the boundary). The shell passes a `TotpCodeSnapshot`
  source and clock, keeps only the DispatcherTimer and maps the result to controls. Entry
  switches cancel stale requests without showing the previous code.
- **Recent-database list is platform-neutral and corruption-safe:** `RecentDatabaseStore` moved
  from the shell to `Keepassity.Core`, gained a testable explicit path, and preserves an
  unreadable `recent-databases.json` as `.corrupt-<timestamp>` instead of silently discarding
  it. Newest-first order, de-duplication, the five-entry cap and removal are covered headlessly.
- **Unreadable stored WebDAV credentials no longer break the UI:** a truncated or
  wrong-user/machine DPAPI blob is moved to `<name>.bin.corrupt-<timestamp>`, logged, and treated
  as "no stored password" instead of throwing into settings or sync. The user can re-enter the
  password and the corrupt bytes are kept for diagnosis.
- **Vault writes are serialized inside one process:** a `VaultWriteGate` now covers saves,
  remote local-copy replacements and backup restores. Two async edit paths can no longer start
  overlapping atomic replaces where a slower, older snapshot lands last; the first save keeps
  its backup and the latest state wins.
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
