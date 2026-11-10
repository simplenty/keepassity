// SPDX-License-Identifier: GPL-3.0-or-later
//! Write-fidelity and sync-conflict regression checks.
//!
//! `write_model` is used after every save and merge, so everything the reader
//! parsed must survive a rewrite. A rewrite that silently drops data (icons,
//! auto-type, colors, custom data, group notes, ...) changes the entry
//! fingerprints and makes the next merge against a vault written by another
//! client report a conflict for content the user never touched.

use keepassity_core::kdbx::{reader, writer};
use keepassity_core::merge::merge;
use keepassity_core::model::{Database, Entry, Group};
use std::path::{Path, PathBuf};

const PASSWORD: &str = "demopass";

fn fixtures() -> Vec<PathBuf> {
    let base = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tests/fixtures/keepass-rs");
    [
        "test_db_kdbx4_with_password_aes.kdbx",
        "test_db_kdbx41_features.kdbx",
        "test_db_with_password.kdbx",
    ]
    .iter()
    .map(|name| base.join(name))
    .collect()
}

fn parse_fixture(path: &Path) -> Database {
    let bytes = std::fs::read(path).expect("fixture bytes");
    reader::parse(&bytes, PASSWORD).expect("parse fixture")
}

fn rewrite(db: &Database) -> Database {
    let bytes = writer::write_model(db, PASSWORD).expect("write model");
    reader::parse(&bytes, PASSWORD).expect("read back")
}

/// `Generator` is written by the backend itself, so it is not part of the
/// model round trip.
fn comparable(mut db: Database) -> Database {
    // Written by the backend itself, or diagnostics of the last read.
    db.meta.generator = String::new();
    db.format_version = String::new();
    db.raw_xml = None;
    db.raw_xml_sha256 = None;
    db
}

fn describe_differences(left: &Database, right: &Database) -> Vec<String> {
    fn diff_paths(a: &serde_json::Value, b: &serde_json::Value, path: &str, out: &mut Vec<String>) {
        match (a, b) {
            (serde_json::Value::Object(ma), serde_json::Value::Object(mb)) => {
                for key in ma.keys().chain(mb.keys()) {
                    let pa = ma.get(key);
                    let pb = mb.get(key);
                    if pa != pb {
                        let child = format!("{path}/{key}");
                        match (pa, pb) {
                            (Some(pa), Some(pb)) => diff_paths(pa, pb, &child, out),
                            _ => out.push(child),
                        }
                    }
                }
            }
            (serde_json::Value::Array(va), serde_json::Value::Array(vb)) => {
                if va.len() != vb.len() {
                    out.push(format!("{path}[len {} vs {}]", va.len(), vb.len()));
                    return;
                }
                for (index, (ca, cb)) in va.iter().zip(vb.iter()).enumerate() {
                    if ca != cb {
                        diff_paths(ca, cb, &format!("{path}[{index}]"), out);
                    }
                }
            }
            _ => {
                if a != b {
                    out.push(path.to_string());
                }
            }
        }
    }
    let a = serde_json::to_value(left).expect("json");
    let b = serde_json::to_value(right).expect("json");
    let mut out = Vec::new();
    diff_paths(&a, &b, "", &mut out);
    out
}

/// Merge intentionally unions and deduplicates history snapshots, so compare
/// the current content only.
fn without_history(mut db: Database) -> Database {
    fn clear(group: &mut Group) {
        for entry in &mut group.entries {
            entry.history.clear();
        }
        for child in &mut group.groups {
            clear(child);
        }
    }
    clear(&mut db.root);
    db
}

fn first_entry_mut(group: &mut Group) -> Option<&mut Entry> {
    if let Some(entry) = group.entries.first_mut() {
        return Some(entry);
    }
    group.groups.iter_mut().find_map(first_entry_mut)
}

#[test]
fn write_model_keeps_every_field_the_reader_parsed() {
    for path in fixtures() {
        let original = parse_fixture(&path);
        let again = rewrite(&original);
        let differences =
            describe_differences(&comparable(again.clone()), &comparable(original.clone()));
        assert!(
            differences.is_empty(),
            "rewriting {} lost data: {:?}",
            path.display(),
            differences
        );
    }
}

#[test]
fn rewriting_a_vault_does_not_introduce_merge_conflicts() {
    for path in fixtures() {
        let original = parse_fixture(&path);
        let rewritten = rewrite(&original);
        let (merged, report) = merge(&rewritten, &original);
        assert_eq!(
            report.conflicts,
            0,
            "{}: a rewrite must not look like a concurrent edit",
            path.display()
        );
        // The same check in the other direction: local file vs remote copy.
        let (_, reversed) = merge(&original, &rewritten);
        assert_eq!(reversed.conflicts, 0, "{}: reversed order", path.display());
        let differences = describe_differences(
            &comparable(without_history(merged)),
            &comparable(without_history(rewritten.clone())),
        );
        assert!(
            differences.is_empty(),
            "{}: merging with the untouched original must keep the vault as is: {:?}",
            path.display(),
            differences
        );
    }
}

/// Simulates the vault files our own earlier builds wrote (and any client that
/// omits optional metadata): same content, no icons, colors, auto-type or
/// custom data.
fn strip_optional_metadata(mut db: Database) -> Database {
    fn strip_entry(entry: &mut Entry) {
        entry.icon_id = 0;
        entry.custom_icon_uuid = None;
        entry.foreground_color.clear();
        entry.background_color.clear();
        entry.override_url.clear();
        entry.auto_type = None;
        entry.custom_data = Default::default();
        for history in &mut entry.history {
            strip_entry(history);
        }
    }
    fn strip_group(group: &mut Group) {
        group.notes.clear();
        group.icon_id = 0;
        group.custom_icon_uuid = None;
        group.is_expanded = false;
        group.default_autotype_sequence.clear();
        group.enable_auto_type = false;
        group.enable_searching = false;
        group.last_top_visible_entry = None;
        group.custom_data = Default::default();
        for entry in &mut group.entries {
            strip_entry(entry);
        }
        for child in &mut group.groups {
            strip_group(child);
        }
    }
    strip_group(&mut db.root);
    db
}

#[test]
fn a_vault_saved_by_an_older_build_merges_without_asking() {
    for path in fixtures() {
        let original = parse_fixture(&path);
        let stripped = strip_optional_metadata(original.clone());
        for (local, remote) in [(&stripped, &original), (&original, &stripped)] {
            let (merged, report) = merge(local, remote);
            assert_eq!(
                report.conflicts,
                0,
                "{}: missing optional metadata must not look like an edit",
                path.display()
            );
            let differences = describe_differences(
                &comparable(without_history(merged)),
                &comparable(without_history(original.clone())),
            );
            assert!(
                differences.is_empty(),
                "{}: the richer copy must win: {:?}",
                path.display(),
                differences
            );
        }
    }
}

#[test]
fn different_metadata_on_both_sides_still_conflicts() {
    let original = parse_fixture(&fixtures()[0]);
    let mut local = original.clone();
    let mut remote = original.clone();
    first_entry_mut(&mut local.root).expect("entry").icon_id = 5;
    first_entry_mut(&mut remote.root).expect("entry").icon_id = 7;
    let (_, report) = merge(&local, &remote);
    assert_eq!(
        report.conflicts, 1,
        "two different icons are a real edit and must ask the user"
    );
}

#[test]
fn a_real_content_change_at_the_same_time_still_conflicts() {
    let original = parse_fixture(&fixtures()[0]);
    let mut remote = original.clone();
    let entry = first_entry_mut(&mut remote.root).expect("entry");
    entry.strings.retain(|field| field.key != "Title");
    entry.strings.push(keepassity_core::model::StringField {
        key: "Title".to_string(),
        value: "changed elsewhere".to_string(),
        protected: false,
        unknown: Vec::new(),
    });
    let (_, report) = merge(&original, &remote);
    assert_eq!(
        report.conflicts, 1,
        "a same-time content change must still ask the user"
    );
}

#[test]
fn a_move_to_the_top_level_survives_a_rewrite_and_merge() {
    let mut local = parse_fixture(&fixtures()[0]);
    let (entry_uuid, _) = first_entry_mut(&mut local.root)
        .map(|entry| (entry.uuid, ()))
        .expect("entry");
    // Mimic the FFI move handler: remove, bump LocationChanged, append to root.
    fn take(group: &mut Group, uuid: keepassity_core::model::Uuid) -> Option<Entry> {
        if let Some(index) = group.entries.iter().position(|entry| entry.uuid == uuid) {
            return Some(group.entries.remove(index));
        }
        for child in &mut group.groups {
            if let Some(entry) = take(child, uuid) {
                return Some(entry);
            }
        }
        None
    }
    let mut entry = take(&mut local.root, entry_uuid).expect("entry exists");
    entry.times.location_changed = "2026-11-10T10:00:00Z".to_string();
    local.root.entries.push(entry);

    let rewritten = rewrite(&local);
    let (merged, report) = merge(&rewritten, &local);
    assert_eq!(report.conflicts, 0, "the move must not conflict");
    assert!(
        merged
            .root
            .entries
            .iter()
            .any(|entry| entry.uuid == entry_uuid),
        "the moved entry must stay at the top level"
    );
}
