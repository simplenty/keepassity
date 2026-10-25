// SPDX-License-Identifier: GPL-3.0-or-later
//! Optional external parity check for TOTP against keepassxc-cli.
//!
//! Not part of CI (keepassxc-cli is not installed there). Run with:
//! `KEEPASSXC_CLI=<path> KEEPASSXC_DB=<fixture> cargo test -p keepassity-core -- --ignored totp_keepassxc`
//!
//! The fixture's public TOTP configuration is
//! `otpauth://totp/KeePassXC:none?secret=JBSWY3DPEHPK3PXP&period=30&digits=6`
//! (SHA-1, 6 digits, 30 s, password `test`).

use keepassity_core::totp::{base32_decode, generate_at, TotpAlgorithm, TotpConfig, TotpEncoder};
use std::io::Write;
use std::process::{Command, Stdio};
use std::time::{SystemTime, UNIX_EPOCH};

fn now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("clock after epoch")
        .as_secs()
}

#[test]
#[ignore = "requires keepassxc-cli and a local TOTP fixture"]
fn matches_keepassxc_cli_current_code() {
    let cli = std::env::var("KEEPASSXC_CLI").expect("KEEPASSXC_CLI");
    let db = std::env::var("KEEPASSXC_DB").expect("KEEPASSXC_DB");
    let config = TotpConfig {
        secret: base32_decode("JBSWY3DPEHPK3PXP").expect("decode secret"),
        algorithm: TotpAlgorithm::Sha1,
        digits: 6,
        period: 30,
        encoder: TotpEncoder::Totp,
    };

    let before = now();
    let mut child = Command::new(cli)
        .args(["show", "-q", "-t", &db, "this entry has totp"])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("spawn keepassxc-cli");
    child
        .stdin
        .as_mut()
        .expect("stdin")
        .write_all(b"test\n")
        .expect("password");
    let output = child.wait_with_output().expect("keepassxc-cli output");
    let after = now();
    assert!(
        output.status.success(),
        "keepassxc-cli failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    let cli_code = String::from_utf8_lossy(&output.stdout).trim().to_string();
    let our_before = generate_at(&config, before).expect("totp").code;
    let our_after = generate_at(&config, after).expect("totp").code;
    assert!(
        cli_code == our_before || cli_code == our_after,
        "keepassxc-cli={cli_code} ours(before={our_before}, after={our_after})"
    );
}
