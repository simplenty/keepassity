// SPDX-License-Identifier: GPL-3.0-or-later
//! `rewrite`: open a KDBX with the ADR-0002 write backend and save a new file.
//!
//! The CLI is the IO layer (core stays IO-free). The output file must be a
//! *different* path from the input; the original is never touched.

use keepassity_core::kdbx::{detect_version, KdbxVersion};
use keepassxcr::model::{Entry, Group};
use keepassxcr::{Credentials, KdbxFile};
use serde_json::json;
use std::fs::File;
use std::io::Read;
use std::path::Path;

/// Run `keepassity-cli rewrite <in.kdbx> <out.kdbx> [--change-title] [--title-suffix S]`.
pub fn run(args: &[String], password: &str) -> Result<i32, String> {
    let input = args
        .get(2)
        .ok_or_else(|| "missing <input.kdbx>".to_string())?;
    let output = args
        .get(3)
        .ok_or_else(|| "missing <output.kdbx>".to_string())?;

    if same_path(input, output) {
        return Err("refusing to write: input and output are the same path".to_string());
    }
    if Path::new(output).exists() && !args.iter().any(|a| a == "--overwrite") {
        return Err(format!(
            "refusing to overwrite existing output {output} (pass --overwrite)"
        ));
    }
    if let Some(parent) = Path::new(output).parent() {
        if !parent.as_os_str().is_empty() && !parent.exists() {
            return Err(format!(
                "output directory does not exist: {}",
                parent.display()
            ));
        }
    }

    let suffix = crate::arg_value(args, "--title-suffix").unwrap_or_else(|| "-rewrite".to_string());
    let change_title = args.iter().any(|a| a == "--change-title");
    let emit_title_change = args.iter().any(|a| a == "--emit-title-change");

    let in_version = detect_path(input)?;
    let credentials = Credentials::password(password);
    let mut file =
        KdbxFile::open(input, &credentials).map_err(|e| format!("cannot open {input}: {e:#}"))?;

    let entries = count_entries(file.database.root());
    let attachments = count_attachments(file.database.root());
    let mut title_change: Option<(String, String)> = None;

    if change_title {
        let old_title = first_entry(file.database.root())
            .map(|entry| entry.title().to_string())
            .ok_or_else(|| "database has no entries to change".to_string())?;
        let new_title = format!("{old_title}{suffix}");
        let entry = first_entry_mut(file.database.root_mut())
            .ok_or_else(|| "database has no entries to change".to_string())?;
        entry.set_title(new_title.clone());
        title_change = Some((old_title, new_title));
    }

    file.save(output, &credentials)
        .map_err(|e| format!("cannot save {output}: {e:#}"))?;

    let out_version = detect_path(output)?;
    let output_bytes = std::fs::metadata(output)
        .map_err(|e| format!("cannot stat {output}: {e}"))?
        .len();

    let mut summary = json!({
        "status": "ok",
        "write_backend": keepassity_core::kdbx::WRITE_BACKEND,
        "input": input,
        "output": output,
        "input_version": in_version.as_str(),
        "output_version": out_version.as_str(),
        "entries": entries,
        "attachments": attachments,
        "title_changed": title_change.is_some(),
        "output_bytes": output_bytes,
    });
    if emit_title_change {
        if let Some((old, new)) = &title_change {
            summary["old_title"] = json!(old);
            summary["new_title"] = json!(new);
        }
    }
    println!(
        "{}",
        serde_json::to_string_pretty(&summary).map_err(|e| format!("json: {e}"))?
    );
    Ok(0)
}

fn detect_path(path: &str) -> Result<KdbxVersion, String> {
    let mut header = [0u8; 12];
    let mut file = File::open(path).map_err(|e| format!("cannot open {path}: {e}"))?;
    file.read_exact(&mut header)
        .map_err(|e| format!("cannot read header of {path}: {e}"))?;
    detect_version(&header).map_err(|e| format!("{path}: {e}"))
}

fn same_path(a: &str, b: &str) -> bool {
    if let (Ok(a), Ok(b)) = (std::fs::canonicalize(a), std::fs::canonicalize(b)) {
        return a == b;
    }
    let a = std::path::absolute(a).unwrap_or_else(|_| Path::new(a).to_path_buf());
    let b = std::path::absolute(b).unwrap_or_else(|_| Path::new(b).to_path_buf());
    a == b
}

fn first_entry(group: &Group) -> Option<&Entry> {
    group
        .entries()
        .first()
        .or_else(|| group.groups().iter().find_map(first_entry))
}

fn first_entry_mut(group: &mut Group) -> Option<&mut Entry> {
    if group.entries_mut().is_empty() {
        for sub in group.groups_mut() {
            if let Some(entry) = first_entry_mut(sub) {
                return Some(entry);
            }
        }
        None
    } else {
        group.entries_mut().first_mut()
    }
}

fn count_entries(group: &Group) -> usize {
    group.entries().len() + group.groups().iter().map(count_entries).sum::<usize>()
}

fn count_attachments(group: &Group) -> usize {
    group
        .entries()
        .iter()
        .map(|e| e.attachments().len())
        .sum::<usize>()
        + group.groups().iter().map(count_attachments).sum::<usize>()
}
