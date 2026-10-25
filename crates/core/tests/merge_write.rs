// SPDX-License-Identifier: GPL-3.0-or-later
//! M2-01 completion: write a merged model back to KDBX and read it with the
//! independent reader. External openability is checked by the ignored helper.

use keepassity_core::kdbx::{detect_version, reader, writer};
use keepassity_core::merge::merge;
use keepassity_core::model::{Database, Entry, Group, StringField, Times};
use std::path::{Path, PathBuf};

fn fixture() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/fixtures/keepass-rs/test_db_kdbx4_with_password_aes.kdbx")
}

fn write_and_read(db: &Database) -> Database {
    let bytes = writer::write_model(db, "test").expect("write merged model");
    assert_eq!(detect_version(&bytes[..12]).unwrap().as_str(), "4.0");
    reader::parse(&bytes, "test").expect("read written model")
}

fn first_entry_mut(group: &mut Group) -> Option<&mut Entry> {
    if let Some(entry) = group.entries.first_mut() {
        return Some(entry);
    }
    group.groups.iter_mut().find_map(first_entry_mut)
}

#[test]
fn merged_result_writes_and_reads_back() {
    let bytes = std::fs::read(fixture()).expect("fixture");
    let local = reader::parse(&bytes, "demopass").expect("parse fixture");
    let mut remote = local.clone();
    let entry = first_entry_mut(&mut remote.root).expect("entry");
    entry.strings.retain(|field| field.key != "Title");
    entry.strings.push(StringField {
        key: "Title".to_string(),
        value: "merged-title".to_string(),
        protected: false,
        unknown: Vec::new(),
    });
    // A later mtime wins the merge.
    entry.times = Times {
        creation: "2026-10-25T10:00:01Z".to_string(),
        last_modification: "2026-10-25T10:00:09Z".to_string(),
        last_access: "2026-10-25T10:00:09Z".to_string(),
        expiry: "2026-10-25T10:00:01Z".to_string(),
        location_changed: "2026-10-25T10:00:09Z".to_string(),
        ..Times::default()
    };

    let (merged, report) = merge(&local, &remote);
    assert!(report.entries_updated >= 1);
    let written = write_and_read(&merged);
    let entry = written
        .root
        .entries
        .iter()
        .find(|entry| entry.field("Title") == Some("merged-title"))
        .expect("merged title survives write");
    assert!(!entry.history.is_empty(), "old snapshot in history");
}

#[test]
fn merged_result_opens_with_independent_reader_for_all_fixture_families() {
    for name in [
        "test_db_kdbx4_with_password_aes.kdbx",
        "test_db_kdbx41_features.kdbx",
        "test_db_with_password.kdbx",
    ] {
        let path = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../tests/fixtures/keepass-rs")
            .join(name);
        let bytes = std::fs::read(&path).expect("fixture");
        let password = "demopass";
        let db = reader::parse(&bytes, password).expect("parse");
        let written = writer::write_model(&db, password).expect("write");
        reader::parse(&written, password).expect("re-read");
    }
}

/// Manual interop helper: write the merged result for KeePassXC/KeePassLib.
#[test]
#[ignore = "requires KEEPASSITY_MERGE_WRITE_OUT"]
fn write_merged_for_external_verification() {
    let output = std::env::var("KEEPASSITY_MERGE_WRITE_OUT").expect("output path");
    let bytes = std::fs::read(fixture()).expect("fixture");
    let local = reader::parse(&bytes, "demopass").expect("parse");
    let mut remote = local.clone();
    let entry = first_entry_mut(&mut remote.root).expect("entry");
    entry.strings.retain(|field| field.key != "Title");
    entry.strings.push(StringField {
        key: "Title".to_string(),
        value: "merged-title".to_string(),
        protected: false,
        unknown: Vec::new(),
    });
    entry.times.last_modification = "2026-10-25T10:00:09Z".to_string();
    entry.times.location_changed = "2026-10-25T10:00:09Z".to_string();
    let (merged, _) = merge(&local, &remote);
    let written = writer::write_model(&merged, "demopass").expect("write");
    std::fs::write(output, written).expect("write output");
}
