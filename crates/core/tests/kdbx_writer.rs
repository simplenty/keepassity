// SPDX-License-Identifier: GPL-3.0-or-later
//! M1-03 write-path coverage: re-encrypt fixtures through the vendored
//! keepassxcr byte API, prove fresh randomness, and read the result back with
//! the independent `keepass` reader.

use std::path::{Path, PathBuf};

fn fixture(name: &str) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/fixtures/keepass-rs")
        .join(name)
}

fn read(path: &Path) -> Vec<u8> {
    std::fs::read(path).expect("read fixture")
}

fn stripped(mut db: keepassity_core::model::Database) -> keepassity_core::model::Database {
    db.raw_xml = None;
    db.raw_xml_sha256 = None;
    // keepassxcr regenerates Meta/Generator on save (upstream behaviour); the
    // M0-06 evaluation accepted this as metadata-only.
    db.meta.generator.clear();
    db
}

#[test]
fn reseal_kdbx4_aes_roundtrip() {
    let bytes = read(&fixture("test_db_kdbx4_with_password_aes.kdbx"));
    let original =
        stripped(keepassity_core::kdbx::reader::parse(&bytes, "demopass").expect("read original"));
    let resealed = keepassity_core::kdbx::writer::reseal(&bytes, "demopass").expect("reseal");
    let again = keepassity_core::kdbx::writer::reseal(&bytes, "demopass").expect("reseal again");
    assert_ne!(resealed, again, "fresh salt/seed must change every save");
    assert_eq!(
        keepassity_core::kdbx::detect_version(&resealed[..12]).unwrap(),
        keepassity_core::kdbx::detect_version(&bytes[..12]).unwrap()
    );

    let opened = stripped(
        keepassity_core::kdbx::reader::parse(&resealed, "demopass").expect("read resealed"),
    );
    assert_eq!(opened, original, "resealed data must match the original");
}

#[test]
fn reseal_kdbx41_features_roundtrip() {
    let bytes = read(&fixture("test_db_kdbx41_features.kdbx"));
    let original =
        stripped(keepassity_core::kdbx::reader::parse(&bytes, "demopass").expect("read original"));
    let resealed = keepassity_core::kdbx::writer::reseal(&bytes, "demopass").expect("reseal");
    let opened = stripped(
        keepassity_core::kdbx::reader::parse(&resealed, "demopass").expect("read resealed"),
    );
    assert_eq!(opened, original, "4.1 features must survive reseal");
}

#[test]
fn reseal_rejects_bad_password_and_broken_data() {
    let bytes = read(&fixture("test_db_kdbx4_with_password_aes.kdbx"));
    assert!(keepassity_core::kdbx::writer::reseal(&bytes, "wrong").is_err());
    assert!(keepassity_core::kdbx::writer::reseal(b"not a kdbx", "x").is_err());
}

/// Manual interop helper: write a resealed fixture to `KEEPASSITY_INTEROP_OUT`
/// so keepassxc-cli / KeePassLib can be pointed at the exact bytes.
#[test]
#[ignore = "requires KEEPASSITY_INTEROP_OUT"]
fn write_resealed_fixture_for_interop() {
    let output = std::env::var("KEEPASSITY_INTEROP_OUT").expect("KEEPASSITY_INTEROP_OUT");
    let bytes = read(&fixture("test_db_kdbx4_with_password_aes.kdbx"));
    let resealed = keepassity_core::kdbx::writer::reseal(&bytes, "demopass").expect("reseal");
    std::fs::write(&output, resealed).expect("write resealed fixture");
}
