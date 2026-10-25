// SPDX-License-Identifier: GPL-3.0-or-later
//! JSON DTOs shared by FFI, CLI, tests and the WinUI client.
//!
//! Field names follow `V1.0开发方案.md` appendix D and are `snake_case`.
//! Times are Unix seconds (UTC); UUIDs are 32 lowercase hex characters.
//! Secret values live only in fields whose name ends with `_secret`.
//!
//! ## Field table (appendix D)
//!
//! | DTO | Fields |
//! |---|---|
//! | `EntrySummary` | `uuid, title, username, url, group_uuid, has_totp, modified_unix` |
//! | `EntryFull` | summary fields + `password_secret, notes, tags[], custom_fields[{key,value,protected}], icon, times{created,modified,accessed,expires}, history_count, totp?` |
//! | `TotpConfig` | `secret_base32, issuer, account, algorithm, digits, period, encoder, counter?` |
//! | `TotpCode` | `code, remaining, period, digits, algorithm, issuer, account, skew_warning` |
//! | `SyncRemoteMeta` | `etag?, size, modified_unix?, reliability` |
//! | `SyncAction` | `action, reason, degraded` |
//! | `ConflictItem` | `uuid, kind, local_modified_unix?, remote_modified_unix?, fields[]` |
//! | `BackupInfo` | `path, size, created_unix` |
//! | `Error` | `code, message, retryable, detail?` |
//!
//! Note: the internal draft `.agents/contracts/dto.schema.json` is not fully
//! aligned with appendix D yet (it lacks `icon`/`has_totp` in `EntryFull` and
//! has an undefined `fields` property). This module follows appendix D, which
//! the M1-01 acceptance criterion names; M2-05 should reconcile the schema via
//! an ADR before the FFI field-name smoke test is frozen.

use crate::model::{xml_datetime_to_unix, Entry, Group, Times, Uuid};
use serde::{Deserialize, Serialize};

/// Summary row used by `list_entries`/`search`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct EntrySummary {
    /// Entry UUID (32 hex).
    pub uuid: String,
    /// Title.
    pub title: String,
    /// Username (may be empty).
    pub username: String,
    /// URL (may be empty).
    pub url: String,
    /// Parent group UUID (32 hex).
    pub group_uuid: String,
    /// Any TOTP field present.
    pub has_totp: bool,
    /// Last modification as Unix seconds when parseable.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub modified_unix: Option<i64>,
}

impl EntrySummary {
    /// Build a summary from a model entry.
    pub fn from_model(entry: &Entry, group_uuid: Uuid) -> Self {
        Self {
            uuid: entry.uuid.to_hex(),
            title: entry.field("Title").unwrap_or("").to_string(),
            username: entry.field("UserName").unwrap_or("").to_string(),
            url: entry.field("URL").unwrap_or("").to_string(),
            group_uuid: group_uuid.to_hex(),
            has_totp: entry.has_totp(),
            modified_unix: entry.times.modified_unix(),
        }
    }
}

/// Times DTO (Unix seconds).
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct TimesDto {
    /// Creation time.
    pub created_unix: i64,
    /// Last modification time.
    pub modified_unix: i64,
    /// Last access time.
    pub accessed_unix: i64,
    /// Expiry time (`null` when the entry does not expire).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub expires_unix: Option<i64>,
}

impl TimesDto {
    /// Build from the model times block.
    pub fn from_times(times: &Times) -> Self {
        Self {
            created_unix: times.created_unix().unwrap_or(0),
            modified_unix: times.modified_unix().unwrap_or(0),
            accessed_unix: times.accessed_unix().unwrap_or(0),
            expires_unix: times.expires_unix(),
        }
    }
}

/// One custom field.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct CustomField {
    /// Field key.
    pub key: String,
    /// Field value.
    pub value: String,
    /// `Protected="True"` flag.
    pub protected: bool,
}

/// Full entry DTO (`get_entry`).
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct EntryFull {
    /// Entry UUID.
    pub uuid: String,
    /// Parent group UUID.
    pub group_uuid: String,
    /// Title.
    pub title: String,
    /// Username.
    pub username: String,
    /// URL.
    pub url: String,
    /// Any TOTP field present.
    pub has_totp: bool,
    /// Last modification as Unix seconds.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub modified_unix: Option<i64>,
    /// SECRET: password value; never log.
    pub password_secret: String,
    /// Notes.
    pub notes: String,
    /// Tags.
    pub tags: Vec<String>,
    /// Non-standard fields.
    pub custom_fields: Vec<CustomField>,
    /// Numeric icon id.
    pub icon: u32,
    /// Times.
    pub times: TimesDto,
    /// Number of history entries.
    pub history_count: usize,
    /// TOTP configuration (filled in M1-05/M1-06).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub totp: Option<TotpConfig>,
}

impl EntryFull {
    /// Build the full DTO from a model entry (TOTP config is filled later).
    pub fn from_model(entry: &Entry, group_uuid: Uuid) -> Self {
        let standard = ["Title", "UserName", "Password", "URL", "Notes"];
        let custom_fields = entry
            .strings
            .iter()
            .filter(|field| !standard.contains(&field.key.as_str()))
            .map(|field| CustomField {
                key: field.key.clone(),
                value: field.value.clone(),
                protected: field.protected,
            })
            .collect();
        Self {
            uuid: entry.uuid.to_hex(),
            group_uuid: group_uuid.to_hex(),
            title: entry.field("Title").unwrap_or("").to_string(),
            username: entry.field("UserName").unwrap_or("").to_string(),
            url: entry.field("URL").unwrap_or("").to_string(),
            has_totp: entry.has_totp(),
            modified_unix: entry.times.modified_unix(),
            password_secret: entry.field("Password").unwrap_or("").to_string(),
            notes: entry.field("Notes").unwrap_or("").to_string(),
            tags: entry.tags.clone(),
            custom_fields,
            icon: entry.icon_id,
            times: TimesDto::from_times(&entry.times),
            history_count: entry.history.len(),
            totp: None,
        }
    }
}

/// TOTP hash algorithm.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "UPPERCASE")]
pub enum TotpAlgorithm {
    /// HMAC-SHA1.
    Sha1,
    /// HMAC-SHA256.
    Sha256,
    /// HMAC-SHA512.
    Sha512,
}

/// TOTP encoder flavor.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum TotpEncoder {
    /// Standard TOTP.
    Totp,
    /// Steam alphabet.
    Steam,
}

/// TOTP configuration DTO.
#[derive(Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TotpConfig {
    /// SECRET: Base32 seed; never log.
    pub secret_base32: String,
    /// Issuer (optional).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub issuer: Option<String>,
    /// Account name (optional).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub account: Option<String>,
    /// Hash algorithm.
    pub algorithm: TotpAlgorithm,
    /// Code digits (6 or 8).
    pub digits: u32,
    /// Period in seconds.
    pub period: u32,
    /// Encoder flavor.
    pub encoder: TotpEncoder,
    /// Steam/counter value (optional).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub counter: Option<u64>,
}

impl std::fmt::Debug for TotpConfig {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("TotpConfig")
            .field("secret_base32", &"<redacted>")
            .field("issuer", &self.issuer)
            .field("account", &self.account)
            .field("algorithm", &self.algorithm)
            .field("digits", &self.digits)
            .field("period", &self.period)
            .field("encoder", &self.encoder)
            .field("counter", &self.counter)
            .finish()
    }
}

/// TOTP code DTO.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct TotpCode {
    /// Current code.
    pub code: String,
    /// Seconds until the next code.
    pub remaining: u32,
    /// Period in seconds.
    pub period: u32,
    /// Digits.
    pub digits: u32,
    /// Algorithm name (e.g. `SHA1`).
    pub algorithm: String,
    /// Issuer.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub issuer: Option<String>,
    /// Account.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub account: Option<String>,
    /// Large clock skew warning.
    pub skew_warning: bool,
}

/// Remote metadata reliability.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Reliability {
    /// Full ETag/metadata available.
    Full,
    /// Degraded (mtime/size/hash only).
    Degraded,
}

/// Remote metadata used by `sync_decide`.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct SyncRemoteMeta {
    /// ETag, if the server provides one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub etag: Option<String>,
    /// Remote byte size.
    pub size: u64,
    /// Remote modified time.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub modified_unix: Option<i64>,
    /// Reliability of the metadata.
    pub reliability: Reliability,
}

/// Sync decision action.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum SyncActionKind {
    /// No action.
    UpToDate,
    /// Upload local changes.
    Upload,
    /// Download remote changes.
    Download,
    /// Download and merge.
    DownloadAndMerge,
    /// Conflict: needs a three-way choice.
    Conflict,
    /// Offline (defer).
    Offline,
}

/// `sync_decide` result.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct SyncAction {
    /// Action to take.
    pub action: SyncActionKind,
    /// Human-readable reason.
    pub reason: String,
    /// Degraded (no ETag) decision.
    pub degraded: bool,
}

/// Kind of conflict item.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ConflictKind {
    /// Entry conflict.
    Entry,
    /// Group conflict.
    Group,
}

/// One conflict item.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct ConflictItem {
    /// UUID of the conflicting object.
    pub uuid: String,
    /// Entry/group.
    pub kind: ConflictKind,
    /// Local modification time.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub local_modified_unix: Option<i64>,
    /// Remote modification time.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub remote_modified_unix: Option<i64>,
    /// Conflicting field names.
    pub fields: Vec<String>,
}

impl From<&crate::merge::MergeConflict> for ConflictItem {
    fn from(conflict: &crate::merge::MergeConflict) -> Self {
        Self {
            uuid: conflict.uuid.to_hex(),
            kind: match conflict.kind {
                crate::merge::ConflictKind::Entry => ConflictKind::Entry,
                crate::merge::ConflictKind::Group => ConflictKind::Group,
            },
            local_modified_unix: xml_datetime_to_unix(&conflict.local_modified),
            remote_modified_unix: xml_datetime_to_unix(&conflict.remote_modified),
            fields: conflict.fields.clone(),
        }
    }
}

/// Backup record.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct BackupInfo {
    /// Backup file path (platform-provided).
    pub path: String,
    /// Size in bytes.
    pub size: u64,
    /// Creation time.
    pub created_unix: i64,
}

/// Error code enum (FFI `code`).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum ErrorCode {
    /// Wrong password/key.
    BadPassword,
    /// HMAC/format corruption.
    CorruptDatabase,
    /// Unsupported KDBX version/feature.
    UnsupportedFormat,
    /// Local file IO failure.
    IoError,
    /// Network failure.
    NetworkError,
    /// Sync conflict.
    Conflict,
    /// Object not found.
    NotFound,
    /// Bad argument.
    InvalidArgument,
    /// Database locked.
    Locked,
    /// API version mismatch.
    VersionMismatch,
    /// Internal inconsistency.
    Internal,
}

/// Error DTO returned by FFI/CLI.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct DtoError {
    /// Error code.
    pub code: ErrorCode,
    /// Human-readable message (redacted).
    pub message: String,
    /// Whether a retry may help (sync).
    pub retryable: bool,
    /// Optional diagnostic detail (redacted).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub detail: Option<String>,
}

/// Build the group UUID string for DTOs.
pub fn group_uuid_string(group: &Group) -> String {
    group.uuid.to_hex()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{StringField, Times};

    fn sample_entry() -> Entry {
        let mut entry = Entry {
            uuid: "00112233445566778899aabbccddeeff".parse().unwrap(),
            icon_id: 7,
            tags: vec!["tag".to_string()],
            times: Times {
                creation: "2026-10-25T10:00:00Z".into(),
                last_modification: "2026-10-25T10:00:00Z".into(),
                last_access: "2026-10-25T10:00:00Z".into(),
                ..Times::default()
            },
            ..Entry::default()
        };
        entry.strings = vec![
            StringField {
                key: "Title".into(),
                value: "Example".into(),
                protected: false,
                unknown: Vec::new(),
            },
            StringField {
                key: "UserName".into(),
                value: "user".into(),
                protected: false,
                unknown: Vec::new(),
            },
            StringField {
                key: "Password".into(),
                value: "secret".into(),
                protected: true,
                unknown: Vec::new(),
            },
            StringField {
                key: "Custom".into(),
                value: "value".into(),
                protected: false,
                unknown: Vec::new(),
            },
        ];
        entry
    }

    fn keys<T: Serialize>(value: &T) -> Vec<String> {
        let json = serde_json::to_value(value).unwrap();
        let mut keys: Vec<String> = json.as_object().expect("object").keys().cloned().collect();
        keys.sort();
        keys
    }

    #[test]
    fn entry_summary_has_appendix_d_fields() {
        let entry = sample_entry();
        let dto = EntrySummary::from_model(&entry, Uuid::default());
        assert_eq!(
            keys(&dto),
            vec![
                "group_uuid",
                "has_totp",
                "modified_unix",
                "title",
                "url",
                "username",
                "uuid",
            ]
        );
        assert_eq!(dto.title, "Example");
        assert_eq!(dto.uuid, "00112233445566778899aabbccddeeff");
        assert_eq!(dto.modified_unix, Some(1_792_922_400));
    }

    #[test]
    fn entry_full_has_appendix_d_fields() {
        let entry = sample_entry();
        let dto = EntryFull::from_model(&entry, Uuid::default());
        let keys = keys(&dto);
        for expected in [
            "uuid",
            "group_uuid",
            "title",
            "username",
            "url",
            "has_totp",
            "modified_unix",
            "password_secret",
            "notes",
            "tags",
            "custom_fields",
            "icon",
            "times",
            "history_count",
        ] {
            assert!(keys.iter().any(|key| key == expected), "missing {expected}");
        }
        assert!(
            !keys.iter().any(|key| key == "totp"),
            "totp is optional and must be omitted when absent"
        );
        assert_eq!(dto.password_secret, "secret");
        assert_eq!(dto.custom_fields.len(), 1);
        assert_eq!(dto.custom_fields[0].key, "Custom");
    }

    #[test]
    fn times_dto_uses_unix_seconds() {
        let dto = TimesDto::from_times(&sample_entry().times);
        assert_eq!(dto.created_unix, 1_792_922_400);
        assert_eq!(dto.expires_unix, None);
    }

    #[test]
    fn error_dto_serializes_code_names() {
        let dto = DtoError {
            code: ErrorCode::BadPassword,
            message: "wrong password".into(),
            retryable: false,
            detail: None,
        };
        let json = serde_json::to_string(&dto).unwrap();
        assert!(json.contains("\"BadPassword\""));
        assert!(!json.contains("detail"));
    }

    #[test]
    fn conflict_and_sync_dtos_serialize() {
        let action = SyncAction {
            action: SyncActionKind::Conflict,
            reason: "both changed".into(),
            degraded: false,
        };
        let json = serde_json::to_string(&action).unwrap();
        assert!(json.contains("\"Conflict\""));

        let item = ConflictItem {
            uuid: "00112233445566778899aabbccddeeff".into(),
            kind: ConflictKind::Entry,
            local_modified_unix: Some(1),
            remote_modified_unix: None,
            fields: vec!["Title".into()],
        };
        let json = serde_json::to_string(&item).unwrap();
        assert!(json.contains("\"entry\""));
        assert!(!json.contains("remote_modified_unix"));
    }
}
