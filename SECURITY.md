# Security policy

This is a single-maintainer, self-hosted, open-source password manager. Security reports are
handled **best effort, with no SLA**. Please do not open a public issue for a vulnerability.

## Supported versions

| Version | Supported |
|---|---|
| `v1.0.x` (current source tree; tag and GitHub Release pending the 30-day acceptance) | Yes |
| Pre-1.0 builds (the obsolete `v0.1.0` draft/tag were removed on 2026-11-09) | No |
| Any other/pre-1.0 build | No |

The `v1.0.0` tag and GitHub Release are created only after the acceptance period described
in `docs/release.md`. Until then, build from source and keep independent backups.

## Reporting a vulnerability

Please report privately through **GitHub Security Advisories** on this repository:
<https://github.com/simplenty/keepassity/security/advisories/new> (Security -> Report a
vulnerability). Do **not** open a public issue, and do not post details in discussions,
pull requests or social media before a fix is available.

A useful report includes:

- affected version or commit, and how you installed it (portable zip / installer / source
  build);
- Windows version, and the database kind (local / WebDAV / new database);
- the impact (confidentiality, integrity, data loss, denial of service) and the conditions
  required to trigger it;
- minimal reproduction steps or a proof of concept.

**Never include real secrets in a report:** no master passwords, TOTP seeds, WebDAV
credentials, real vault contents or real `.kdbx` files. Use a synthetic database created for
the report, or a redacted fixture. If sharing an encrypted sample is unavoidable, describe
the setup instead of sending the vault; the maintainer will ask for a safe sample if needed.

## What happens next

There is no guaranteed response or fix timeline. Critical issues — KDBX
parsing/writing corruption, master-password or TOTP-secret exposure, and data loss in
save/sync/backup — are prioritised. For other reports the maintainer will acknowledge and
triage as time allows.

If the report is accepted, the maintainer will agree a coordinated-disclosure timeline with
you before any public details are published (typically after a fixed release is available).
Credit is given in the release notes unless you ask to stay anonymous — say which you prefer.

## Scope

In scope (v1.0):

- KDBX parsing/writing correctness, including round-trip and interoperability regressions;
- handling of master passwords, key-derivation and TOTP seeds (memory, logs, files, UI);
- clipboard handling and auto-lock/session-lock behavior;
- WebDAV sync: conflict handling, overwrite/data-loss paths, credential storage;
- local state files (`%LOCALAPPDATA%\keepassity\`), backups and restore validation;
- the Inno Setup installer and release artifacts (unsigned-build verification, checksums).

Out of scope (v1.0): browser integration, auto-type, Windows Hello, key file or
challenge-response credentials, MSIX packaging, auto-update, multi-database, mobile, Linux
and macOS, and cloud sync services.

Also out of scope:

- recovery of a lost master password (there is no backdoor or recovery key — by design);
- attacks that require an already-compromised Windows account, admin/root access or malware
  on the machine (e.g. OS keyloggers, process inspection of an unlocked app);
- physical attacks;
- a misconfigured or compromised WebDAV server you chose to connect to (but bugs in how
  keepassity handles malicious server responses *are* in scope);
- vulnerabilities in third-party dependencies with no exploitable path through keepassity —
  report those upstream, but tell us if keepassity exposes them.

## Guarantees

- No telemetry, no accounts, no server. The only network access is the WebDAV server you
  configure.
- Logs and the diagnostics export are redacted: no passwords, TOTP seeds, entry contents or
  WebDAV credentials, and the diagnostics snapshot excludes database paths, WebDAV URLs and
  usernames. Raw log files can still contain local filesystem paths (for example the
  self-test report path); they stay on the machine unless the user exports and shares
  diagnostics, so review an export before sending it.
- WebDAV passwords are encrypted with Windows DPAPI for the current user and stored outside
  the settings file; they are never written to configuration files.
- The release build is unsigned. Verify `SHA256SUMS` and, where provided, the build
  provenance attestation before running a release artifact.
