// SPDX-License-Identifier: GPL-3.0-or-later
//! Fuzz target: merge / sync decision path.
//!
//! There is no byte-level KDBX entry point for merge (merge takes two decrypted
//! `model::Database` values). The stable decoder that feeds merge in practice is
//! the JSON model used by `Database::canonical_json` / the FFI DTO layer, so this
//! target fuzzes exactly that boundary:
//!
//! * fuzzer input = `JSON(local) || 0x00 || JSON(remote)` (JSON text never
//!   contains a raw NUL byte, so the split is unambiguous);
//! * both sides are deserialized with `serde_json::from_slice::<Database>`;
//! * when both parse, `sync::merge_for_sync` merges them, and the merged model
//!   is re-serialized (canonical dump) to traverse the result.
//!
//! If one side is malformed it falls back to an empty model, so a single valid
//! document still exercises the merge path while JSON deserialization is also
//! under test. `sync::conflict` copy naming (used on merge conflicts) is fuzzed
//! with the same text.
//!
//! Seeds are produced by `scripts/seed-corpus.ps1`, which writes two
//! `Database` JSON documents with overlapping UUIDs and different
//! `LastModificationTime` values.
#![no_main]

use keepassity_core::model::Database;
use keepassity_core::sync::conflict;
use libfuzzer_sys::fuzz_target;

/// Split `data` into `(local_json, remote_json)` on the first NUL byte.
fn split(data: &[u8]) -> (&[u8], &[u8]) {
    match data.iter().position(|byte| *byte == 0) {
        Some(index) => (&data[..index], &data[index + 1..]),
        None => (data, &[]),
    }
}

fuzz_target!(|data: &[u8]| {
    let (local_bytes, remote_bytes) = split(data);

    // Deserialization of the model is part of the surface; invalid JSON is an
    // ordinary error, not a failure.
    let local: Database = serde_json::from_slice(local_bytes).unwrap_or_default();
    let remote: Database = serde_json::from_slice(remote_bytes).unwrap_or_default();

    let merged = keepassity_core::sync::merge_for_sync(&local, &remote);

    // Traverse the merge result (conflicts, moved entries, history) and ensure
    // the canonical dump stays total.
    let _ = merged.database.canonical_json();
    let _ = merged.report.conflicts;
    let _ = merged.conflicts.len();

    // Conflict-copy naming consumes arbitrary text (timestamp + original name).
    let text = String::from_utf8_lossy(data);
    let _ = conflict::valid_conflict_timestamp(&text);
    let _ = conflict::conflict_copy_name("Notes.txt", &text);
    let _ = conflict::conflict_copy_name(&text, "2026-01-02T03-04-05Z");
});
