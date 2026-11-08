// SPDX-License-Identifier: GPL-3.0-or-later
//! Locks the Argon2id parameters used for databases this backend creates.
//!
//! They are calibrated, not arbitrary: the v1.0 requirement asks for roughly
//! 0.5-1 s of KDF work per unlock on the reference machine. Measured with the
//! real reader (crates/core/tests/perf_paths_500.rs, method in
//! workshop/docs/verify/perf-2026-11-09.md), 64 MiB / I=3 / P=2 unlocked in
//! ~0.32 s and 128 MiB / I=6 / P=4 in ~0.71 s, so the latter is the default for
//! files written by this backend.
//!
//! Existing databases keep the parameters recorded in their own header, so this
//! only affects new databases. If the default moves, this test and the
//! performance report must move together.
//!
//! `default_argon2id` is the single source of truth in the vendored format
//! crate; this test pins the values the product depends on so an accidental
//! vendor sync cannot silently weaken new vaults.

use keepassxcr::crypto::{KdfConfig, KdfType};

/// A new database written through the product write path must carry the
/// calibrated parameters in its header (the header is what other KeePass
/// clients read), and that header must equal the backend default.
#[test]
fn new_database_header_uses_the_calibrated_argon2id_parameters() {
    let bytes =
        keepassity_core::kdbx::writer::create_empty("kdf-defaults-test", "kdf-defaults-test")
            .expect("create a new database");

    let credentials = keepassxcr::Credentials::password("kdf-defaults-test");
    let file = keepassxcr::format::read_kdbx_bytes(&bytes, &credentials)
        .expect("the written database must be readable");

    match (file.kdf, KdfConfig::default_argon2id()) {
        (
            KdfConfig::Argon2 {
                kind,
                iterations,
                memory_kib,
                parallelism,
                version,
                ..
            },
            KdfConfig::Argon2 {
                kind: default_kind,
                iterations: default_iterations,
                memory_kib: default_memory,
                parallelism: default_parallelism,
                version: default_version,
                ..
            },
        ) => {
            assert_eq!(kind, KdfType::Argon2id, "new databases use Argon2id");
            assert_eq!(kind, default_kind);
            assert_eq!(
                memory_kib,
                128 * 1024,
                "128 MiB: requirement lower bound is 64 MiB, 128 MiB measured at ~0.71 s"
            );
            assert_eq!(memory_kib, default_memory);
            assert_eq!(iterations, 6, "I=6 measured inside the 0.5-1 s window");
            assert_eq!(iterations, default_iterations);
            assert_eq!(parallelism, 4, "P=4 per REQUIREMENTS section 6.7");
            assert_eq!(parallelism, default_parallelism);
            assert_eq!(version, 0x13, "Argon2 version 1.3 (0x13)");
            assert_eq!(version, default_version);
        }
        (written, default) => panic!("expected Argon2id headers, got {written:?} / {default:?}"),
    }
}
