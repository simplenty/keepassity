// SPDX-License-Identifier: GPL-3.0-or-later
//! `verify`: independent structural comparison of two KDBX files.
//!
//! Reading uses the project's established read path (`keepass 0.17`). Nothing
//! is written; failure messages contain entry UUIDs and field names only — never
//! field values, passwords or TOTP secrets.

use keepass::db::{CustomDataValue, GroupRef};
use keepass::{Database, DatabaseKey};
use keepassity_core::kdbx::detect_version;
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::collections::{BTreeMap, BTreeSet};
use std::fs::File;
use std::io::Read;

#[derive(Clone, Debug, Serialize, PartialEq)]
struct FieldInfo {
    value: String,
    protected: bool,
}

#[derive(Clone, Debug, Serialize, PartialEq)]
struct AttachmentInfo {
    name: String,
    size: usize,
    sha256: String,
}

#[derive(Clone, Debug, Serialize, PartialEq)]
struct EntryInfo {
    uuid: String,
    title: String,
    username: String,
    password: String,
    url: String,
    notes: String,
    tags: Vec<String>,
    fields: BTreeMap<String, FieldInfo>,
    history: usize,
    attachments: Vec<AttachmentInfo>,
    custom_data_keys: Vec<String>,
}

#[derive(Clone, Debug, Serialize, PartialEq)]
struct GroupInfo {
    uuid: String,
    name: String,
}

#[derive(Clone, Debug, Serialize, PartialEq)]
struct MemoryProtectionInfo {
    protect_title: bool,
    protect_username: bool,
    protect_password: bool,
    protect_url: bool,
    protect_notes: bool,
}

#[derive(Clone, Debug, Serialize, Default, PartialEq)]
struct MetaInfo {
    database_name: Option<String>,
    database_description: Option<String>,
    default_username: Option<String>,
    memory_protection: Option<MemoryProtectionInfo>,
    recyclebin_enabled: Option<bool>,
    history_max_items: Option<isize>,
    history_max_size: Option<isize>,
    custom_data: BTreeMap<String, String>,
}

#[derive(Clone, Debug, Serialize, Default, PartialEq)]
struct XmlStats {
    protected_total: usize,
    protected_by_key: BTreeMap<String, usize>,
}

#[derive(Clone, Debug)]
struct Dump {
    bytes: u64,
    version: String,
    groups: Vec<GroupInfo>,
    entries: Vec<EntryInfo>,
    deleted_objects: Vec<String>,
    meta: MetaInfo,
    xml: XmlStats,
}

/// Run `keepassity-cli verify <original.kdbx> <modified.kdbx>`.
pub fn run(args: &[String], password: &str) -> Result<i32, String> {
    let original_path = args
        .get(2)
        .ok_or_else(|| "missing <original.kdbx>".to_string())?;
    let modified_path = args
        .get(3)
        .ok_or_else(|| "missing <modified.kdbx>".to_string())?;

    let original = dump(original_path, password)?;
    let modified = dump(modified_path, password)?;
    let expect_title = match (
        crate::arg_value(args, "--expect-title-old"),
        crate::arg_value(args, "--expect-title-new"),
    ) {
        (Some(old), Some(new)) => Some((old, new)),
        _ => None,
    };
    let allow_empty_standard_fields = args.iter().any(|a| a == "--allow-empty-standard-fields");
    let failures = compare(
        &original,
        &modified,
        expect_title,
        allow_empty_standard_fields,
    );
    let pass = failures.is_empty();
    for failure in &failures {
        println!("FAIL: {failure}");
    }
    if pass {
        println!("PASS: all compared fields preserved");
    }
    let summary = serde_json::json!({
        "pass": pass,
        "original_version": original.version,
        "modified_version": modified.version,
        "original_entries": original.entries.len(),
        "modified_entries": modified.entries.len(),
        "original_groups": original.groups.len(),
        "modified_groups": modified.groups.len(),
        "original_attachments": original.entries.iter().map(|e| e.attachments.len()).sum::<usize>(),
        "modified_attachments": modified.entries.iter().map(|e| e.attachments.len()).sum::<usize>(),
        "original_deleted_objects": original.deleted_objects.len(),
        "modified_deleted_objects": modified.deleted_objects.len(),
        "protected_fields": original.xml.protected_total,
        "modified_protected_fields": modified.xml.protected_total,
        "failures": failures,
    });
    println!(
        "{}",
        serde_json::to_string(&summary).map_err(|e| format!("json: {e}"))?
    );
    Ok(if pass { 0 } else { 1 })
}

fn sha256_hex(bytes: &[u8]) -> String {
    let digest = Sha256::digest(bytes);
    let mut out = String::with_capacity(digest.len() * 2);
    for byte in digest {
        out.push_str(&format!("{byte:02x}"));
    }
    out
}

fn detect_path(path: &str) -> Result<String, String> {
    let mut header = [0u8; 12];
    let mut file = File::open(path).map_err(|e| format!("cannot open {path}: {e}"))?;
    file.read_exact(&mut header)
        .map_err(|e| format!("cannot read header of {path}: {e}"))?;
    detect_version(&header)
        .map(|v| v.as_str().to_string())
        .map_err(|e| format!("{path}: {e}"))
}

fn open_db(path: &str, password: &str) -> Result<Database, String> {
    let mut file = File::open(path).map_err(|e| format!("cannot open {path}: {e}"))?;
    let key = DatabaseKey::new().with_password(password);
    Database::open(&mut file, key).map_err(|e| format!("cannot read {path}: {e:?}"))
}

fn get_xml(path: &str, password: &str) -> Result<Vec<u8>, String> {
    let mut file = File::open(path).map_err(|e| format!("cannot open {path}: {e}"))?;
    let key = DatabaseKey::new().with_password(password);
    Database::get_xml(&mut file, key).map_err(|e| format!("cannot decrypt {path}: {e:?}"))
}

fn walk_groups(group: &GroupRef<'_>, out: &mut Vec<GroupInfo>) {
    out.push(GroupInfo {
        uuid: group.id().uuid().to_string(),
        name: group.name.clone(),
    });
    for sub in group.groups() {
        walk_groups(&sub, out);
    }
}

fn custom_data_map(
    map: &std::collections::HashMap<String, keepass::db::CustomDataItem>,
) -> BTreeMap<String, String> {
    map.iter()
        .map(|(key, item)| {
            let value = match &item.value {
                Some(CustomDataValue::String(s)) => s.clone(),
                Some(CustomDataValue::Binary(b)) => format!("<binary:{} bytes>", b.len()),
                None => String::new(),
            };
            (key.clone(), value)
        })
        .collect()
}

fn dump_meta(db: &Database) -> MetaInfo {
    let meta = &db.meta;
    MetaInfo {
        database_name: meta.database_name.clone(),
        database_description: meta.database_description.clone(),
        default_username: meta.default_username.clone(),
        memory_protection: meta
            .memory_protection
            .as_ref()
            .map(|p| MemoryProtectionInfo {
                protect_title: p.protect_title,
                protect_username: p.protect_username,
                protect_password: p.protect_password,
                protect_url: p.protect_url,
                protect_notes: p.protect_notes,
            }),
        recyclebin_enabled: meta.recyclebin_enabled,
        history_max_items: meta.history_max_items,
        history_max_size: meta.history_max_size,
        custom_data: custom_data_map(&meta.custom_data),
    }
}

fn xml_stats(xml: &[u8]) -> XmlStats {
    let text = String::from_utf8_lossy(xml);
    let mut protected_total = 0usize;
    let mut protected_by_key: BTreeMap<String, usize> = BTreeMap::new();
    let mut offset = 0usize;
    while let Some(rel) = text[offset..].find("Protected=\"True\"") {
        let pos = offset + rel;
        protected_total += 1;
        if let Some(key_pos) = text[..pos].rfind("<Key>") {
            let after = &text[key_pos + "<Key>".len()..];
            if let Some(end) = after.find("</Key>") {
                *protected_by_key
                    .entry(after[..end].to_string())
                    .or_insert(0) += 1;
            }
        }
        offset = pos + 1;
    }
    XmlStats {
        protected_total,
        protected_by_key,
    }
}

fn dump(path: &str, password: &str) -> Result<Dump, String> {
    let db = open_db(path, password)?;
    let version = detect_path(path)?;
    let bytes = std::fs::metadata(path)
        .map_err(|e| format!("cannot stat {path}: {e}"))?
        .len();

    let mut groups = Vec::new();
    walk_groups(&db.root(), &mut groups);
    groups.sort_by(|a, b| (&a.uuid, &a.name).cmp(&(&b.uuid, &b.name)));

    let mut entries: Vec<EntryInfo> = Vec::new();
    for entry in db.iter_all_entries() {
        let mut fields = BTreeMap::new();
        for (key, value) in entry.fields.iter() {
            fields.insert(
                key.clone(),
                FieldInfo {
                    value: value.get().clone(),
                    protected: value.is_protected(),
                },
            );
        }
        let mut attachments: Vec<AttachmentInfo> = entry
            .attachments_named()
            .map(|(name, att)| {
                let data = att.data.get();
                AttachmentInfo {
                    name: name.to_string(),
                    size: data.len(),
                    sha256: sha256_hex(data),
                }
            })
            .collect();
        attachments.sort_by(|a, b| a.name.cmp(&b.name));
        let mut history = 0usize;
        while entry.historical(history).is_some() {
            history += 1;
        }
        let mut custom_data_keys: Vec<String> = entry.custom_data.keys().cloned().collect();
        custom_data_keys.sort();
        entries.push(EntryInfo {
            uuid: entry.id().uuid().to_string(),
            title: entry.get_title().unwrap_or("").to_string(),
            username: entry.get_username().unwrap_or("").to_string(),
            password: entry.get_password().unwrap_or("").to_string(),
            url: entry.get_url().unwrap_or("").to_string(),
            notes: entry.get("Notes").unwrap_or("").to_string(),
            tags: {
                let mut tags = entry.tags.clone();
                tags.sort();
                tags
            },
            fields,
            history,
            attachments,
            custom_data_keys,
        });
    }
    entries.sort_by(|a, b| a.uuid.cmp(&b.uuid));

    let mut deleted_objects: Vec<String> = db
        .deleted_objects
        .keys()
        .map(|uuid| uuid.to_string())
        .collect();
    deleted_objects.sort();

    let xml = xml_stats(&get_xml(path, password)?);

    Ok(Dump {
        bytes,
        version,
        groups,
        entries,
        deleted_objects,
        meta: dump_meta(&db),
        xml,
    })
}

fn compare(
    original: &Dump,
    output: &Dump,
    expect_title: Option<(String, String)>,
    allow_empty_standard_fields: bool,
) -> Vec<String> {
    let mut failures = Vec::new();

    let original_groups: BTreeMap<&str, &str> = original
        .groups
        .iter()
        .map(|g| (g.uuid.as_str(), g.name.as_str()))
        .collect();
    let output_groups: BTreeMap<&str, &str> = output
        .groups
        .iter()
        .map(|g| (g.uuid.as_str(), g.name.as_str()))
        .collect();
    for uuid in original_groups.keys() {
        if !output_groups.contains_key(uuid) {
            failures.push(format!("group {uuid} missing from output"));
        }
    }
    for uuid in output_groups.keys() {
        if !original_groups.contains_key(uuid) {
            failures.push(format!("group {uuid} added by output"));
        }
    }
    for (uuid, name) in &original_groups {
        if let Some(after) = output_groups.get(uuid) {
            if name != after {
                failures.push(format!("group {uuid}: name changed"));
            }
        }
    }

    let original_entries: BTreeMap<&str, &EntryInfo> = original
        .entries
        .iter()
        .map(|e| (e.uuid.as_str(), e))
        .collect();
    let output_entries: BTreeMap<&str, &EntryInfo> = output
        .entries
        .iter()
        .map(|e| (e.uuid.as_str(), e))
        .collect();
    for uuid in original_entries.keys() {
        if !output_entries.contains_key(uuid) {
            failures.push(format!("entry {uuid} missing from output"));
        }
    }
    for uuid in output_entries.keys() {
        if !original_entries.contains_key(uuid) {
            failures.push(format!("entry {uuid} added by output"));
        }
    }

    // Work out which entry is allowed to change title: exactly one entry whose
    // title went old -> new. All other entries must keep their title unchanged.
    let mut title_change_uuid: Option<String> = None;
    if let Some((old, new)) = &expect_title {
        let changed: Vec<&str> = original
            .entries
            .iter()
            .filter(|before| {
                output_entries
                    .get(before.uuid.as_str())
                    .map(|after| &before.title == old && &after.title == new)
                    .unwrap_or(false)
            })
            .map(|e| e.uuid.as_str())
            .collect();
        if changed.len() == 1 {
            title_change_uuid = Some(changed[0].to_string());
        } else {
            failures.push(format!(
                "expected title change not observed exactly once (matches={})",
                changed.len()
            ));
        }
    }

    for (uuid, before) in &original_entries {
        let Some(after) = output_entries.get(*uuid) else {
            continue;
        };
        if before.username != after.username {
            failures.push(format!("entry {uuid}: username changed"));
        }
        if before.password != after.password {
            failures.push(format!("entry {uuid}: password changed"));
        }
        if before.url != after.url {
            failures.push(format!("entry {uuid}: url changed"));
        }
        if before.notes != after.notes {
            failures.push(format!("entry {uuid}: notes changed"));
        }
        if before.tags != after.tags {
            failures.push(format!("entry {uuid}: tags changed"));
        }
        if before.history != after.history {
            failures.push(format!(
                "entry {uuid}: history count {} -> {}",
                before.history, after.history
            ));
        }
        if before.attachments != after.attachments {
            failures.push(format!("entry {uuid}: attachments changed"));
        }
        if before.custom_data_keys != after.custom_data_keys {
            failures.push(format!("entry {uuid}: custom_data keys changed"));
        }

        let title_allowed = match &title_change_uuid {
            Some(changed) if changed == uuid => {
                if let Some((old, new)) = &expect_title {
                    before.title == *old && after.title == *new
                } else {
                    before.title == after.title
                }
            }
            _ => before.title == after.title,
        };
        if !title_allowed {
            failures.push(format!("entry {uuid}: title not as expected"));
        }

        let field_keys: BTreeSet<&String> =
            before.fields.keys().chain(after.fields.keys()).collect();
        for key in field_keys {
            if key == "Title" && title_allowed {
                continue;
            }
            match (before.fields.get(key), after.fields.get(key)) {
                (Some(a), Some(b)) => {
                    if a.value != b.value {
                        failures.push(format!("entry {uuid}: field {key} value changed"));
                    }
                    if a.protected != b.protected {
                        failures.push(format!("entry {uuid}: field {key} protected flag changed"));
                    }
                }
                (Some(_), None) => failures.push(format!("entry {uuid}: field {key} lost")),
                (None, Some(b)) => {
                    // Accepted Q10 deviation: keepassxcr materialises empty
                    // Notes/URL/UserName fields for legacy entries that omitted
                    // them. Only allow that exact, semantics-free addition.
                    let empty_standard = allow_empty_standard_fields
                        && matches!(key.as_str(), "Notes" | "URL" | "UserName")
                        && b.value.is_empty();
                    if !empty_standard {
                        failures.push(format!("entry {uuid}: field {key} added"));
                    }
                }
                (None, None) => {}
            }
        }
    }

    if original.deleted_objects != output.deleted_objects {
        failures.push("deleted_objects changed".to_string());
    }

    let meta_pairs: [(&str, String, String); 7] = [
        (
            "database_name",
            format!("{:?}", original.meta.database_name),
            format!("{:?}", output.meta.database_name),
        ),
        (
            "database_description",
            format!("{:?}", original.meta.database_description),
            format!("{:?}", output.meta.database_description),
        ),
        (
            "default_username",
            format!("{:?}", original.meta.default_username),
            format!("{:?}", output.meta.default_username),
        ),
        (
            "memory_protection",
            format!("{:?}", original.meta.memory_protection),
            format!("{:?}", output.meta.memory_protection),
        ),
        (
            "recyclebin_enabled",
            format!("{:?}", original.meta.recyclebin_enabled),
            format!("{:?}", output.meta.recyclebin_enabled),
        ),
        (
            "history_max_items",
            format!("{:?}", original.meta.history_max_items),
            format!("{:?}", output.meta.history_max_items),
        ),
        (
            "history_max_size",
            format!("{:?}", original.meta.history_max_size),
            format!("{:?}", output.meta.history_max_size),
        ),
    ];
    for (name, a, b) in meta_pairs {
        if a != b {
            failures.push(format!("meta {name} changed"));
        }
    }
    if original.meta.custom_data != output.meta.custom_data {
        failures.push("meta custom_data changed".to_string());
    }

    if original.xml.protected_total != output.xml.protected_total
        || original.xml.protected_by_key != output.xml.protected_by_key
    {
        failures.push("protected fields changed".to_string());
    }

    let _ = (original.bytes, output.bytes);
    failures
}
