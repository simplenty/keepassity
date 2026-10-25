// SPDX-License-Identifier: GPL-3.0-or-later
//! CLI harness: CI, interop, and acceptance checks. Not shipped with the app.
//!
//! `inspect` prints a read-only JSON summary; `rewrite` exercises the KDBX
//! write backend; `verify` compares two databases field by field.

mod rewrite;
mod verify;

use keepass::{Database, DatabaseKey};
use sha2::{Digest, Sha256};
use std::fs::File;

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let cmd = args.get(1).map(String::as_str).unwrap_or("version");
    match cmd {
        "version" => {
            println!(
                "keepassity-cli {} (core api v{})",
                keepassity_core::version(),
                keepassity_core::API_MAJOR
            );
        }
        "inspect" => finish(run_inspect(&args)),
        "rewrite" => finish(read_password(&args).and_then(|pw| rewrite::run(&args, &pw))),
        "verify" => finish(read_password(&args).and_then(|pw| verify::run(&args, &pw))),
        other => {
            eprintln!("unknown command: {other}");
            eprintln!(
                "usage: keepassity-cli <version|inspect|rewrite|verify> ...\n  inspect <file.kdbx>\n  rewrite <in.kdbx> <out.kdbx> [--change-title] [--title-suffix S] [--overwrite] [--emit-title-change]\n  verify <original.kdbx> <modified.kdbx> [--expect-title-old OLD --expect-title-new NEW]\n  password: --password <pw>, --password-env VAR, or KEEPASSITY_PASSWORD"
            );
            std::process::exit(2);
        }
    }
}

fn finish(result: Result<i32, String>) -> ! {
    match result {
        Ok(code) => std::process::exit(code),
        Err(message) => {
            eprintln!("error: {message}");
            std::process::exit(1);
        }
    }
}

pub(crate) fn arg_value(args: &[String], name: &str) -> Option<String> {
    args.iter()
        .position(|a| a == name)
        .and_then(|i| args.get(i + 1))
        .cloned()
}

pub(crate) fn read_password(args: &[String]) -> Result<String, String> {
    if let Some(v) = arg_value(args, "--password") {
        return Ok(v);
    }
    if let Some(var) = arg_value(args, "--password-env") {
        return std::env::var(&var).map_err(|_| format!("environment variable {var} is not set"));
    }
    std::env::var("KEEPASSITY_PASSWORD").map_err(|_| {
        "no password: pass --password, --password-env VAR, or set KEEPASSITY_PASSWORD".to_string()
    })
}

fn run_inspect(args: &[String]) -> Result<i32, String> {
    let path = args
        .get(2)
        .ok_or_else(|| "missing <file.kdbx>".to_string())?;
    let password = read_password(args)?;
    let mut file = File::open(path).map_err(|e| format!("cannot open {path}: {e}"))?;
    let size = file.metadata().map(|m| m.len()).unwrap_or(0);
    let key = DatabaseKey::new().with_password(&password);
    let db = Database::open(&mut file, key).map_err(|e| format!("cannot read database: {e:?}"))?;

    let mut entries: Vec<serde_json::Value> = Vec::new();
    for entry in db.iter_all_entries() {
        let mut history = 0usize;
        while entry.historical(history).is_some() {
            history += 1;
        }
        entries.push(serde_json::json!({
            "title": entry.get_title().unwrap_or(""),
            "username": entry.get_username().unwrap_or(""),
            "url": entry.get_url().unwrap_or(""),
            "has_password": entry.get_password().is_some(),
            "has_otp_field": entry.get_raw_otp_value().is_some(),
            "history": history,
        }));
    }
    entries.sort_by_key(|v| {
        (
            v["title"].as_str().unwrap_or("").to_string(),
            v["username"].as_str().unwrap_or("").to_string(),
            v["url"].as_str().unwrap_or("").to_string(),
        )
    });

    let totp_count = entries
        .iter()
        .filter(|v| v["has_otp_field"].as_bool().unwrap_or(false))
        .count();
    let entry_count = entries.len();
    let canonical = serde_json::json!({
        "schema": 1,
        "entries": entries,
    });
    let canonical_json = serde_json::to_string(&canonical).map_err(|e| format!("json: {e}"))?;
    let digest = Sha256::digest(canonical_json.as_bytes());
    let sha256 = digest
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect::<String>();

    let out = serde_json::json!({
        "schema": 1,
        "file": { "name": std::path::Path::new(path).file_name().map(|s| s.to_string_lossy().to_string()).unwrap_or_default(), "bytes": size },
        "entry_count": entry_count,
        "totp_entry_count": totp_count,
        "canonical_sha256": sha256,
        "entries": canonical["entries"].clone(),
    });
    println!(
        "{}",
        serde_json::to_string_pretty(&out).map_err(|e| format!("json: {e}"))?
    );
    Ok(0)
}
