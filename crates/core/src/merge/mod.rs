// SPDX-License-Identifier: GPL-3.0-or-later
//! Two-database merge (KeePass semantics; no three-way base snapshot).
//!
//! Five elements: UUID, LastModificationTime, History, DeletedObjects and
//! LocationChanged. The newer entry/group wins; the losing snapshot goes to
//! `History`; deleted UUIDs never resurrect; a newer LocationChanged decides
//! the parent group. The output is deterministic and does not mutate inputs.

use crate::model::{AutoType, Database, DeletedObject, Entry, Group, HistoryLimits, Times, Uuid};
use std::collections::{BTreeMap, BTreeSet};

/// What kind of object conflicts.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ConflictKind {
    /// Entry conflict.
    Entry,
    /// Group conflict.
    Group,
}

/// One conflict item for the three-way UI and sync status.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MergeConflict {
    /// Conflicting object UUID.
    pub uuid: Uuid,
    /// Entry or group.
    pub kind: ConflictKind,
    /// Local LastModificationTime text.
    pub local_modified: String,
    /// Remote LastModificationTime text.
    pub remote_modified: String,
    /// Differing field names (`Title`, `Tags`, ...).
    pub fields: Vec<String>,
}

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
    /// Conflict details for the three-way UI.
    pub conflict_items: Vec<MergeConflict>,
    /// Entries whose parent group changed.
    pub moved: usize,
}

/// Merge `remote` into `local` and return the result plus a report.
pub fn merge(local: &Database, remote: &Database) -> (Database, MergeReport) {
    let a = index(local);
    let b = index(remote);
    let mut report = MergeReport::default();
    // The database that receives the merge owns the history retention policy.
    let limits = HistoryLimits::from_meta(&local.meta);

    // Deletion records are a union by UUID; when either side has several
    // records for the same object, the latest deletion wins. The root group is
    // never deletable, even if a corrupt remote says so.
    let mut deleted = merge_deleted(local, remote);
    deleted.retain(|item| item.uuid != a.root && item.uuid != b.root);
    let mut deletion_times: BTreeMap<Uuid, i64> = BTreeMap::new();
    for item in &deleted {
        let time = timestamp(&item.deletion_time);
        deletion_times
            .entry(item.uuid)
            .and_modify(|known| *known = (*known).max(time))
            .or_insert(time);
    }

    let all_group_uuids: BTreeSet<Uuid> = a.groups.keys().chain(b.groups.keys()).copied().collect();
    let all_entry_uuids: BTreeSet<Uuid> =
        a.entries.keys().chain(b.entries.keys()).copied().collect();

    // A deleted group also covers its subtree. Carry the latest deletion time
    // down each side's tree so an unchanged child is removed with its parent,
    // while a child changed after the deletion can still survive.
    let mut group_deletion_times: BTreeMap<Uuid, i64> = BTreeMap::new();
    inherit_group_deletions(&a, &deletion_times, &mut group_deletion_times);
    inherit_group_deletions(&b, &deletion_times, &mut group_deletion_times);

    // Only an object that was not modified after the deletion may be removed.
    // A newer local/remote edit wins and is surfaced as a conflict, so the user
    // can see that one side deleted data the other side had modified.
    let mut entry_deleted: BTreeSet<Uuid> = BTreeSet::new();
    for uuid in &all_entry_uuids {
        let modified = latest_entry_time(&a, &b, *uuid);
        let parent = choose_entry_parent(&a, &b, *uuid);
        let own = deletion_times.get(uuid).copied();
        let inherited = parent
            .and_then(|parent| group_deletion_times.get(&parent))
            .copied();
        let effective = match (own, inherited) {
            (Some(own), Some(inherited)) => Some(own.max(inherited)),
            (Some(time), None) | (None, Some(time)) => Some(time),
            (None, None) => None,
        };
        let Some(effective) = effective else {
            continue;
        };
        if modified <= effective {
            entry_deleted.insert(*uuid);
            report.deleted_skipped += 1;
            continue;
        }
        if own.is_some() {
            report.conflicts += 1;
            report.conflict_items.push(entry_deletion_conflict(
                *uuid,
                a.entries.get(uuid),
                b.entries.get(uuid),
                latest_deleted(local, *uuid),
                latest_deleted(remote, *uuid),
            ));
        }
    }

    // Groups are decided to a fixed point, deepest last: a group can only be
    // deleted when nothing inside it survives (a newer entry, a newer subgroup,
    // or a subgroup that itself still has content).
    let mut group_deleted: BTreeSet<Uuid> = BTreeSet::new();
    let mut group_deletable: BTreeSet<Uuid> = BTreeSet::new();
    for uuid in &all_group_uuids {
        if *uuid == a.root || *uuid == b.root {
            continue;
        }
        let Some(effective) = group_deletion_times.get(uuid).copied() else {
            continue;
        };
        let modified = latest_group_time(&a, &b, *uuid);
        if modified <= effective {
            group_deletable.insert(*uuid);
        } else if deletion_times.contains_key(uuid) {
            report.conflicts += 1;
            report.conflict_items.push(group_deletion_conflict(
                *uuid,
                a.groups.get(uuid),
                b.groups.get(uuid),
                latest_deleted(local, *uuid),
                latest_deleted(remote, *uuid),
            ));
        }
    }
    loop {
        let mut changed = false;
        for uuid in &all_group_uuids {
            if group_deleted.contains(uuid) || !group_deletable.contains(uuid) {
                continue;
            }
            if has_surviving_content(&a, &b, *uuid, &entry_deleted, &group_deleted) {
                continue;
            }
            group_deleted.insert(*uuid);
            report.deleted_skipped += 1;
            changed = true;
        }
        if !changed {
            break;
        }
    }

    // A deletion record for a live object was not applied (newer edit or
    // surviving content); drop it. Records for objects that are gone stay, so a
    // stale copy cannot resurrect them.
    deleted.retain(|item| {
        if entry_deleted.contains(&item.uuid) || group_deleted.contains(&item.uuid) {
            return true;
        }
        !(a.entries.contains_key(&item.uuid)
            || b.entries.contains_key(&item.uuid)
            || a.groups.contains_key(&item.uuid)
            || b.groups.contains_key(&item.uuid))
    });

    let root = a.root;
    let mut merged_groups: BTreeMap<Uuid, Group> = BTreeMap::new();
    let mut group_parent: BTreeMap<Uuid, Uuid> = BTreeMap::new();
    for uuid in &all_group_uuids {
        if group_deleted.contains(uuid) {
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
        let parent = if group_deleted.contains(&parent) || !all_group_uuids.contains(&parent) {
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
        if both {
            if let (Some(left), Some(right)) = (a.groups.get(uuid), b.groups.get(uuid)) {
                if timestamp(&left.times.last_modification)
                    == timestamp(&right.times.last_modification)
                    && group_fingerprint(left) != group_fingerprint(right)
                {
                    if group_content_key(left) == group_content_key(right)
                        && group_metadata_compatible(left, right)
                    {
                        // Same content; only optional metadata (notes, icon,
                        // auto-type flags) exists on one side, e.g. after a
                        // save by a build that dropped it. Keep the richer copy
                        // silently instead of asking about a non-edit.
                        let richer = if group_metadata_score(right) > group_metadata_score(left) {
                            right
                        } else {
                            left
                        };
                        group = richer.clone();
                        group.groups.clear();
                        group.entries.clear();
                    } else {
                        report.conflicts += 1;
                        report.conflict_items.push(MergeConflict {
                            uuid: *uuid,
                            kind: ConflictKind::Group,
                            local_modified: left.times.last_modification.clone(),
                            remote_modified: right.times.last_modification.clone(),
                            fields: group_diff_fields(left, right),
                        });
                    }
                }
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
        if entry_deleted.contains(uuid) {
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
                } else if entry_content_fingerprint(left) == entry_content_fingerprint(right)
                    && entry_metadata_compatible(left, right)
                {
                    // Same content; only optional metadata (icon, colors,
                    // auto-type, custom data) exists on one side. This is what a
                    // save by an older build, or a client that omits those
                    // fields, looks like: keep the richer copy instead of asking
                    // about a change the user never made.
                    if entry_metadata_score(right) > entry_metadata_score(left) {
                        (right, left)
                    } else {
                        (left, right)
                    }
                } else {
                    report.conflicts += 1;
                    report.conflict_items.push(MergeConflict {
                        uuid: *uuid,
                        kind: ConflictKind::Entry,
                        local_modified: left.times.last_modification.clone(),
                        remote_modified: right.times.last_modification.clone(),
                        fields: entry_diff_fields(left, right),
                    });
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
            // KeePass keeps history oldest first; sorting before truncation
            // makes the newest items the ones that survive the limits.
            seen.sort_by_key(|snapshot| timestamp(&snapshot.times.last_modification));
            let added = seen.len().saturating_sub(entry.history.len());
            if !seen.is_empty() {
                entry.history = seen;
            }
            report.history_added += added;
        }
        // The database that receives the merge owns the retention policy.
        entry.truncate_history(limits);

        let local_parent = a.entry_parent.get(uuid).copied();
        // A surviving entry must not disappear just because its chosen parent
        // group was deleted; move it to the root instead of dropping data.
        let parent = choose_entry_parent(&a, &b, *uuid).unwrap_or(root);
        let parent = if group_deleted.contains(&parent) {
            root
        } else if merged_groups.contains_key(&parent) {
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
    let mut deleted: Vec<DeletedObject> = Vec::new();
    for item in local
        .deleted_objects
        .iter()
        .chain(remote.deleted_objects.iter())
    {
        match deleted
            .iter()
            .position(|existing| existing.uuid == item.uuid)
        {
            Some(index) => {
                // A later deletion of the same object supersedes an earlier
                // record; the first record keeps its deterministic position.
                if timestamp(&item.deletion_time) > timestamp(&deleted[index].deletion_time) {
                    deleted[index] = item.clone();
                }
            }
            None => deleted.push(item.clone()),
        }
    }
    deleted
}

fn latest_deleted(database: &Database, uuid: Uuid) -> Option<&DeletedObject> {
    database
        .deleted_objects
        .iter()
        .filter(|item| item.uuid == uuid)
        .max_by_key(|item| timestamp(&item.deletion_time))
}

/// Carry group deletion times down both trees: an unchanged child of a deleted
/// group is covered by the parent's deletion time, but a child changed later
/// keeps its own (later) modification time and can survive.
fn inherit_group_deletions(
    side: &Side,
    deletion_times: &BTreeMap<Uuid, i64>,
    out: &mut BTreeMap<Uuid, i64>,
) {
    fn walk(
        side: &Side,
        uuid: Uuid,
        inherited: Option<i64>,
        deletion_times: &BTreeMap<Uuid, i64>,
        out: &mut BTreeMap<Uuid, i64>,
    ) {
        let own = deletion_times.get(&uuid).copied();
        let effective = match (inherited, own) {
            (Some(inherited), Some(own)) => Some(inherited.max(own)),
            (Some(time), None) | (None, Some(time)) => Some(time),
            (None, None) => None,
        };
        if let Some(time) = effective {
            out.entry(uuid)
                .and_modify(|known| *known = (*known).max(time))
                .or_insert(time);
        }
        for child in side.group_order.get(&uuid).into_iter().flatten() {
            walk(side, *child, effective, deletion_times, out);
        }
    }
    walk(side, side.root, None, deletion_times, out);
}

fn latest_entry_time(a: &Side, b: &Side, uuid: Uuid) -> i64 {
    let mut latest = i64::MIN;
    if let Some(entry) = a.entries.get(&uuid) {
        latest = latest.max(timestamp(&entry.times.last_modification));
    }
    if let Some(entry) = b.entries.get(&uuid) {
        latest = latest.max(timestamp(&entry.times.last_modification));
    }
    latest
}

fn latest_group_time(a: &Side, b: &Side, uuid: Uuid) -> i64 {
    let mut latest = i64::MIN;
    if let Some(group) = a.groups.get(&uuid) {
        latest = latest.max(timestamp(&group.times.last_modification));
    }
    if let Some(group) = b.groups.get(&uuid) {
        latest = latest.max(timestamp(&group.times.last_modification));
    }
    latest
}

/// The parent group after merging, decided by the latest `LocationChanged`.
/// This mirrors the layout rule used when the entry is attached.
fn choose_entry_parent(a: &Side, b: &Side, uuid: Uuid) -> Option<Uuid> {
    let local = a.entry_parent.get(&uuid).copied();
    let remote = b.entry_parent.get(&uuid).copied();
    match (local, remote) {
        (Some(local), Some(remote)) => {
            let local_time = a
                .entries
                .get(&uuid)
                .map(|entry| timestamp(&entry.times.location_changed))
                .unwrap_or(i64::MIN);
            let remote_time = b
                .entries
                .get(&uuid)
                .map(|entry| timestamp(&entry.times.location_changed))
                .unwrap_or(i64::MIN);
            if remote_time > local_time {
                Some(remote)
            } else {
                Some(local)
            }
        }
        (Some(local), None) => Some(local),
        (None, Some(remote)) => Some(remote),
        (None, None) => None,
    }
}

fn has_surviving_content(
    a: &Side,
    b: &Side,
    group: Uuid,
    entry_deleted: &BTreeSet<Uuid>,
    group_deleted: &BTreeSet<Uuid>,
) -> bool {
    [a, b].into_iter().any(|side| {
        side.entry_order
            .get(&group)
            .into_iter()
            .flatten()
            .any(|uuid| !entry_deleted.contains(uuid))
            || side
                .group_order
                .get(&group)
                .into_iter()
                .flatten()
                .any(|uuid| !group_deleted.contains(uuid))
    })
}

fn entry_deletion_conflict(
    uuid: Uuid,
    local: Option<&Entry>,
    remote: Option<&Entry>,
    local_deletion: Option<&DeletedObject>,
    remote_deletion: Option<&DeletedObject>,
) -> MergeConflict {
    let deleted_remotely = remote_deletion.is_some() && local.is_some() && local_deletion.is_none();
    let deleted_locally = local_deletion.is_some() && remote.is_some() && remote_deletion.is_none();
    let fields = if deleted_remotely {
        // A readable trace for the UI/log; never a field value.
        vec!["deleted remotely but modified locally".to_string()]
    } else if deleted_locally {
        vec!["deleted locally but modified remotely".to_string()]
    } else {
        vec!["modified after deletion".to_string()]
    };
    // Show the deletion event on the side that deleted the object and the edit
    // time on the side that kept it; a stale copy's timestamp would hide when
    // the deletion actually happened.
    let local_modified = if deleted_remotely {
        local
            .map(|entry| entry.times.last_modification.clone())
            .unwrap_or_default()
    } else if deleted_locally {
        local_deletion
            .map(|item| item.deletion_time.clone())
            .unwrap_or_default()
    } else {
        local
            .map(|entry| entry.times.last_modification.clone())
            .or_else(|| local_deletion.map(|item| item.deletion_time.clone()))
            .unwrap_or_default()
    };
    let remote_modified = if deleted_locally {
        remote
            .map(|entry| entry.times.last_modification.clone())
            .unwrap_or_default()
    } else if deleted_remotely {
        remote_deletion
            .map(|item| item.deletion_time.clone())
            .unwrap_or_default()
    } else {
        remote
            .map(|entry| entry.times.last_modification.clone())
            .or_else(|| remote_deletion.map(|item| item.deletion_time.clone()))
            .unwrap_or_default()
    };
    MergeConflict {
        uuid,
        kind: ConflictKind::Entry,
        local_modified,
        remote_modified,
        fields,
    }
}

fn group_deletion_conflict(
    uuid: Uuid,
    local: Option<&Group>,
    remote: Option<&Group>,
    local_deletion: Option<&DeletedObject>,
    remote_deletion: Option<&DeletedObject>,
) -> MergeConflict {
    let deleted_remotely = remote_deletion.is_some() && local.is_some() && local_deletion.is_none();
    let deleted_locally = local_deletion.is_some() && remote.is_some() && remote_deletion.is_none();
    let fields = if deleted_remotely {
        vec!["deleted remotely but modified locally".to_string()]
    } else if deleted_locally {
        vec!["deleted locally but modified remotely".to_string()]
    } else {
        vec!["modified after deletion".to_string()]
    };
    let local_modified = if deleted_remotely {
        local
            .map(|group| group.times.last_modification.clone())
            .unwrap_or_default()
    } else if deleted_locally {
        local_deletion
            .map(|item| item.deletion_time.clone())
            .unwrap_or_default()
    } else {
        local
            .map(|group| group.times.last_modification.clone())
            .or_else(|| local_deletion.map(|item| item.deletion_time.clone()))
            .unwrap_or_default()
    };
    let remote_modified = if deleted_locally {
        remote
            .map(|group| group.times.last_modification.clone())
            .unwrap_or_default()
    } else if deleted_remotely {
        remote_deletion
            .map(|item| item.deletion_time.clone())
            .unwrap_or_default()
    } else {
        remote
            .map(|group| group.times.last_modification.clone())
            .or_else(|| remote_deletion.map(|item| item.deletion_time.clone()))
            .unwrap_or_default()
    };
    MergeConflict {
        uuid,
        kind: ConflictKind::Group,
        local_modified,
        remote_modified,
        fields,
    }
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

/// Semantic content of an entry, used for conflict detection.
///
/// Serialization details that different KeePass clients and our own writer
/// normalise differently must not turn into conflicts: field order, tags order,
/// empty placeholder fields, the `Protected` attribute, and unknown XML. What
/// stays in the fingerprint is the actual content the user can change.
fn fingerprint(entry: &Entry) -> String {
    let mut clean = entry.clone();
    clean.history.clear();
    clean.times = Times::default();
    clean.previous_parent_group = None;
    clean
        .strings
        .sort_by(|left, right| left.key.cmp(&right.key));
    clean.strings.retain(|field| !field.value.is_empty());
    for field in &mut clean.strings {
        field.protected = false;
    }
    clean
        .binaries
        .sort_by(|left, right| left.key.cmp(&right.key));
    clean.tags.sort();
    clean.tags.dedup();
    clean.unknown.clear();
    serde_json::to_string(&clean).unwrap_or_default()
}

fn group_fingerprint(group: &Group) -> String {
    let mut clean = group.clone();
    clean.groups.clear();
    clean.entries.clear();
    clean.times = Times::default();
    clean.previous_parent_group = None;
    clean.tags.sort();
    clean.tags.dedup();
    clean.unknown.clear();
    // Expanding a group is a view setting, not content.
    clean.is_expanded = false;
    serde_json::to_string(&clean).unwrap_or_default()
}

/// Fields that describe the entry content, ignoring optional metadata that a
/// writer may drop (icons, colors, auto-type, custom data).
fn entry_content_fingerprint(entry: &Entry) -> String {
    let mut clean = entry.clone();
    clean.icon_id = 0;
    clean.custom_icon_uuid = None;
    clean.foreground_color.clear();
    clean.background_color.clear();
    clean.override_url.clear();
    clean.auto_type = None;
    clean.custom_data = crate::model::CustomData::default();
    fingerprint(&clean)
}

fn entry_metadata_score(entry: &Entry) -> usize {
    usize::from(entry.icon_id != 0)
        + usize::from(entry.custom_icon_uuid.is_some())
        + usize::from(!entry.foreground_color.is_empty())
        + usize::from(!entry.background_color.is_empty())
        + usize::from(!entry.override_url.is_empty())
        + usize::from(entry.auto_type.is_some())
        + entry.custom_data.items.len()
}

/// True when the metadata present on both sides agrees, so a difference can
/// only mean one side lost optional data. A real metadata edit (both sides have
/// different icons or different auto-type settings) is not compatible.
fn entry_metadata_compatible(left: &Entry, right: &Entry) -> bool {
    let optional_equal = |a: &str, b: &str| a.is_empty() || b.is_empty() || a == b;
    if !optional_equal(&left.foreground_color, &right.foreground_color)
        || !optional_equal(&left.background_color, &right.background_color)
        || !optional_equal(&left.override_url, &right.override_url)
    {
        return false;
    }
    if left.icon_id != 0 && right.icon_id != 0 && left.icon_id != right.icon_id {
        return false;
    }
    if let (Some(left_icon), Some(right_icon)) = (left.custom_icon_uuid, right.custom_icon_uuid) {
        if left_icon != right_icon {
            return false;
        }
    }
    if let (Some(left_auto), Some(right_auto)) = (&left.auto_type, &right.auto_type) {
        if auto_type_key(left_auto) != auto_type_key(right_auto) {
            return false;
        }
    }
    for (key, item) in &left.custom_data.items {
        if let Some(other) = right.custom_data.items.get(key) {
            if item != other {
                return false;
            }
        }
    }
    true
}

fn auto_type_key(auto_type: &AutoType) -> String {
    let mut clean = auto_type.clone();
    clean.unknown.clear();
    serde_json::to_string(&clean).unwrap_or_default()
}

/// Group fields that a writer may drop without touching the content.
fn group_content_key(group: &Group) -> String {
    let mut clean = group.clone();
    clean.groups.clear();
    clean.entries.clear();
    clean.notes.clear();
    clean.icon_id = 0;
    clean.custom_icon_uuid = None;
    clean.is_expanded = false;
    clean.default_autotype_sequence.clear();
    clean.enable_auto_type = false;
    clean.enable_searching = false;
    clean.last_top_visible_entry = None;
    clean.custom_data = crate::model::CustomData::default();
    clean.times = Times::default();
    clean.previous_parent_group = None;
    clean.unknown.clear();
    clean.tags.sort();
    clean.tags.dedup();
    serde_json::to_string(&clean).unwrap_or_default()
}

fn group_metadata_score(group: &Group) -> usize {
    usize::from(!group.notes.is_empty())
        + usize::from(group.icon_id != 0)
        + usize::from(group.custom_icon_uuid.is_some())
        + usize::from(group.is_expanded)
        + usize::from(!group.default_autotype_sequence.is_empty())
        + usize::from(group.enable_auto_type)
        + usize::from(group.enable_searching)
        + group.custom_data.items.len()
}

fn group_metadata_compatible(left: &Group, right: &Group) -> bool {
    if !left.notes.is_empty() && !right.notes.is_empty() && left.notes != right.notes {
        return false;
    }
    if left.icon_id != 0 && right.icon_id != 0 && left.icon_id != right.icon_id {
        return false;
    }
    if let (Some(left_icon), Some(right_icon)) = (left.custom_icon_uuid, right.custom_icon_uuid) {
        if left_icon != right_icon {
            return false;
        }
    }
    if !left.default_autotype_sequence.is_empty()
        && !right.default_autotype_sequence.is_empty()
        && left.default_autotype_sequence != right.default_autotype_sequence
    {
        return false;
    }
    for (key, item) in &left.custom_data.items {
        if let Some(other) = right.custom_data.items.get(key) {
            if item != other {
                return false;
            }
        }
    }
    true
}

fn entry_diff_fields(left: &Entry, right: &Entry) -> Vec<String> {
    let mut fields = BTreeSet::new();
    let mut keys = BTreeSet::new();
    keys.extend(left.strings.iter().map(|field| field.key.clone()));
    keys.extend(right.strings.iter().map(|field| field.key.clone()));
    for key in keys {
        let left_value = left.strings.iter().find(|field| field.key == key);
        let right_value = right.strings.iter().find(|field| field.key == key);
        if left_value.map(|field| &field.value) != right_value.map(|field| &field.value)
            || left_value.map(|field| field.protected) != right_value.map(|field| field.protected)
        {
            fields.insert(key);
        }
    }
    if left.tags != right.tags {
        fields.insert("Tags".to_string());
    }
    if left.auto_type != right.auto_type {
        fields.insert("AutoType".to_string());
    }
    if left.binaries != right.binaries {
        fields.insert("Attachments".to_string());
    }
    if fields.is_empty() {
        fields.insert("<metadata>".to_string());
    }
    fields.into_iter().collect()
}

fn group_diff_fields(left: &Group, right: &Group) -> Vec<String> {
    let mut fields = Vec::new();
    if left.name != right.name {
        fields.push("Name".to_string());
    }
    if left.notes != right.notes {
        fields.push("Notes".to_string());
    }
    if left.tags != right.tags {
        fields.push("Tags".to_string());
    }
    if left.icon_id != right.icon_id {
        fields.push("Icon".to_string());
    }
    if fields.is_empty() {
        fields.push("<metadata>".to_string());
    }
    fields
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
