# Third-party licenses

> Every dependency listed here must be verifiable with `cargo deny` or the package's license file.

| Component | Version | License | Usage | Notes |
|---|---|---|---|---|
| `keepass` | 0.17.0 | MIT | KDBX read path (CLI, independent verifier) | not shipped in the UI path |
| `keepassxcr` | 0.3.1 | GPL-3.0-or-later | KDBX write backend (ADR-0002) | library-only build; not the CLI app |
| KeePassLib (KeePass 2.x, test-only) | 2.59.0 (managed source build) | GPL-2.0-or-later | Interop verification only; not shipped | build from official source; see `tools/interop/README.md` |
| ZXing.Net | 0.16.x | Apache-2.0 | QR decode (UI layer, planned) | Compatible with GPL-3.0-or-later |
| CommunityToolkit.Mvvm | 8.4.x | MIT | MVVM (planned) | |
| Rust crates | per `Cargo.lock` | permissive (MIT/Apache-2.0/BSD/ISC/MPL-2.0/Unicode-3.0) | crypto, serialization, CLI | checked by `cargo deny` |
