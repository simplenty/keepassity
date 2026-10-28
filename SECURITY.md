# Security policy

This is a single-maintainer, self-hosted, open-source password manager. Best effort, no SLA.

## Reporting a vulnerability

Please report privately via GitHub Security Advisories on this repository (Security → Report a vulnerability).
Do **not** open a public issue for vulnerabilities.

## Scope

In scope: KDBX parsing/writing correctness, memory/handling of master passwords and TOTP secrets,
clipboard handling, WebDAV sync (conflict/overwrite/data-loss), local state files, installer.

Out of scope (v1.0): browser integration, auto-type, mobile, multi-user, cloud service.

## Guarantees

- No telemetry, no accounts, no server. The only network access is the WebDAV server you configure.
- Logs are redacted: no passwords, TOTP seeds, entry contents, or full paths.
- Credentials are stored in Windows Credential Manager, never in config files.
- The release build is unsigned; verify SHA256SUMS.txt from the GitHub release.