// SPDX-License-Identifier: GPL-3.0-or-later
//! KDBX reader coverage: parse every supported public fixture into the core
//! model and prove that the canonical dump is stable and that raw XML evidence
//! is retained. Keyfile fixtures are out of v1.0 scope; broken fixtures must be
//! rejected.

use std::path::{Path, PathBuf};

fn fixture_dir() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/fixtures/keepass-rs")
        .canonicalize()
        .expect("fixture directory")
}

fn password_for(name: &str) -> &'static str {
    if name.contains("totp") {
        "test"
    } else if name.contains("chacha20_protected_fields") {
        "password"
    } else {
        "demopass"
    }
}

#[test]
fn parses_supported_fixtures_and_keeps_raw_xml_evidence() {
    let mut parsed = 0usize;
    let mut broken = 0usize;
    for entry in std::fs::read_dir(fixture_dir()).expect("read fixtures") {
        let path = entry.expect("dir entry").path();
        if path.extension().and_then(|extension| extension.to_str()) != Some("kdbx") {
            continue;
        }
        let name = path.file_name().unwrap().to_string_lossy().to_string();
        if name.contains("keyfile") || name.contains("challenge_response") {
            continue;
        }
        let bytes = std::fs::read(&path).expect("read fixture");
        let result = keepassity_core::kdbx::reader::parse(&bytes, password_for(&name));

        if name.starts_with("broken_") {
            assert!(result.is_err(), "broken fixture {name} must be rejected");
            broken += 1;
            continue;
        }

        let db = result.unwrap_or_else(|e| panic!("parse {name}: {e}"));
        assert!(
            matches!(db.format_version.as_str(), "3.1" | "4.0" | "4.1"),
            "{name}: unexpected version {}",
            db.format_version
        );
        let hash = db.raw_xml_sha256.as_deref().expect("raw XML hash");
        assert_eq!(hash.len(), 64, "{name}: bad raw XML hash");
        assert!(db.raw_xml.as_ref().is_some_and(|xml| !xml.is_empty()));

        let canonical = db.canonical_json().expect("canonical dump");
        assert!(
            canonical.contains("raw_xml_sha256"),
            "{name}: canonical dump omits raw XML evidence"
        );
        assert!(
            !canonical.contains("raw_xml\""),
            "{name}: raw XML must not be dumped"
        );
        assert!(
            !canonical.contains("orisitart"),
            "{name}: canonical dump leaked a fixture password"
        );
        let value: serde_json::Value = serde_json::from_str(&canonical).expect("valid JSON");
        assert!(value.get("meta").is_some(), "{name}: missing meta");
        assert!(value.get("root").is_some(), "{name}: missing root");
        parsed += 1;
    }
    assert!(parsed >= 15, "expected fixture corpus, parsed {parsed}");
    assert!(broken >= 2, "expected broken fixtures, got {broken}");
}

#[test]
fn canonical_dump_is_stable_across_parse_runs() {
    let path = fixture_dir().join("test_db_kdbx41_features.kdbx");
    let bytes = std::fs::read(path).expect("read fixture");
    let first = keepassity_core::kdbx::reader::parse(&bytes, "demopass").expect("first parse");
    let second = keepassity_core::kdbx::reader::parse(&bytes, "demopass").expect("second parse");
    assert_eq!(
        first.canonical_json().unwrap(),
        second.canonical_json().unwrap()
    );
    assert_eq!(first.raw_xml_sha256, second.raw_xml_sha256);
}

/// Manual real-database check (G0 already proved the write path; this checks the
/// read layer on the user's database). Run with:
/// `KEEPASSITY_TEST_REAL_DB=<path> KEEPASSITY_TEST_REAL_PASSWORD=<pw> cargo test -p keepassity-core -- --ignored`
#[test]
#[ignore = "requires a real database path and password in environment"]
fn parses_real_database_from_env() {
    let path = std::env::var("KEEPASSITY_TEST_REAL_DB").expect("KEEPASSITY_TEST_REAL_DB");
    let password =
        std::env::var("KEEPASSITY_TEST_REAL_PASSWORD").expect("KEEPASSITY_TEST_REAL_PASSWORD");
    let bytes = std::fs::read(&path).expect("read real database");
    let db = keepassity_core::kdbx::reader::parse(&bytes, &password).expect("parse real database");
    assert_eq!(db.canonical_json().unwrap(), db.canonical_json().unwrap());
    assert!(db.raw_xml_sha256.is_some());
    assert!(db.raw_xml.as_ref().is_some_and(|xml| !xml.is_empty()));
}
