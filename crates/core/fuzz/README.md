# keepassity-core fuzzing (M5-02)

`cargo-fuzz` + `libFuzzer` harnesses for the core input surfaces that receive
attacker-controlled data. This crate is **not** part of the repository workspace
(it declares its own `[workspace]` in `Cargo.toml`), so the stable workspace
commands stay unaffected:

```powershell
cargo test --workspace          # from repo/ — does not build this crate
cargo clippy --workspace --all-targets -- -D warnings
cargo fmt --all -- --check
```

## Targets

| Target | Input | Calls | Why |
|---|---|---|---|
| `kdbx_parse` | raw `.kdbx` bytes | `kdbx::reader::parse(bytes, "demopass")`, then canonical model dump | highest risk: attacker-supplied database file; fixed public test password so mutations reach KDF/HMAC/XML/model code |
| `totp_uri` | UTF-8 text | `totp::fields::parse_uri` + round-trip, `base32_decode`, `read_fields` (otp / TOTP Seed / TimeOtp-*), `generate_at`/`prefetch`/`hotp` with byte-built configs | `otpauth://` / `steam://` URIs and Base32 secrets are pasted/untrusted text; must never panic |
| `merge_json` | `JSON(local)` `0x00` `JSON(remote)` | `serde_json::from_slice::<model::Database>` ×2, `sync::merge_for_sync`, canonical dump, `sync::conflict` copy naming | merge has no byte-level entry point; the stable decoder feeding it is the JSON `Database` model (same shape as `Database::canonical_json` / FFI DTO) |

Notes on scope:

- The core accepts `otpauth://` and `steam://`. `otp://` is not a supported
  scheme (it is rejected via the same parser); the fuzzer still feeds it.
- The `kdbx_parse` seed corpus is the public `demopass` fixtures
  (`tests/fixtures/keepass-rs/`). Never seed with a real vault.
- **The corpus is generated outside the repository** (default
  `<workspace>/out/fuzz-corpus`). It must not live under `repo/`: the project
  safety check (`workshop/scripts/check-system.ps1`) fails on any `.kdbx`
  outside `tests/fixtures/`. `scripts/seed-corpus.ps1` recreates it from the
  checked-in fixtures plus small synthetic JSON/text seeds and refuses to write
  inside the repository.

## Prerequisites (pinned)

Versions used for the M5-02 evidence run and in `.github/workflows/fuzz.yml`:

| Tool | Version | Install |
|---|---|---|
| nightly Rust | `nightly-2026-11-08` = rustc 1.102.0-nightly (f5cedb83a 2026-11-07), plus `rust-src` | `rustup toolchain install nightly-2026-11-08 --profile minimal --component rust-src` |
| cargo-fuzz | `0.13.2` | `cargo install cargo-fuzz --locked --version 0.13.2` |
| C++11 compiler | VS 2022/2026 MSVC build tools (Windows) or g++/clang (Linux) | needed by `libfuzzer-sys` |
| libFuzzer | bundled by `libfuzzer-sys 0.4.13` (pinned in `Cargo.lock`) | via `cargo fuzz` |

From the [cargo-fuzz book](https://rust-fuzz.github.io/book/cargo-fuzz/setup.html):
libFuzzer needs LLVM sanitizer support (x86-64 Linux, x86-64/arm64 macOS, and
Windows via the MSVC AddressSanitizer), a C++ compiler and a nightly toolchain
for `-Zsanitizer`.

Windows/ASan: the run needs `clang_rt.asan_dynamic-x86_64.dll`, which ships with
the Visual Studio MSVC toolset rather than Rust. Put its directory first on
`PATH` for `cargo fuzz run` (adjust the VS version/path):

```powershell
$env:PATH = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231\bin\Hostx64\x64;" + $env:PATH
```

If no ASan runtime is available, the targets still build and run without
sanitizers with `cargo +nightly-2026-11-08 fuzz run <target> --sanitizer none -- ...`;
that loses use-after-free/overflow detection but still catches panics/aborts.

## Commands

Keep cargo output out of `repo/target` (workspace policy). Use an absolute
target directory, e.g. `out/cargo-target`:

```powershell
$env:CARGO_TARGET_DIR = 'C:\Users\lisha\Desktop\keepassity\out\cargo-target'
$env:PATH = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231\bin\Hostx64\x64;" + $env:PATH
cd C:\Users\lisha\Desktop\keepassity\repo\crates\core\fuzz

# corpus goes outside the repo (mirrors CARGO_TARGET_DIR: out/fuzz-corpus)
pwsh -File scripts/seed-corpus.ps1
$corpusRoot = Join-Path (Split-Path -Parent $env:CARGO_TARGET_DIR) 'fuzz-corpus'

cargo +nightly-2026-11-08 fuzz build              # all targets
cargo +nightly-2026-11-08 fuzz list

# bounded local run per target (seconds; libFuzzer flags go after `--`)
$artifacts = Join-Path (Split-Path -Parent $env:CARGO_TARGET_DIR) 'fuzz-artifacts'
New-Item -ItemType Directory -Force (Join-Path $artifacts 'kdbx_parse') | Out-Null
cargo +nightly-2026-11-08 fuzz run kdbx_parse (Join-Path $corpusRoot 'kdbx_parse') -- `
  -max_total_time=180 -timeout=10 -rss_limit_mb=2048 -print_final_stats=1 `
  -artifact_prefix=(Join-Path $artifacts 'kdbx_parse')/
```

Repeat the last two lines for `totp_uri` and `merge_json` (separate corpus and
artifact subdirectories). `scripts/seed-corpus.ps1` prints the exact
`cargo fuzz run` line for the corpus root it used.

`-print_final_stats=1` prints the exec count / coverage (`cov`, `ft`) used in the
M5-02 evidence. Crash inputs go to the external `-artifact_prefix` directory;
logs can be captured with `2>&1 | Tee-Object -FilePath <path>`.

## Triage

```powershell
# reproduce
cargo +nightly-2026-11-08 fuzz run <target> artifacts/<target>/crash-<hash>
# minimize (keep the smallest still-crashing input)
cargo +nightly-2026-11-08 fuzz tmin <target> artifacts/<target>/crash-<hash>
```

A minimized crash must become a small regression test in
`crates/core/src/**` (or `crates/core/tests/`) with the exact bytes, and the
underlying panic fixed in `crates/core` before the finding is considered done.

## CI

`.github/workflows/fuzz.yml` runs each target for 600 s on `ubuntu-latest`
(daily at 03:00 UTC + `workflow_dispatch`), uploads logs/artifacts from
`$RUNNER_TEMP`, uses the pinned `nightly-2026-11-08` toolchain and
`cargo-fuzz 0.13.2`, and is kept out of `ci.yml`. It needs
`.github/workflows/fuzz.yml` added to `public.allowlist` before it can be
tracked in the public repository.

## Sources

- cargo-fuzz book, setup & usage: <https://rust-fuzz.github.io/book/cargo-fuzz.html>,
  <https://rust-fuzz.github.io/book/cargo-fuzz/setup.html>
- libFuzzer flags (`-max_total_time`, `-timeout`, `-rss_limit_mb`,
  `-print_final_stats`, `-artifact_prefix`): <https://llvm.org/docs/LibFuzzer.html>
- Rust sanitizer support (`-Zsanitizer=address` needs nightly):
  <https://doc.rust-lang.org/beta/unstable-book/compiler-flags/sanitizer.html>
- MSVC AddressSanitizer runtime (Windows DLL):
  <https://learn.microsoft.com/cpp/sanitizers/asan>
