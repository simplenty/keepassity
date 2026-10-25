// SPDX-License-Identifier: GPL-3.0-or-later
//! Two-database merge (KeePass semantics; no three-way base snapshot).
//!
//! Five elements: UUID, LastModificationTime, History, DeletedObjects and
//! LocationChanged. The newer entry/group wins; the losing snapshot goes to
//! `History`; deleted UUIDs never resurrect; a newer LocationChanged decides
//! the parent group. The output is deterministic and does not mutate inputs.

use crate::model::{Database, DeletedObject, Entry, Group, Times, Uuid};
use std::collections::{BTreeMap, BTreeSet};

/// Merge counters for sync status / logs. No secret values.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct MergeReport {
    /// Entries present only in one side.
    pub entries_added: usize,
    /// Groups present only in one side.
    pub groups_added: usize,
    /// Entries whose content was replaced by the newer side.
    pub entries_updated: usize,
    /// Groups whose content was replaced by the newer side.
    pub groups_updated: usize,
    /// Older snapshots appended to History.
    pub history_added: usize,
    /// Entries/groups skipped because of DeletedObjects.
    pub deleted_skipped: usize,
    /// Same-mtime content conflicts (winner is deterministic; loser kept in History).
    pub conflicts: usize,
    /// Entries whose parent group changed.
    pub moved: usize,
}

/// Merge `remote` into `local` and return the result plus a report.
pub fn merge(local: &Database, remote: &Database) -> (Database, MergeReport) {
    let a = index(local);
    let b = index(remote);
    let mut report = MergeReport::default();

    let deleted = merge_deleted(local, remote);
    let deleted_uuids: BTreeSet<Uuid> = deleted.iter().map(|item| item.uuid).collect();
    let mut deleted_groups = BTreeSet::new();
    // A deleted group deletes its whole subtree (cannot resurrect descendants).
    loop {
        let mut changed = false;
        for uuid in a.groups.keys().chain(b.groups.keys()) {
            if deleted_groups.contains(uuid) {
                continue;
            }
            let parent = parent_of_group(&a, &b, *uuid);
            if parent.is_some_and(|parent| {
                deleted_uuids.contains(&parent) || deleted_groups.contains(&parent)
            }) {
                deleted_groups.insert(*uuid);
                changed = true;
            }
        }
        for uuid in deleted_uuids
            .iter()
            .filter(|uuid| a.groups.contains_key(uuid) || b.groups.contains_key(uuid))
        {
            if deleted_groups.insert(*uuid) {
                changed = true;
            }
        }
        if !changed {
            break;
        }
    }

    let all_group_uuids: BTreeSet<Uuid> = a.groups.keys().chain(b.groups.keys()).copied().collect();
    let all_entry_uuids: BTreeSet<Uuid> =
        a.entries.keys().chain(b.entries.keys()).copied().collect();

    let root = a.root;
    let mut merged_groups: BTreeMap<Uuid, Group> = BTreeMap::new();
    let mut group_parent: BTreeMap<Uuid, Uuid> = BTreeMap::new();
    for uuid in &all_group_uuids {
        if deleted_groups.contains(uuid) {
            report.deleted_skipped += 1;
            continue;
        }
        let (winner, other, both) = match (a.groups.get(uuid), b.groups.get(uuid)) {
            (Some(left), Some(right)) => choose_group(left, right),
            (Some(left), None) => (left, None, false),
            (None, Some(right)) => (right, None, false),
            (None, None) => continue,
        };
        let mut group = winner.clone();
        group.groups.clear();
        group.entries.clear();
        let parent = choose_parent(
            &a,
            &b,
            |side| side.group_parent.get(uuid).copied().flatten(),
            |side| side.groups.get(uuid).map(|group| &group.times),
            root,
        );
        let parent = if deleted_groups.contains(&parent) || !all_group_uuids.contains(&parent) {
            root
        } else {
            parent
        };
        group_parent.insert(*uuid, parent);
        if *uuid == root {
            group_parent.insert(*uuid, root);
        }
        if let Some(other) = other {
            let changed = group.times != other.times || group.name != other.name;
            if both && changed {
                report.groups_updated += 1;
            }
        }
        if !a.groups.contains_key(uuid) || !b.groups.contains_key(uuid) {
            report.groups_added += 1;
        }
        merged_groups.insert(*uuid, group);
    }
    if !merged_groups.contains_key(&root) {
        // Degenerate input without local root: fall back to remote root.
        if let Some(group) = b.groups.get(&b.root) {
            let mut group = group.clone();
            group.groups.clear();
            group.entries.clear();
            merged_groups.insert(b.root, group);
            group_parent.insert(b.root, b.root);
        }
    }

    let mut merged_entries: BTreeMap<Uuid, Entry> = BTreeMap::new();
    let mut entry_parent: BTreeMap<Uuid, Uuid> = BTreeMap::new();
    for uuid in &all_entry_uuids {
        if deleted_uuids.contains(uuid) {
            report.deleted_skipped += 1;
            continue;
        }
        let (winner, loser) = match (a.entries.get(uuid), b.entries.get(uuid)) {
            (Some(left), Some(right)) => {
                let left_time = timestamp(&left.times.last_modification);
                let right_time = timestamp(&right.times.last_modification);
                let left_fp = fingerprint(left);
                let right_fp = fingerprint(right);
                let (winner, loser) = if left_time > right_time {
                    (left, right)
                } else if right_time > left_time {
                    (right, left)
                } else if left_fp == right_fp {
                    (left, right)
                } else {
                    report.conflicts += 1;
                    if left_fp >= right_fp {
                        (left, right)
                    } else {
                        (right, left)
                    }
                };
                if fingerprint(winner) != fingerprint(loser) {
                    report.entries_updated += 1;
                }
                (winner, Some(loser))
            }
            (Some(left), None) => {
                report.entries_added += 1;
                (left, None)
            }
            (None, Some(right)) => {
                report.entries_added += 1;
                (right, None)
            }
            (None, None) => continue,
        };

        let mut entry = winner.clone();
        if let Some(loser) = loser {
            let mut seen: Vec<Entry> = Vec::new();
            for candidate in winner.history.iter().chain(loser.history.iter()) {
                let mut candidate = candidate.clone();
                candidate.history.clear();
                candidate.previous_parent_group = None;
                if seen.iter().any(|entry| snapshot_eq(entry, &candidate)) {
                    continue;
                }
                seen.push(candidate);
            }
            if !snapshot_eq(winner, loser) {
                let mut candidate = loser.clone();
                candidate.history.clear();
                candidate.previous_parent_group = None;
                if !seen.iter().any(|entry| snapshot_eq(entry, &candidate)) {
                    seen.push(candidate);
                }
            }
            let added = seen.len().saturating_sub(entry.history.len());
            if !seen.is_empty() {
                entry.history = seen;
            }
            report.history_added += added;
        }

        let local_parent = a.entry_parent.get(uuid).copied();
        let remote_parent = b.entry_parent.get(uuid).copied();
        let layout_side = if local_parent.is_some() && remote_parent.is_some() {
            let left = a.entries.get(uuid).map(|entry| &entry.times);
            let right = b.entries.get(uuid).map(|entry| &entry.times);
            match (left, right) {
                (Some(left), Some(right))
                    if timestamp(&right.location_changed) > timestamp(&left.location_changed) =>
                {
                    remote_parent
                }
                _ => local_parent,
            }
        } else {
            local_parent.or(remote_parent)
        };
        let parent = layout_side.unwrap_or(root);
        if deleted_groups.contains(&parent) {
            report.deleted_skipped += 1;
            continue;
        }
        let parent = if merged_groups.contains_key(&parent) {
            parent
        } else {
            root
        };
        if let (Some(local_parent), Some(parent)) = (local_parent, Some(parent)) {
            if local_parent != parent {
                report.moved += 1;
                if entry.previous_parent_group.is_none() {
                    entry.previous_parent_group = Some(local_parent);
                }
            }
        }
        entry_parent.insert(*uuid, parent);
        merged_entries.insert(*uuid, entry);
    }

    // Group order: local order first, then remote-only; same for entries.
    let mut group_order: BTreeMap<Uuid, Vec<Uuid>> = BTreeMap::new();
    let mut entry_order: BTreeMap<Uuid, Vec<Uuid>> = BTreeMap::new();
    for side in [&a, &b] {
        for (parent, children) in &side.group_order {
            let order = group_order.entry(*parent).or_default();
            for child in children {
                if merged_groups.contains_key(child)
                    && group_parent.get(child) == Some(parent)
                    && !order.contains(child)
                {
                    order.push(*child);
                }
            }
        }
        for (parent, children) in &side.entry_order {
            let order = entry_order.entry(*parent).or_default();
            for child in children {
                if merged_entries.contains_key(child)
                    && entry_parent.get(child) == Some(parent)
                    && !order.contains(child)
                {
                    order.push(*child);
                }
            }
        }
    }
    // Any merged object not present in either side's order list goes last.
    for (uuid, parent) in &group_parent {
        if *uuid == *parent {
            continue;
        }
        let order = group_order.entry(*parent).or_default();
        if !order.contains(uuid) {
            order.push(*uuid);
        }
    }
    for (uuid, parent) in &entry_parent {
        let order = entry_order.entry(*parent).or_default();
        if !order.contains(uuid) {
            order.push(*uuid);
        }
    }

    let mut result = local.clone();
    result.raw_xml = None;
    result.raw_xml_sha256 = None;
    result.root = build_group(
        root,
        &merged_groups,
        &group_order,
        &entry_order,
        &merged_entries,
        &mut BTreeSet::new(),
    );
    result.deleted_objects = deleted;
    (result, report)
}

struct Side {
    root: Uuid,
    groups: BTreeMap<Uuid, Group>,
    group_parent: BTreeMap<Uuid, Option<Uuid>>,
    entries: BTreeMap<Uuid, Entry>,
    entry_parent: BTreeMap<Uuid, Uuid>,
    group_order: BTreeMap<Uuid, Vec<Uuid>>,
    entry_order: BTreeMap<Uuid, Vec<Uuid>>,
}

fn index(db: &Database) -> Side {
    let mut side = Side {
        root: db.root.uuid,
        groups: BTreeMap::new(),
        group_parent: BTreeMap::new(),
        entries: BTreeMap::new(),
        entry_parent: BTreeMap::new(),
        group_order: BTreeMap::new(),
        entry_order: BTreeMap::new(),
    };
    walk(&db.root, None, &mut side);
    side
}

fn walk(group: &Group, parent: Option<Uuid>, side: &mut Side) {
    let uuid = group.uuid;
    let mut clean = group.clone();
    clean.groups.clear();
    clean.entries.clear();
    side.groups.insert(uuid, clean);
    side.group_parent.insert(uuid, parent);
    if let Some(parent) = parent {
        side.group_order.entry(parent).or_default().push(uuid);
    }
    for entry in &group.entries {
        side.entries.insert(entry.uuid, entry.clone());
        side.entry_parent.insert(entry.uuid, uuid);
        side.entry_order.entry(uuid).or_default().push(entry.uuid);
    }
    for child in &group.groups {
        walk(child, Some(uuid), side);
    }
}

fn build_group(
    uuid: Uuid,
    groups: &BTreeMap<Uuid, Group>,
    group_order: &BTreeMap<Uuid, Vec<Uuid>>,
    entry_order: &BTreeMap<Uuid, Vec<Uuid>>,
    entries: &BTreeMap<Uuid, Entry>,
    visited: &mut BTreeSet<Uuid>,
) -> Group {
    if !visited.insert(uuid) {
        let mut group = groups.get(&uuid).cloned().unwrap_or_default();
        group.groups.clear();
        group.entries.clear();
        return group;
    }
    let mut group = groups.get(&uuid).cloned().unwrap_or_default();
    group.uuid = uuid;
    group.groups = group_order
        .get(&uuid)
        .into_iter()
        .flatten()
        .map(|child| build_group(*child, groups, group_order, entry_order, entries, visited))
        .collect();
    group.entries = entry_order
        .get(&uuid)
        .into_iter()
        .flatten()
        .filter_map(|entry| entries.get(entry).cloned())
        .collect();
    group
}

fn merge_deleted(local: &Database, remote: &Database) -> Vec<DeletedObject> {
    let mut deleted = local.deleted_objects.clone();
    for item in &remote.deleted_objects {
        if !deleted.iter().any(|existing| existing.uuid == item.uuid) {
            deleted.push(item.clone());
        }
    }
    deleted
}

fn parent_of_group(a: &Side, b: &Side, uuid: Uuid) -> Option<Uuid> {
    a.group_parent
        .get(&uuid)
        .copied()
        .flatten()
        .or_else(|| b.group_parent.get(&uuid).copied().flatten())
}

fn choose_group<'a>(left: &'a Group, right: &'a Group) -> (&'a Group, Option<&'a Group>, bool) {
    let left_time = timestamp(&left.times.last_modification);
    let right_time = timestamp(&right.times.last_modification);
    if left_time >= right_time {
        (left, Some(right), true)
    } else {
        (right, Some(left), true)
    }
}

fn choose_parent(
    a: &Side,
    b: &Side,
    parent_from: impl Fn(&Side) -> Option<Uuid>,
    times_from: impl Fn(&Side) -> Option<&Times>,
    root: Uuid,
) -> Uuid {
    let left = parent_from(a);
    let right = parent_from(b);
    match (left, right) {
        (Some(left), Some(right)) => {
            let left_time = times_from(a)
                .map(|times| timestamp(&times.location_changed))
                .unwrap_or(0);
            let right_time = times_from(b)
                .map(|times| timestamp(&times.location_changed))
                .unwrap_or(0);
            if right_time > left_time {
                right
            } else {
                left
            }
        }
        (Some(left), None) => left,
        (None, Some(right)) => right,
        (None, None) => root,
    }
}

fn timestamp(value: &str) -> i64 {
    crate::model::xml_datetime_to_unix(value).unwrap_or(i64::MIN)
}

fn fingerprint(entry: &Entry) -> String {
    let mut clean = entry.clone();
    clean.history.clear();
    clean.times = Times::default();
    clean.previous_parent_group = None;
    serde_json::to_string(&clean).unwrap_or_default()
}

fn snapshot_eq(left: &Entry, right: &Entry) -> bool {
    fingerprint(left) == fingerprint(right)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{DeletedObject, Entry, StringField, Times};

    fn uuid(byte: u8) -> Uuid {
        Uuid::from_bytes([byte; 16])
    }

    fn time(second: u8) -> String {
        format!("2026-10-25T10:00:{second:02}Z")
    }

    fn entry(byte: u8, title: &str, modified: u8, location: u8) -> Entry {
        let mut entry = Entry {
            uuid: uuid(byte),
            icon_id: 1,
            tags: vec!["tag".to_string()],
            times: Times {
                creation: time(1),
                last_modification: time(modified),
                last_access: time(modified),
                location_changed: time(location),
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

    fn database(entries: Vec<Entry>) -> Database {
        Database {
            format_version: "4.0".to_string(),
            root: Group {
                uuid: uuid(1),
                name: "Root".to_string(),
                entries,
                ..Group::default()
            },
            ..Database::default()
        }
    }

    #[test]
    fn same_database_is_idempotent() {
        let a = database(vec![entry(2, "one", 2, 2)]);
        let (result, report) = merge(&a, &a);
        assert_eq!(result, a);
        assert_eq!(report.conflicts, 0);
        assert_eq!(report.entries_added, 0);
    }

    #[test]
    fn newer_entry_wins_and_loser_goes_to_history() {
        let local = database(vec![entry(2, "old", 2, 2)]);
        let remote = database(vec![entry(2, "new", 3, 2)]);
        let (result, report) = merge(&local, &remote);
        let merged = &result.root.entries[0];
        assert_eq!(merged.field("Title"), Some("new"));
        assert_eq!(report.entries_updated, 1);
        assert_eq!(merged.history.len(), 1);
        assert_eq!(merged.history[0].field("Title"), Some("old"));
    }

    #[test]
    fn deleted_objects_prevent_resurrection() {
        let mut remote = database(vec![entry(2, "deleted", 3, 3)]);
        remote.deleted_objects = vec![DeletedObject {
            uuid: uuid(2),
            deletion_time: time(3),
        }];
        let local = database(vec![entry(2, "old", 1, 1)]);
        let (result, report) = merge(&local, &remote);
        assert!(result.root.entries.is_empty());
        assert_eq!(result.deleted_objects.len(), 1);
        assert!(report.deleted_skipped >= 1);
    }

    #[test]
    fn newer_location_changed_decides_parent() {
        let entry_uuid = uuid(2);
        let mut local = database(vec![]);
        local.root.groups.push(Group {
            uuid: uuid(3),
            name: "A".to_string(),
            entries: vec![entry(2, "x", 2, 2)],
            ..Group::default()
        });
        let mut remote = database(vec![]);
        remote.root.groups.push(Group {
            uuid: uuid(4),
            name: "B".to_string(),
            entries: vec![entry(2, "x", 2, 5)],
            ..Group::default()
        });
        assert_eq!(entry_uuid, local.root.groups[0].entries[0].uuid);
        let (result, report) = merge(&local, &remote);
        let group_b = result
            .root
            .groups
            .iter()
            .find(|group| group.uuid == uuid(4))
            .expect("B");
        assert!(group_b.entries.iter().any(|entry| entry.uuid == entry_uuid));
        let group_a = result
            .root
            .groups
            .iter()
            .find(|group| group.uuid == uuid(3))
            .expect("A");
        assert!(group_a.entries.is_empty());
        assert_eq!(report.moved, 1);
    }

    #[test]
    fn conflict_same_mtime_keeps_both_in_history() {
        let local = database(vec![entry(2, "left", 3, 2)]);
        let remote = database(vec![entry(2, "right", 3, 2)]);
        let (result, report) = merge(&local, &remote);
        assert_eq!(report.conflicts, 1);
        let merged = &result.root.entries[0];
        assert_eq!(merged.history.len(), 1);
    }

    #[test]
    fn union_of_different_entries() {
        let local = database(vec![entry(2, "two", 2, 2)]);
        let remote = database(vec![entry(3, "three", 2, 2)]);
        let (result, report) = merge(&local, &remote);
        assert_eq!(result.root.entries.len(), 2);
        assert_eq!(report.entries_added, 2);
    }

    #[test]
    fn merge_converges_without_duplicate_history() {
        let local = database(vec![entry(2, "old", 2, 2)]);
        let remote = database(vec![entry(2, "new", 3, 2)]);
        let (first, _) = merge(&local, &remote);
        let (second, report) = merge(&first, &remote);
        assert_eq!(first, second);
        assert_eq!(report.history_added, 0);
        assert_eq!(second.root.entries[0].history.len(), 1);
    }

    #[test]
    fn commutativity_preserves_entries_and_parents() {
        let mut local = database(vec![entry(2, "shared", 2, 2)]);
        local.root.groups.push(Group {
            uuid: uuid(3),
            name: "A".to_string(),
            ..Group::default()
        });
        let mut remote = database(vec![entry(4, "remote-only", 2, 2)]);
        remote.root.groups.push(Group {
            uuid: uuid(5),
            name: "B".to_string(),
            ..Group::default()
        });
        let layout = |db: &Database| {
            let mut out = BTreeMap::new();
            fn walk(group: &Group, parent: Option<Uuid>, out: &mut BTreeMap<Uuid, Option<Uuid>>) {
                out.insert(group.uuid, parent);
                for entry in &group.entries {
                    out.insert(entry.uuid, Some(group.uuid));
                }
                for child in &group.groups {
                    walk(child, Some(group.uuid), out);
                }
            }
            walk(&db.root, None, &mut out);
            out
        };
        let (left, _) = merge(&local, &remote);
        let (right, _) = merge(&remote, &local);
        assert_eq!(layout(&left), layout(&right));
    }

    #[test]
    fn deleted_group_subtree_is_removed() {
        let mut local = database(vec![]);
        local.root.groups.push(Group {
            uuid: uuid(3),
            name: "Doomed".to_string(),
            entries: vec![entry(2, "child", 2, 2)],
            ..Group::default()
        });
        let mut remote = database(vec![entry(9, "live", 2, 2)]);
        remote.deleted_objects.push(DeletedObject {
            uuid: uuid(3),
            deletion_time: time(3),
        });
        let (result, report) = merge(&local, &remote);
        assert!(!result.root.groups.iter().any(|group| group.uuid == uuid(3)));
        assert!(!result
            .root
            .entries
            .iter()
            .any(|entry| entry.uuid == uuid(2)));
        assert!(result
            .root
            .entries
            .iter()
            .any(|entry| entry.uuid == uuid(9)));
        assert!(report.deleted_skipped >= 2);
    }
}
