// SPDX-License-Identifier: GPL-3.0-or-later
//! End-to-end write closure test for the CLI:
//! fixture --(rewrite via keepassxcr)--> output --(verify via keepass 0.17)--> PASS.

use std::path::{Path, PathBuf};
use std::process::Command;

fn cli() -> &'static str {
    env!("CARGO_BIN_EXE_keepassity-cli")
}

fn fixture_dir() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/fixtures/keepass-rs")
        .canonicalize()
        .expect("fixture dir exists")
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
fn rewrite_all_supported_fixtures_and_verify() {
    let work = Path::new(env!("CARGO_TARGET_TMPDIR")).join("rewrite-roundtrip");
    if work.exists() {
        std::fs::remove_dir_all(&work).expect("clean old work dir");
    }
    std::fs::create_dir_all(&work).expect("create work dir");

    let mut supported = 0usize;
    let mut broken = 0usize;

    for entry in std::fs::read_dir(fixture_dir()).expect("read fixtures") {
        let path = entry.expect("dir entry").path();
        if path.extension().and_then(|e| e.to_str()) != Some("kdbx") {
            continue;
        }
        let name = path.file_name().unwrap().to_string_lossy().to_string();

        // Keyfile-protected fixtures are outside the v1.0 scope.
        if name.contains("keyfile") || name.contains("challenge_response") {
            continue;
        }

        let output = work.join(format!("out-{name}"));
        let rewritten = Command::new(cli())
            .arg("rewrite")
            .arg(&path)
            .arg(&output)
            .arg("--password")
            .arg(password_for(&name))
            .status()
            .expect("run rewrite");

        if name.starts_with("broken_") {
            assert!(
                !rewritten.success(),
                "broken fixture {name} must not be rewritten"
            );
            broken += 1;
            continue;
        }

        assert!(
            rewritten.success(),
            "rewrite failed for {name} (exit {:?})",
            rewritten.code()
        );
        let verified = Command::new(cli())
            .arg("verify")
            .arg(&path)
            .arg(&output)
            .arg("--password")
            .arg(password_for(&name))
            .arg("--allow-empty-standard-fields")
            .status()
            .expect("run verify");
        assert!(
            verified.success(),
            "verify failed for {name} (exit {:?})",
            verified.code()
        );
        supported += 1;
    }

    assert!(
        supported >= 15,
        "expected the fixture corpus, got {supported}"
    );
    assert!(
        broken >= 2,
        "expected the two broken fixtures, got {broken}"
    );
    std::fs::remove_dir_all(&work).ok();
}
