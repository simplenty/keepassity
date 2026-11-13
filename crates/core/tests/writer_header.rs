// SPDX-License-Identifier: GPL-3.0-or-later
//! A save must preserve the KDBX4 outer header the user's file arrived with.
//!
//! Regression for the old writer, which built a fresh `KdbxFile::new` on every
//! save: a 4.1 file was written back as 4.0 and an AES-KDF database was
//! silently converted to Argon2id, changing open time and compatibility.

use keepassity_core::kdbx::reader::parse;
use keepassity_core::kdbx::save::verify_rewritten;
use keepassity_core::kdbx::writer::{verify_written_with_cache, write_model, write_model_cached};
use keepassity_core::merge::merge;
use keepassxcr::format::read_kdbx_bytes;
use keepassxcr::Credentials;

fn fixture(name: &str) -> Vec<u8> {
    std::fs::read(format!(
        "{}/../../tests/fixtures/keepass-rs/{name}",
        env!("CARGO_MANIFEST_DIR")
    ))
    .expect("fixture")
}

#[test]
fn save_preserves_the_outer_header() {
    for name in [
        "test_db_kdbx4_with_password_aes.kdbx",
        "test_db_kdbx4_with_password_argon2id.kdbx",
        "test_db_kdbx4_with_password_argon2_chacha20.kdbx",
        "test_db_kdbx4_with_password_argon2_twofish.kdbx",
        "test_db_kdbx4_with_password_argon2id_chacha20.kdbx",
        "test_db_kdbx4_with_password_argon2id_twofish.kdbx",
        "test_db_kdbx41_features.kdbx",
    ] {
        let database = parse(&fixture(name), "demopass").expect("read fixture");
        let header = database
            .outer
            .clone()
            .unwrap_or_else(|| panic!("{name}: no outer header"));
        assert_eq!(database.format_version, header.version, "{name}");

        let written = write_model(&database, "demopass").expect("write model");
        let reread = parse(&written, "demopass").expect("read written model");
        assert_eq!(
            reread.format_version, header.version,
            "{name}: version changed"
        );
        assert_eq!(
            reread.outer.as_ref(),
            Some(&header),
            "{name}: outer header changed"
        );
    }
}

#[test]
fn merge_preserves_the_outer_header() {
    let database =
        parse(&fixture("test_db_kdbx4_with_password_aes.kdbx"), "demopass").expect("read fixture");
    let (merged, report) = merge(&database, &database);
    assert_eq!(report.conflicts, 0);
    assert_eq!(merged.outer, database.outer);
}

#[test]
fn a_written_database_passes_verified_rewrite() {
    // The old writer reset the header, so verify_rewritten rejected every
    // output. The preserved header is what makes the core save protocol usable.
    let original = fixture("test_db_kdbx4_with_password_aes.kdbx");
    let database = parse(&original, "demopass").expect("read fixture");
    let written = write_model(&database, "demopass").expect("write model");
    verify_rewritten(&original, &written, "demopass").expect("verified rewrite");
}

#[test]
fn cached_saves_reuse_the_kdf_salt() {
    // The first save derives the transformed key and returns a cache; the
    // second save must reuse the same KDF salt/transformed key instead of
    // running the KDF again, while still drawing a fresh master seed and IV.
    let database =
        parse(&fixture("test_db_kdbx4_with_password_aes.kdbx"), "demopass").expect("read fixture");
    let (first, cache) =
        write_model_cached(&database, "demopass", None).expect("first cached write");
    let (second, _cache) =
        write_model_cached(&database, "demopass", Some(&cache)).expect("second cached write");
    assert_ne!(first, second, "master seed/IV must still be fresh");

    let credentials = Credentials::password("demopass");
    let first_file = read_kdbx_bytes(&first, &credentials).expect("read first");
    let second_file = read_kdbx_bytes(&second, &credentials).expect("read second");
    assert_eq!(first_file.kdf.seed(), second_file.kdf.seed());
    assert!(parse(&second, "demopass").is_ok());

    verify_written_with_cache(&second, "demopass", &cache).expect("verify cached save");
    let mut damaged = second.clone();
    let last = damaged.len() - 1;
    damaged[last] ^= 0xff;
    assert!(verify_written_with_cache(&damaged, "demopass", &cache).is_err());
}
