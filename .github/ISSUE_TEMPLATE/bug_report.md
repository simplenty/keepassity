---
name: Bug report
about: Report something that is broken or unsafe
title: ""
labels: bug
assignees: ""
---

<!-- Before you start: read SUPPORT.md. This is best-effort support with no SLA.
     NEVER paste your master password, TOTP seeds, WebDAV credentials, entry contents or a
     .kdbx file into this issue. A sanitized diagnostics archive exported from Settings is OK. -->

## What happened

<!-- Describe the behaviour you saw. Redact usernames/URLs if they are sensitive. -->

## What you expected

<!-- Describe the behaviour you expected. -->

## Steps to reproduce

1.
2.
3.

## Environment

- keepassity version (About page, or the portable folder name):
- Windows version and edition (x64):
- Install method: portable `.zip` / Inno Setup installer / built from source
- Database kind: local file / WebDAV / new database
- Source build only: Rust toolchain (`rust-toolchain.toml` pin) and .NET SDK version

## Logs and diagnostics

<!-- Optional. A diagnostics archive exported from Settings is welcome; it contains counts
     and redacted settings, never secrets. Redacted log excerpts are useful too.
     Do not attach your .kdbx file or any file containing secrets. -->

## Data safety

- [ ] I did not include the master password, any TOTP seed, any WebDAV credential, or entry
      contents in this issue.
- [ ] I did not attach a `.kdbx` file or any other secret-bearing file.
- [ ] I can share a sanitized diagnostics archive if needed.
