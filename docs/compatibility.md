# Compatibility matrix

> Principle: only list what has actually been tested. Untested = "not tested".

## KDBX clients

"Files we write" are produced by the ADR-0002 write backend (`keepassxcr`). The fixture set is
`tests/fixtures/keepass-rs`: 25 public samples, of which 17 are rewritten and verified
end-to-end, two are broken files that must be rejected, and six key-file /
challenge-response samples are outside the v1.0 scope.

| Client | Can read files we write | We can read files it writes | Version | Status |
|---|:--:|:--:|---|---|
| KeePass 2.x (official) | tested: real KDBX 4.1 database rewritten and reopened; 10 entries (including 3 TOTP entries) spot-checked by the maintainer | not tested | 2.x (maintainer install; not pinned in CI) | manual gate G0, 2026-10-25, signed off |
| KeePassLib (official C# library, test-only) | tested: fixture rewrite round trips + real database (canonical hash unchanged); broken files rejected as expected | not tested | 2.59.0 managed source build | local `tools/interop` only; Twofish fixtures skipped (no plugin) |
| keepassxc-cli | tested: fixture rewrite round trips + real database; broken files rejected as expected | not tested | 2.8.0 / distro build | local check + CI `interop` job (green on `main`, 2026-11-08) |

KeePass 2.x and KeePassLib were exercised manually/local-only; only the keepassxc-cli
round-trip runs in CI. The database used for the real-vault checks is never committed.

## WebDAV servers

The app uses a minimal WebDAV client (PROPFIND depth 0, GET, conditional PUT with
ETag/If-Match and If-None-Match, 412 handling). It has only been tested against a local
mock server; no real WebDAV provider has been verified yet.

| Server | PROPFIND | GET | PUT | If-Match (412) | Non-ASCII paths | Large files | Status |
|---|:--:|:--:|:--:|:--:|:--:|:--:|---|
| Local mock WebDAV server (development) | tested | tested | tested | tested (incl. 412/404) | tested (percent-encoded target) | not tested | in-process mock in `tests/app-core` (runs in the CI `app` job) |
| Jianguoyun (`dav.jianguoyun.com`) | not tested | not tested | not tested | not tested | not tested | not tested | **required for v1.0**; manual check pending |
| rclone serve webdav | not tested | not tested | not tested | not tested | not tested | not tested | not used by CI |
| Nextcloud | not tested | not tested | not tested | not tested | not tested | not tested | best effort (not promised) |
| Apache mod_dav | not tested | not tested | not tested | not tested | not tested | not tested | optional |

## Known limitations (v1.0)

- Windows 11 x64 only; master password only (no key files, no challenge-response, no
  Windows Hello).
- No attachments/history/recycle-bin management UI (the data itself is preserved).
- Single database, English UI.
- No auto-update, no MSIX; the installer and portable binaries are unsigned (README notes
  SmartScreen).
- WebDAV: tested against a local mock only; real-server verification (Jianguoyun) is still
  required for v1.0.
