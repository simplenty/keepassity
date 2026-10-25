# Changelog

All notable changes are documented here. Format: Keep a Changelog. Versioning: SemVer.
KDBX write-path changes must state the compatibility impact.

## [Unreleased]

### Added

- Project scaffolding: Rust workspace (`crates/core`, `crates/ffi`, `crates/cli`) and CI skeleton.
- Read-only CLI: `keepassity-cli inspect` prints a JSON summary + canonical SHA-256.
- Test fixtures: 25 public KDBX samples from keepass-rs (MIT); registry in
  `tests/fixtures/README.md`.

### Notes

- KDBX compatibility: no write path yet.
