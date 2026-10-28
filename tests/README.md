# Tests

Shared fixtures and cross-language test assets live here. Unit and integration tests
stay next to the Rust code they cover; see `docs/testing.md` for the full layer list.

## Layout

| Path | Purpose |
|---|---|
| `fixtures/keepass-rs/` | Public KDBX samples from keepass-rs (MIT), used by parser/writer tests |
| `fixtures/manifest.json` | Authoritative SHA-256, size, test password and usability of each fixture |
| `ffi-csharp/` | C# P/Invoke smoke test for the C ABI |
| `contracts/` (planned) | FFI JSON and sidecar/sync-state schema snapshots |
| `app/` (planned) | Windows system checks: application self-test and package smoke |

## Fixture rules

- Fixtures contain **test passwords only**; never add a real vault.
- To change or add a fixture, update `fixtures/manifest.json` with its SHA-256 and size,
  then run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-fixtures.ps1`.
- CI runs the same check, so an unregistered or modified fixture fails the build.
