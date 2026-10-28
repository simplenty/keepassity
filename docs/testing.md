# Testing

Tests are grouped by layer. Unit and integration tests live next to the code they cover;
shared fixtures and cross-language/system checks live under `tests/`.

| Layer | Where | Command |
|---|---|---|
| L0 unit | `crates/*/src/**` (`#[cfg(test)]`) | `cargo test --workspace` |
| L1 integration | `crates/core/tests`, `crates/ffi/tests`, `crates/cli/tests` | `cargo test --workspace` |
| L2 contract | `tests/contracts` (FFI JSON and sidecar/sync-state schemas) | `cargo test -p keepassity-ffi` |
| L3 system | `tests/ffi-csharp`, `tests/app` and `scripts/selftest.ps1` | `dotnet run --project tests/ffi-csharp -c Release`, `./scripts/selftest.ps1 ...` |
| L4 interop | `tools/interop` | `./tools/interop/verify.ps1 -FixturesOnly -KeepassXcPath keepassxc-cli` |
| L5 manual | release checklist in the private process repo | G3 checklist |

## Fixtures

- `tests/fixtures/keepass-rs/` contains public samples (MIT) used by parser/writer tests.
- The fixture list, passwords and expectations are documented in
  `tests/fixtures/README.md`; the file is public and contains only test passwords.
- A real vault must never be added to the repository. `scripts/check-public.ps1` and
  the repository `.gitignore` guard against accidental commits.

## Adding a test

1. Prefer a unit test in the module you changed.
2. Add an integration test when the behaviour crosses module or crate boundaries.
3. Run the narrowest command first, then `cargo test --workspace` before committing.
4. Do not commit failing or ignored-by-default tests without a documented reason.

## CI

`.github/workflows/ci.yml` runs:

- `core` — fmt, clippy, tests on Ubuntu and Windows;
- `interop` — fixture round trip against keepassxc-cli;
- `sync-matrix` — the 18-scenario sync decision matrix;
- `ffi` — C harness and C# P/Invoke smoke on Ubuntu;
- `app` — Windows build of the WinUI shell and application self-tests;
- `supply-chain` — cargo-deny;
- `public-hygiene` — public allowlist.
