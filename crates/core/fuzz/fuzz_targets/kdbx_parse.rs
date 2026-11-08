// SPDX-License-Identifier: GPL-3.0-or-later
//! Fuzz target: KDBX file parsing (the highest-risk input surface).
//!
//! The fuzzer input is treated as an attacker-supplied `.kdbx` file and passed
//! to `keepassity_core::kdbx::reader::parse` with a fixed public test password.
//! The seeded corpus is made of the `demopass` fixtures from
//! `tests/fixtures/keepass-rs/`, so mutations start from a valid container and
//! reach the KDF, HMAC/decryption and XML-to-model mapping code instead of
//! stopping at the 12-byte signature check.
//!
//! Expected results are `Err(...)` for malformed, truncated, wrong-key or
//! unsupported files; the only failure signal is a panic, abort, sanitizer
//! report, out-of-memory or timeout. Resource bounds are libFuzzer flags
//! (`-timeout=10 -rss_limit_mb=2048`), documented in README.md.
//!
//! Corpus files must stay public test fixtures — never add a real vault.
#![no_main]

use libfuzzer_sys::fuzz_target;

/// Public test-database password (`tests/fixtures/README.md`); not a secret.
const TEST_PASSWORD: &str = "demopass";

fuzz_target!(|data: &[u8]| {
    let result = keepassity_core::kdbx::reader::parse(data, TEST_PASSWORD);

    // On success, exercise the mapped model too (traversal, ordering, canonical
    // dump / secret redaction); a panic there is still a core bug.
    if let Ok(database) = result {
        let _ = database.canonical_value();
        let _ = database.meta.database_name.len();
        let _ = database.root.entries.len();
    }
});
