# Contributing

Thanks for the interest. This is a self-use project published openly; the maintainer does not
commit to SLAs or timelines.

## Build

- Rust stable (pinned in `rust-toolchain.toml`) + `rustfmt` + `clippy`.
- .NET SDK: CI installs **8.0.x**. `global.json` pins SDK `8.0.100` with
  `rollForward: latestMajor`, so a newer local SDK also builds the shell — for example .NET
  10.0.402 is what the maintainer currently uses locally. The app targets
  `net8.0-windows10.0.19041.0` and publishes self-contained for `win-x64`.
- Visual Studio with the Windows App SDK C# templates (WinUI 3 shell); the target project is
  `apps/windows/Keepassity.sln`.
- The native FFI library must be built first:
  `cargo build -p keepassity-ffi --release`.
- Inno Setup 6 only if you build the installer (`scripts/build-installer.ps1`); the release
  workflow installs it on the CI runner.

## Before you open a PR

1. Keep changes scoped and explain what you verified.
2. Data-path changes (KDBX read/write, crypto, backup/sync, installer) require tests and
   interop evidence; describe the risk and how you checked for data loss.
3. Run `cargo fmt --all -- --check`, `cargo clippy --workspace --all-targets -- -D warnings`,
   and `cargo test --workspace`. `scripts/test.ps1` also builds the Windows app and runs the
   headless service tests; add `-Full` to run the application self-tests.
4. The UI is English-only: run `powershell -NoProfile -ExecutionPolicy Bypass -File
   tools\check-ui-strings.ps1` if you touch `apps/windows/` (it rejects hard-coded CJK text;
   see `docs/testing.md`).
5. Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-public.ps1`
   before pushing. It enforces `public.allowlist`; process documents, real vaults and
   credentials never enter this repository.
6. AI-generated code is fine, but the human submitting the PR is responsible for verifying
   behavior — say in the PR what you verified manually.

## Licensing of contributions

Contributions are accepted under GPL-3.0-or-later. By submitting a PR you agree to license it
under those terms. Do not paste code from AGPL or unlicensed sources.
