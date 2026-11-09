# Release

The public repository ships self-contained Windows packages built from a tag.

## Current status

- The source tree is at version **1.0.0**; no `v1.0.0` tag exists yet and no GitHub Release
  has been published.
- Publishing is gated by the **30-day real-use acceptance**: the 1.0.0 build is produced for
  the acceptance period first, and the `v1.0.0` tag is created only after it passes.
- No GitHub Release exists yet: the obsolete `v0.1.0` draft and tag were removed on
  2026-11-09, and `v1.0.0` is created only after the 30-day acceptance passes.
- Code signing is not configured. Releases ship with SHA-256 checksums and build-provenance
  attestations only (no Authenticode signature, no MSIX, no auto-update).

## Version source

- `Directory.Build.props` defines `KeepassityVersion` (currently `1.0.0`).
- `scripts/publish-windows.ps1` reads `KeepassityVersion` when `-Version` is not passed; the
  release workflow passes the tag value as `-Version`, which becomes
  `/p:KeepassityVersion=...` on `dotnet publish`. The script's `1.0.0-dev` fallback only
  applies if the property is missing from `Directory.Build.props`.
- The Rust crate versions in `Cargo.toml` follow the same release number.
- The Inno Setup script (`installer/keepassity.iss`) defaults to `1.0.0`; CI overrides it with
  the tag value through `scripts/build-installer.ps1 -Version`.

## Build for acceptance (no tag, no publish)

Produce the 1.0.0 build and use it for the 30-day acceptance without creating a tag.

The build output must live in a **normal (Medium integrity) folder on disk** if your
working directory carries a Low integrity label (some sandboxed setups do): a `setup.exe`
started from a Low-labeled folder cannot create its `%TEMP%\is-*.tmp` files, so the install
fails even though the build succeeded, and `icacls ... /setintegritylevel Medium` cannot
relabel it without `SeRelabelPrivilege`. A folder such as
`%USERPROFILE%\keepassity-release` works; `out\dist` inside a Low-labeled workspace does
not. The four steps below produce the portable folder, the installer, the checksums and the
SBOM, and `tools/installer/verify-install.ps1` checks the result after installing it.

The app project's default FFI lookup is `out\cargo-target\release`, which is what
`CARGO_TARGET_DIR` below matches. Inno Setup 6 must be installed for the installer step.

```powershell
$env:CARGO_TARGET_DIR = "$PWD\..\out\cargo-target"
cargo build -p keepassity-ffi --release
# ..\out\dist works for archive copies, but install from a Medium folder instead
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish-windows.ps1 `
  -Version 1.0.0 -Zip -OutputRoot "$env:USERPROFILE\Desktop\keepassity-release"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-installer.ps1 `
  -Version 1.0.0 `
  -SourceDir "$env:USERPROFILE\Desktop\keepassity-release\keepassity-1.0.0-win-x64" `
  -OutputDir "$env:USERPROFILE\Desktop\keepassity-release"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make-checksums.ps1 `
  -Directory "$env:USERPROFILE\Desktop\keepassity-release"
```

The acceptance build is not uploaded anywhere; it stays local (or in the private process
repository's evidence) until the tag is created.

## Tag and release (after acceptance)

1. Merge the release branch into `main`.
2. Create an annotated tag `v1.0.0` on `main` and push it; the workflow triggers on `v*` tags.
3. The release workflow (`.github/workflows/release.yml`, Windows runner):
   - verifies the source: `cargo test --workspace`, the `tests/app-core` suite and
     `scripts/verify-fixtures.ps1`;
   - builds `cargo build -p keepassity-ffi --release`;
   - publishes the self-contained app with `scripts/publish-windows.ps1 -Version <tag> -Zip`
     into `out/dist/keepassity-<version>-win-x64/` and
     `out/dist/keepassity-<version>-win-x64.zip`;
   - self-tests the published executable against two fixtures;
   - builds the Inno Setup installer with `scripts/build-installer.ps1` into
     `out/dist/keepassity-<version>-win-x64-setup.exe` (per-user install, no UAC; registers a
     per-user candidate `.kdbx` association without changing the default app).
    
   - writes `SHA256SUMS` for the artifacts;
   - generates an SPDX SBOM (`sbom.spdx.json`) and build provenance attestations
     (`actions/attest-build-provenance`).
4. The workflow attaches the zip, the installer, `SHA256SUMS` and `sbom.spdx.json` to a
   GitHub Release **draft** (`draft: true`). Publishing is manual.
5. Review the draft: install it on a clean Windows machine, check the release body (for
   `v1.0.0` a copy-paste-ready draft is kept in the private process repository), then publish.

## Expected artifacts for 1.0.0

| File | What |
|---|---|
| `keepassity-1.0.0-win-x64.zip` | portable, self-contained build |
| `keepassity-1.0.0-win-x64-setup.exe` | Inno Setup per-user installer |
| `SHA256SUMS` | SHA-256 of the zip and the setup executable |
| `sbom.spdx.json` | SPDX software bill of materials |

Build-provenance attestations for `out/dist/*` are generated by the workflow
(`actions/attest-build-provenance`) and are verifiable with
`gh attestation verify <file> -R simplenty/keepassity`.

## Manual gates

- G0 (real-database round trip), G3 (main flows) and G4 (sync, conflict, restore,
  install/uninstall) are signed off; their checklists record the signer and the evidence in
  the private process repository.
- The 30-day real-use acceptance is the remaining gate before tagging. It starts with the
  1.0.0 acceptance build and must pass with zero data loss before the `v1.0.0` tag is created.

## Rollback

- A bad release can be superseded by a new patch tag; do not rewrite published tags.
- If a build was published by mistake, mark the GitHub Release as pre-release and
  publish a corrected patch release.

## Signing

Code signing is not configured yet. Until a certificate exists, releases ship with
SHA-256 checksums and provenance attestations only; the README and release notes must
say the binaries are unsigned.
