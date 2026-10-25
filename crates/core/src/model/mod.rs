// SPDX-License-Identifier: GPL-3.0-or-later
//! In-memory KDBX model (single source of truth for core and UI).
//!
//! The model is deliberately lossless: every collection keeps the original order,
//! raw XML that the parser does not recognise is stored in [`RawXml`], and binary
//! content is kept as bytes. The KDBX parser/writer (M1-02/M1-03) is responsible
//! for filling and consuming these fields; this module is IO-free.

use serde::{Deserialize, Serialize};
use sha2::Digest;
use std::collections::BTreeMap;
use std::fmt;
use std::str::FromStr;

/// 16-byte UUID as used by KDBX. Serialized as 32 lowercase hex characters.
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Default)]
pub struct Uuid([u8; 16]);

impl Uuid {
    /// Create from raw bytes.
    pub fn from_bytes(bytes: [u8; 16]) -> Self {
        Self(bytes)
    }

    /// All-zero UUID (KDBX "no value").
    pub fn nil() -> Self {
        Self([0u8; 16])
    }

    /// Raw bytes.
    pub fn as_bytes(&self) -> &[u8; 16] {
        &self.0
    }

    /// `true` for the all-zero UUID.
    pub fn is_nil(&self) -> bool {
        self.0 == [0u8; 16]
    }

    /// 32 lowercase hex characters.
    pub fn to_hex(self) -> String {
        let mut out = String::with_capacity(32);
        for byte in self.0 {
            out.push_str(&format!("{byte:02x}"));
        }
        out
    }
}

impl fmt::Display for Uuid {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.to_hex())
    }
}

impl fmt::Debug for Uuid {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "Uuid({})", self.to_hex())
    }
}

impl FromStr for Uuid {
    type Err = String;

    fn from_str(value: &str) -> Result<Self, Self::Err> {
        let value = value.trim();
        let hex = value.replace('-', "");
        if hex.len() != 32 {
            return Err(format!("invalid UUID length: {}", hex.len()));
        }
        let mut bytes = [0u8; 16];
        for (index, byte) in bytes.iter_mut().enumerate() {
            let start = index * 2;
            *byte = u8::from_str_radix(&hex[start..start + 2], 16)
                .map_err(|_| format!("invalid UUID hex: {value}"))?;
        }
        Ok(Self(bytes))
    }
}

impl Serialize for Uuid {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str(&self.to_hex())
    }
}

impl<'de> Deserialize<'de> for Uuid {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let value = String::deserialize(deserializer)?;
        value.parse().map_err(serde::de::Error::custom)
    }
}

/// Raw XML element preserved verbatim for unknown tags/attributes.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct RawXml {
    /// Element name (e.g. `CustomPluginData`).
    pub name: String,
    /// Attributes in deterministic order.
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub attributes: BTreeMap<String, String>,
    /// Direct text content, if any.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub text: Option<String>,
    /// Nested elements.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub children: Vec<RawXml>,
}

impl RawXml {
    /// Empty element with a name.
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            ..Self::default()
        }
    }
}

/// KDBX times block. Strings keep the original XML date-time text so a rewrite
/// is byte-faithful; helpers convert to Unix seconds for DTOs.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct Times {
    /// `CreationTime`.
    pub creation: String,
    /// `LastModificationTime` — determines newness in merge.
    pub last_modification: String,
    /// `LastAccessTime`.
    pub last_access: String,
    /// `ExpiryTime` (only meaningful when `expires`).
    pub expiry: String,
    /// `Expires` flag.
    pub expires: bool,
    /// `UsageCount`.
    pub usage_count: u64,
    /// `LocationChanged` — determines latest location after a move.
    pub location_changed: String,
    /// Unknown child elements.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub unknown: Vec<RawXml>,
}

impl Times {
    /// Parsed `CreationTime` as Unix seconds.
    pub fn created_unix(&self) -> Option<i64> {
        xml_datetime_to_unix(&self.creation)
    }

    /// Parsed `LastModificationTime` as Unix seconds.
    pub fn modified_unix(&self) -> Option<i64> {
        xml_datetime_to_unix(&self.last_modification)
    }

    /// Parsed `LastAccessTime` as Unix seconds.
    pub fn accessed_unix(&self) -> Option<i64> {
        xml_datetime_to_unix(&self.last_access)
    }

    /// Parsed `ExpiryTime` as Unix seconds when `expires` is set.
    pub fn expires_unix(&self) -> Option<i64> {
        if self.expires {
            xml_datetime_to_unix(&self.expiry)
        } else {
            None
        }
    }
}

/// Parse the KDBX UTC date-time format (`YYYY-MM-DDTHH:MM:SS[.fraction]Z`).
///
/// Returns `None` for formats this minimal parser does not understand; callers
/// keep the raw string in that case.
pub fn xml_datetime_to_unix(value: &str) -> Option<i64> {
    let bytes = value.as_bytes();
    if bytes.len() < 20 || bytes[4] != b'-' || bytes[7] != b'-' || bytes[10] != b'T' {
        return None;
    }
    if bytes[13] != b':' || bytes[16] != b':' {
        return None;
    }
    let year = value.get(0..4)?.parse::<i64>().ok()?;
    let month = value.get(5..7)?.parse::<u32>().ok()?;
    let day = value.get(8..10)?.parse::<u32>().ok()?;
    let hour = value.get(11..13)?.parse::<i64>().ok()?;
    let minute = value.get(14..16)?.parse::<i64>().ok()?;
    let second = value.get(17..19)?.parse::<i64>().ok()?;
    if !value.ends_with('Z') {
        return None;
    }
    if !(1..=12).contains(&month) || !(1..=31).contains(&day) {
        return None;
    }
    let days = days_from_civil(year, month, day);
    Some(days * 86_400 + hour * 3_600 + minute * 60 + second)
}

fn days_from_civil(year: i64, month: u32, day: u32) -> i64 {
    let year = year - i64::from(month <= 2);
    let era = if year >= 0 { year } else { year - 399 } / 400;
    let year_of_era = year - era * 400;
    let month = month as i64;
    let day_of_year = (153 * (month + if month > 2 { -3 } else { 9 }) + 2) / 5 + day as i64 - 1;
    let day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    era * 146_097 + day_of_era - 719_468
}

/// Memory-protection flags from KDBX `<MemoryProtection>`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct MemoryProtection {
    /// Protect title field.
    pub protect_title: bool,
    /// Protect username field.
    pub protect_username: bool,
    /// Protect password field.
    pub protect_password: bool,
    /// Protect URL field.
    pub protect_url: bool,
    /// Protect notes field.
    pub protect_notes: bool,
    /// Unknown children.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub unknown: Vec<RawXml>,
}

impl Default for MemoryProtection {
    fn default() -> Self {
        Self {
            protect_title: false,
            protect_username: false,
            protect_password: true,
            protect_url: false,
            protect_notes: false,
            unknown: Vec::new(),
        }
    }
}

/// Value of a custom-data item.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum CustomDataValue {
    /// Text value.
    String(String),
    /// Binary value (hex-encoded in JSON to keep dumps readable).
    Binary(#[serde(with = "hex_bytes")] Vec<u8>),
}

/// One `<Item>` inside `<CustomData>`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct CustomDataItem {
    /// Item value (absent means empty item).
    #[serde(default)]
    pub value: Option<CustomDataValue>,
    /// `LastModificationTime` text.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_modification: Option<String>,
    /// Unknown children.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub unknown: Vec<RawXml>,
}

/// `<CustomData>` block (stable key order).
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct CustomData {
    /// Items keyed by plugin/name.
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub items: BTreeMap<String, CustomDataItem>,
    /// Unknown children.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub unknown: Vec<RawXml>,
}

/// One custom icon from `<Meta><CustomIcons>`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct CustomIcon {
    /// Icon UUID.
    pub uuid: Uuid,
    /// Raw image bytes.
    #[serde(with = "hex_bytes")]
    pub data: Vec<u8>,
    /// Optional `Name` (KDBX 4.1).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    /// Optional `LastModificationTime` (KDBX 4.1).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_modification: Option<String>,
    /// Unknown children.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub unknown: Vec<RawXml>,
}

/// `<Meta>` block.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct Meta {
    /// Generator string.
    pub generator: String,
    /// Database name.
    pub database_name: String,
    /// Database description.
    pub database_description: String,
    /// Default username for new entries.
    pub default_username: String,
    /// Memory protection flags.
    pub memory_protection: MemoryProtection,
    /// Recycle-bin enabled flag.
    pub recycle_bin_enabled: bool,
    /// Recycle-bin group UUID.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub recycle_bin_uuid: Option<Uuid>,
    /// Recycle-bin changed time.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub recycle_bin_changed: Option<String>,
    /// History max items setting.
    pub history_max_items: i64,
    /// History max size setting.
    pub history_max_size: i64,
    /// Custom data.
    pub custom_data: CustomData,
    /// Custom icons.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub custom_icons: Vec<CustomIcon>,
    /// Unknown children (maintenance history, etc.).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub unknown: Vec<RawXml>,
}

/// One `<String>` entry of an entry.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct StringField {
    /// Field key (`Title`, `UserName`, `Password`, `URL`, `Notes`, custom keys).
    pub key: String,
    /// Plain value (only in memory, inside the encrypted container).
    pub value: String,
    /// `Protected="True"` flag.
    pub protected: bool,
    /// Unknown children/attributes.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub unknown: Vec<RawXml>,
}

/// One `<Binary>` reference on an entry (actual bytes in the inner header).
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct Binary {
    /// Attachment key (file name).
    pub key: String,
    /// Attachment bytes (resolved from the inner header).
    #[serde(with = "hex_bytes")]
    pub value: Vec<u8>,
    /// Protected flag (`Flags` in the inner header).
    pub protected: bool,
    /// Unknown children.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub unknown: Vec<RawXml>,
}

/// Auto-type association.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct Association {
    /// Window title pattern.
    pub window: String,
    /// Keystroke sequence.
    pub keystroke_sequence: String,
    /// Unknown children.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub unknown: Vec<RawXml>,
}

/// `<AutoType>` block.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct AutoType {
    /// Enabled flag.
    pub enabled: bool,
    /// Data transfer obfuscation flag.
    pub data_transfer_obfuscation: bool,
    /// Default sequence.
    pub default_sequence: String,
    /// Associations.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub associations: Vec<Association>,
    /// Unknown children.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub unknown: Vec<RawXml>,
}

/// One `<Entry>` (also used for history entries).
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct Entry {
    /// UUID.
    pub uuid: Uuid,
    /// Numeric icon id.
    pub icon_id: u32,
    /// Foreground color text (KDBX 4.1).
    pub foreground_color: String,
    /// Background color text (KDBX 4.1).
    pub background_color: String,
    /// Override-URL flag/string.
    pub override_url: String,
    /// Tags.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub tags: Vec<String>,
    /// Previous parent group UUID (merge hint).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub previous_parent_group: Option<Uuid>,
    /// Times.
    pub times: Times,
    /// String fields (`Title`/`UserName`/... and custom fields).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub strings: Vec<StringField>,
    /// Attachments.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub binaries: Vec<Binary>,
    /// Auto-type settings.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub auto_type: Option<AutoType>,
    /// History entries, oldest first.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub history: Vec<Entry>,
    /// Custom data.
    pub custom_data: CustomData,
    /// Unknown children.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub unknown: Vec<RawXml>,
}

impl Entry {
    /// Standard field value (e.g. `Title`).
    pub fn field(&self, key: &str) -> Option<&str> {
        self.strings
            .iter()
            .find(|field| field.key == key)
            .map(|field| field.value.as_str())
    }

    /// `true` when the entry has an `otp` or KeePass `TimeOtp-*` field.
    pub fn has_totp(&self) -> bool {
        self.strings.iter().any(|field| {
            field.key == "otp"
                || field.key.starts_with("TimeOtp-")
                || field.key == "TOTP Seed"
                || field.key == "TOTP Settings"
        })
    }
}

/// One `<Group>` (root group included).
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct Group {
    /// UUID.
    pub uuid: Uuid,
    /// Name.
    pub name: String,
    /// Notes.
    pub notes: String,
    /// Numeric icon id.
    pub icon_id: u32,
    /// Times.
    pub times: Times,
    /// UI expand flag.
    pub is_expanded: bool,
    /// Default auto-type enabled.
    pub default_auto_type: bool,
    /// Default auto-type sequence for the group.
    pub default_autotype_sequence: String,
    /// Auto-type enabled.
    pub enable_auto_type: bool,
    /// Searching enabled.
    pub enable_searching: bool,
    /// Last visible top entry.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_top_visible_entry: Option<Uuid>,
    /// Tags.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub tags: Vec<String>,
    /// Previous parent group UUID.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub previous_parent_group: Option<Uuid>,
    /// Custom data.
    pub custom_data: CustomData,
    /// Child groups (order preserved).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub groups: Vec<Group>,
    /// Entries (order preserved).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub entries: Vec<Entry>,
    /// Unknown children.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub unknown: Vec<RawXml>,
}

/// One `<DeletedObject>`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct DeletedObject {
    /// UUID of the deleted object.
    pub uuid: Uuid,
    /// Deletion time text.
    pub deletion_time: String,
}

/// Whole database model (everything decrypted).
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct Database {
    /// Format version as detected by [`crate::kdbx`] (`"3.1"` / `"4.0"` / `"4.1"`).
    pub format_version: String,
    /// `<Meta>`.
    pub meta: Meta,
    /// Root group.
    pub root: Group,
    /// Deleted-object records.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub deleted_objects: Vec<DeletedObject>,
    /// Unknown database-level XML.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub unknown: Vec<RawXml>,
    /// Raw decrypted XML bytes (not serialized; may contain secrets).
    ///
    /// Kept so M1-02 does not lose data that the reader model does not map yet;
    /// canonical dumps expose only its SHA-256, never the bytes.
    #[serde(skip)]
    pub raw_xml: Option<Vec<u8>>,
    /// SHA-256 of [`Database::raw_xml`], included in canonical dumps as
    /// evidence that unknown XML was preserved without leaking secrets.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub raw_xml_sha256: Option<String>,
}

impl Database {
    /// Canonical JSON dump of the full model.
    ///
    /// Keys are emitted in struct order; deterministic collections use
    /// `BTreeMap`/sorted keys. Structural order (groups/entries/strings) is kept
    /// because it is meaningful for KeePass display and round-trip fidelity.
    ///
    /// Secret values (protected strings, `Password`, TOTP seeds) are replaced by
    /// `sha256:<hex>` so a dump can be compared/logged without leaking them.
    pub fn canonical_json(&self) -> Result<String, serde_json::Error> {
        let mut value = serde_json::to_value(self)?;
        redact_value(&mut value);
        serde_json::to_string_pretty(&value)
    }

    /// Canonical JSON value (useful for tests/tools); secrets are redacted.
    pub fn canonical_value(&self) -> Result<serde_json::Value, serde_json::Error> {
        let mut value = serde_json::to_value(self)?;
        redact_value(&mut value);
        Ok(value)
    }
}

fn is_secret_key(key: &str) -> bool {
    key == "Password"
        || key == "otp"
        || key == "TOTP Seed"
        || key.starts_with("TimeOtp-Secret")
        || key.ends_with("_secret")
}

fn redact_value(value: &mut serde_json::Value) {
    match value {
        serde_json::Value::Array(items) => {
            for item in items {
                redact_value(item);
            }
        }
        serde_json::Value::Object(map) => {
            let secret = map
                .get("key")
                .and_then(|key| key.as_str())
                .is_some_and(is_secret_key)
                || map
                    .get("protected")
                    .and_then(|flag| flag.as_bool())
                    .unwrap_or(false);
            if secret {
                if let Some(serde_json::Value::String(text)) = map.get_mut("value") {
                    let digest = sha2::Sha256::digest(text.as_bytes());
                    let mut hex = String::with_capacity(digest.len() * 2);
                    for byte in digest {
                        hex.push_str(&format!("{byte:02x}"));
                    }
                    *text = format!("sha256:{hex}");
                }
                if let Some(serde_json::Value::String(text)) = map.get_mut("password_secret") {
                    let digest = sha2::Sha256::digest(text.as_bytes());
                    let mut hex = String::with_capacity(digest.len() * 2);
                    for byte in digest {
                        hex.push_str(&format!("{byte:02x}"));
                    }
                    *text = format!("sha256:{hex}");
                }
            }
            for child in map.values_mut() {
                redact_value(child);
            }
        }
        _ => {}
    }
}

/// Hex encoding for byte arrays in canonical JSON.
mod hex_bytes {
    use serde::{Deserialize, Deserializer, Serializer};

    pub fn serialize<S: Serializer>(bytes: &[u8], serializer: S) -> Result<S::Ok, S::Error> {
        let mut out = String::with_capacity(bytes.len() * 2);
        for byte in bytes {
            out.push_str(&format!("{byte:02x}"));
        }
        serializer.serialize_str(&out)
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Vec<u8>, D::Error> {
        let value = String::deserialize(deserializer)?;
        if value.len() % 2 != 0 {
            return Err(serde::de::Error::custom("odd hex length"));
        }
        let mut out = Vec::with_capacity(value.len() / 2);
        for index in (0..value.len()).step_by(2) {
            out.push(
                u8::from_str_radix(&value[index..index + 2], 16)
                    .map_err(serde::de::Error::custom)?,
            );
        }
        Ok(out)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn uuid_roundtrip() {
        let uuid: Uuid = "0123456789abcdef0123456789abcdef"
            .parse()
            .expect("valid uuid");
        assert_eq!(uuid.to_string(), "0123456789abcdef0123456789abcdef");
        let json = serde_json::to_string(&uuid).unwrap();
        assert_eq!(json, "\"0123456789abcdef0123456789abcdef\"");
        let back: Uuid = serde_json::from_str(&json).unwrap();
        assert_eq!(back, uuid);
    }

    #[test]
    fn datetime_parser_handles_kdbx_format() {
        // 1970-01-01T00:00:00Z
        assert_eq!(xml_datetime_to_unix("1970-01-01T00:00:00Z"), Some(0));
        // 2026-10-25T10:00:00Z
        assert_eq!(
            xml_datetime_to_unix("2026-10-25T10:00:00Z"),
            Some(1_792_922_400)
        );
        // fractional seconds are ignored
        assert_eq!(
            xml_datetime_to_unix("2026-10-25T10:00:00.1234567Z"),
            Some(1_792_922_400)
        );
        assert_eq!(xml_datetime_to_unix("not a date"), None);
    }

    #[test]
    fn model_json_roundtrip_keeps_unknown_xml() {
        let mut entry = Entry {
            uuid: Uuid::from_bytes([1u8; 16]),
            ..Entry::default()
        };
        entry.strings.push(StringField {
            key: "Password".into(),
            value: "secret".into(),
            protected: true,
            unknown: Vec::new(),
        });
        entry.unknown.push(RawXml::new("PluginData"));
        let db = Database {
            format_version: "4.1".into(),
            root: Group {
                entries: vec![entry],
                ..Group::default()
            },
            ..Database::default()
        };
        let json = serde_json::to_string(&db).unwrap();
        let back: Database = serde_json::from_str(&json).unwrap();
        assert_eq!(back, db);
        assert!(json.contains("PluginData"));
        assert!(json.contains("\"format_version\":\"4.1\""));
        let canonical = db.canonical_json().unwrap();
        assert!(!canonical.contains("\"secret\""));
        assert!(canonical.contains("sha256:"));
    }
}
