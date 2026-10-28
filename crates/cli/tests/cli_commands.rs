// SPDX-License-Identifier: GPL-3.0-or-later
//! M1-07 CLI harness tests: non-interactive JSON commands and machine-readable
//! exit codes.

use std::path::{Path, PathBuf};
use std::process::{Command, Output};

const PASSWORD_VAR: &str = "KEEPASSITY_CLI_TEST_PW";

fn fixture(name: &str) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/fixtures/keepass-rs")
        .join(name)
}

fn run(args: &[&str]) -> Output {
    let mut command = Command::new(env!("CARGO_BIN_EXE_keepassity-cli"));
    command.args(args).env(PASSWORD_VAR, "test");
    command.output().expect("run cli")
}

fn json(output: &Output) -> serde_json::Value {
    let stdout = String::from_utf8_lossy(&output.stdout);
    serde_json::from_str(&stdout)
        .unwrap_or_else(|error| panic!("invalid JSON: {error}\nstdout={stdout}"))
}

#[test]
fn open_prints_summary_json() {
    let db = fixture("test_db_kdbx4_with_totp_entry.kdbx");
    let output = run(&["open", db.to_str().unwrap(), "--password-env", PASSWORD_VAR]);
    assert!(output.status.success(), "{output:?}");
    let value = json(&output);
    assert_eq!(value["status"], "ok");
    assert_eq!(value["entries"], 1);
    assert_eq!(value["totp_entry_count"], serde_json::json!(null));
    assert!(value["raw_xml_sha256"].as_str().unwrap().len() == 64);
}

#[test]
fn list_prints_entries_json() {
    let db = fixture("test_db_kdbx4_with_totp_entry.kdbx");
    let output = run(&["list", db.to_str().unwrap(), "--password-env", PASSWORD_VAR]);
    assert!(output.status.success(), "{output:?}");
    let value = json(&output);
    assert_eq!(value["status"], "ok");
    assert_eq!(value["count"], 1);
    assert_eq!(value["entries"][0]["title"], "this entry has totp");
    assert_eq!(value["entries"][0]["has_totp"], true);
}

#[test]
fn totp_prints_code_without_secret() {
    let db = fixture("test_db_kdbx4_with_totp_entry.kdbx");
    let output = run(&[
        "totp",
        db.to_str().unwrap(),
        "--entry",
        "this entry has totp",
        "--at",
        "59",
        "--password-env",
        PASSWORD_VAR,
    ]);
    assert!(output.status.success(), "{output:?}");
    let value = json(&output);
    assert_eq!(value["status"], "ok");
    assert_eq!(value["digits"], 6);
    assert_eq!(value["code"].as_str().unwrap().len(), 6);
    let text = String::from_utf8_lossy(&output.stdout);
    assert!(!text.contains("JBSWY3DPEHPK3PXP"));
    assert!(!text.to_ascii_lowercase().contains("secret"));
}

#[test]
fn export_model_hashes_and_structure() {
    let db = fixture("test_db_kdbx4_with_payload_aes.kdbx");
    let db = if db.exists() {
        db
    } else {
        fixture("test_db_kdbx4_with_password_aes.kdbx")
    };
    let output = run(&[
        "export-model",
        db.to_str().unwrap(),
        "--sha256",
        "--password",
        "demopass",
    ]);
    assert!(output.status.success(), "{output:?}");
    let hash = String::from_utf8_lossy(&output.stdout).trim().to_string();
    assert_eq!(hash.len(), 64, "hash={hash}");
    assert!(hash.chars().all(|c| c.is_ascii_hexdigit()));

    let output = run(&[
        "export-model",
        db.to_str().unwrap(),
        "--password",
        "demopass",
    ]);
    assert!(output.status.success(), "{output:?}");
    let value = json(&output);
    assert!(value.get("meta").is_some());
    assert!(value.get("root").is_some());
    assert!(value.get("raw_xml_sha256").is_some());
}

#[test]
fn sync_compares_local_files() {
    let db = fixture("test_db_kdbx4_with_password_aes.kdbx");
    let output = run(&[
        "sync",
        db.to_str().unwrap(),
        db.to_str().unwrap(),
        "--password",
        "demopass",
    ]);
    assert!(output.status.success(), "{output:?}");
    let value = json(&output);
    assert_eq!(value["same"], true);
    assert_eq!(value["mode"], "local-compare");
}

#[test]
fn errors_have_machine_readable_codes() {
    let db = fixture("test_db_kdbx4_with_password_aes.kdbx");

    let wrong = run(&[
        "open",
        db.to_str().unwrap(),
        "--password",
        "definitely-wrong",
        "--json-errors",
    ]);
    assert_eq!(wrong.status.code(), Some(3), "{wrong:?}");
    assert!(String::from_utf8_lossy(&wrong.stderr).contains("AUTH"));

    let missing = run(&["open"]);
    assert_eq!(missing.status.code(), Some(2), "{missing:?}");

    let bad_format = run(&[
        "open",
        "does-not-exist.kdbx",
        "--password-env",
        PASSWORD_VAR,
        "--json-errors",
    ]);
    assert_eq!(bad_format.status.code(), Some(4), "{bad_format:?}");
}
