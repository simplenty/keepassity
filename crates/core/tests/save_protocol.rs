// SPDX-License-Identifier: GPL-3.0-or-later
//! Fault-injection tests for the M1-04 save protocol.
//!
//! Five failure classes (backup, temp write, temp fsync, verification, atomic
//! replace) must abort and leave the original database unchanged. A cleanup
//! failure must leave an identifiable residual temp file.

use keepassity_core::kdbx::save::{
    find_residual_temps, save_atomic, SaveError, SaveIo, SaveReport,
};
use std::collections::BTreeMap;
use std::path::Path;

#[derive(Default)]
struct FakeIo {
    files: BTreeMap<String, Vec<u8>>,
    temp_data: Option<Vec<u8>>,
    temp_path: Option<String>,
    backup: Option<(String, Vec<u8>)>,
    fail_write_temp: bool,
    fail_sync_temp: bool,
    fail_backup: bool,
    fail_replace: bool,
    fail_remove: bool,
    bad_temp_name: bool,
}

impl FakeIo {
    fn with_file(path: &str, bytes: &[u8]) -> Self {
        let mut io = Self::default();
        io.files.insert(path.to_string(), bytes.to_vec());
        io
    }
}

impl SaveIo for FakeIo {
    fn exists(&mut self, path: &str) -> Result<bool, SaveError> {
        Ok(self.files.contains_key(path))
    }

    fn write_temp(&mut self, path: &str, data: &[u8]) -> Result<String, SaveError> {
        if self.fail_write_temp {
            return Err(SaveError::TempWrite("injected write failure".into()));
        }
        let temp = if self.bad_temp_name {
            format!("{path}.tmp-bad")
        } else {
            format!("{path}.keepassity-tmp-injected")
        };
        self.temp_data = Some(data.to_vec());
        self.temp_path = Some(temp.clone());
        Ok(temp)
    }

    fn sync_temp(&mut self, _temp: &str) -> Result<(), SaveError> {
        if self.fail_sync_temp {
            Err(SaveError::TempSync("injected fsync failure".into()))
        } else {
            Ok(())
        }
    }

    fn backup(&mut self, path: &str, original: &[u8]) -> Result<(), SaveError> {
        if self.fail_backup {
            return Err(SaveError::Backup("injected backup failure".into()));
        }
        self.backup = Some((path.to_string(), original.to_vec()));
        Ok(())
    }

    fn replace(&mut self, path: &str, _temp: &str) -> Result<(), SaveError> {
        if self.fail_replace {
            return Err(SaveError::Replace("injected rename failure".into()));
        }
        let data = self.temp_data.take().expect("temp data");
        self.files.insert(path.to_string(), data);
        self.temp_path = None;
        Ok(())
    }

    fn remove(&mut self, _temp: &str) -> Result<(), SaveError> {
        if self.fail_remove {
            return Err(SaveError::Cleanup("injected cleanup failure".into()));
        }
        self.temp_path = None;
        self.temp_data = None;
        Ok(())
    }

    fn sync_parent(&mut self, _path: &str) -> Result<(), SaveError> {
        Ok(())
    }
}

fn fixture() -> Vec<u8> {
    let path = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/fixtures/keepass-rs/test_db_kdbx4_with_password_aes.kdbx");
    std::fs::read(path).expect("fixture")
}

const DB: &str = "C:/data/test.kdbx";

fn run(io: &mut FakeIo, new_bytes: &[u8]) -> Result<SaveReport, SaveError> {
    let old = fixture();
    save_atomic(io, DB, &old, new_bytes, "demopass")
}

#[test]
fn success_backs_up_and_replaces() {
    let old = fixture();
    let mut io = FakeIo::with_file(DB, &old);
    let report = run(&mut io, &old).expect("save should succeed");
    assert!(report.backed_up);
    assert_eq!(io.files[DB], old);
    assert_eq!(io.backup.as_ref().expect("backup").1, old);
    assert!(io.temp_path.is_none());
}

#[test]
fn writer_output_passes_verification_and_is_installed() {
    let old = fixture();
    let new = keepassity_core::kdbx::writer::reseal(&old, "demopass").expect("reseal fixture");
    let mut io = FakeIo::with_file(DB, &old);
    let report = save_atomic(&mut io, DB, &old, &new, "demopass").expect("verified reseal save");
    assert!(report.backed_up);
    assert_eq!(io.files[DB], new);
    assert_eq!(io.backup.as_ref().expect("backup").1, old);
}

#[test]
fn new_database_skips_backup() {
    let old = fixture();
    let mut io = FakeIo::default();
    let report = run(&mut io, &old).expect("save should succeed");
    assert!(!report.backed_up);
    assert_eq!(io.files[DB], old);
}

#[test]
fn backup_failure_aborts_and_keeps_original() {
    let old = fixture();
    let mut io = FakeIo::with_file(DB, &old);
    io.fail_backup = true;
    let error = run(&mut io, &old).expect_err("backup failure must abort");
    assert!(matches!(error, SaveError::Backup(_)), "{error}");
    assert_eq!(io.files[DB], old);
    assert!(io.temp_path.is_none(), "temp must be cleaned up");
}

#[test]
fn temp_write_failure_keeps_original() {
    let old = fixture();
    let mut io = FakeIo::with_file(DB, &old);
    io.fail_write_temp = true;
    let error = run(&mut io, &old).expect_err("temp write failure must abort");
    assert!(matches!(error, SaveError::TempWrite(_)), "{error}");
    assert_eq!(io.files[DB], old);
}

#[test]
fn temp_sync_failure_keeps_original() {
    let old = fixture();
    let mut io = FakeIo::with_file(DB, &old);
    io.fail_sync_temp = true;
    let error = run(&mut io, &old).expect_err("temp sync failure must abort");
    assert!(matches!(error, SaveError::TempSync(_)), "{error}");
    assert_eq!(io.files[DB], old);
    assert!(io.temp_path.is_none());
}

#[test]
fn verification_failure_happens_before_any_write() {
    let old = fixture();
    let mut io = FakeIo::with_file(DB, &old);
    let error = run(&mut io, b"not a kdbx file").expect_err("verification must fail");
    assert!(matches!(error, SaveError::Verify(_)), "{error}");
    assert_eq!(io.files[DB], old);
    assert!(io.temp_path.is_none());
    assert!(io.backup.is_none());
}

#[test]
fn replace_failure_keeps_original_and_cleans_temp() {
    let old = fixture();
    let mut io = FakeIo::with_file(DB, &old);
    io.fail_replace = true;
    let error = run(&mut io, &old).expect_err("replace failure must abort");
    assert!(matches!(error, SaveError::Replace(_)), "{error}");
    assert_eq!(io.files[DB], old);
    assert!(io.temp_path.is_none());
}

#[test]
fn cleanup_failure_leaves_identifiable_residual_temp() {
    let old = fixture();
    let mut io = FakeIo::with_file(DB, &old);
    io.fail_replace = true;
    io.fail_remove = true;
    let error = run(&mut io, &old).expect_err("replace failure must abort");
    assert!(matches!(error, SaveError::Cleanup(_)), "{error}");
    assert_eq!(io.files[DB], old);
    let temp = io.temp_path.clone().expect("residual temp");
    let candidates = io
        .files
        .keys()
        .cloned()
        .chain(std::iter::once(temp.clone()));
    let found = find_residual_temps(DB, candidates);
    assert_eq!(found, vec![temp]);
}

#[test]
fn bad_host_temp_name_is_rejected_and_cleaned() {
    let old = fixture();
    let mut io = FakeIo::with_file(DB, &old);
    io.bad_temp_name = true;
    let error = run(&mut io, &old).expect_err("bad temp naming must be rejected");
    assert!(matches!(error, SaveError::TempWrite(_)), "{error}");
    assert_eq!(io.files[DB], old);
    assert!(io.temp_path.is_none());
}
