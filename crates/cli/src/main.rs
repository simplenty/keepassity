// SPDX-License-Identifier: GPL-3.0-or-later
//! CLI harness: CI, interop, and acceptance checks. Not shipped with the app.
//!
//! All commands are non-interactive (password via `--password`,
//! `--password-env`, or `KEEPASSITY_PASSWORD`) and print JSON unless stated.
//! Exit codes: 0 ok, 2 usage, 3 authentication/key, 4 file IO, 5 KDBX format.

mod rewrite;
mod verify;

use keepass::{Database as KeepassDatabase, DatabaseKey};
use keepassity_core::kdbx::reader;
use keepassity_core::model::{Database, Entry, Group};
use keepassity_core::totp::fields::{self as totp_fields, TotpSource};
use keepassity_core::totp::{generate_at, TotpAlgorithm, TotpEncoder};
use serde_json::json;
use sha2::{Digest, Sha256};
use std::fs::File;
use std::path::Path;
use std::time::{SystemTime, UNIX_EPOCH};

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
        "--help" | "-h" => {
            println!("{}", usage());
        }
        "open" => finish(run_open(&args)),
        "inspect" => finish(run_inspect(&args)),
        "list" => finish(run_list(&args)),
        "totp" => finish(run_totp(&args)),
        "rewrite" => finish(read_password(&args).and_then(|pw| rewrite::run(&args, &pw))),
        "verify" => finish(read_password(&args).and_then(|pw| verify::run(&args, &pw))),
        "export-model" => finish(run_export_model(&args)),
        "sync" => finish(run_sync(&args)),
        other => {
            eprintln!("unknown command: {other}");
            eprintln!("{}", usage());
            std::process::exit(2);
        }
    }
}

fn usage() -> &'static str {
    "usage: keepassity-cli <command> ...\n\
     \n\
     read:   open <file> | inspect <file> | list <file> [--group NAME]\n\
             totp <file> --entry TITLE|UUID [--at UNIX]\n\
             export-model <file> [--sha256]\n\
     write:  rewrite <in> <out> [--change-title] [--title-suffix S] [--overwrite] [--emit-title-change]\n\
             verify <original> <modified> [--expect-title-old OLD --expect-title-new NEW]\n\
     sync:   sync <local> <remote>   (local compare; network sync arrives in M4)\n\
     global: --json-errors\n\
     password: --password <pw>, --password-env VAR, or KEEPASSITY_PASSWORD"
}

fn finish(result: Result<i32, String>) -> ! {
    match result {
        Ok(code) => std::process::exit(code),
        Err(message) => {
            let (code, name) = classify_error(&message);
            if std::env::args().any(|arg| arg == "--json-errors") {
                eprintln!(
                    "{}",
                    json!({"status": "error", "error": {"code": name, "message": message}})
                );
            } else {
                eprintln!("error: {message}");
            }
            std::process::exit(code);
        }
    }
}

fn classify_error(message: &str) -> (i32, &'static str) {
    let lower = message.to_ascii_lowercase();
    if lower.contains("missing") || lower.contains("unknown command") || lower.contains("usage") {
        (2, "USAGE")
    } else if lower.contains("password")
        || lower.contains("credential")
        || lower.contains("invalid key")
        || lower.contains("cannot open kdbx")
    {
        (3, "AUTH")
    } else if lower.contains("cannot open")
        || lower.contains("cannot read")
        || lower.contains("no such file")
        || lower.contains("cannot stat")
    {
        (4, "IO")
    } else if lower.contains("invalid kdbx")
        || lower.contains("unsupported")
        || lower.contains("corrupt")
        || lower.contains("truncated")
    {
        (5, "FORMAT")
    } else {
        (1, "INTERNAL")
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

fn open_model(args: &[String]) -> Result<(String, Vec<u8>, Database), String> {
    let path = args
        .get(2)
        .ok_or_else(|| "missing <file.kdbx>".to_string())?
        .clone();
    let password = read_password(args)?;
    let bytes = std::fs::read(&path).map_err(|e| format!("cannot open {path}: {e}"))?;
    let db = reader::parse(&bytes, &password).map_err(|e| format!("cannot open KDBX: {e}"))?;
    Ok((path, bytes, db))
}

fn count_groups(group: &Group) -> usize {
    1 + group.groups.iter().map(count_groups).sum::<usize>()
}

fn count_entries(group: &Group) -> usize {
    group.entries.len() + group.groups.iter().map(count_entries).sum::<usize>()
}

fn count_attachments(group: &Group) -> usize {
    group
        .entries
        .iter()
        .map(|entry| entry.binaries.len())
        .sum::<usize>()
        + group.groups.iter().map(count_attachments).sum::<usize>()
}

fn run_open(args: &[String]) -> Result<i32, String> {
    let (path, bytes, db) = open_model(args)?;
    println!(
        "{}",
        serde_json::to_string_pretty(&json!({
            "status": "ok",
            "file": {
                "name": Path::new(&path).file_name().map(|name| name.to_string_lossy().to_string()).unwrap_or_default(),
                "bytes": bytes.len(),
            },
            "kdbx_version": db.format_version,
            "generator": db.meta.generator,
            "database_name": db.meta.database_name,
            "groups": count_groups(&db.root),
            "entries": count_entries(&db.root),
            "attachments": count_attachments(&db.root),
            "deleted_objects": db.deleted_objects.len(),
            "raw_xml_sha256": db.raw_xml_sha256,
        }))
        .map_err(|e| format!("json: {e}"))?
    );
    Ok(0)
}

struct FlatEntry<'a> {
    group_path: String,
    entry: &'a Entry,
}

fn flatten<'a>(group: &'a Group, path: &mut Vec<String>, out: &mut Vec<FlatEntry<'a>>) {
    if path.is_empty() {
        path.push(group.name.clone());
    }
    for entry in &group.entries {
        out.push(FlatEntry {
            group_path: path.join("/"),
            entry,
        });
    }
    for child in &group.groups {
        path.push(child.name.clone());
        flatten(child, path, out);
        path.pop();
    }
}

fn run_list(args: &[String]) -> Result<i32, String> {
    let (_, _, db) = open_model(args)?;
    let group_filter = arg_value(args, "--group");
    let mut flat = Vec::new();
    flatten(&db.root, &mut Vec::new(), &mut flat);
    let mut entries: Vec<serde_json::Value> = flat
        .into_iter()
        .filter(|item| {
            group_filter
                .as_deref()
                .is_none_or(|filter| item.group_path.contains(filter))
        })
        .map(|item| {
            json!({
                "uuid": item.entry.uuid.to_hex(),
                "group": item.group_path,
                "title": item.entry.field("Title").unwrap_or(""),
                "username": item.entry.field("UserName").unwrap_or(""),
                "url": item.entry.field("URL").unwrap_or(""),
                "has_totp": totp_fields::read_entry(item.entry).ok().flatten().is_some(),
                "history_count": item.entry.history.len(),
            })
        })
        .collect();
    entries.sort_by_key(|entry| {
        (
            entry["group"].as_str().unwrap_or("").to_string(),
            entry["title"].as_str().unwrap_or("").to_string(),
            entry["uuid"].as_str().unwrap_or("").to_string(),
        )
    });
    println!(
        "{}",
        serde_json::to_string_pretty(&json!({
            "status": "ok",
            "count": entries.len(),
            "entries": entries,
        }))
        .map_err(|e| format!("json: {e}"))?
    );
    Ok(0)
}

fn find_entry<'a>(group: &'a Group, needle: &str) -> Option<&'a Entry> {
    if let Some(entry) = group
        .entries
        .iter()
        .find(|entry| entry.uuid.to_hex() == needle || entry.field("Title") == Some(needle))
    {
        return Some(entry);
    }
    group
        .groups
        .iter()
        .find_map(|child| find_entry(child, needle))
}

fn run_totp(args: &[String]) -> Result<i32, String> {
    let (_, _, db) = open_model(args)?;
    let needle =
        arg_value(args, "--entry").ok_or_else(|| "missing --entry TITLE|UUID".to_string())?;
    let entry =
        find_entry(&db.root, &needle).ok_or_else(|| format!("entry not found: {needle}"))?;
    let read = totp_fields::read_entry(entry)
        .map_err(|e| format!("cannot read TOTP fields: {e}"))?
        .ok_or_else(|| "entry has no TOTP configuration".to_string())?;
    let at = match arg_value(args, "--at") {
        Some(value) => value
            .parse::<u64>()
            .map_err(|_| "--at must be Unix seconds".to_string())?,
        None => SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|e| format!("clock: {e}"))?
            .as_secs(),
    };
    let code = generate_at(&read.spec.config, at).map_err(|e| format!("TOTP: {e}"))?;
    let source = match read.source {
        TotpSource::OtpUri => "otp",
        TotpSource::KeepassXcLegacy => "totp-seed-settings",
        TotpSource::TimeOtp => "timeotp",
    };
    println!(
        "{}",
        serde_json::to_string_pretty(&json!({
            "status": "ok",
            "entry": {
                "uuid": entry.uuid.to_hex(),
                "title": entry.field("Title").unwrap_or(""),
            },
            "source": source,
            "code": code.code,
            "remaining": code.remaining,
            "period": code.period,
            "digits": code.digits,
            "algorithm": match code.algorithm {
                TotpAlgorithm::Sha1 => "SHA1",
                TotpAlgorithm::Sha256 => "SHA256",
                TotpAlgorithm::Sha512 => "SHA512",
            },
            "encoder": match read.spec.config.encoder {
                TotpEncoder::Totp => "totp",
                TotpEncoder::Steam => "steam",
            },
            "issuer": read.spec.issuer,
            "account": read.spec.account,
        }))
        .map_err(|e| format!("json: {e}"))?
    );
    Ok(0)
}

fn run_export_model(args: &[String]) -> Result<i32, String> {
    let (_, _, db) = open_model(args)?;
    let canonical = db.canonical_json().map_err(|e| format!("json: {e}"))?;
    if args.iter().any(|arg| arg == "--sha256") {
        let hash = Sha256::digest(canonical.as_bytes());
        println!(
            "{}",
            hash.iter()
                .map(|byte| format!("{byte:02x}"))
                .collect::<String>()
        );
    } else {
        println!("{canonical}");
    }
    Ok(0)
}

fn run_sync(args: &[String]) -> Result<i32, String> {
    let local = args
        .get(2)
        .ok_or_else(|| "missing <local.kdbx>".to_string())?;
    let remote = args
        .get(3)
        .ok_or_else(|| "missing <remote.kdbx>".to_string())?;
    let password = read_password(args)?;
    let read = |path: &str| -> Result<String, String> {
        let bytes = std::fs::read(path).map_err(|e| format!("cannot open {path}: {e}"))?;
        let mut db = reader::parse(&bytes, &password)
            .map_err(|e| format!("cannot open KDBX {path}: {e}"))?;
        db.raw_xml = None;
        db.raw_xml_sha256 = None;
        db.meta.generator.clear();
        db.canonical_json().map_err(|e| format!("json: {e}"))
    };
    let local_json = read(local)?;
    let remote_json = read(remote)?;
    let hash = |text: &str| {
        let digest = Sha256::digest(text.as_bytes());
        digest
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect::<String>()
    };
    println!(
        "{}",
        serde_json::to_string_pretty(&json!({
            "status": "ok",
            "mode": "local-compare",
            "same": local_json == remote_json,
            "local_sha256": hash(&local_json),
            "remote_sha256": hash(&remote_json),
            "note": "network sync decision is implemented in M4",
        }))
        .map_err(|e| format!("json: {e}"))?
    );
    Ok(0)
}

fn run_inspect(args: &[String]) -> Result<i32, String> {
    let path = args
        .get(2)
        .ok_or_else(|| "missing <file.kdbx>".to_string())?;
    let password = read_password(args)?;
    let mut file = File::open(path).map_err(|e| format!("cannot open {path}: {e}"))?;
    let size = file.metadata().map(|m| m.len()).unwrap_or(0);
    let key = DatabaseKey::new().with_password(&password);
    let db = KeepassDatabase::open(&mut file, key)
        .map_err(|e| format!("cannot read database: {e:?}"))?;

    let mut entries: Vec<serde_json::Value> = Vec::new();
    for entry in db.iter_all_entries() {
        let mut history = 0usize;
        while entry.historical(history).is_some() {
            history += 1;
        }
        entries.push(json!({
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
    let canonical = json!({
        "schema": 1,
        "entries": entries,
    });
    let canonical_json = serde_json::to_string(&canonical).map_err(|e| format!("json: {e}"))?;
    let digest = Sha256::digest(canonical_json.as_bytes());
    let sha256 = digest
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect::<String>();

    let out = json!({
        "schema": 1,
        "file": { "name": Path::new(path).file_name().map(|s| s.to_string_lossy().to_string()).unwrap_or_default(), "bytes": size },
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
