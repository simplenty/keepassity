# Third-party licenses

> Every dependency listed here must be verifiable with `cargo deny` or the package's license
> file. Direct NuGet dependencies are pinned in `Directory.Packages.props`; resolved versions
> (including transitives) are recorded in `apps/windows/Keepassity.App/packages.lock.json`
> and `tests/*/packages.lock.json`.

| Component | Version | License | Usage | Notes |
|---|---|---|---|---|
| `keepass` | 0.17.0 | MIT | KDBX read path (CLI, independent verifier) | not shipped in the UI path |
| `keepassxcr` | 0.3.1 (vendored fork) | GPL-3.0-or-later | KDBX write backend (ADR-0002); vendored under `vendor/keepassxcr` to expose a byte-level read/write API (Q11/D9) | library-only build; upstream project https://github.com/tynes/keepassxcr |
| KeePassLib (KeePass 2.x, test-only) | 2.59.0 (managed source build) | GPL-2.0-or-later | Interop verification only; not shipped | build from official source; see `tools/interop/README.md` |
| Microsoft.WindowsAppSDK | 1.8.260921001 | Microsoft Software License Terms (Windows App SDK) | WinUI 3 framework and self-contained Windows App SDK runtime | redistributable per the license terms (distributable code section); not a GPL-licensed component |
| Microsoft.Web.WebView2 | 1.0.3179.45 | BSD-3-Clause | transitive dependency of the Windows App SDK / WinUI | licensed under its own `LICENSE.txt` in the NuGet package |
| H.NotifyIcon.WinUI | 2.3.2 | MIT | notification-area icon and tray menu (UI layer) | pulls `H.NotifyIcon` 2.3.2 (MIT) and `H.GeneratedIcons.System.Drawing` 2.3.2 (MIT) |
| ZXing.Net | 0.16.11 | Apache-2.0 | QR decoding for TOTP import in the UI layer | shipped with the app; compatible with GPL-3.0-or-later |
| Rust crates | per `Cargo.lock` | permissive (MIT/Apache-2.0/BSD/ISC/MPL-2.0/Unicode-3.0) | crypto, serialization, CLI | checked by `cargo deny` |

Keep this list in sync with `Directory.Packages.props` (direct NuGet packages), `Cargo.lock`
(Rust crates) and `tools/interop` (test-only tools). A package that is not referenced from a
project file does not belong in the table.
