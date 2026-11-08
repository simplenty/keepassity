## What changed

<!-- One or two sentences. Link the issue/discussion this implements, and reference a
     requirement or ADR if there is one. Keep the diff scoped. -->

## Verification

Paste the commands you actually ran and their results (`scripts\test.ps1` runs most of the
local layers, including `cargo fmt`, `cargo clippy`, `cargo test`, the fixture manifest, the
Windows build and the headless service tests; `-Full` adds the application self-tests).

```text
<commands and results>
```

- [ ] `cargo fmt --all -- --check`
- [ ] `cargo clippy --workspace --all-targets -- -D warnings`
- [ ] `cargo test --workspace` (or the targeted subset stated above)
- [ ] Windows shell changed: app builds and `--selftest` passes
- [ ] `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-public.ps1`
- [ ] Changed `apps/windows/`: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-ui-strings.ps1`
- [ ] Changed fixtures: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-fixtures.ps1`
- [ ] Changed dependencies / lockfiles: reviewed the lockfile diff and, where relevant, `cargo deny check`
- [ ] `CHANGELOG.md` updated for user-visible changes (or an entry is not needed)

## KDBX write-path compatibility

Required when the PR touches KDBX parsing/writing, crypto, save/sync, backups or the
installer's file handling. Skip only if the PR provably does not affect them.

- [ ] This PR does **not** change the KDBX write path or the save protocol.
- [ ] Compatibility impact is stated: written KDBX version/format and any behaviour change.
- [ ] A saved database reopens with this build **and** with KeePass 2.x / `keepassxc-cli`
      (paste the evidence above).
- [ ] The save protocol is preserved: backup of the previous file -> write temp file ->
      validate -> atomic replace; no silent overwrite/delete/upload.
- [ ] New or changed behaviour is covered by a test that would fail without the fix.

## Data safety

- [ ] No `.kdbx` file (other than the registered public test fixtures), key file, master
      password, TOTP seed, WebDAV URL+credential or real vault content is added to the
      repository, tests, logs or artifacts.
- [ ] No internal process documents (task cards, requirements, acceptance records) or other
      non-public files are added; `public.allowlist` is updated for any new public file.
- [ ] Error messages and logs stay redacted (no secrets, no full vault contents).

## Notes for reviewers

<!-- Anything surprising, deferred, or intentionally out of scope. AI-assisted code: state
     what you verified by hand. -->
