# Compatibility matrix

> Principle: only list what has actually been tested. Untested = "not tested".

## KDBX clients

| Client | Can read files we write | We can read files it writes | Version | Status |
|---|:--:|:--:|---|---|
| KeePass 2.x (official) | not tested | not tested | 2.61.x | manual check after the real-database round trip (planned) |
| KeePassLib (official C# library, test-only) | tested on fixture rewrites (19/19; Twofish skipped) | not tested | 2.59.0 managed source build | local `tools/interop`; real database pending |
| keepassxc-cli | tested on fixture rewrites (19/19) | not tested | 2.8.0 / distro build | local check + CI interop job |

## WebDAV servers

| Server | PROPFIND | GET | PUT | If-Match (412) | Non-ASCII paths | Large files | Status |
|---|:--:|:--:|:--:|:--:|:--:|:--:|---|
| Jianguoyun (`dav.jianguoyun.com`) | | | | | | | required for v1.0 |
| rclone serve webdav | | | | | | | used in CI integration tests |
| Nextcloud | | | | | | | best effort (not promised) |
| Apache mod_dav | | | | | | | optional |

## Known limitations (v1.0)

- Windows 11 only; master password only (no key files, no Windows Hello).
- No attachments/history/recycle-bin UI (the data itself is preserved).
- No auto-update, no MSIX, unsigned installer (README notes SmartScreen).
- Single database, English UI.
