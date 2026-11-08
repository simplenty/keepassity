// SPDX-License-Identifier: GPL-3.0-or-later
//! Performance evidence for the paths the 500-entry fixture does not cover yet
//! (M5-03, measured 2026-11-09): save, sync decide/merge and Argon2id KDF
//! calibration.
//!
//! This is a measurement, not a unit test, so it is ignored by default. Run it
//! in release mode on the machine to be measured:
//!
//! ```text
//! cargo test --release -p keepassity-core --test perf_paths_500 -- \
//!     --ignored --nocapture
//! ```
//!
//! What is measured (all in-process, `std::time::Instant`, no extra crates):
//!
//! * `kdf_new_database` — the Argon2id derivation cost using the KDF parameters
//!   actually written into a brand-new database by `writer::create_empty` (the
//!   app's "new database" path), read back from that file's header. The fixture
//!   header parameters are printed as well; they are the same defaults because
//!   the committed fixture was written through `writer::write_model`.
//! * `kdf_tuning_*` — the same derivation measured for candidate parameter sets
//!   inside the requirement bounds (M=64..256 MiB, I=3..10, P=4) so a tuning
//!   recommendation is based on numbers from this machine instead of a guess.
//!   Nothing here changes the product default; it only measures.
//! * `save_write_model` — `kdbx::writer::write_model` on the open 500-entry
//!   model: XML serialisation + fresh KDF salt/IV/master seed + Argon2id +
//!   AES-256 encryption + HMAC. This is exactly the FFI `kp_save` path the app
//!   uses (`kp_save` calls `writer::write_model`), so it is the app's save cost
//!   without the platform-layer file replace.
//! * `save_reseal` / `save_verify_rewritten` / `save_reseal_and_verify` — the
//!   documented safe-save protocol's steps on the supported `reseal` path (same
//!   database content, fresh crypto): serialise + encrypt, verification (two
//!   independent reads of a ~28 KB database), and both together, to compare
//!   against the §5.7 wording "serialisation + encryption + verification
//!   < 500 ms". The app's save path (kp_save → `write_model`) does not run the
//!   verification pass; the test prints whether verification accepts a
//!   `write_model` round trip instead of asserting it.
//! * `sync_decide` — `sync::SyncState::decide` for a realistic changed-remote
//!   pair (ETag + SHA-256 baseline, local dirty → `DownloadAndMerge`) and for the
//!   degraded no-ETag path.
//! * `sync_merge` — `sync::merge_for_sync` (index + five-element merge) on the
//!   realistic pair: 5 changed entries, 1 remote-only entry, 1 remote deletion.
//!   A second pair with a same-LastModificationTime content difference measures
//!   the conflict path (conflict count reported).
//!
//! Release mode only; a debug build prints numbers but they are not
//! representative. The test asserts the documented save budget (`p95 < 500 ms`)
//! and the expected merge counters, so a regression fails the run instead of
//! only printing.

use keepassity_core::kdbx::{reader, save, writer};
use keepassity_core::model::{Database, DeletedObject, Entry, Group, StringField, Uuid};
use keepassity_core::sync::{
    local_hash, merge_for_sync, RemoteMeta, SyncActionKind, SyncBaseline, SyncState,
};
use keepassxcr::crypto::{KdfConfig, KdfType};
use keepassxcr::format::{read_kdbx_bytes, write_kdbx_bytes};
use keepassxcr::Credentials;
use std::collections::BTreeSet;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

const FIXTURE_NAME: &str = "synthetic-500.kdbx";
const MASTER_PASSWORD: &str = "demopass";
const ENTRY_COUNT: usize = 500;
/// Enough samples for a meaningful p95 (nearest-rank p95 of 10 samples is the
/// maximum, which would overstate the tail).
const SAVE_ITERATIONS: usize = 20;
const KDF_ITERATIONS: usize = 20;
const UNLOCK_ITERATIONS: usize = 5;
const TUNING_UNLOCK_ITERATIONS: usize = 3;
const VERIFY_ITERATIONS: usize = 5;
const DECIDE_ITERATIONS: usize = 200;
const MERGE_ITERATIONS: usize = 20;
const CONFLICT_MERGE_ITERATIONS: usize = 10;

fn fixture_path() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/fixtures/synthetic")
        .join(FIXTURE_NAME)
}

// ---------------------------------------------------------------------------
// Timing helpers (same style as `perf_synthetic_500.rs`).
// ---------------------------------------------------------------------------

struct Stats {
    n: usize,
    total: Duration,
    min: Duration,
    p50: Duration,
    p95: Duration,
    max: Duration,
}

fn percentile(sorted: &[Duration], percent: f64) -> Duration {
    // nearest-rank percentile: index = ceil(p/100 * n) - 1, clamped.
    let rank = ((percent / 100.0) * sorted.len() as f64).ceil() as usize;
    sorted[rank.clamp(1, sorted.len()) - 1]
}

fn stats(samples: &mut [Duration]) -> Stats {
    samples.sort();
    let total: Duration = samples.iter().sum();
    Stats {
        n: samples.len(),
        total,
        min: samples[0],
        p50: percentile(samples, 50.0),
        p95: percentile(samples, 95.0),
        max: *samples.last().expect("non-empty"),
    }
}

fn measure(iterations: usize, mut body: impl FnMut()) -> Stats {
    let mut samples = Vec::with_capacity(iterations);
    for _ in 0..iterations {
        let started = Instant::now();
        body();
        samples.push(started.elapsed());
    }
    stats(&mut samples)
}

fn ms(value: Duration) -> f64 {
    value.as_secs_f64() * 1_000.0
}

fn us(value: Duration) -> f64 {
    value.as_secs_f64() * 1_000_000.0
}

fn print_stats(prefix: &str, stats: &Stats, unit: fn(Duration) -> f64, suffix: &str) {
    let mean = unit(stats.total) / stats.n as f64;
    println!(
        "{prefix} n={} mean_{suffix}={mean:.3} min_{suffix}={:.3} p50_{suffix}={:.3} p95_{suffix}={:.3} max_{suffix}={:.3}",
        stats.n,
        unit(stats.min),
        unit(stats.p50),
        unit(stats.p95),
        unit(stats.max)
    );
}

fn env_or_unknown(name: &str) -> String {
    std::env::var(name).unwrap_or_else(|_| "unknown".to_string())
}

// ---------------------------------------------------------------------------
// Model helpers.
// ---------------------------------------------------------------------------

fn entry_count(group: &Group) -> usize {
    group.entries.len() + group.groups.iter().map(entry_count).sum::<usize>()
}

fn collect_entries_mut<'a>(group: &'a mut Group, out: &mut Vec<&'a mut Entry>) {
    out.extend(group.entries.iter_mut());
    for child in &mut group.groups {
        collect_entries_mut(child, out);
    }
}

fn set_field(entry: &mut Entry, key: &str, value: &str) {
    match entry.strings.iter_mut().find(|field| field.key == key) {
        Some(field) => field.value = value.to_string(),
        None => entry.strings.push(StringField {
            key: key.to_string(),
            value: value.to_string(),
            protected: false,
            unknown: Vec::new(),
        }),
    }
}

fn remove_entry(group: &mut Group, uuid: Uuid) -> Option<Entry> {
    if let Some(position) = group.entries.iter().position(|entry| entry.uuid == uuid) {
        return Some(group.entries.remove(position));
    }
    for child in &mut group.groups {
        if let Some(entry) = remove_entry(child, uuid) {
            return Some(entry);
        }
    }
    None
}

/// Realistic sync pair: the remote copy has 5 newer edits (newer
/// LastModificationTime), 1 remote-only entry and 1 remote deletion tombstone.
/// Everything else is identical to the local copy.
fn realistic_remote(local: &Database) -> Database {
    let mut remote = local.clone();
    let mut changed = 0usize;
    {
        let mut entries = Vec::new();
        collect_entries_mut(&mut remote.root, &mut entries);
        for (index, entry) in entries.iter_mut().enumerate() {
            // Touch a deterministic handful spread over the database.
            if matches!(index, 3 | 101 | 202 | 303 | 404) {
                entry.times.last_modification = format!("2026-11-09T00:00:{index:02}Z");
                entry.times.last_access = entry.times.last_modification.clone();
                let title = entry.field("Title").unwrap_or("entry").to_string();
                set_field(entry, "Title", &format!("{title} (remote edit)"));
                changed += 1;
            }
        }
    }
    assert_eq!(changed, 5, "the realistic pair must contain 5 remote edits");

    // One entry that only exists on the remote side.
    let mut added = remote.root.groups[0].entries[0].clone();
    added.uuid = Uuid::from_bytes([0xfe, 0x4b, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]);
    added.times.last_modification = "2026-11-09T00:01:00Z".to_string();
    added.history.clear();
    set_field(&mut added, "Title", "Remote added entry");
    remote.root.groups[0].entries.push(added);

    // One deletion propagated by the remote side.
    let victim = remote.root.groups[1].entries[0].uuid;
    remove_entry(&mut remote.root, victim).expect("victim entry exists");
    remote.deleted_objects.push(DeletedObject {
        uuid: victim,
        deletion_time: "2026-11-09T00:02:00Z".to_string(),
    });

    remote
}

/// Conflict pair: the same entry is edited on both sides and the
/// LastModificationTime is identical, so the merge has to report a conflict.
fn conflicting_remote(local: &Database) -> Database {
    let mut remote = local.clone();
    let mut entries = Vec::new();
    collect_entries_mut(&mut remote.root, &mut entries);
    let target = entries.get_mut(42).expect("entry 42 exists");
    set_field(target, "Title", "Conflicting title (remote edit)");
    remote
}

// ---------------------------------------------------------------------------
// KDF helpers.
// ---------------------------------------------------------------------------

fn kdf_description(kdf: &KdfConfig) -> String {
    match kdf {
        KdfConfig::Argon2 {
            kind,
            iterations,
            memory_kib,
            parallelism,
            version,
            ..
        } => format!(
            "kind={kind:?} i={iterations} m_kib={memory_kib} m_mib={} p={parallelism} version=0x{version:02x}",
            memory_kib / 1024
        ),
        KdfConfig::Aes { rounds, legacy, .. } => {
            format!("kind=Aes rounds={rounds} legacy={legacy}")
        }
    }
}

/// `Argon2id` candidate parameters inside REQUIREMENTS §6.7 (M=64..256 MiB,
/// I=3..10, P=4). Used only for the tuning measurement.
fn candidate(memory_mib: u32, iterations: u32) -> KdfConfig {
    KdfConfig::Argon2 {
        kind: KdfType::Argon2id,
        salt: vec![0x5a; 32],
        iterations,
        memory_kib: memory_mib * 1024,
        parallelism: 4,
        version: 0x13,
    }
}

fn measure_kdf(label: &str, kdf: &KdfConfig, iterations: usize) -> Stats {
    let raw_key = [0x42u8; 32];
    let expected = kdf.transform(&raw_key).expect("argon2 transform must run");
    assert_eq!(expected.len(), 32, "Argon2 output must be 32 bytes");
    let stats = measure(iterations, || {
        let output = kdf
            .transform(&raw_key)
            .expect("argon2 transform during perf run");
        std::hint::black_box(&output);
    });
    print_stats(
        &format!("PERF kdf_{label} params={}", kdf_description(kdf)),
        &stats,
        ms,
        "ms",
    );
    stats
}

// ---------------------------------------------------------------------------

#[test]
#[ignore = "perf evidence: run explicitly in release mode with --ignored --nocapture"]
fn perf_paths_500() {
    let path = fixture_path();
    let bytes = std::fs::read(&path)
        .unwrap_or_else(|error| panic!("cannot read {}: {error}", path.display()));

    println!(
        "PERF machine os={} arch={} cpus={} processor={}",
        std::env::consts::OS,
        std::env::consts::ARCH,
        env_or_unknown("NUMBER_OF_PROCESSORS"),
        env_or_unknown("PROCESSOR_IDENTIFIER")
    );
    println!(
        "PERF build profile={} debug_assertions={}",
        if cfg!(debug_assertions) {
            "debug"
        } else {
            "release"
        },
        cfg!(debug_assertions)
    );
    println!("PERF fixture path={} bytes={}", path.display(), bytes.len());

    let database = reader::parse(&bytes, MASTER_PASSWORD)
        .expect("fixture must open with the documented password");
    let local_entries = entry_count(&database.root);
    assert_eq!(local_entries, ENTRY_COUNT);
    println!(
        "PERF fixture format={} groups_entries={local_entries}",
        database.format_version
    );

    let credentials = Credentials::password(MASTER_PASSWORD);
    let fixture_file = read_kdbx_bytes(&bytes, &credentials)
        .expect("fixture header must be readable by the write backend");
    println!(
        "PERF kdf_fixture_header {}",
        kdf_description(&fixture_file.kdf)
    );

    // ---- KDF as configured for new databases -----------------------------
    let fresh = writer::create_empty("Perf KDF calibration", MASTER_PASSWORD)
        .expect("create_empty must produce a KDBX file");
    let fresh_file =
        read_kdbx_bytes(&fresh, &credentials).expect("new database header must be readable");
    let new_database_kdf = fresh_file.kdf.clone();
    println!(
        "PERF kdf_new_database_header bytes={} {}",
        fresh.len(),
        kdf_description(&new_database_kdf)
    );
    let default_kdf = KdfConfig::default_argon2id();
    assert_eq!(
        kdf_description(&new_database_kdf),
        kdf_description(&default_kdf),
        "writer::create_empty must use the backend default Argon2id parameters"
    );

    let kdf_default = measure_kdf("new_database", &new_database_kdf, KDF_ITERATIONS);
    let unlock_component_ms = 2.0 * ms(kdf_default.p50);
    println!(
        "PERF kdf_unlock_implication derived_per_unlock=2 p50_unlock_kdf_ms={unlock_component_ms:.3} \
         (crates/core/src/kdbx/reader.rs derives the key twice: model + raw-XML evidence hash)"
    );

    // Candidate tunings, requirement bounds M=64..256 MiB, I=3..10, P=4.
    let candidates: [(&str, KdfConfig); 6] = [
        ("m64_i6_p4", candidate(64, 6)),
        ("m128_i3_p4", candidate(128, 3)),
        ("m128_i6_p4", candidate(128, 6)),
        ("m256_i3_p4", candidate(256, 3)),
        ("m256_i6_p4", candidate(256, 6)),
        ("m256_i10_p4", candidate(256, 10)),
    ];
    println!("PERF kdf_tuning iterations_per_candidate=3 target_ms=500..1000");
    for (label, config) in &candidates {
        measure_kdf(label, config, 3);
    }

    // The unlock path (reader) uses the `keepass` crate's rust-argon2, which runs
    // the configured lanes as threads; the write backend uses the sequential
    // RustCrypto Argon2. The same file parameters therefore cost less per
    // derivation on the read path. Measure the real unlock as well.
    let unlock = measure(UNLOCK_ITERATIONS, || {
        let database = reader::parse(&bytes, MASTER_PASSWORD).expect("unlock during perf run");
        std::hint::black_box(&database);
    });
    print_stats("PERF unlock_reader_parse", &unlock, ms, "ms");
    println!(
        "PERF kdf_implementation paths read=keepass+rust-argon2(lanes run as threads) \
         write=keepassxcr+RustCrypto(sequential lanes); reader::parse derives the key twice, \
         writer::write_model once"
    );

    // Unlock (reader::parse) with candidate KDF parameters: take the fixture
    // database, re-encrypt it with the candidate parameters through the write
    // backend, then measure the real unlock. That is the metric REQUIREMENTS
    // §6.7 calibrates ("解锁约 0.5~1s").
    let unlock_candidates: [(&str, KdfConfig); 4] = [
        ("m64_i6_p4", candidate(64, 6)),
        ("m128_i6_p4", candidate(128, 6)),
        ("m256_i3_p4", candidate(256, 3)),
        ("m256_i6_p4", candidate(256, 6)),
    ];
    println!("PERF unlock_tuning iterations_per_candidate=3 target_ms=500..1000");
    for (label, config) in &unlock_candidates {
        let mut tuned = fixture_file.clone();
        tuned.kdf = config.clone();
        let tuned_bytes =
            write_kdbx_bytes(&tuned, &credentials).expect("re-encrypt with candidate KDF");
        let control = reader::parse(&tuned_bytes, MASTER_PASSWORD).expect("tuned unlock");
        assert_eq!(entry_count(&control.root), ENTRY_COUNT);
        drop(control);
        let unlock = measure(TUNING_UNLOCK_ITERATIONS, || {
            let database =
                reader::parse(&tuned_bytes, MASTER_PASSWORD).expect("tuned unlock during perf run");
            std::hint::black_box(&database);
        });
        print_stats(
            &format!(
                "PERF unlock_tuning_{label} params={}",
                kdf_description(config)
            ),
            &unlock,
            ms,
            "ms",
        );
    }

    // ---- Save (writer::write_model = FFI kp_save = app save) ---------------
    let mut sizes: Vec<usize> = Vec::with_capacity(SAVE_ITERATIONS);
    let mut outputs: BTreeSet<String> = BTreeSet::new();
    let mut sample: Option<Vec<u8>> = None;
    let save = measure(SAVE_ITERATIONS, || {
        let out = writer::write_model(&database, MASTER_PASSWORD).expect("write_model");
        sizes.push(out.len());
        outputs.insert(local_hash(&out));
        std::hint::black_box(&out);
        sample = Some(out);
    });
    print_stats("PERF save_write_model", &save, ms, "ms");
    let saved = sample.expect("one saved sample");
    sizes.sort_unstable();
    let size_min = sizes[0];
    let size_max = sizes[sizes.len() - 1];
    println!(
        "PERF save_write_model_size bytes_sample={} bytes_min={size_min} bytes_max={size_max} distinct_outputs={} n={}",
        saved.len(),
        outputs.len(),
        sizes.len()
    );
    assert!(
        size_max - size_min <= 256,
        "the encrypted container size must be stable; only the backend's XML field order may spill a few compressed bytes (min={size_min}, max={size_max})"
    );
    assert_eq!(
        outputs.len(),
        SAVE_ITERATIONS,
        "every save must draw a fresh salt/IV/master seed"
    );

    let reopened = reader::parse(&saved, MASTER_PASSWORD).expect("saved bytes must open");
    assert_eq!(entry_count(&reopened.root), ENTRY_COUNT);
    let saved_file = read_kdbx_bytes(&saved, &credentials).expect("saved header must be readable");
    println!(
        "PERF save_readback entries={} format={} header={}",
        entry_count(&reopened.root),
        reopened.format_version,
        kdf_description(&saved_file.kdf)
    );

    // ---- Safe-save protocol: serialise+encrypt and verification ----------
    // `kdbx::save::save_atomic` verifies new bytes by re-reading them with the
    // independent reader and comparing normalised models; its tests use
    // `writer::reseal` (same content, fresh crypto), which is the supported
    // combination. `writer::write_model` is documented as non-full-fidelity, so
    // the app's save path (kp_save) does not run the verification pass.
    let resealed = writer::reseal(&bytes, MASTER_PASSWORD).expect("reseal fixture");
    save::verify_rewritten(&bytes, &resealed, MASTER_PASSWORD).expect("reseal pair must verify");

    let reseal = measure(3, || {
        let out = writer::reseal(&bytes, MASTER_PASSWORD).expect("reseal");
        std::hint::black_box(&out);
    });
    print_stats("PERF save_reseal", &reseal, ms, "ms");

    let verify = measure(VERIFY_ITERATIONS, || {
        save::verify_rewritten(&bytes, &resealed, MASTER_PASSWORD).expect("verify_rewritten");
    });
    print_stats("PERF save_verify_rewritten", &verify, ms, "ms");

    let reseal_and_verify = measure(3, || {
        let out = writer::reseal(&bytes, MASTER_PASSWORD).expect("reseal");
        save::verify_rewritten(&bytes, &out, MASTER_PASSWORD).expect("verify_rewritten");
    });
    print_stats("PERF save_reseal_and_verify", &reseal_and_verify, ms, "ms");

    let write_model_verify_accepted =
        save::verify_rewritten(&bytes, &saved, MASTER_PASSWORD).is_ok();
    println!(
        "PERF save_verify_write_model accepted={write_model_verify_accepted} \
         note=verify_rewritten compares normalised models; writer::write_model is non-full-fidelity \
         and is not verified by the app save path"
    );
    let reseal_and_verify_p95_ms = ms(reseal_and_verify.p95);
    println!(
        "PERF save_protocol_target_500ms reseal_and_verify_p95_ms={reseal_and_verify_p95_ms:.3} \
         under_target={} note=includes verification (4 Argon2 derivations); the app save path is \
         save_write_model above",
        reseal_and_verify_p95_ms < 500.0
    );

    // ---- Sync decide / merge ---------------------------------------------
    let remote = realistic_remote(&database);
    // 500 local entries - 1 remote deletion + 1 remote-only entry.
    assert_eq!(entry_count(&remote.root), ENTRY_COUNT);

    let mut state = SyncState::new(SyncBaseline {
        etag: Some("\"etag-local-baseline\"".to_string()),
        sha256: Some(local_hash(&bytes)),
        size: Some(bytes.len() as u64),
        modified_unix: Some(1_760_000_000),
    });
    state.mark_dirty();
    let remote_meta = RemoteMeta {
        etag: Some("\"etag-remote-new\"".to_string()),
        size: bytes.len() as u64 + 4096,
        modified_unix: Some(1_760_000_300),
        sha256: Some("remote-sha256".to_string()),
    };
    let decide = measure(DECIDE_ITERATIONS, || {
        let decision = state.decide(&remote_meta);
        std::hint::black_box(&decision);
    });
    let decision = state.decide(&remote_meta);
    assert_eq!(decision.action, SyncActionKind::DownloadAndMerge);
    assert!(!decision.degraded);
    print_stats(
        "PERF sync_decide_etag_dirty_remote_changed",
        &decide,
        us,
        "us",
    );

    let mut degraded_state = SyncState::new(SyncBaseline {
        etag: None,
        sha256: Some("sha-local-baseline".to_string()),
        size: Some(bytes.len() as u64),
        modified_unix: Some(1_760_000_000),
    });
    degraded_state.mark_dirty();
    let degraded_meta = RemoteMeta {
        etag: None,
        size: bytes.len() as u64 + 4096,
        modified_unix: Some(1_760_000_300),
        sha256: Some("sha-remote-new".to_string()),
    };
    let decide_degraded = measure(DECIDE_ITERATIONS, || {
        let decision = degraded_state.decide(&degraded_meta);
        std::hint::black_box(&decision);
    });
    let degraded_decision = degraded_state.decide(&degraded_meta);
    assert_eq!(degraded_decision.action, SyncActionKind::DownloadAndMerge);
    assert!(degraded_decision.degraded);
    print_stats(
        "PERF sync_decide_degraded_no_etag",
        &decide_degraded,
        us,
        "us",
    );

    let mut merged_sample = None;
    let merge = measure(MERGE_ITERATIONS, || {
        let result = merge_for_sync(&database, &remote);
        std::hint::black_box(&result);
        merged_sample = Some(result);
    });
    print_stats("PERF sync_merge", &merge, ms, "ms");
    let merged = merged_sample.expect("one merge result");
    println!(
        "PERF sync_merge_report entries_added={} entries_updated={} groups_added={} groups_updated={} \
         history_added={} deleted_skipped={} moved={} conflicts={} has_conflicts={} merged_entries={}",
        merged.report.entries_added,
        merged.report.entries_updated,
        merged.report.groups_added,
        merged.report.groups_updated,
        merged.report.history_added,
        merged.report.deleted_skipped,
        merged.report.moved,
        merged.report.conflicts,
        merged.has_conflicts,
        entry_count(&merged.database.root)
    );
    assert_eq!(merged.report.entries_updated, 5);
    assert_eq!(merged.report.entries_added, 1);
    assert_eq!(merged.report.deleted_skipped, 1);
    assert_eq!(merged.report.conflicts, 0);
    assert!(!merged.has_conflicts);
    assert_eq!(
        entry_count(&merged.database.root),
        ENTRY_COUNT,
        "500 local - 1 remote deletion + 1 remote-only entry"
    );

    let conflict_remote = conflicting_remote(&database);
    let mut conflicts_sample = None;
    let merge_conflicts = measure(CONFLICT_MERGE_ITERATIONS, || {
        let result = merge_for_sync(&database, &conflict_remote);
        std::hint::black_box(&result);
        conflicts_sample = Some(result);
    });
    print_stats(
        "PERF sync_merge_same_mtime_conflict",
        &merge_conflicts,
        ms,
        "ms",
    );
    let conflicted = conflicts_sample.expect("one conflict merge result");
    println!(
        "PERF sync_merge_conflict_report conflicts={} conflict_items={} entries_updated={} has_conflicts={}",
        conflicted.report.conflicts,
        conflicted.conflicts.len(),
        conflicted.report.entries_updated,
        conflicted.has_conflicts
    );
    assert_eq!(conflicted.report.conflicts, 1);
    assert_eq!(conflicted.conflicts.len(), 1);
    assert!(conflicted.has_conflicts);

    // ---- Budget verdicts (release builds only) ---------------------------
    if !cfg!(debug_assertions) {
        let save_p95_ms = ms(save.p95);
        println!(
            "PERF save_target_500ms p95_ms={save_p95_ms:.3} under_target={}",
            save_p95_ms < 500.0
        );
        assert!(
            save_p95_ms < 500.0,
            "v1.0 save target is <500 ms on 500 entries; measured p95 = {save_p95_ms} ms"
        );
        let kdf_ms = ms(kdf_default.p50);
        println!(
            "PERF kdf_target_500_1000ms p50_ms={kdf_ms:.3} in_range={} unlock_2x_p50_ms={unlock_component_ms:.3} unlock_in_range={}",
            (500.0..=1000.0).contains(&kdf_ms),
            (500.0..=1000.0).contains(&unlock_component_ms)
        );
    }
}
