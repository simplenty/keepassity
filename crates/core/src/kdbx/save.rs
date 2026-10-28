// SPDX-License-Identifier: GPL-3.0-or-later
//! Save protocol: verified, backed-up, atomic replacement with fault injection.
//!
//! Core stays IO-free: the host (CLI/app) provides a [`SaveIo`] implementation.
//! The protocol is deliberately ordered so that **any failure before `replace`
//! leaves the original database unchanged**:
//!
//! 1. verify the new bytes by re-reading them with the independent reader and
//!    comparing the normalised model with the original (fresh salts/IV do not
//!    matter; `Meta/Generator` is normalised);
//! 2. write a temporary file next to the database;
//! 3. fsync the temporary file;
//! 4. back up the original through the backup hook (backup failure = save
//!    failure, original untouched);
//! 5. atomically replace the original with the temporary file;
//! 6. fsync the parent directory (durability; a failure here is reported as a
//!    warning in [`SaveReport`], because replacement already happened).
//!
//! Temporary files MUST be named `<database>.keepassity-tmp-<nonce>`. If a
//! crash leaves one behind, [`is_residual_temp_path`] / [`find_residual_temps`]
//! let the next start-up identify it without guessing.
//!
//! The backup hook is [`SaveIo::backup`]; the host owns the backup location,
//! retention and encryption policy. It is called **before** replacement and
//! its failure aborts the save.

use crate::kdbx::reader;
use crate::model::Database;
use std::fmt;

/// Marker inserted between the database path and the random nonce.
pub const TEMP_INFIX: &str = ".keepassity-tmp-";

/// Save failure. Messages never contain secret values or database bytes.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SaveError {
    /// Empty database path.
    InvalidPath,
    /// Input/output verification failed before any write.
    Verify(String),
    /// Temporary file could not be written.
    TempWrite(String),
    /// Temporary file could not be fsynced.
    TempSync(String),
    /// Backup hook failed; save must abort.
    Backup(String),
    /// Atomic replacement failed.
    Replace(String),
    /// Temporary file cleanup failed after an earlier error.
    Cleanup(String),
    /// Parent directory fsync failed after a successful replace.
    ParentSync(String),
    /// File existence check failed.
    Exists(String),
}

impl fmt::Display for SaveError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidPath => write!(f, "invalid database path"),
            Self::Verify(message) => write!(f, "save verification failed: {message}"),
            Self::TempWrite(message) => write!(f, "temporary file write failed: {message}"),
            Self::TempSync(message) => write!(f, "temporary file fsync failed: {message}"),
            Self::Backup(message) => write!(f, "backup failed; save aborted: {message}"),
            Self::Replace(message) => write!(f, "atomic replace failed: {message}"),
            Self::Cleanup(message) => write!(f, "temporary file cleanup failed: {message}"),
            Self::ParentSync(message) => write!(f, "directory fsync failed: {message}"),
            Self::Exists(message) => write!(f, "file check failed: {message}"),
        }
    }
}

impl std::error::Error for SaveError {}

/// Host-provided IO operations for the save protocol.
///
/// The host is responsible for real file IO, permissions, durable fsync and the
/// backup destination. Core decides the order and aborts on any failure.
pub trait SaveIo {
    /// Whether `path` currently exists.
    fn exists(&mut self, path: &str) -> Result<bool, SaveError>;
    /// Write `data` to a new temporary path and return that path. It MUST be
    /// `<path>.keepassity-tmp-<nonce>` and must not overwrite the original.
    fn write_temp(&mut self, path: &str, data: &[u8]) -> Result<String, SaveError>;
    /// Flush the temporary file to stable storage.
    fn sync_temp(&mut self, temp: &str) -> Result<(), SaveError>;
    /// Persist a backup of the original bytes before replacement.
    fn backup(&mut self, path: &str, original: &[u8]) -> Result<(), SaveError>;
    /// Atomically rename `temp` over `path`.
    fn replace(&mut self, path: &str, temp: &str) -> Result<(), SaveError>;
    /// Remove a temporary file (cleanup path; may be called after failures).
    fn remove(&mut self, temp: &str) -> Result<(), SaveError>;
    /// Flush the parent directory after replacement.
    fn sync_parent(&mut self, path: &str) -> Result<(), SaveError>;
}

/// Result of a successful save.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SaveReport {
    /// Number of bytes written to the database path.
    pub bytes_written: usize,
    /// Whether the backup hook was invoked (original existed).
    pub backed_up: bool,
    /// Parent-directory fsync failed after replacement (data is already new).
    pub parent_sync_warning: Option<String>,
}

/// Verify that `new_bytes` re-reads (independently) to the same normalised
/// database as `old_bytes`. Called before any file is written.
pub fn verify_rewritten(
    old_bytes: &[u8],
    new_bytes: &[u8],
    password: &str,
) -> Result<(), SaveError> {
    let mut old = reader::parse(old_bytes, password)
        .map_err(|error| SaveError::Verify(format!("original unreadable: {error}")))?;
    let mut new = reader::parse(new_bytes, password)
        .map_err(|error| SaveError::Verify(format!("written bytes unreadable: {error}")))?;
    normalise(&mut old);
    normalise(&mut new);
    if old != new {
        return Err(SaveError::Verify(
            "written database differs from the original".to_string(),
        ));
    }
    Ok(())
}

fn normalise(db: &mut Database) {
    // Fresh salts/IV are expected; raw XML and generator are not data.
    db.raw_xml = None;
    db.raw_xml_sha256 = None;
    db.meta.generator.clear();
}

/// Run the full safe-save protocol. `old_bytes` is the current on-disk content
/// (read by the host); `new_bytes` is the encrypted output to install.
pub fn save_atomic(
    io: &mut dyn SaveIo,
    path: &str,
    old_bytes: &[u8],
    new_bytes: &[u8],
    password: &str,
) -> Result<SaveReport, SaveError> {
    if path.trim().is_empty() {
        return Err(SaveError::InvalidPath);
    }

    // 1. Verify before touching the filesystem.
    verify_rewritten(old_bytes, new_bytes, password)?;

    // 2. Temporary file.
    let temp = io.write_temp(path, new_bytes)?;
    if !is_residual_temp_path(&temp, path) {
        let cleanup = io.remove(&temp);
        return Err(SaveError::TempWrite(match cleanup {
            Ok(()) => "host returned a temp path outside the protocol naming scheme".to_string(),
            Err(error) => format!("host returned a bad temp path; cleanup also failed: {error}"),
        }));
    }
    if let Err(error) = io.sync_temp(&temp) {
        return Err(with_cleanup(
            io,
            &temp,
            SaveError::TempSync(error.to_string()),
        ));
    }

    // 3. Backup hook: its failure aborts the save.
    let existed = io
        .exists(path)
        .map_err(|error| with_cleanup(io, &temp, SaveError::Exists(error.to_string())))?;
    let mut backed_up = false;
    if existed {
        if let Err(error) = io.backup(path, old_bytes) {
            return Err(with_cleanup(
                io,
                &temp,
                SaveError::Backup(error.to_string()),
            ));
        }
        backed_up = true;
    }

    // 4. Atomic replace is the commit point.
    if let Err(error) = io.replace(path, &temp) {
        return Err(with_cleanup(
            io,
            &temp,
            SaveError::Replace(error.to_string()),
        ));
    }

    // 5. Durability of the rename. The new file is already in place, so this
    //    is reported as a warning instead of pretending the save failed.
    let parent_sync_warning = io.sync_parent(path).err().map(|error| error.to_string());
    Ok(SaveReport {
        bytes_written: new_bytes.len(),
        backed_up,
        parent_sync_warning,
    })
}

fn with_cleanup(io: &mut dyn SaveIo, temp: &str, primary: SaveError) -> SaveError {
    match io.remove(temp) {
        Ok(()) => primary,
        Err(cleanup) => SaveError::Cleanup(format!("{primary}; temp={temp}; cleanup: {cleanup}")),
    }
}

/// Whether `temp` follows the `\<database>.keepassity-tmp-\<nonce>` convention.
pub fn is_residual_temp_path(temp: &str, database_path: &str) -> bool {
    temp != database_path
        && temp
            .strip_prefix(database_path)
            .is_some_and(|suffix| suffix.starts_with(TEMP_INFIX) && suffix.len() > TEMP_INFIX.len())
}

/// Filter candidate paths down to residual temp files for `database_path`.
pub fn find_residual_temps<I: IntoIterator<Item = String>>(
    database_path: &str,
    candidates: I,
) -> Vec<String> {
    let mut found: Vec<String> = candidates
        .into_iter()
        .filter(|candidate| is_residual_temp_path(candidate, database_path))
        .collect();
    found.sort();
    found
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn temp_naming_is_recognised() {
        assert!(is_residual_temp_path(
            "db.kdbx.keepassity-tmp-abc123",
            "db.kdbx"
        ));
        assert!(!is_residual_temp_path("db.kdbx", "db.kdbx"));
        assert!(!is_residual_temp_path("db.kdbx.keepassity-tmp-", "db.kdbx"));
        assert!(!is_residual_temp_path(
            "other.keepassity-tmp-abc",
            "db.kdbx"
        ));
        assert_eq!(
            find_residual_temps(
                "db.kdbx",
                vec![
                    "db.kdbx".to_string(),
                    "db.kdbx.keepassity-tmp-b".to_string(),
                    "db.kdbx.keepassity-tmp-a".to_string(),
                ]
            ),
            vec![
                "db.kdbx.keepassity-tmp-a".to_string(),
                "db.kdbx.keepassity-tmp-b".to_string()
            ]
        );
    }
}
