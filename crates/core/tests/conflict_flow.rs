// SPDX-License-Identifier: GPL-3.0-or-later
//! M2-03: conflict report + conflict-copy + no-silent-overwrite acceptance.

use keepassity_core::dto::ConflictItem;
use keepassity_core::model::{Database, Entry, Group, StringField, Times, Uuid};
use keepassity_core::sync::conflict::{conflict_copy_name, ConflictChoice, ConflictPlan};
use keepassity_core::sync::merge_for_sync;

fn uuid(byte: u8) -> Uuid {
    Uuid::from_bytes([byte; 16])
}

fn entry(title: &str) -> Entry {
    let mut entry = Entry {
        uuid: uuid(2),
        times: Times {
            last_modification: "2026-10-26T00:00:00Z".to_string(),
            location_changed: "2026-10-26T00:00:00Z".to_string(),
            ..Times::default()
        },
        ..Entry::default()
    };
    entry.strings.push(StringField {
        key: "Title".to_string(),
        value: title.to_string(),
        protected: false,
        unknown: Vec::new(),
    });
    entry
}

fn db(title: &str) -> Database {
    Database {
        root: Group {
            uuid: uuid(1),
            name: "Root".to_string(),
            entries: vec![entry(title)],
            ..Group::default()
        },
        ..Database::default()
    }
}

#[test]
fn conflict_merge_returns_dto_ready_conflicts_and_requires_copy() {
    let local = db("left");
    let remote = db("right");
    let merged = merge_for_sync(&local, &remote);
    assert!(merged.has_conflicts);
    assert_eq!(merged.conflicts.len(), 1);
    assert!(merged.report.conflicts >= 1);

    let dto = ConflictItem::from(&merged.conflicts[0]);
    assert_eq!(dto.uuid, uuid(2).to_hex());
    assert_eq!(dto.kind, keepassity_core::dto::ConflictKind::Entry);
    assert!(dto.fields.iter().any(|field| field == "Title"));
    assert_eq!(dto.local_modified_unix, Some(1_792_972_800));
    // DTO serializes without a secret/value payload.
    let json = serde_json::to_string(&dto).unwrap();
    assert!(!json.contains("left"));
    assert!(!json.contains("right"));
}

#[test]
fn all_three_choices_copy_first() {
    for choice in [
        ConflictChoice::KeepLocal,
        ConflictChoice::KeepRemote,
        ConflictChoice::KeepBoth,
    ] {
        let plan: ConflictPlan = choice.plan();
        assert!(plan.write_conflict_copy);
    }
}

#[test]
fn conflict_copy_names_follow_the_documented_rule() {
    let name = conflict_copy_name("C:\\data\\passwords.kdbx", "2026-10-26 00-30-00").unwrap();
    assert_eq!(
        name,
        "C:\\data\\passwords (conflict 2026-10-26 00-30-00).kdbx"
    );
    assert!(conflict_copy_name("passwords.kdbx", "../bad").is_err());
}
