// SPDX-License-Identifier: GPL-3.0-or-later
//! M1-08: BadPassword vs CorruptDatabase vs Unsupported classification.

use keepassity_core::kdbx::reader::{parse, KdbxReadError};
use std::path::{Path, PathBuf};

fn fixture(name: &str) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/fixtures/keepass-rs")
        .join(name)
}

#[test]
fn classifies_five_bad_inputs() {
    let valid = std::fs::read(fixture("test_db_kdbx4_with_password_aes.kdbx")).expect("fixture");

    // 1. Wrong password.
    assert!(
        matches!(
            parse(&valid, "definitely-wrong"),
            Err(KdbxReadError::BadPassword)
        ),
        "wrong password must be BadPassword"
    );

    // 2. Truncated file.
    let truncated = &valid[..valid.len() / 2];
    assert!(
        matches!(parse(truncated, "demopass"), Err(KdbxReadError::Corrupt(_))),
        "truncated file must be Corrupt"
    );

    // 3. Random bytes with no KDBX signature.
    assert!(
        matches!(parse(&[0x5au8; 64], "x"), Err(KdbxReadError::Corrupt(_))),
        "random bytes must be Corrupt"
    );

    // 4. Valid signature but garbage header fields.
    let mut garbage = valid[..12].to_vec();
    garbage.extend_from_slice(&[0xa5u8; 80]);
    assert!(
        matches!(parse(&garbage, "demopass"), Err(KdbxReadError::Corrupt(_))),
        "garbage header must be Corrupt"
    );

    // 5. Unknown version word.
    let mut unsupported = valid[..8].to_vec();
    unsupported.extend_from_slice(&0x0009_0000u32.to_le_bytes());
    unsupported.extend_from_slice(&[0u8; 64]);
    match parse(&unsupported, "demopass") {
        Err(KdbxReadError::Unsupported(_)) => {}
        other => panic!("unknown version must be Unsupported, got {other:?}"),
    }
}

#[test]
fn error_messages_do_not_include_password() {
    let valid = std::fs::read(fixture("test_db_kdbx4_with_password_aes.kdbx")).expect("fixture");
    let error = parse(&valid, "super-secret-password").expect_err("wrong password");
    assert!(!format!("{error}").contains("super-secret-password"));
    assert!(!format!("{error:?}").contains("super-secret-password"));
}
