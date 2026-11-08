# Support

keepassity is a single-maintainer, self-hosted, open-source password manager. Support is
**best effort, with no SLA** — the same policy as the rest of the project. Please read this
page before opening an issue; it routes questions to the right place and tells you what this
project can and cannot help with.

> **Unofficial software.** keepassity is not affiliated with, endorsed by, or sponsored by
> the KeePass project; "KeePass" is a trademark of its owner. This project only reads and
> writes `.kdbx` files.

> **Back up your vault.** keepassity is not a sync or backup service. It writes to the
> database file you choose (and to timestamped local backups next to it), but hardware
> failure, user error, a closed lid or a bug can still cost data. Keep independent backups of
> your database and test that you can open them with KeePass 2.x or KeePassXC before relying
> on any new build — especially the unsigned 1.0.0 preview.

## Where to ask

| What | Where |
|---|---|
| Bug, crash, data-loss risk, unexpected behavior | Open a **Bug report** issue (template in `.github/ISSUE_TEMPLATE/`) |
| Feature idea or scope question | Open a **Feature request** issue and check the `Scope` section of [README](README.md#scope) first |
| How-to / usage question | Prefer **GitHub Discussions** if the repository has them enabled; otherwise open a **Question** issue |
| Security vulnerability | **Private** report through GitHub Security Advisories — see [SECURITY.md](SECURITY.md). Never open a public issue for a vulnerability. |
| Contribution rules | [CONTRIBUTING.md](CONTRIBUTING.md) and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) |

Pull requests are welcome for scoped, verified fixes — see [CONTRIBUTING.md](CONTRIBUTING.md).
Please do not open a PR for a large feature that has not been discussed first; v1.0 scope is
frozen.

## What this project cannot help with

### Password recovery

**There is no password recovery.** keepassity — like KeePass, KeePassXC and any correct KDBX
implementation — has no backdoor, recovery key, or master-password bypass. If the master
password (or key file) for a database is lost, the data cannot be decrypted by anyone,
including the maintainer. No "recovery service" offered anywhere will change that; treat
offers to recover a `.kdbx` as scams.

Likewise, this project cannot repair an arbitrary vault file that other tools cannot open.
If KeePass 2.x / KeePassXC can open a database but keepassity cannot, that is a bug — report
it with a **synthetic** fixture (see "Never share secrets" below).

### Out of scope for v1.0

Keepassity is not affiliated with or a replacement for the KeePass ecosystem. Not supported
in v1.0: browser integration/extension, auto-type, Windows Hello, key file or challenge-response
credentials, attachments/history/recycle-bin management UI, MSIX packaging, auto-update,
multi-database, mobile, Linux and macOS. See [README](README.md#scope) and
[docs/compatibility.md](docs/compatibility.md) for the exact scope and the tested KDBX matrix.
Feature requests beyond that scope are noted but are not v1.0 blockers.

## Never share secrets

**Never paste any of the following into an issue, discussion, PR or chat:** master password,
TOTP seeds/codes, WebDAV URL + username + password, entry passwords or notes, or a `.kdbx`
file (even "empty" ones). Issue templates also remind you of this.

- A **sanitized diagnostics archive** exported from Settings is welcome — it contains
  counts, versions and redacted settings, never secrets, and never your database.
- If a bug needs sample data, create a **new** throw-away database with dummy values and use
  that.
- Redact usernames and URLs if they are sensitive.

## Before opening an issue

1. Search existing issues — the problem may already be reported or fixed in the current
   build.
2. Reproduce on the current build if you can; note the version from the About page or the
   portable folder name.
3. Include in the report:
   - keepassity version (About page, or the portable folder name);
   - Windows version and edition (x64);
   - how you installed it: portable `.zip`, the Inno Setup installer, or a source build;
   - the database kind: local file, WebDAV, or a new database;
   - exact steps, expected behavior and actual behavior, and redacted log excerpts if
     relevant.
4. Follow the data-safety checklist in the issue template and confirm you included no
   secrets.

## Common questions

**Can it open my existing KeePass database?** It reads and writes KDBX 3.1 / 4.0 / 4.1 with a
master password. Key file / challenge-response credentials are out of scope for v1.0. See
[docs/compatibility.md](docs/compatibility.md) for the tested matrix.

**Where are settings, WebDAV state and backups stored?** Settings, WebDAV working copies and
sync state live under `%LOCALAPPDATA%\keepassity\`; backups are written next to the database
in `.keepassity\backups\<database>\`. WebDAV passwords are stored with Windows DPAPI, never in
the settings file.

**Where is the release download?** The `v1.0.0` tag and GitHub Release are created only after
the 30-day real-use acceptance; until then build the portable folder from source (see
[README](README.md#build-from-source) and [docs/release.md](docs/release.md)). Released
binaries are unsigned — verify `SHA256SUMS` before running them.

**How do I build it, and how do I run the tests?** See
[README](README.md#build-from-source) and [docs/testing.md](docs/testing.md); the one-shot
local verification is `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1`.

**Is my data sent anywhere?** No telemetry, no accounts, no server. The only network access
is the WebDAV server you configure. See the guarantees in [SECURITY.md](SECURITY.md).
