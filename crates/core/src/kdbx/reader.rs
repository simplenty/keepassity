// SPDX-License-Identifier: GPL-3.0-or-later
//! KDBX reader: decrypt a KDBX 3.1/4.0/4.1 byte buffer and map it onto the
//! core model.
//!
//! The established `keepass` crate (MIT) performs decryption and KDF work; this
//! module adds the mapping to our model. It performs **no file IO** — callers
//! pass the file bytes. The raw decrypted XML is retained (not serialized) so
//! that unmapped/unknown data is not lost; canonical dumps only expose its
//! SHA-256.

use crate::kdbx::detect_version;
use crate::model::{Association, AutoType, Binary, CustomData, CustomDataItem, CustomDataValue};
use crate::model::{Database, DeletedObject, Entry, Group, MemoryProtection, Meta, StringField};
use crate::model::{Times, Uuid};
use keepass::db::{
    AutoType as KpAutoType, CustomDataItem as KpCustomDataItem,
    CustomDataValue as KpCustomDataValue, DataTransferObfuscation, Icon, Times as KpTimes,
};
use keepass::{Database as KpDatabase, DatabaseKey};
use sha2::{Digest, Sha256};
use std::collections::BTreeMap;
use std::fmt;
use std::io::Cursor;

/// Error returned by the KDBX reader.
#[derive(Debug)]
pub enum KdbxReadError {
    /// Wrong password/key (or a key-like failure).
    BadPassword,
    /// Corrupt/truncated data, bad HMAC, or unparsable content.
    Corrupt(String),
    /// Unsupported KDBX version or format feature.
    Unsupported(String),
    /// Header is missing/unknown.
    Header(String),
}

impl fmt::Display for KdbxReadError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::BadPassword => write!(f, "bad password or key (database could not be decrypted)"),
            Self::Corrupt(message) => write!(f, "corrupt KDBX database: {message}"),
            Self::Unsupported(message) => write!(f, "unsupported KDBX database: {message}"),
            Self::Header(message) => write!(f, "invalid KDBX header: {message}"),
        }
    }
}

impl std::error::Error for KdbxReadError {}

fn classify_open_error(error: &impl fmt::Debug) -> KdbxReadError {
    let raw = format!("{error:?}");
    if raw.contains("IncorrectKey") || raw.contains("EmptyKey") {
        KdbxReadError::BadPassword
    } else if raw.contains("UnsupportedVersion") || raw.contains("VersionParse") {
        KdbxReadError::Unsupported(raw)
    } else if raw.contains("UnexpectedEof") {
        KdbxReadError::Corrupt("unexpected end of file".to_string())
    } else {
        KdbxReadError::Corrupt(raw)
    }
}

/// Parse a KDBX byte buffer into the core model.
///
/// * `password` is used only to derive the key; it never enters errors/dumps.
/// * The returned model keeps `raw_xml` (not serialized) and its SHA-256 so
///   unknown data can be preserved without leaking plaintext XML in dumps.
pub fn parse(bytes: &[u8], password: &str) -> Result<Database, KdbxReadError> {
    let header = bytes
        .get(..12)
        .ok_or_else(|| KdbxReadError::Header("shorter than 12 bytes".to_string()))?;
    let version = detect_version(header).map_err(|error| match error {
        crate::kdbx::KdbxHeaderError::TooShort => {
            KdbxReadError::Corrupt("header shorter than 12 bytes".to_string())
        }
        crate::kdbx::KdbxHeaderError::BadSignature => {
            KdbxReadError::Corrupt("not a KDBX file (bad signature)".to_string())
        }
        crate::kdbx::KdbxHeaderError::UnknownVersion(version) => {
            KdbxReadError::Unsupported(format!("unknown KDBX version word 0x{version:08X}"))
        }
    })?;

    let mut model_cursor = Cursor::new(bytes);
    let key = DatabaseKey::new().with_password(password);
    let kp =
        KpDatabase::open(&mut model_cursor, key).map_err(|error| classify_open_error(&error))?;

    let mut xml_cursor = Cursor::new(bytes);
    let xml_key = DatabaseKey::new().with_password(password);
    let xml = KpDatabase::get_xml(&mut xml_cursor, xml_key)
        .map_err(|error| classify_open_error(&error))?;
    let xml_hash = {
        let digest = Sha256::digest(&xml);
        let mut out = String::with_capacity(digest.len() * 2);
        for byte in digest {
            out.push_str(&format!("{byte:02x}"));
        }
        out
    };

    let meta = meta_from(&kp.meta, &kp);
    let root = group_from(&kp.root(), &kp);

    let mut deleted_objects: Vec<DeletedObject> = kp
        .deleted_objects
        .iter()
        .map(|(uuid, time)| DeletedObject {
            uuid: uuid_from_display(uuid),
            deletion_time: time.map(format_time).unwrap_or_default(),
        })
        .collect();
    deleted_objects.sort_by_key(|a| a.uuid);

    Ok(Database {
        format_version: version.as_str().to_string(),
        meta,
        root,
        deleted_objects,
        unknown: Vec::new(),
        raw_xml: Some(xml),
        raw_xml_sha256: Some(xml_hash),
    })
}

/// Format a `chrono::NaiveDateTime` (or any displayable time) as KDBX XML text.
///
/// The concrete type comes from the `keepass` API, so this module does not need
/// a direct `chrono` dependency. `Display` for `NaiveDateTime` is
/// `YYYY-MM-DD HH:MM:SS`; KDBX uses `T` and a trailing `Z`.
fn format_time<T: fmt::Display>(value: T) -> String {
    let text = value.to_string();
    let text = text.replacen(' ', "T", 1);
    format!("{text}Z")
}

fn uuid_from_display(value: impl fmt::Display) -> Uuid {
    value.to_string().parse().unwrap_or_default()
}

fn icon_id(icon: Option<&Icon>) -> u32 {
    match icon {
        Some(Icon::BuiltIn(index)) => u32::try_from(*index).unwrap_or(0),
        _ => 0,
    }
}

fn times_from(times: &KpTimes) -> Times {
    Times {
        creation: times.creation.map(format_time).unwrap_or_default(),
        last_modification: times.last_modification.map(format_time).unwrap_or_default(),
        last_access: times.last_access.map(format_time).unwrap_or_default(),
        expiry: format_time(times.expiry),
        expires: times.expires.unwrap_or(false),
        usage_count: times.usage_count.unwrap_or(0) as u64,
        location_changed: times.location_changed.map(format_time).unwrap_or_default(),
        unknown: Vec::new(),
    }
}

fn custom_data_from(map: &std::collections::HashMap<String, KpCustomDataItem>) -> CustomData {
    let mut items = BTreeMap::new();
    for (key, item) in map {
        items.insert(
            key.clone(),
            CustomDataItem {
                value: item.value.as_ref().map(|value| match value {
                    KpCustomDataValue::String(text) => CustomDataValue::String(text.clone()),
                    KpCustomDataValue::Binary(bytes) => CustomDataValue::Binary(bytes.clone()),
                }),
                last_modification: item.last_modification_time.map(format_time),
                unknown: Vec::new(),
            },
        );
    }
    CustomData {
        items,
        unknown: Vec::new(),
    }
}

fn memory_protection_from(protection: Option<&keepass::db::MemoryProtection>) -> MemoryProtection {
    let mut out = MemoryProtection::default();
    if let Some(protection) = protection {
        out.protect_title = protection.protect_title;
        out.protect_username = protection.protect_username;
        out.protect_password = protection.protect_password;
        out.protect_url = protection.protect_url;
        out.protect_notes = protection.protect_notes;
    }
    out
}

fn meta_from(meta: &keepass::db::Meta, db: &KpDatabase) -> Meta {
    let mut custom_icons = Vec::new();
    for icon in db.iter_all_custom_icons() {
        custom_icons.push(crate::model::CustomIcon {
            uuid: uuid_from_display(icon.id().uuid()),
            data: icon.data.clone(),
            name: icon.name.clone(),
            last_modification: icon.last_modification_time.map(format_time),
            unknown: Vec::new(),
        });
    }
    custom_icons.sort_by_key(|a| a.uuid);
    Meta {
        generator: meta.generator.clone().unwrap_or_default(),
        database_name: meta.database_name.clone().unwrap_or_default(),
        database_description: meta.database_description.clone().unwrap_or_default(),
        default_username: meta.default_username.clone().unwrap_or_default(),
        memory_protection: memory_protection_from(meta.memory_protection.as_ref()),
        recycle_bin_enabled: meta.recyclebin_enabled.unwrap_or(false),
        recycle_bin_uuid: meta.recyclebin_uuid.map(uuid_from_display),
        recycle_bin_changed: meta.recyclebin_changed.map(format_time),
        history_max_items: meta.history_max_items.unwrap_or(0) as i64,
        history_max_size: meta.history_max_size.unwrap_or(0) as i64,
        custom_data: custom_data_from(&meta.custom_data),
        custom_icons,
        unknown: Vec::new(),
    }
}

fn group_from(group: &keepass::db::GroupRef<'_>, db: &KpDatabase) -> Group {
    let _ = db;
    Group {
        uuid: uuid_from_display(group.id()),
        name: group.name.clone(),
        notes: group.notes.clone().unwrap_or_default(),
        icon_id: icon_id(group.icon()),
        times: times_from(&group.times),
        is_expanded: group.is_expanded,
        default_auto_type: group.enable_autotype.unwrap_or(false),
        default_autotype_sequence: group.default_autotype_sequence.clone().unwrap_or_default(),
        enable_auto_type: group.enable_autotype.unwrap_or(false),
        enable_searching: group.enable_searching.unwrap_or(false),
        last_top_visible_entry: None,
        tags: group.tags.clone(),
        previous_parent_group: None,
        custom_data: custom_data_from(&group.custom_data),
        groups: group.groups().map(|child| group_from(&child, db)).collect(),
        entries: group
            .entries()
            .map(|entry| entry_from(&entry, db))
            .collect(),
        unknown: Vec::new(),
    }
}

fn entry_from(entry: &keepass::db::EntryRef<'_>, db: &KpDatabase) -> Entry {
    let _ = db;
    let strings = {
        let mut fields = entry
            .fields
            .iter()
            .map(|(key, value)| StringField {
                key: key.clone(),
                value: value.get().clone(),
                protected: value.is_protected(),
                unknown: Vec::new(),
            })
            .collect::<Vec<_>>();
        fields.sort_by(|a, b| a.key.cmp(&b.key));
        fields
    };
    let binaries = {
        let mut binaries = entry
            .attachments_named()
            .map(|(name, attachment)| Binary {
                key: name.to_string(),
                value: attachment.data.get().clone(),
                protected: attachment.data.is_protected(),
                unknown: Vec::new(),
            })
            .collect::<Vec<_>>();
        binaries.sort_by(|a, b| a.key.cmp(&b.key));
        binaries
    };
    let mut history = Vec::new();
    let mut index = 0usize;
    while let Some(previous) = entry.historical(index) {
        history.push(entry_from(&previous, db));
        index += 1;
    }
    Entry {
        uuid: uuid_from_display(entry.id()),
        icon_id: icon_id(entry.icon()),
        foreground_color: entry
            .foreground_color
            .as_ref()
            .map(ToString::to_string)
            .unwrap_or_default(),
        background_color: entry
            .background_color
            .as_ref()
            .map(ToString::to_string)
            .unwrap_or_default(),
        override_url: entry.override_url.clone().unwrap_or_default(),
        tags: entry.tags.clone(),
        previous_parent_group: None,
        times: times_from(&entry.times),
        strings,
        binaries,
        auto_type: entry.autotype.as_ref().map(auto_type_from),
        history,
        custom_data: custom_data_from(&entry.custom_data),
        unknown: Vec::new(),
    }
}

fn auto_type_from(auto_type: &KpAutoType) -> AutoType {
    AutoType {
        enabled: auto_type.enabled,
        data_transfer_obfuscation: matches!(
            auto_type.data_transfer_obfuscation,
            DataTransferObfuscation::UseClipboard
        ),
        default_sequence: auto_type.default_sequence.clone().unwrap_or_default(),
        associations: auto_type
            .associations
            .iter()
            .map(|association| Association {
                window: association.window.clone(),
                keystroke_sequence: association.sequence.clone(),
                unknown: Vec::new(),
            })
            .collect(),
        unknown: Vec::new(),
    }
}
