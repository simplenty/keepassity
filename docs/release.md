# Release

The public repository ships self-contained Windows packages built from a tag.

## Version source

- `Directory.Build.props` defines `KeepassityVersion` (default `0.1.0-dev`).
- A release build passes the tag value:
  `dotnet build ... /p:KeepassityVersion=1.2.3`.
- The Rust crate versions in `Cargo.toml` follow the same release number.

## Tag and build

1. Merge the release branch into `main`.
2. Create an annotated tag `vX.Y.Z` on `main` and push it.
3. The release workflow builds:
   - `cargo build -p keepassity-ffi --release` (Windows);
   - `dotnet publish apps/windows/Keepassity.Mock/Keepassity.Mock.csproj -c Release -r win-x64 --self-contained true`;
   - the portable folder and zip;
   - the Inno Setup installer;
   - `SHA256SUMS`, an SBOM and build provenance attestations.
4. The workflow attaches the artifacts to a GitHub Release draft. Review the draft,
   install it on a clean Windows machine, then publish.

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
