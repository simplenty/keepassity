// SPDX-License-Identifier: GPL-3.0-or-later
//! KeePass merge semantics for DeletedObjects, history retention and group
//! subtrees. These are regressions for the old behaviour that applied every
//! deletion unconditionally and ignored `deletion_time`, silently discarding a
//! local edit (with no conflict and no history).

use keepassity_core::merge::{merge, ConflictKind};
use keepassity_core::model::{Database, DeletedObject, Entry, Group, StringField, Times, Uuid};

fn uuid(byte: u8) -> Uuid {
    Uuid::from_bytes([byte; 16])
}

fn stamp(second: u8) -> String {
    format!("2026-10-26T00:00:{second:02}Z")
}

fn entry(byte: u8, title: &str, modified: u8, location: u8) -> Entry {
    let mut entry = Entry {
        uuid: uuid(byte),
        times: Times {
            creation: stamp(1),
            last_modification: stamp(modified),
            last_access: stamp(modified),
            location_changed: stamp(location),
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

fn group(byte: u8, name: &str, modified: u8) -> Group {
    Group {
        uuid: uuid(byte),
        name: name.to_string(),
        times: Times {
            creation: stamp(1),
            last_modification: stamp(modified),
            last_access: stamp(modified),
            location_changed: stamp(1),
            ..Times::default()
        },
        ..Group::default()
    }
}

fn database() -> Database {
    Database {
        root: Group {
            uuid: uuid(1),
            name: "Root".to_string(),
            ..Group::default()
        },
        ..Database::default()
    }
}

fn delete_record(byte: u8, second: u8) -> DeletedObject {
    DeletedObject {
        uuid: uuid(byte),
        deletion_time: stamp(second),
    }
}

fn child_group(db: &Database, byte: u8) -> Option<&Group> {
    db.root.groups.iter().find(|group| group.uuid == uuid(byte))
}

#[test]
fn remote_delete_after_local_modification_keeps_entry_and_conflicts() {
    let local = {
        let mut db = database();
        db.root.entries.push(entry(2, "local-edit", 10, 2));
        db
    };
    let mut remote = database();
    remote.deleted_objects.push(delete_record(2, 5));

    let (merged, report) = merge(&local, &remote);

    let entry = merged
        .root
        .entries
        .iter()
        .find(|entry| entry.uuid == uuid(2))
        .expect("the later local edit must survive");
    assert_eq!(entry.field("Title"), Some("local-edit"));

    assert_eq!(report.conflicts, 1);
    assert_eq!(report.conflict_items.len(), 1);
    let conflict = &report.conflict_items[0];
    assert_eq!(conflict.uuid, uuid(2));
    assert_eq!(conflict.kind, ConflictKind::Entry);
    assert_eq!(
        conflict.fields,
        vec!["deleted remotely but modified locally".to_string()]
    );
    assert_eq!(conflict.local_modified, stamp(10));
    assert_eq!(conflict.remote_modified, stamp(5));
    assert!(
        !merged
            .deleted_objects
            .iter()
            .any(|item| item.uuid == uuid(2)),
        "a deletion that was not applied must not remain in the merged output"
    );
}

#[test]
fn local_delete_after_remote_modification_keeps_entry_and_conflicts() {
    let mut local = database();
    local.deleted_objects.push(delete_record(2, 5));
    let remote = {
        let mut db = database();
        db.root.entries.push(entry(2, "remote-edit", 10, 2));
        db
    };

    let (merged, report) = merge(&local, &remote);

    let entry = merged
        .root
        .entries
        .iter()
        .find(|entry| entry.uuid == uuid(2))
        .expect("the later remote edit must survive");
    assert_eq!(entry.field("Title"), Some("remote-edit"));

    assert_eq!(report.conflicts, 1);
    let conflict = &report.conflict_items[0];
    assert_eq!(conflict.kind, ConflictKind::Entry);
    assert_eq!(
        conflict.fields,
        vec!["deleted locally but modified remotely".to_string()]
    );
    assert_eq!(conflict.local_modified, stamp(5));
    assert_eq!(conflict.remote_modified, stamp(10));
}

#[test]
fn deletion_at_or_after_modification_applies() {
    // Remote deletion at the same time or later than the edit -> delete.
    for deletion_second in [5, 6] {
        let mut local = database();
        local.root.entries.push(entry(2, "old", 5, 2));
        let mut remote = database();
        remote
            .deleted_objects
            .push(delete_record(2, deletion_second));

        let (merged, report) = merge(&local, &remote);
        assert!(
            merged
                .root
                .entries
                .iter()
                .all(|entry| entry.uuid != uuid(2)),
            "deletion at {deletion_second} must remove an edit at 5"
        );
        assert_eq!(report.conflicts, 0);
        assert!(merged
            .deleted_objects
            .iter()
            .any(|item| item.uuid == uuid(2)));
    }

    // Local deletes before the remote edit -> the edit survives.
    let mut local = database();
    local.deleted_objects.push(delete_record(2, 5));
    let mut remote = database();
    remote.root.entries.push(entry(2, "newer", 6, 2));

    let (merged, report) = merge(&local, &remote);
    assert!(merged
        .root
        .entries
        .iter()
        .any(|entry| entry.uuid == uuid(2)));
    assert_eq!(report.conflicts, 1);
}

#[test]
fn group_deleted_remotely_but_modified_locally_is_kept_and_conflicts() {
    let mut local = database();
    local.root.groups.push(group(3, "local-renamed", 10));
    let mut remote = database();
    remote.root.groups.push(group(3, "old", 4));
    remote.deleted_objects.push(delete_record(3, 5));

    let (merged, report) = merge(&local, &remote);

    let group = child_group(&merged, 3).expect("the later local rename must survive");
    assert_eq!(group.name, "local-renamed");
    assert_eq!(report.conflicts, 1);
    let conflict = &report.conflict_items[0];
    assert_eq!(conflict.uuid, uuid(3));
    assert_eq!(conflict.kind, ConflictKind::Group);
    assert_eq!(
        conflict.fields,
        vec!["deleted remotely but modified locally".to_string()]
    );
    assert_eq!(conflict.local_modified, stamp(10));
    assert_eq!(conflict.remote_modified, stamp(5));
}

#[test]
fn deleted_group_with_a_later_local_edit_survives_with_its_content() {
    let mut local = database();
    let mut doomed = group(3, "Doomed", 2);
    doomed
        .entries
        .push(entry(2, "edited-after-deletion", 10, 2));
    local.root.groups.push(doomed);
    let mut remote = database();
    remote.deleted_objects.push(delete_record(3, 5));

    let (merged, report) = merge(&local, &remote);

    let group = child_group(&merged, 3)
        .expect("a deleted group holding surviving content must not be removed");
    assert_eq!(group.name, "Doomed");
    assert_eq!(
        group
            .entries
            .iter()
            .find(|entry| entry.uuid == uuid(2))
            .and_then(|entry| entry.field("Title")),
        Some("edited-after-deletion")
    );
    assert!(
        !merged
            .deleted_objects
            .iter()
            .any(|item| item.uuid == uuid(3)),
        "the group deletion was not applied, so its record must not be output"
    );
    assert_eq!(report.conflicts, 0, "the group itself was not edited");
}

#[test]
fn duplicate_deletion_records_keep_the_latest_time() {
    let mut local = database();
    // Same UUID twice: only the later deletion (7) may be used. The remote
    // entry was modified at 6, so it must be deleted.
    local.deleted_objects.push(delete_record(2, 5));
    local.deleted_objects.push(delete_record(2, 7));
    let mut remote = database();
    remote.root.entries.push(entry(2, "six", 6, 2));

    let (merged, report) = merge(&local, &remote);

    assert!(!merged
        .root
        .entries
        .iter()
        .any(|entry| entry.uuid == uuid(2)));
    let record = merged
        .deleted_objects
        .iter()
        .find(|item| item.uuid == uuid(2))
        .expect("deletion record");
    assert_eq!(record.deletion_time, stamp(7));
    assert_eq!(report.deleted_skipped, 1);

    // A later record on the remote side also wins over an earlier local one.
    let mut local = database();
    local.deleted_objects.push(delete_record(4, 7));
    let mut remote = database();
    remote.root.entries.push(entry(4, "six", 6, 2));
    remote.deleted_objects.push(delete_record(4, 5));
    let (merged, _) = merge(&local, &remote);
    assert!(!merged
        .root
        .entries
        .iter()
        .any(|entry| entry.uuid == uuid(4)));
}

#[test]
fn merged_history_is_truncated_to_local_item_limit() {
    let mut local = database();
    local.meta.history_max_items = 1;
    local.meta.history_max_size = -1;
    let mut current = entry(2, "v1", 2, 2);
    current.history.push(entry(2, "v0", 1, 1));
    local.root.entries.push(current);
    let mut remote = database();
    remote.root.entries.push(entry(2, "v2", 3, 2));

    let (merged, report) = merge(&local, &remote);

    let entry = &merged.root.entries[0];
    assert_eq!(entry.field("Title"), Some("v2"));
    assert_eq!(
        entry.history.len(),
        1,
        "the 10-item default must be overridden by the local limit"
    );
    assert_eq!(entry.history[0].field("Title"), Some("v1"));
    assert_eq!(report.conflicts, 0);
}

#[test]
fn merged_history_is_truncated_to_local_size_limit() {
    let mut local = database();
    local.meta.history_max_items = -1;
    local.meta.history_max_size = 0;
    let mut current = entry(2, "v1", 2, 2);
    current.history.push(entry(2, "v0", 1, 1));
    local.root.entries.push(current);
    let mut remote = database();
    remote.root.entries.push(entry(2, "v2", 3, 2));

    let (merged, _) = merge(&local, &remote);

    let entry = &merged.root.entries[0];
    assert_eq!(entry.field("Title"), Some("v2"));
    assert!(
        entry.history.is_empty(),
        "a zero size limit must keep no history after merge"
    );
}

#[test]
fn default_database_meta_uses_keepass_history_defaults() {
    let db = Database::default();
    assert!(db.meta.recycle_bin_enabled);
    assert_eq!(db.meta.history_max_items, 10);
    assert_eq!(db.meta.history_max_size, 6 * 1024 * 1024);
}
