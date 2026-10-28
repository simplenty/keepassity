// SPDX-License-Identifier: GPL-3.0-or-later
//! Conflict-copy naming and three-way conflict plans.
//!
//! Core never writes files: the platform layer supplies the local-time
//! timestamp and performs the copy. These helpers enforce the hard rule that
//! **every conflict path writes a conflict copy first** — there is no silent
//! overwrite branch.

use std::path::Path;

/// Conflict-copy naming error.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ConflictCopyError {
    /// Timestamp must be a safe `yyyy-MM-dd HH-mm-ss`-style string.
    InvalidTimestamp,
    /// Original path has no file name.
    MissingFileName,
}

/// The three user choices from the conflict dialog.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ConflictChoice {
    /// Keep local: copy remote to a conflict file, then upload local.
    KeepLocal,
    /// Keep remote: copy local to a conflict file, then adopt remote.
    KeepRemote,
    /// Keep both: merge, upload, and keep a conflict copy.
    KeepBoth,
}

impl ConflictChoice {
    /// Machine-readable name (matches future DTO values).
    pub fn as_str(self) -> &'static str {
        match self {
            Self::KeepLocal => "keep_local",
            Self::KeepRemote => "keep_remote",
            Self::KeepBoth => "keep_both",
        }
    }
}

/// Required actions for a conflict choice. Every plan writes a copy first.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ConflictPlan {
    /// Always true; there is no silent-overwrite path.
    pub write_conflict_copy: bool,
    /// Upload local content after the copy (KeepLocal/KeepBoth).
    pub upload_local: bool,
    /// Adopt remote content after the copy (KeepRemote).
    pub adopt_remote: bool,
    /// Merge local+remote and upload (KeepBoth).
    pub merge: bool,
}

impl ConflictChoice {
    /// Plan the safe ordered actions. `write_conflict_copy` is always true.
    pub fn plan(self) -> ConflictPlan {
        match self {
            Self::KeepLocal => ConflictPlan {
                write_conflict_copy: true,
                upload_local: true,
                adopt_remote: false,
                merge: false,
            },
            Self::KeepRemote => ConflictPlan {
                write_conflict_copy: true,
                upload_local: false,
                adopt_remote: true,
                merge: false,
            },
            Self::KeepBoth => ConflictPlan {
                write_conflict_copy: true,
                upload_local: true,
                adopt_remote: false,
                merge: true,
            },
        }
    }
}

/// Validate a platform-provided local timestamp used in a conflict copy name.
///
/// Accepts digits, `-`, space, `:`, `_`, `.` and `T`; rejects empty strings,
/// path separators and `..`.
pub fn valid_conflict_timestamp(timestamp: &str) -> bool {
    !timestamp.is_empty()
        && timestamp.len() <= 64
        && !timestamp.contains("..")
        && timestamp
            .chars()
            .all(|ch| ch.is_ascii_digit() || matches!(ch, '-' | ' ' | ':' | '_' | '.' | 'T'))
}

/// Build `name (conflict yyyy-MM-dd HH-mm-ss).kdbx` for `original`.
///
/// The platform layer formats the timestamp in local time (the plan's copy
/// name uses local time); core only validates and splices names.
pub fn conflict_copy_name(original: &str, timestamp: &str) -> Result<String, ConflictCopyError> {
    if !valid_conflict_timestamp(timestamp) {
        return Err(ConflictCopyError::InvalidTimestamp);
    }
    let path = Path::new(original);
    let stem = path
        .file_stem()
        .and_then(|value| value.to_str())
        .ok_or(ConflictCopyError::MissingFileName)?;
    let extension = path
        .extension()
        .and_then(|value| value.to_str())
        .unwrap_or("kdbx");
    let name = format!("{stem} (conflict {timestamp}).{extension}");
    let parent = path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty());
    Ok(match parent {
        Some(parent) => parent.join(name).to_string_lossy().into_owned(),
        None => name,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn conflict_copy_name_matches_protocol() {
        assert_eq!(
            conflict_copy_name("C:\\data\\My Passwords.kdbx", "2026-10-26 00-30-00").unwrap(),
            "C:\\data\\My Passwords (conflict 2026-10-26 00-30-00).kdbx"
        );
        assert_eq!(
            conflict_copy_name("db.kdbx", "2026-10-26 00-30-00").unwrap(),
            "db (conflict 2026-10-26 00-30-00).kdbx"
        );
    }

    #[test]
    fn conflict_copy_name_rejects_unsafe_timestamps() {
        assert_eq!(
            conflict_copy_name("db.kdbx", "../evil"),
            Err(ConflictCopyError::InvalidTimestamp)
        );
        assert_eq!(
            conflict_copy_name("db.kdbx", "2026/10/26"),
            Err(ConflictCopyError::InvalidTimestamp)
        );
        assert_eq!(
            conflict_copy_name("db.kdbx", ""),
            Err(ConflictCopyError::InvalidTimestamp)
        );
    }

    #[test]
    fn every_conflict_choice_writes_a_copy() {
        for choice in [
            ConflictChoice::KeepLocal,
            ConflictChoice::KeepRemote,
            ConflictChoice::KeepBoth,
        ] {
            let plan = choice.plan();
            assert!(
                plan.write_conflict_copy,
                "{} must copy first",
                choice.as_str()
            );
            assert!(
                plan.upload_local || plan.adopt_remote || plan.merge,
                "{} must do something after the copy",
                choice.as_str()
            );
        }
    }

    #[test]
    fn keep_local_and_keep_both_upload_keep_remote_adopts() {
        assert!(ConflictChoice::KeepLocal.plan().upload_local);
        assert!(!ConflictChoice::KeepLocal.plan().adopt_remote);
        assert!(ConflictChoice::KeepBoth.plan().merge);
        assert!(ConflictChoice::KeepBoth.plan().upload_local);
        assert!(ConflictChoice::KeepRemote.plan().adopt_remote);
        assert!(!ConflictChoice::KeepRemote.plan().upload_local);
    }
}
