## What changed

<!-- One or two sentences. Reference the relevant requirement or ADR if there is one. -->

## How it was verified

<!-- Commands and results, including any self-test or interop run. -->

- [ ] `cargo test --workspace` (or the relevant subset)
- [ ] application built and `--selftest` passed (for shell changes)
- [ ] `scripts/check-public.ps1` (tracked files stay inside the public allowlist)

## Data-safety checklist

- [ ] No `.kdbx` files, passwords, TOTP seeds, WebDAV URLs or credentials are added to the repository, logs or artifacts.
- [ ] Save/sync paths keep the temp-file, backup and atomic-replace protocol.

## Notes for reviewers

<!-- Anything surprising, deferred, or intentionally out of scope. -->
