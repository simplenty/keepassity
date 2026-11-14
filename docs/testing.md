# Testing

Tests are grouped by layer. Unit and integration tests live next to the code they cover;
shared fixtures and cross-language checks live under `tests/` and `tools/`.

| Layer | Where | Command |
|---|---|---|
| L0 unit | `crates/*/src/**` (`#[cfg(test)]`) | `cargo test --workspace` |
| L1 integration | `crates/core/tests`, `crates/cli/tests` | `cargo test --workspace` |
| L2 FFI boundary | `crates/ffi/tests/kp_smoke.c` (C harness) and `tests/ffi-csharp` (C# P/Invoke smoke) | `cargo build -p keepassity-ffi --release`, then the CI `ffi` job commands below |
| L3 application services | `tests/app-core/Keepassity.Core.Tests` (headless console harness) | `dotnet run --project tests/app-core/Keepassity.Core.Tests -c Release` |
| L4 app self-test | `--selftest` mode driven by `scripts/selftest.ps1` | `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\selftest.ps1 -Fixture <kdbx> -Password <pw>` |
| L5 interop | `tools/interop/verify.ps1` (keepassxc-cli; optional KeePassLib) | `pwsh -File tools/interop/verify.ps1 -FixturesOnly` |
| L6 manual | acceptance checklist in the private process repo | not in this repository |

`scripts/test.ps1` runs the local layers in one go (fmt, clippy, `cargo test`, fixture
manifest, Windows build, headless service tests; `-Full` adds the application self-tests).

## What each project covers

- `crates/core/tests`: reader/writer round-trips (`kdbx_reader`, `kdbx_writer`), merge and
  save protocol, model/DTO, TOTP fields (including a keepassxc-generated TOTP fixture),
  error classification, property tests, the 18-scenario sync matrix (`sync_matrix`) and the
  generated 500-entry synthetic database (`synthetic_fixture`, `perf_synthetic_500`).
  Unit tests inside `crates/core/src` cover crypto, merge/sync decisions and TOTP.
- `crates/cli/tests`: CLI inspect/rewrite/verify commands and rewrite round-trips.
- `crates/ffi/tests/kp_smoke.c`: C harness that opens, lists, saves and reopens a fixture
  through the FFI C ABI; compiled against the static library in CI.
- `tests/ffi-csharp`: C# P/Invoke smoke (open/list/TOTP/save), run in CI.
- `tests/app-core/Keepassity.Core.Tests`: settings round-trip/schema and credential
  storage, sync-state round-trip and per-user keying, deterministic WebDAV working-copy
  paths, the WebDAV client against an in-process mock server (PROPFIND, GET, conditional
  PUT, 412, non-ASCII/percent-encoded paths), the sync coordinator's ordering and conflict
  rules with fake remote/vault/core ports (including a local save landing during a download,
  merge or upload snapshot, which must pause instead of being overwritten, and conflict copies
  reused by hash-derived names even after a restart), the WebDAV open ordering (pending bytes are only
  promoted after the master password opens them; one end-to-end case runs with the real core
  and the loopback WebDAV server), the vault session's single-writer rules (lock ownership,
  external-change refusal, save serialization, replacement/restore with backups, write-idle
  tracking used by the shutdown drain, a failed save leaving the session usable for a retry, reads
  serialized with writes), the database lock's two layers (shared-folder lock taken/removed, a
  lock held by another account refuses the open, an unwritable folder falls back to the account
  lock), the TOTP display state machine (window countdown, pre-fetch, entry
  switch, a failing source shown as unavailable rather than throwing), new-database
  preparation (create-only local write, WebDAV working copy with a clean baseline), DPAPI
  credential round-trip (Windows), backups (retention/restore, corrupted-backup rejection,
  the same-volume rename fallback for volumes without `File.Replace`,
  the run marker that reports an unclean previous session (clean close, dead process, live
  process, malformed marker, atomic write),
  the WebDAV cache cleanup (clean+old copies pruned with their backups/metadata/state and
  forgotten in the recent list, while dirty, recent, unknown-provenance, hash-mismatched and
  listed copies stay),
  the diagnostics export (unique names within one millisecond, readable archive without database
  paths/URLs/usernames, newest ten retained, crash-left temporaries removed),
  WebDAV per-request timeouts (short metadata bound, long transfer bound, stalled transfers fail
  with a clear message, caller cancellation preserved),
  unreadable-remote handling (the default pause keeps the local vault untouched and dirty, the
  explicit repair uploads the verified local copy and records a clean baseline),
  crash-residue cleanup (save/restore/backup temporaries at unlock and across the application
  data tree, fresh files and unrelated files kept),
  the last-moment replace guard (save, sync replacement and backup restore abort on an external
  change and keep the live file plus the recovery backup),
  log bounds (two writers on one day file, read-while-writing, size-cap rotation, retention and
  the total-size ceiling),
  recovery candidate selection: newest decryptable backup wins while corrupt, unreadable and
  wrong-password candidates are skipped, verified against the real core as well),
  diagnostics archive and logging.
- `apps/windows` `--selftest`: non-UI application smoke test (open fixture copy -> list ->
  TOTP -> save -> backup create/restore -> reopen -> verify, plus localization, launch-argument,
  single-instance payload and merge-fidelity checks). It exits non-zero on failure and writes
  counts/status only, never secrets.

## Fixtures

- `tests/fixtures/keepass-rs/` contains 25 public samples (MIT) used by the reader/writer,
  interop and self-test paths.
- `tests/fixtures/synthetic/` contains a generated 500-entry KDBX 4.0 database
  (Argon2id, non-ASCII titles/usernames, TOTP entries, history) written by the project's own
  keepassxcr backend through the ignored generator test in
  `crates/core/tests/synthetic_fixture.rs`; refresh it with
  `tools/fixture-gen/regenerate.ps1`. It contains no real keys.
- `tests/fixtures/manifest.json` records the SHA-256, size and test password (where
  applicable) of every fixture; `scripts/verify-fixtures.ps1` fails on a hash mismatch or an
  unregistered `.kdbx`.
- The fixture list and test passwords are documented in `tests/fixtures/README.md`; the file
  is public and contains only test passwords.
- A real vault must never be added to the repository. `scripts/check-public.ps1` and
  `.gitignore` (which only re-allows `tests/fixtures/**/*.kdbx`) guard against accidental
  commits.

## UI strings

The Windows shell ships English and Simplified Chinese and can switch at runtime (see
`apps/windows/Keepassity.App/Localization/`). All user-visible shell text lives in the
embedded catalogs under `apps/windows/Keepassity.Core/Localization/*.zh-CN.json`; the English
source text is the catalog key, so an untranslated string falls back to English instead of a
raw key. Code uses `Loc.T(...)` / `Loc.TLong(...)`; XAML uses the `loc:LocMarkup.Text`,
`ToolTip`, `Name` and `HelpText` attached properties.

Two gates protect this:

- `tools/check-ui-strings.ps1` scans `apps/windows/**/*.cs` and `*.xaml` (excluding `bin/`,
  `obj/` and test/fixture directories) for hard-coded CJK text and fails with `file:line`, so
  translations cannot leak into the sources. The only allowed hits are the two documented
  data-matching literals in `MainWindow.xaml.cs` (`回收站` and `模板`, the localized KeePass
  group names for the recycle bin and templates groups — compared against database content,
  never shown in the UI).
- `tools/check-localization.ps1` cross-checks the sources against the catalogs: every
  `Loc.T`/`Loc.TLong` call and XAML attached-property key must have a translation, composite
  format placeholders (`{0}`…) must match between key and translation, catalog files must
  parse and must not define the same key twice; unused entries, identical (untranslated)
  values and suspicious unwrapped UI literals are reported as warnings. It also fails when a
  catalog key or translation contains the lowercase product name: UI text spells the product
  **Keepassity**, while `keepassity` is reserved for file/folder names, environment variables,
  identifiers and URLs.

Run both locally with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-ui-strings.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-localization.ps1
```

## Adding a test

1. Prefer a unit test in the module you changed.
2. Add an integration test when the behaviour crosses module or crate boundaries.
3. Run the narrowest command first, then `cargo test --workspace` before committing.
4. Do not commit failing or ignored-by-default tests without a documented reason.

## CI

`.github/workflows/ci.yml` runs on pushes to `main` and on pull requests:

- `core` — fmt, clippy and `cargo test --workspace` on Ubuntu and Windows, plus the fixture
  manifest check on Linux;
- `interop` — fixture round trip against `keepassxc-cli`;
- `sync-matrix` — `cargo test -p keepassity-core --test sync_matrix` (18 scenarios);
- `ffi` — C harness (`kp_smoke.c`) and C# P/Invoke smoke, building the FFI library on Ubuntu;
- `app` — Windows build of the WinUI shell, headless `tests/app-core` tests, two
  `--selftest` cases, and an informational dependency-vulnerability audit;
- `ui-strings` — `tools/check-ui-strings.ps1` (no hard-coded CJK in shell sources) and
  `tools/check-localization.ps1` (catalog completeness/freshness);
- `secret-scan` — gitleaks;
- `supply-chain` — `cargo deny check`;
- `public-hygiene` — public allowlist check (`scripts/check-public.ps1`).

The release workflow (`.github/workflows/release.yml`) additionally runs `cargo test
--workspace`, the `tests/app-core` suite and `scripts/verify-fixtures.ps1` before packaging,
then self-tests the published application and builds the installer.

On a machine with Inno Setup, `scripts/verify-installer-runtime.ps1 -Version <v> -SourceDir
<published folder>` builds a throwaway installer with its own AppId and mutex (`TestMode`: no
shortcuts, no file-association registry writes, no Run entry), installs it, upgrades it, runs the
installed app's `--selftest` against a temporary data directory and uninstalls it. It asserts that
a file a user put next to the executable and the application data directory survive the
uninstall, while the real installation under `%LOCALAPPDATA%\Programs\keepassity` and the real
HKCU associations are unchanged.

## Fuzzing

`.github/workflows/fuzz.yml` is a separate scheduled workflow (03:00 UTC plus manual
dispatch; it never blocks pull requests). It fuzzes three core input surfaces with
`cargo-fuzz` on nightly Rust — `kdbx_parse`, `totp_uri` and `merge_json` — for 600 seconds
each, seeded from the public fixtures, and uploads logs/crash inputs as artifacts. Reproduce
locally with `cargo +nightly fuzz run <target>` from `crates/core/fuzz/` (see its README).

## Static analysis and supply chain

| Surface | What runs in CI | Notes |
|---|---|---|
| Rust | `cargo fmt --check`, `cargo clippy --workspace --all-targets -D warnings`, `cargo test --workspace` | clippy is the Rust static analyzer; CodeQL has no Rust support |
| Rust dependencies | `cargo deny check` (licenses, advisories, bans) in the `supply-chain` job | config in `deny.toml`, now including a sources policy |
| C# / WinUI | `dotnet build` of the solution + the project-level publish path, CodeQL `csharp` (`security-extended`, generated files excluded) | CodeQL only analyzes the C# side; there is no C# formatting gate yet (tracked as a v1.x idea) |
| App text | `tools/check-ui-strings.ps1` (no hard-coded CJK in shell sources) + `tools/check-localization.ps1` (catalog completeness) | UI text lives in the embedded language catalogs, never in the sources |
| KDBX/parsers | fuzzing (`crates/core/fuzz`, nightly job) | panics inside the guarded upstream reader are classified, see `crates/core/fuzz/README.md` |

CodeQL does not support Rust (checked 2026-11); the Rust side relies on clippy, the
fuzzer, `cargo deny` and the integration tests above.