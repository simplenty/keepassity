# Contributing

Thanks for the interest. This is a self-use project published openly; the maintainer does not
commit to SLAs or timelines. Participation is governed by
[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). For questions and support (not contributions), read
[SUPPORT.md](SUPPORT.md) first — there is no password recovery, and issues/PRs must never
contain secrets.

## Build

- **Rust:** pinned by `rust-toolchain.toml` to **1.99.1** (the same version CI uses, together
  with `rustfmt` and `clippy`). Update the pin and CI in the same change.
- **.NET SDK:** CI installs **8.0.x**. `global.json` pins SDK `8.0.100` with
  `rollForward: latestMajor`, so a newer local SDK is used automatically. The maintainer's
  machine-wide SDK installation (currently 10.0.402) is partially broken (missing workload
  resolvers), so local builds use a user-local `dotnet-install` copy in `%USERPROFILE%\.dotnet`
  that `scripts\test.ps1` prefers, and the shell is built **project-level** from the
  checked-in files. Do not scaffold or regenerate the app with `dotnet new`/WinUI templates;
  if your local SDK cannot build the checked-in solution, install the 8.0.x SDK and let
  `global.json` select it.
- **Windows App SDK / IDE:** Visual Studio with the Windows App SDK C# templates (WinUI 3
  shell); the target project is `apps/windows/Keepassity.sln`.
- **Native FFI library** must be built first:
  `cargo build -p keepassity-ffi --release`. The app copies `keepassity_ffi.dll` next to its
  output and cannot open a database without it.
- **Inno Setup 6** only if you build the installer (`scripts/build-installer.ps1`); the
  release workflow installs it on the CI runner.

## Before you open a PR

1. Keep changes scoped and explain what you verified. Link the issue/discussion the change
   comes from; do not open large features without prior discussion (v1.0 scope is frozen).
2. Data-path changes (KDBX read/write, crypto, backup/sync, installer) require tests and
   interop evidence; describe the compatibility impact and how you checked for data loss.
3. Run the local verification layers:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1
   # add -Full to also run the application self-tests
   ```

   That script runs `cargo fmt --all -- --check`, `cargo clippy --workspace --all-targets --
   -D warnings`, `cargo test --workspace`, the fixture manifest check, the Windows build and
   the headless service tests. You can also run the individual commands from the README
   ("Build from source" / "Testing").
4. UI text lives in the language catalogs: run `powershell -NoProfile -ExecutionPolicy Bypass
   -File tools\check-ui-strings.ps1` and `... -File tools\check-localization.ps1 -Strict` if
   you touch `apps/windows/` (no hard-coded CJK; every `Loc.T`/XAML key has a translation; see
   `docs/testing.md`).
5. Product-name casing: UI text (window title, About, tray menu, notifications, installer and
   file-association labels) spells the product **Keepassity**. The lowercase `keepassity`
   spelling is reserved for file/folder names, environment variables, identifiers (AUMID,
   ProgID) and URLs. `tools/check-localization.ps1` fails on a lowercase product name in a UI
   catalog key or translation.
6. Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-public.ps1` before
   pushing. It enforces `public.allowlist`; real vaults and credentials never enter this
   repository.
7. AI-generated code is fine, but the human submitting the PR is responsible for verifying
   behavior — say in the PR what you verified manually.

## Public repository boundary

Only files matching `public.allowlist` may be tracked. **Internal process documents — task
cards, requirements, acceptance checklists, decision records, private verification notes —
are not part of this repository.** Do not add them, copy them in, or link to private paths in
public files. If you add a new public file, add it to `public.allowlist` in the same PR so
`scripts/check-public.ps1` and the CI `public-hygiene` job stay green.

Never add a `.kdbx` file other than the registered public test fixtures, and never add real
passwords, TOTP seeds, WebDAV credentials or vault contents to code, tests, logs or fixtures.

## Code of conduct

Participation in this project — issues, discussions, pull requests, code review — is subject
to [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Report unacceptable behavior as described there.
Security issues have their own private channel: [SECURITY.md](SECURITY.md).

## Licensing of contributions

Contributions are accepted under GPL-3.0-or-later. By submitting a PR you agree to license it
under those terms. Do not paste code from AGPL or unlicensed sources.
