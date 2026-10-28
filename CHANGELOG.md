# Changelog

All notable changes are documented here. Format: Keep a Changelog. Versioning: SemVer.
KDBX write-path changes must state the compatibility impact.

## [Unreleased]

### Added

- Project scaffolding: Rust workspace (`crates/core`, `crates/ffi`, `crates/cli`) and CI skeleton.
- Read-only CLI: `keepassity-cli inspect` prints a JSON summary + canonical SHA-256.
- Test fixtures: 25 public KDBX samples from keepass-rs (MIT); registry in
  `tests/fixtures/README.md`.
- Write closure (M0-07): `keepassity-cli rewrite` re-saves KDBX through the
  ADR-0002 backend (`keepassxcr`) and `keepassity-cli verify` compares two
  databases field by field. Interop fixtures run through `keepassxc-cli` and
  the official KeePassLib in `tools/interop/`.
- CI: `interop` job round-trips all supported fixtures with keepassxc-cli.

### Notes

- KDBX compatibility: no write path yet.
