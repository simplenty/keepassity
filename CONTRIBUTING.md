# Contributing

Thanks for the interest. This is a self-use project published openly; the maintainer does not
commit to SLAs or timelines.

## Build

- Rust stable (pinned in `rust-toolchain.toml`) + `rustfmt` + `clippy`
- .NET 10 SDK + Visual Studio with the Windows App SDK C# templates (Windows shell; planned)
- Inno Setup 6 (installer; planned)

## Before you open a PR

1. Keep changes scoped and explain what you verified.
2. Data-path changes (KDBX read/write, crypto, backup/sync, installer) require tests and
   interop evidence; describe the risk and how you checked for data loss.
3. Run `cargo fmt --all -- --check`, `cargo clippy --workspace --all-targets -- -D warnings`,
   and `cargo test --workspace`.
4. AI-generated code is fine, but the human submitting the PR is responsible for verifying
   behavior — say in the PR what you verified manually.

## Licensing of contributions

Contributions are accepted under GPL-3.0-or-later. By submitting a PR you agree to license it
under those terms. Do not paste code from AGPL or unlicensed sources.
