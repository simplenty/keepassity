# Interop verification (test-only)

Purpose: prove that what we write can be read by real clients, and that we can read what they write.
**Nothing here ships with the app.**

Planned pieces:

| Piece | What | Notes |
|---|---|---|
| `KeePassLibTool` (.NET console) | Create/open/verify `.kdbx` with the official KeePass 2.x library | KeePassLib is GPL-2.0-or-later; the official KeePass project does not publish it on NuGet — reference it from the official source release |
| `verify.ps1` | Round-trip: our CLI writes → KeePassLib reads → compare canonical dump | Exit non-zero on any field mismatch |
| `keepassxc-cli` (CI) | Ubuntu job: `apt-get install keepassxc`, `db-info` + `export` | Do not use the AppImage in CI (FUSE issues) |

Acceptance rule (gates): a fixture passes only if the canonical dump (sorted, stable JSON → SHA-256)
is identical before and after the round trip, and a third-party client opens the file.
