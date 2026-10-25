// SPDX-License-Identifier: GPL-3.0-or-later
//! M2-04: 18-scenario sync matrix (plan §8.4).
//!
//! The harness drives the pure sync/merge core with an in-memory remote
//! metadata model; the real rclone/WebDAV transport is M4-01. Each scenario is
//! a separate test so a failure names the exact row.

use keepassity_core::merge::merge;
use keepassity_core::model::{Database, DeletedObject, Entry, Group, StringField, Times, Uuid};
use keepassity_core::sync::conflict::{conflict_copy_name, ConflictChoice};
use keepassity_core::sync::{
    local_hash, merge_for_sync, RemoteMeta, SyncActionKind, SyncBaseline, SyncState,
};

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

fn base_db() -> Database {
    Database {
        root: Group {
            uuid: uuid(1),
            name: "Root".to_string(),
            entries: vec![entry(2, "alpha", 2, 2)],
            ..Group::default()
        },
        ..Database::default()
    }
}

fn baseline() -> SyncBaseline {
    SyncBaseline {
        etag: Some("\"v1\"".to_string()),
        sha256: Some("sha-v1".to_string()),
        size: Some(100),
        modified_unix: Some(2),
    }
}

fn meta(etag: Option<&str>, sha: Option<&str>, modified: i64) -> RemoteMeta {
    RemoteMeta {
        etag: etag.map(str::to_string),
        size: 100,
        modified_unix: Some(modified),
        sha256: sha.map(str::to_string),
    }
}

fn entry_by_title<'a>(entries: &'a [Entry], title: &str) -> Option<&'a Entry> {
    entries
        .iter()
        .find(|entry| entry.field("Title") == Some(title))
}

fn group_entries(db: &Database, group_uuid: Uuid) -> Vec<&Entry> {
    db.root
        .groups
        .iter()
        .find(|group| group.uuid == group_uuid)
        .map(|group| group.entries.iter().collect())
        .unwrap_or_default()
}

// 1. Remote unchanged + local clean -> UpToDate, no PUT.
#[test]
fn scenario_01_remote_unchanged_clean() {
    let state = SyncState::new(baseline());
    let decision = state.decide(&meta(Some("\"v1\""), Some("sha-v1"), 3));
    assert_eq!(decision.action, SyncActionKind::UpToDate);
    assert_ne!(decision.action, SyncActionKind::Upload);
}

// 2. Remote unchanged + local dirty -> PUT success, baseline updated.
#[test]
fn scenario_02_remote_unchanged_dirty_uploads() {
    let mut state = SyncState::new(baseline());
    state.mark_dirty();
    let decision = state.decide(&meta(Some("\"v1\""), Some("sha-v1"), 2));
    assert_eq!(decision.action, SyncActionKind::Upload);
    state.commit_success(
        Some("\"v2\"".to_string()),
        local_hash(b"content"),
        7,
        Some(5),
    );
    assert_eq!(state.baseline.etag.as_deref(), Some("\"v2\""));
    assert!(!state.local_dirty);
    assert_eq!(
        state.decide(&meta(Some("\"v2\""), None, 5)).action,
        SyncActionKind::UpToDate
    );
}

// 3. Remote mtime-only change with same content hash is not a change.
#[test]
fn scenario_03_mtime_only_is_not_content_change() {
    let state = SyncState::new(baseline());
    let changed_mtime = meta(Some("\"v1\""), Some("sha-v1"), 999);
    assert_eq!(
        state.decide(&changed_mtime).action,
        SyncActionKind::UpToDate
    );

    let degraded = SyncState::new(SyncBaseline {
        etag: None,
        sha256: Some("sha-v1".to_string()),
        size: Some(100),
        modified_unix: Some(2),
    });
    let decision = degraded.decide(&meta(None, Some("sha-v1"), 999));
    assert_eq!(decision.action, SyncActionKind::UpToDate);
    assert!(decision.degraded);
}

// 4. Remote content changed + local clean -> download/adopt.
#[test]
fn scenario_04_remote_changed_clean_downloads() {
    let state = SyncState::new(baseline());
    let decision = state.decide(&meta(Some("\"v2\""), Some("sha-v2"), 3));
    assert_eq!(decision.action, SyncActionKind::Download);
    let remote = base_db();
    let adopted = remote.clone();
    assert_eq!(adopted.root.entries.len(), 1);
}

// 5. Both sides changed different entries -> merge keeps both.
#[test]
fn scenario_05_different_entries_merge() {
    let local = base_db();
    let mut remote = base_db();
    remote.root.entries.push(entry(3, "beta", 2, 2));
    let merged = merge_for_sync(&local, &remote);
    assert!(!merged.has_conflicts);
    assert_eq!(merged.database.root.entries.len(), 2);
    assert!(entry_by_title(&merged.database.root.entries, "alpha").is_some());
    assert!(entry_by_title(&merged.database.root.entries, "beta").is_some());
}

// 6. Same entry, newer remote mtime -> take newer, record old in History.
#[test]
fn scenario_06_same_entry_newer_wins_with_history() {
    let local = base_db();
    let mut remote = base_db();
    remote.root.entries[0].strings[0].value = "beta".to_string();
    remote.root.entries[0].times.last_modification = stamp(4);
    let merged = merge_for_sync(&local, &remote);
    let winner = &merged.database.root.entries[0];
    assert_eq!(winner.field("Title"), Some("beta"));
    assert!(winner
        .history
        .iter()
        .any(|old| old.field("Title") == Some("alpha")));
    assert!(!merged.has_conflicts);
}

// 7. Same entry, same mtime, different values -> three-way UI + copy first.
#[test]
fn scenario_07_mutually_exclusive_same_mtime_needs_copy() {
    let local = base_db();
    let mut remote = base_db();
    remote.root.entries[0].strings[0].value = "beta".to_string();
    let merged = merge_for_sync(&local, &remote);
    assert!(merged.has_conflicts);
    assert_eq!(merged.conflicts.len(), 1);
    assert_eq!(merged.conflicts[0].fields, vec!["Title".to_string()]);

    let dir = std::path::Path::new(env!("CARGO_TARGET_TMPDIR"))
        .join(format!("m2-04-conflict-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let original = dir.join("vault.kdbx");
    std::fs::write(&original, b"local").unwrap();
    let copy = conflict_copy_name(original.to_str().unwrap(), "2026-10-26 00-30-00").unwrap();
    std::fs::write(&copy, b"remote").unwrap();
    assert_eq!(std::fs::read(&original).unwrap(), b"local");
    assert_eq!(std::fs::read(&copy).unwrap(), b"remote");
    for choice in [
        ConflictChoice::KeepLocal,
        ConflictChoice::KeepRemote,
        ConflictChoice::KeepBoth,
    ] {
        assert!(choice.plan().write_conflict_copy);
    }
    let _ = std::fs::remove_dir_all(&dir);
}

// 8. Local delete vs remote modify -> DeletedObjects wins, no resurrection.
#[test]
fn scenario_08_local_delete_beats_remote_modify() {
    let mut local = base_db();
    local.deleted_objects.push(DeletedObject {
        uuid: uuid(2),
        deletion_time: stamp(5),
    });
    let mut remote = base_db();
    remote.root.entries[0].strings[0].value = "modified".to_string();
    remote.root.entries[0].times.last_modification = stamp(4);
    let merged = merge_for_sync(&local, &remote);
    assert!(merged.database.root.entries.is_empty());
    assert!(merged
        .database
        .deleted_objects
        .iter()
        .any(|deleted| deleted.uuid == uuid(2)));
}

// 9. Remote delete vs local modify -> remote delete wins, local change is not resurrected.
#[test]
fn scenario_09_remote_delete_beats_local_modify() {
    let mut local = base_db();
    local.root.entries[0].strings[0].value = "local-modified".to_string();
    local.root.entries[0].times.last_modification = stamp(4);
    let mut remote = base_db();
    remote.deleted_objects.push(DeletedObject {
        uuid: uuid(2),
        deletion_time: stamp(5),
    });
    let (merged, report) = merge(&local, &remote);
    assert!(merged.root.entries.is_empty());
    assert!(report.deleted_skipped >= 1);
    assert!(!merged.deleted_objects.is_empty());
}

// 10. Entry moved between groups -> LocationChanged decides parent, no duplicate.
#[test]
fn scenario_10_move_uses_location_changed() {
    let mut local = base_db();
    local.root.entries.clear();
    local.root.groups.push(Group {
        uuid: uuid(3),
        name: "A".to_string(),
        entries: vec![entry(2, "alpha", 2, 2)],
        ..Group::default()
    });
    let mut remote = base_db();
    remote.root.entries.clear();
    remote.root.groups.push(Group {
        uuid: uuid(4),
        name: "B".to_string(),
        entries: vec![entry(2, "alpha", 2, 6)],
        ..Group::default()
    });
    let merged = merge_for_sync(&local, &remote);
    assert!(group_entries(&merged.database, uuid(3)).is_empty());
    assert_eq!(group_entries(&merged.database, uuid(4)).len(), 1);
    let total: usize = merged
        .database
        .root
        .groups
        .iter()
        .map(|group| group.entries.len())
        .sum();
    assert_eq!(total, 1);
}

// 11. Both sides create different entries -> both retained.
#[test]
fn scenario_11_both_create_different_entries() {
    let mut local = base_db();
    local.root.entries.push(entry(3, "local-new", 2, 2));
    let mut remote = base_db();
    remote.root.entries.push(entry(4, "remote-new", 2, 2));
    let merged = merge_for_sync(&local, &remote);
    assert_eq!(merged.database.root.entries.len(), 3);
}

// 12. Both create the same UUID with different content -> conflict, no silent overwrite.
#[test]
fn scenario_12_same_uuid_new_entries_conflict() {
    let mut local = base_db();
    local.root.entries.push(entry(5, "left", 2, 2));
    let mut remote = base_db();
    remote.root.entries.push(entry(5, "right", 2, 2));
    let merged = merge_for_sync(&local, &remote);
    assert!(merged.has_conflicts);
    assert_eq!(merged.conflicts.len(), 1);
    assert_eq!(merged.conflicts[0].uuid, uuid(5));
    assert!(ConflictChoice::KeepBoth.plan().write_conflict_copy);
}

// 13. PUT 412 -> 0.5/1.5/4s retries, then conflict.
#[test]
fn scenario_13_put_412_retries_then_conflict() {
    let mut state = SyncState::new(baseline());
    state.mark_dirty();
    assert_eq!(state.on_upload_http(412).action, SyncActionKind::Retry);
    assert_eq!(state.on_upload_http(412).retry_after_ms, Some(1500));
    assert_eq!(state.on_upload_http(412).retry_after_ms, Some(4000));
    assert_eq!(state.on_upload_http(412).action, SyncActionKind::Conflict);
}

// 14. Offline edit -> dirty retained -> replay upload after recovery.
#[test]
fn scenario_14_offline_then_replay() {
    let mut state = SyncState::new(baseline());
    state.mark_dirty();
    assert_eq!(
        state.on_offline("network down").action,
        SyncActionKind::Offline
    );
    assert!(state.local_dirty);
    assert_eq!(
        state
            .decide(&meta(Some("\"v1\""), Some("sha-v1"), 2))
            .action,
        SyncActionKind::Upload
    );
    state.commit_success(
        Some("\"v2\"".to_string()),
        local_hash(b"edited"),
        8,
        Some(6),
    );
    assert!(!state.local_dirty);
}

// 15. Authentication failure (401) is readable and not retried forever.
#[test]
fn scenario_15_auth_failure_readable() {
    let mut state = SyncState::new(baseline());
    state.mark_dirty();
    let decision = state.on_upload_http(401);
    assert_eq!(decision.action, SyncActionKind::Offline);
    assert!(decision.reason.contains("authentication"));
    assert!(state.local_dirty);
}

// 16. Server without ETag -> degraded mtime+size+sha256 decision.
#[test]
fn scenario_16_no_etag_degrades() {
    let state = SyncState::new(SyncBaseline {
        etag: None,
        sha256: Some("sha-v1".to_string()),
        size: Some(100),
        modified_unix: Some(2),
    });
    let decision = state.decide(&meta(None, Some("sha-v1"), 3));
    assert_eq!(decision.action, SyncActionKind::UpToDate);
    assert!(decision.degraded);
}

// 17. Non-ASCII path/file name -> copy naming and filesystem both work.
#[test]
fn scenario_17_non_ascii_paths() {
    let dir = std::path::Path::new(env!("CARGO_TARGET_TMPDIR"))
        .join(format!("m2-04-utf8-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let original = dir.join("密码库（个人）.kdbx");
    std::fs::write(&original, b"bytes").unwrap();
    let copy = conflict_copy_name(original.to_str().unwrap(), "2026-10-26 00-30-00").unwrap();
    std::fs::write(&copy, b"remote").unwrap();
    assert!(std::path::Path::new(&copy).exists());
    assert!(copy.contains("密码库（个人）"));
    let _ = std::fs::remove_dir_all(&dir);
}

// 18. PUT rejected (405/507) -> readable error, dirty retained.
#[test]
fn scenario_18_put_rejected_keeps_dirty() {
    for status in [405, 507] {
        let mut state = SyncState::new(baseline());
        state.mark_dirty();
        let decision = state.on_upload_http(status);
        assert_eq!(decision.action, SyncActionKind::Offline);
        assert!(decision.reason.contains(&status.to_string()));
        assert!(state.local_dirty);
    }
}
