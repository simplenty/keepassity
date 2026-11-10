// SPDX-License-Identifier: GPL-3.0-or-later
//! KDBX reader: decrypt a KDBX 3.1/4.0/4.1 byte buffer and map it onto the
//! core model.
//!
//! The established `keepass` crate (MIT) performs decryption and KDF work; this
//! module adds the mapping to our model. It performs **no file IO** — callers
//! pass the file bytes. The raw decrypted XML is retained (not serialized) so
//! that unmapped/unknown data is not lost; canonical dumps only expose its
//! SHA-256.

use crate::kdbx::{detect_version, KdbxVersion};
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

/// Run one upstream `keepass` reader call and turn a panic into a parse error.
///
/// The upstream `keepass` crate (0.17) panics instead of returning an error for
/// a few malformed header shapes (for example a variant dictionary whose length
/// field points past the end of the buffer: `byteorder::read_u64` indexes out of
/// bounds). A KDBX file is untrusted input - it arrives from WebDAV, a shared
/// folder or the file manager - so a malformed file must surface as a normal
/// read error, not abort the application or the fuzzer. The panic message
/// carries no secret material; it is dropped here and the reason is reported as
/// "malformed header" instead.
fn guard_keepass<T>(
    call: impl FnOnce() -> Result<T, keepass::error::DatabaseOpenError>,
) -> Result<T, KdbxReadError> {
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(call)) {
        Ok(Ok(value)) => Ok(value),
        Ok(Err(error)) => Err(classify_open_error(&error)),
        Err(_) => Err(KdbxReadError::Corrupt(
            "malformed KDBX header (the upstream reader panicked; file rejected)".to_string(),
        )),
    }
}

/// Upper bounds for the key-derivation work a KDBX file may ask for.
///
/// The header is untrusted input: it can name arbitrary KDF parameters, and the
/// reader would otherwise spend unbounded time (or memory) deriving a key from a
/// hostile file before even noticing a wrong password. The daily fuzzing job
/// found exactly that shape (a KDBX 3.1 header with ~2e18 AES-KDF rounds made the
/// reader run for minutes). The limits are far above real-world databases - the
/// writer's default is 128 MiB / 6 iterations / 4 lanes, about 0.7 s - and only
/// reject requests that are clearly meant to exhaust the machine.
const MAX_AES_KDF_ROUNDS: u64 = 10_000_000;
const MAX_ARGON2_MEMORY_BYTES: u64 = 2 * 1024 * 1024 * 1024;
const MAX_ARGON2_ITERATIONS: u64 = 64;
const MAX_ARGON2_PARALLELISM: u32 = 16;
/// `memory (MiB) * iterations` bound; about eight times the write default.
const MAX_ARGON2_WORK_MIB_ITERATIONS: u64 = 6_144;

const UUID_AES_KDF: [u8; 16] = [
    0xC9, 0xD9, 0xF3, 0x9A, 0x62, 0x8A, 0x44, 0x60, 0xBF, 0x74, 0x0D, 0x08, 0xC1, 0x8A, 0x4F, 0xEA,
];
const UUID_ARGON2D: [u8; 16] = [
    0xEF, 0x63, 0x6D, 0xDF, 0x8C, 0x29, 0x44, 0x4B, 0x91, 0xF7, 0xA9, 0xA4, 0x03, 0xE3, 0x0A, 0x0C,
];
const UUID_ARGON2ID: [u8; 16] = [
    0x9E, 0x29, 0x8B, 0x19, 0x56, 0xDB, 0x47, 0x73, 0xB2, 0x3D, 0xFC, 0x3E, 0xC6, 0xF0, 0xA1, 0xE6,
];

fn extreme_kdf_error(detail: impl fmt::Display) -> KdbxReadError {
    KdbxReadError::Unsupported(format!(
        "the database asks for an extreme {detail} (opening it would take a very long \
         time); open it in KeePass and re-save it with normal KDF settings"
    ))
}

fn read_le_u16(bytes: &[u8], offset: usize) -> Option<u16> {
    Some(u16::from_le_bytes(
        bytes.get(offset..offset + 2)?.try_into().ok()?,
    ))
}

fn read_le_u32(bytes: &[u8], offset: usize) -> Option<u32> {
    Some(u32::from_le_bytes(
        bytes.get(offset..offset + 4)?.try_into().ok()?,
    ))
}

fn read_le_u64(bytes: &[u8], offset: usize) -> Option<u64> {
    Some(u64::from_le_bytes(
        bytes.get(offset..offset + 8)?.try_into().ok()?,
    ))
}

/// Rejects headers whose KDF parameters would make key derivation take far too
/// long. Called before the third-party reader runs; a file that fails here is a
/// normal read error (never a panic and never a multi-minute stall).
pub(crate) fn validate_kdf_cost(bytes: &[u8], version: KdbxVersion) -> Result<(), KdbxReadError> {
    match version {
        KdbxVersion::V3_1 => validate_kdbx3_kdf(bytes),
        KdbxVersion::V4_0 | KdbxVersion::V4_1 => validate_kdbx4_kdf(bytes),
    }
}

fn validate_kdbx3_kdf(bytes: &[u8]) -> Result<(), KdbxReadError> {
    let mut offset = 12usize;
    loop {
        let field = match bytes.get(offset) {
            Some(value) => *value,
            None => return Ok(()),
        };
        if field == 0 {
            return Ok(());
        }
        let Some(length) = read_le_u16(bytes, offset + 1) else {
            return Ok(());
        };
        let start = offset + 3;
        let end = start + length as usize;
        let Some(payload) = bytes.get(start..end) else {
            return Ok(());
        };
        if field == 6 && payload.len() == 8 {
            let rounds = u64::from_le_bytes(payload.try_into().expect("length checked"));
            if rounds > MAX_AES_KDF_ROUNDS {
                return Err(extreme_kdf_error(format_args!(
                    "AES-KDF round count ({rounds})"
                )));
            }
            return Ok(());
        }
        offset = end;
    }
}

fn validate_kdbx4_kdf(bytes: &[u8]) -> Result<(), KdbxReadError> {
    let mut offset = 12usize;
    loop {
        let field = match bytes.get(offset) {
            Some(value) => *value,
            None => return Ok(()),
        };
        if field == 0 {
            return Ok(());
        }
        let Some(length) = read_le_u32(bytes, offset + 1) else {
            return Ok(());
        };
        let start = offset + 5;
        let end = start + length as usize;
        let Some(payload) = bytes.get(start..end) else {
            return Ok(());
        };
        if field == 11 {
            return validate_kdbx4_kdf_parameters(payload);
        }
        offset = end;
    }
}

fn validate_kdbx4_kdf_parameters(payload: &[u8]) -> Result<(), KdbxReadError> {
    // VariantDictionary: u16 version, then (type u8, key, value) entries.
    let mut offset = 2usize;
    let mut uuid: Option<[u8; 16]> = None;
    let mut rounds: Option<u64> = None;
    let mut memory_bytes: Option<u64> = None;
    let mut iterations: Option<u64> = None;
    let mut parallelism: Option<u32> = None;
    while offset < payload.len() {
        let value_type = payload[offset];
        offset += 1;
        if value_type == 0 {
            break;
        }
        let Some(key_length) = read_le_u32(payload, offset) else {
            return Ok(());
        };
        offset += 4;
        let key_end = offset + key_length as usize;
        let Some(key) = payload.get(offset..key_end) else {
            return Ok(());
        };
        offset = key_end;
        let Some(value_length) = read_le_u32(payload, offset) else {
            return Ok(());
        };
        offset += 4;
        let value_end = offset + value_length as usize;
        let Some(value) = payload.get(offset..value_end) else {
            return Ok(());
        };
        offset = value_end;
        if key == b"$UUID" && value_type == 0x0C && value.len() == 16 {
            let mut parsed = [0u8; 16];
            parsed.copy_from_slice(value);
            uuid = Some(parsed);
        } else if key == b"R" && value_type == 0x05 {
            rounds = read_le_u64(value, 0);
        } else if key == b"M" && value_type == 0x05 {
            memory_bytes = read_le_u64(value, 0);
        } else if key == b"I" && value_type == 0x05 {
            iterations = read_le_u64(value, 0);
        } else if key == b"P" && value_type == 0x04 {
            parallelism = read_le_u32(value, 0);
        }
    }
    let Some(uuid) = uuid else {
        return Ok(());
    };
    if uuid == UUID_AES_KDF {
        if let Some(rounds) = rounds {
            if rounds > MAX_AES_KDF_ROUNDS {
                return Err(extreme_kdf_error(format_args!(
                    "AES-KDF round count ({rounds})"
                )));
            }
        }
        return Ok(());
    }
    if uuid != UUID_ARGON2D && uuid != UUID_ARGON2ID {
        // Unknown KDFs are rejected by the reader itself, quickly.
        return Ok(());
    }
    if let Some(memory) = memory_bytes {
        if memory > MAX_ARGON2_MEMORY_BYTES {
            return Err(extreme_kdf_error(format_args!(
                "Argon2 memory size ({memory} bytes)"
            )));
        }
    }
    if let Some(iterations) = iterations {
        if iterations > MAX_ARGON2_ITERATIONS {
            return Err(extreme_kdf_error(format_args!(
                "Argon2 iteration count ({iterations})"
            )));
        }
    }
    if let Some(parallelism) = parallelism {
        if parallelism > MAX_ARGON2_PARALLELISM {
            return Err(extreme_kdf_error(format_args!(
                "Argon2 parallelism ({parallelism})"
            )));
        }
    }
    if let (Some(memory), Some(iterations)) = (memory_bytes, iterations) {
        let work = (memory / (1024 * 1024)).saturating_mul(iterations);
        if work > MAX_ARGON2_WORK_MIB_ITERATIONS {
            return Err(extreme_kdf_error(format_args!(
                "Argon2 work factor ({memory} bytes x {iterations} iterations)"
            )));
        }
    }
    Ok(())
}

fn open_with_keepass(bytes: &[u8], password: &str) -> Result<KpDatabase, KdbxReadError> {
    guard_keepass(|| {
        let mut cursor = Cursor::new(bytes);
        let key = DatabaseKey::new().with_password(password);
        KpDatabase::open(&mut cursor, key)
    })
}

fn xml_with_keepass(bytes: &[u8], password: &str) -> Result<Vec<u8>, KdbxReadError> {
    guard_keepass(|| {
        let mut cursor = Cursor::new(bytes);
        let key = DatabaseKey::new().with_password(password);
        KpDatabase::get_xml(&mut cursor, key)
    })
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

    validate_kdf_cost(bytes, version)?;
    let kp = open_with_keepass(bytes, password)?;
    let xml = xml_with_keepass(bytes, password)?;
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

fn custom_icon_uuid(icon: Option<&Icon>) -> Option<Uuid> {
    match icon {
        Some(Icon::Custom(id)) => Some(uuid_from_display(id.uuid())),
        _ => None,
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
        custom_icon_uuid: custom_icon_uuid(group.icon()),
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
        custom_icon_uuid: custom_icon_uuid(entry.icon()),
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
#[cfg(test)]
mod kdf_limit_tests {
    use super::{
        validate_kdf_cost, KdbxVersion, MAX_AES_KDF_ROUNDS, MAX_ARGON2_ITERATIONS,
        MAX_ARGON2_MEMORY_BYTES, MAX_ARGON2_PARALLELISM,
    };

    const UUID_AES: [u8; 16] = [
        0xC9, 0xD9, 0xF3, 0x9A, 0x62, 0x8A, 0x44, 0x60, 0xBF, 0x74, 0x0D, 0x08, 0xC1, 0x8A, 0x4F,
        0xEA,
    ];
    const UUID_ARGON2ID: [u8; 16] = [
        0x9E, 0x29, 0x8B, 0x19, 0x56, 0xDB, 0x47, 0x73, 0xB2, 0x3D, 0xFC, 0x3E, 0xC6, 0xF0, 0xA1,
        0xE6,
    ];

    fn variant_entry(value_type: u8, key: &[u8], value: &[u8]) -> Vec<u8> {
        let mut entry = vec![value_type];
        entry.extend_from_slice(&(key.len() as u32).to_le_bytes());
        entry.extend_from_slice(key);
        entry.extend_from_slice(&(value.len() as u32).to_le_bytes());
        entry.extend_from_slice(value);
        entry
    }

    fn u64_entry(key: &[u8], value: u64) -> Vec<u8> {
        variant_entry(0x05, key, &value.to_le_bytes())
    }

    fn u32_entry(key: &[u8], value: u32) -> Vec<u8> {
        variant_entry(0x04, key, &value.to_le_bytes())
    }

    fn variant_dictionary(entries: &[Vec<u8>]) -> Vec<u8> {
        let mut dictionary = vec![0x00, 0x01];
        for entry in entries {
            dictionary.extend_from_slice(entry);
        }
        dictionary.push(0);
        dictionary
    }

    fn kdbx3_header(rounds: u64) -> Vec<u8> {
        let mut bytes = vec![
            0x03, 0xD9, 0xA2, 0x9A, 0x67, 0xFB, 0x4B, 0xB5, 0x01, 0x00, 0x03, 0x00,
        ];
        bytes.push(0x06);
        bytes.extend_from_slice(&8u16.to_le_bytes());
        bytes.extend_from_slice(&rounds.to_le_bytes());
        bytes.push(0x00);
        bytes.extend_from_slice(&4u16.to_le_bytes());
        bytes.extend_from_slice(&[0x0D, 0x0A, 0x0D, 0x0A]);
        bytes
    }

    fn kdbx4_header(dictionary: Vec<u8>) -> Vec<u8> {
        let mut bytes = vec![
            0x03, 0xD9, 0xA2, 0x9A, 0x67, 0xFB, 0x4B, 0xB5, 0x00, 0x00, 0x04, 0x00,
        ];
        bytes.push(0x0B);
        bytes.extend_from_slice(&(dictionary.len() as u32).to_le_bytes());
        bytes.extend_from_slice(&dictionary);
        bytes.push(0x00);
        bytes.extend_from_slice(&4u32.to_le_bytes());
        bytes.extend_from_slice(&[0x0D, 0x0A, 0x0D, 0x0A]);
        bytes
    }

    fn argon2_dictionary(
        uuid: [u8; 16],
        memory: u64,
        iterations: u64,
        parallelism: u32,
    ) -> Vec<u8> {
        variant_dictionary(&[
            variant_entry(0x0C, b"$UUID", &uuid),
            u64_entry(b"M", memory),
            u64_entry(b"I", iterations),
            u32_entry(b"P", parallelism),
        ])
    }

    #[test]
    fn normal_kdbx31_rounds_pass() {
        assert!(validate_kdf_cost(&kdbx3_header(600_000), KdbxVersion::V3_1).is_ok());
        assert!(validate_kdf_cost(&kdbx3_header(MAX_AES_KDF_ROUNDS), KdbxVersion::V3_1).is_ok());
    }

    #[test]
    fn extreme_kdbx31_rounds_are_rejected() {
        let error = validate_kdf_cost(&kdbx3_header(MAX_AES_KDF_ROUNDS + 1), KdbxVersion::V3_1)
            .expect_err("an extreme round count must be rejected");
        assert!(matches!(error, super::KdbxReadError::Unsupported(_)));
    }

    #[test]
    fn normal_argon2id_parameters_pass() {
        let bytes = kdbx4_header(argon2_dictionary(UUID_ARGON2ID, 128 * 1024 * 1024, 6, 4));
        assert!(validate_kdf_cost(&bytes, KdbxVersion::V4_0).is_ok());
    }

    #[test]
    fn extreme_argon2_parameters_are_rejected() {
        for (memory, iterations, parallelism) in [
            (MAX_ARGON2_MEMORY_BYTES + 1, 1, 1),
            (128 * 1024 * 1024, MAX_ARGON2_ITERATIONS + 1, 1),
            (128 * 1024 * 1024, 1, MAX_ARGON2_PARALLELISM + 1),
            (2 * 1024 * 1024 * 1024, 8, 4), // within per-field caps, over the work cap
        ] {
            let bytes = kdbx4_header(argon2_dictionary(
                UUID_ARGON2ID,
                memory,
                iterations,
                parallelism,
            ));
            assert!(
                validate_kdf_cost(&bytes, KdbxVersion::V4_0).is_err(),
                "memory={memory} iterations={iterations} parallelism={parallelism} must be rejected"
            );
        }
    }

    #[test]
    fn normal_aes_kdf_in_kdbx4_passes() {
        let dictionary = variant_dictionary(&[
            variant_entry(0x0C, b"$UUID", &UUID_AES),
            u64_entry(b"R", 600_000),
        ]);
        assert!(validate_kdf_cost(&kdbx4_header(dictionary), KdbxVersion::V4_0).is_ok());
    }

    #[test]
    fn unknown_kdf_is_left_to_the_reader() {
        let dictionary = variant_dictionary(&[variant_entry(0x0C, b"$UUID", &[0x42; 16])]);
        assert!(validate_kdf_cost(&kdbx4_header(dictionary), KdbxVersion::V4_0).is_ok());
    }
}
