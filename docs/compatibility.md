# Compatibility matrix

> Principle: only list what has actually been tested. Untested = "not tested".

## KDBX clients

"Files we write" come from two paths: an **app save** preserves the source KDBX 4 outer
format (version, cipher, compression and KDF settings) through the model writer, while a
KDBX 3.1 source follows the documented upgrade to KDBX 4.0. The **CLI `rewrite`** used for
interop evidence re-saves through the backend and preserves the input format version. The
fixture set is `tests/fixtures/keepass-rs`: 25 public samples, of which 17 are rewritten and
verified end-to-end, two are broken files that must be rejected, and six key-file /
challenge-response samples are outside the v1.0 scope.

| Client | Can read files we write | We can read files it writes | Version | Status |
|---|:--:|:--:|---|---|
| KeePass 2.x (official) | tested: real KDBX 4.1 database rewritten and reopened, 10 entries (including 3 TOTP entries) spot-checked; an app-edited fixture was reopened by KeePass 2.x in the G3 manual gate | tested: the real KDBX 4.1 database opened at G0 was a KeePass 2.x database | 2.x (maintainer install; not pinned in CI) | manual gates G0 2026-10-25 and G3 2026-11-08, signed off |
| KeePassLib (official C# library, test-only) | tested: fixture rewrite round trips + real database (canonical hash unchanged); broken files rejected as expected | not tested | 2.59.0 managed source build | local `tools/interop` only; Twofish fixtures skipped (no plugin) |
| keepassxc-cli | tested: fixture rewrite round trips + real database; broken files rejected as expected | not tested | 2.8.0 / distro build | local check + CI `interop` job (green on `main`, 2026-11-08) |

KeePass 2.x and KeePassLib are exercised manually/local-only; only the keepassxc-cli round
trip runs in CI. The database used for the real-vault checks is never committed.

### KDBX write format

App saves preserve the KDBX 4 outer header read from the source file: the format version
(4.0 stays 4.0, 4.1 stays 4.1), outer cipher (AES-128/256, Twofish or ChaCha20), compression,
KDF type and cost (AES-KDF rounds or Argon2 memory/iterations/parallelism) and the
`PublicCustomData` header field. The KDF salt is chosen on the first save and the transformed
key is reused for later saves in the same unlocked session; a fresh master seed, encryption IV
and protected-stream key are still drawn on every save. A newly created database uses the
write defaults (KDBX 4.0, AES-256, Argon2id 128 MiB / 6 iterations / 4 lanes, gzip). A KDBX 3.1
source is upgraded to KDBX 4.0 with those defaults, because KDBX3 has no equivalent outer
header after the upgrade. The protected-stream algorithm follows the write backend (ChaCha20).
Model data that the writer maps (entries, groups, fields, times, tags, history, attachments,
deleted objects, TOTP fields) is carried through an edit/save; raw XML blocks, custom icons and
auto-type data that the model writer does not map are not preserved by an app save. The app
edit/save path was manually checked at G3 (edit, save, reopen in KeePass 2.x); the interop
matrix above covers the backend rewrite round trip.

## WebDAV TLS and self-signed certificates

WebDAV URLs must use `https://`: the password is sent with Basic authentication, so the app
rejects plain `http://` URLs instead of leaking credentials over the network.

Server certificates are validated by the operating system first. A certificate Windows does
not trust (typical for a self-installed NAS, Nextcloud or router) is never accepted silently;
the app opens a confirmation dialog that shows the server, subject, issuer, expiry and the
**SHA-256 fingerprint** of the presented certificate:

- **Trust** pins that exact certificate for the `host:port` in `settings.json` and retries the
  connection. Only that certificate is accepted from then on, in addition to the normal
  platform-trusted chain - certificate validation is never disabled globally.
- **Cancel** aborts the operation; no request, credentials or database bytes are sent.
- If the server certificate changes later (renewal, re-install), the app notices during the
  next connection check and asks again instead of silently trusting the new certificate.

Only trust a certificate if you installed the server yourself or your administrator gave you the
fingerprint out of band.

## WebDAV servers

The app uses a minimal WebDAV client (PROPFIND depth 0, GET, conditional PUT with
ETag/If-Match and If-None-Match; a 412 means the remote changed while the upload was
being prepared and is retried with 0.5 s / 1.5 s / 4 s back-off before the three-way
merge/conflict flow). It is covered by an in-process mock in
`tests/app-core` and by the mock-WebDAV drills (protocol, restore, offline replay, conflict);
one real provider (Jianguoyun) was exercised manually during the G4 gate on 2026-11-09
(configuration, save-sync, startup check, manual sync). The Open-from-WebDAV dialog
offers URL presets for Nextcloud/ownCloud, Synology WebDAV and Jianguoyun, and
two-factor accounts are pointed at app passwords.

| Server | PROPFIND | GET | PUT | If-Match (412) | Non-ASCII paths | Large files | Status |
|---|:--:|:--:|:--:|:--:|:--:|:--:|---|
| Local mock WebDAV (development) | tested | tested | tested | tested (incl. 412/404) | tested (percent-encoded target) | not tested | in-process mock in `tests/app-core` (CI `app` job) + `tools/drills` |
| Jianguoyun (`dav.jianguoyun.com`) | tested (manual) | tested (manual) | tested (manual) | tested (manual save sync) | not tested | not tested | manual acceptance G4, 2026-11-09 |
| rclone serve webdav | not tested | not tested | not tested | not tested | not tested | not tested | not used by CI |
| Nextcloud | not tested | not tested | not tested | not tested | not tested | not tested | best effort (not promised) |
| Apache mod_dav | not tested | not tested | not tested | not tested | not tested | not tested | optional |

## Windows integration

| Item | Status |
|---|---|
| Installer / uninstaller | tested: per-user install (no UAC) under `%LOCALAPPDATA%\Programs\keepassity`, Start menu/desktop shortcuts, Apps & Features entry; clean install on a clean Windows 11 machine was a G4 manual item; uninstall removes program files and shortcuts and keeps user data; `tools/installer/verify-install.ps1` PASS at the 2026-11-08 gate (the v1.0.0 script adds `.kdbx` association checks) |
| `.kdbx` file association | the installer registers a per-user candidate association (no default-app override); double-click opens keepassity after the user confirms/chooses it in Windows. Verified on the installed v1.0.0 build (2026-11-09). |
| SmartScreen | the binaries are unsigned; Windows may show "Windows protected your PC". Verify the SHA-256 checksum first, then More info -> Run anyway |
| Shell integration beyond open | no MSIX packaging, no auto-update, no Windows Hello, no auto-type |

## Key-derivation safety limits

A KDBX header is untrusted input: it declares the KDF parameters (AES-KDF round count or
Argon2 memory/iterations/parallelism) the reader must execute before it can even tell the
password is wrong. A hostile or corrupt header could therefore keep the app busy for a very
long time. The reader validates the parameters before deriving any key and rejects files
that ask for an extreme amount of work with a normal, explainable error (no hang, no panic):

| KDF | Limit | Why |
|---|---|---|
| AES-KDF (KDBX 3.1 and 4.x) | 10,000,000 rounds | about 2-3 s of work on the reference machine; KeePass defaults are ~600k |
| Argon2d / Argon2id memory | 2 GiB | far above real vaults (the writer default is 128 MiB) |
| Argon2 iterations | 64 | the writer default is 6 |
| Argon2 parallelism | 16 | the writer default is 4 |
| Argon2 total work | memory (MiB) × iterations ≤ 6,144 | ~8x the write default, about 6 s |

The limits only reject clearly abusive values: normal KeePass 2.x / KeePassXC databases,
including high-security ones, stay well inside them. A file that trips a limit can still be
opened in KeePass, which lets you re-save it with normal settings.
## Known limitations (v1.0)

- Windows 11 x64 only; master password only (no key files, no challenge-response, no Windows
  Hello).
- No attachments/history/recycle-bin management UI (the mapped data itself is preserved).
- Single database, English UI.
- No auto-update, no MSIX; the installer and portable binaries are unsigned (README notes
  SmartScreen).
- WebDAV: local mock and one real provider (Jianguoyun) tested; other providers are untested.
- The app preserves KDBX 4 outer format settings on save and upgrades KDBX 3.1 to 4.0 (see
  KDBX write format); use KeePass 2.x / KeePassXC to open the files if you need the data in
  another client.
- The 30-day acceptance (G5) is still pending; `v1.0.0` is not published yet.
