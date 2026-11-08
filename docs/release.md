# Release

The public repository ships self-contained Windows packages built from a tag.

## Version source

- `Directory.Build.props` defines `KeepassityVersion` (default `0.1.0-dev`).
- The release workflow passes the tag value to `scripts/publish-windows.ps1 -Version`,
  which forwards it as `/p:KeepassityVersion=...` to `dotnet publish`.
- The Rust crate versions in `Cargo.toml` follow the same release number.

## Tag and build

1. Merge the release branch into `main`.
2. Create an annotated tag `vX.Y.Z` on `main` and push it; the workflow triggers on `v*` tags.
3. The release workflow (`.github/workflows/release.yml`, Windows runner):
   - verifies the source: `cargo test --workspace`, the `tests/app-core` suite and
     `scripts/verify-fixtures.ps1`;
   - builds `cargo build -p keepassity-ffi --release`;
   - publishes the self-contained app with `scripts/publish-windows.ps1 -Version <tag> -Zip`
     into `out/dist/keepassity-<version>-win-x64/` and
     `out/dist/keepassity-<version>-win-x64.zip`;
   - self-tests the published executable against two fixtures;
   - builds the Inno Setup installer with `scripts/build-installer.ps1`
     (`out/dist/keepassity-<version>-win-x64-setup.exe`);
   - writes `SHA256SUMS` for the artifacts;
   - generates an SPDX SBOM (`sbom.spdx.json`) and build provenance attestations
     (`actions/attest-build-provenance`).
4. The workflow attaches the zip, the installer, `SHA256SUMS` and `sbom.spdx.json` to a
   GitHub Release **draft**. Review the draft, install it on a clean Windows machine, then
   publish.

The first tagged build, `v0.1.0` (2026-10-28), exists as such a draft and has not been
published.

## Manual gate

Before publishing a release, run the G3 checklist from the private process repository
(launch, unlock, browse, edit/save, TOTP, clipboard, auto-lock, WebDAV sync, conflict
recovery, install/uninstall). The checklist records the signer and the evidence.

## Rollback

- A bad release can be superseded by a new patch tag; do not rewrite published tags.
- If a build was published by mistake, mark the GitHub Release as pre-release and
  publish a corrected patch release.

## Signing

Code signing is not configured yet. Until a certificate exists, releases ship with
SHA-256 checksums and provenance attestations only; the README and release notes must
say the binaries are unsigned.
