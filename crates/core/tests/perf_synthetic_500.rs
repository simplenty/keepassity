// SPDX-License-Identifier: GPL-3.0-or-later
//! Performance evidence for the 500-entry synthetic fixture (M5-03 / M3-04).
//!
//! This is a measurement, not a unit test, so it is ignored by default. Run it
//! in release mode on the machine to be measured:
//!
//! ```text
//! cargo test --release -p keepassity-core --test perf_synthetic_500 -- \
//!     --ignored --nocapture
//! ```
//!
//! What is measured (all in-process, `std::time::Instant`, no extra crates):
//!
//! * `open` — `kdbx::reader::parse` of the fixture bytes. This is the app's
//!   unlock path: KDF (Argon2id) + decrypt + model mapping. The reader derives
//!   the key twice (once for the model, once for the raw XML evidence hash), so
//!   this number is deliberately the real production path, not a shortcut.
//! * `open_and_list` — open followed by flattening groups + entries into the
//!   list rows the UI shows (unlock-to-list latency).
//! * `list` — flatten 7 groups + 500 entry rows on an already-open model.
//! * `search` — the case-insensitive substring search kernel implemented by the
//!   FFI `search` command (`crates/ffi/src/lib.rs`): fields default to
//!   Title/UserName/URL/Notes, stop after `limit` matches (100, the app's
//!   default). Both broad (early-stop) and single-hit queries are measured.
//!
//! Search evidence is compared against the v1.0 target "search stays under
//! 100 ms for a 500-entry database" (release builds only; debug builds are not
//! representative and only print numbers).

use keepassity_core::model::{Database, Group};
use std::collections::BTreeSet;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

const FIXTURE_NAME: &str = "synthetic-500.kdbx";
const MASTER_PASSWORD: &str = "demopass";
const SEARCH_LIMIT: usize = 100;

fn fixture_path() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/fixtures/synthetic")
        .join(FIXTURE_NAME)
}

struct ListRow {
    group: String,
    uuid: String,
    title: String,
    username: String,
    url: String,
    has_totp: bool,
    history: usize,
}

/// Flatten groups + entries the way `list_entries` does for the UI list.
fn flatten(database: &Database) -> (Vec<ListRow>, usize) {
    fn walk(group: &Group, path: &mut Vec<String>, rows: &mut Vec<ListRow>, groups: &mut usize) {
        if path.is_empty() {
            path.push(group.name.clone());
        }
        *groups += 1;
        for entry in &group.entries {
            rows.push(ListRow {
                group: path.join("/"),
                uuid: entry.uuid.to_hex(),
                title: entry.field("Title").unwrap_or("").to_string(),
                username: entry.field("UserName").unwrap_or("").to_string(),
                url: entry.field("URL").unwrap_or("").to_string(),
                has_totp: entry.has_totp(),
                history: entry.history.len(),
            });
        }
        for child in &group.groups {
            path.push(child.name.clone());
            walk(child, path, rows, groups);
            path.pop();
        }
    }
    let mut rows = Vec::with_capacity(512);
    let mut groups = 0usize;
    walk(&database.root, &mut Vec::new(), &mut rows, &mut groups);
    (rows, groups)
}

/// Match count for one query, mirroring the FFI `search` implementation.
fn search_count(database: &Database, query: &str, fields: &[&str], limit: usize) -> usize {
    fn walk(
        group: &Group,
        needle: &str,
        fields: &[&str],
        limit: usize,
        matches: &mut usize,
    ) -> bool {
        for entry in &group.entries {
            if fields.iter().any(|field| {
                entry
                    .field(field)
                    .is_some_and(|value| value.to_lowercase().contains(needle))
            }) {
                *matches += 1;
                if *matches >= limit {
                    return true;
                }
            }
        }
        for child in &group.groups {
            if walk(child, needle, fields, limit, matches) {
                return true;
            }
        }
        false
    }
    let needle = query.to_lowercase();
    let mut matches = 0usize;
    let _ = walk(&database.root, &needle, fields, limit, &mut matches);
    matches
}

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

#[test]
#[ignore = "perf evidence: run explicitly in release mode with --ignored --nocapture"]
fn perf_synthetic_500() {
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

    // ---- open / unlock ---------------------------------------------------
    let warm = keepassity_core::kdbx::reader::parse(&bytes, MASTER_PASSWORD)
        .expect("fixture must open with the documented password");
    let (rows, groups) = flatten(&warm);
    println!(
        "PERF fixture format={} groups={} entries={} list_rows={}",
        warm.format_version,
        groups,
        rows.len(),
        rows.len()
    );

    // Verify that the measured list rows really carry the UI columns, so the
    // list timing is not measuring empty work.
    let distinct_uuids = rows
        .iter()
        .map(|row| &row.uuid)
        .collect::<BTreeSet<_>>()
        .len();
    let distinct_groups = rows
        .iter()
        .map(|row| row.group.as_str())
        .collect::<BTreeSet<_>>()
        .len();
    let totp_rows = rows.iter().filter(|row| row.has_totp).count();
    let history_rows = rows.iter().filter(|row| row.history > 0).count();
    let non_ascii_titles = rows.iter().filter(|row| !row.title.is_ascii()).count();
    let non_ascii_usernames = rows.iter().filter(|row| !row.username.is_ascii()).count();
    let encoded_urls = rows
        .iter()
        .filter(|row| row.url.contains("%E8%B4%A6%E6%88%B7"))
        .count();
    println!(
        "PERF list_rows_shape rows={} distinct_uuids={distinct_uuids} distinct_groups={distinct_groups} totp_rows={totp_rows} history_rows={history_rows} non_ascii_titles={non_ascii_titles} non_ascii_usernames={non_ascii_usernames} encoded_urls={encoded_urls}",
        rows.len()
    );
    assert_eq!(rows.len(), 500);
    assert_eq!(distinct_uuids, 500);
    assert_eq!(distinct_groups, 6);
    assert_eq!(totp_rows, 20);
    assert_eq!(history_rows, 10);
    assert_eq!(non_ascii_titles, 39);
    assert_eq!(non_ascii_usernames, 30);
    assert_eq!(encoded_urls, 46);
    drop(rows);

    let open = measure(10, || {
        let database = keepassity_core::kdbx::reader::parse(&bytes, MASTER_PASSWORD)
            .expect("open during perf run");
        std::hint::black_box(&database);
    });
    print_stats("PERF open", &open, ms, "ms");

    let open_and_list = measure(10, || {
        let database = keepassity_core::kdbx::reader::parse(&bytes, MASTER_PASSWORD)
            .expect("open during perf run");
        let (rows, groups) = flatten(&database);
        std::hint::black_box((&rows, groups));
    });
    print_stats("PERF open_and_list", &open_and_list, ms, "ms");

    // ---- list ------------------------------------------------------------
    let database = warm;
    let list = measure(200, || {
        let (rows, groups) = flatten(&database);
        std::hint::black_box((&rows, groups));
    });
    print_stats("PERF list_groups_entries", &list, us, "us");

    // ---- search ----------------------------------------------------------
    let default_fields = ["Title", "UserName", "URL", "Notes"];
    let cases: Vec<(&str, Vec<&str>)> = vec![
        ("synthetic", default_fields.to_vec()),
        ("entry 042", default_fields.to_vec()),
        ("user0042", default_fields.to_vec()),
        ("account/0042", default_fields.to_vec()),
        ("keyword-0042", default_fields.to_vec()),
        ("条目 052", default_fields.to_vec()),
        ("recovery-0040", vec!["Recovery"]),
        ("zzzz-no-such-entry", default_fields.to_vec()),
    ];
    println!(
        "PERF search_kernel fields=Title,UserName,URL,Notes limit={SEARCH_LIMIT} iterations=200"
    );
    let mut slowest_p95 = 0.0f64;
    for (query, fields) in &cases {
        let expected_hits = search_count(&database, query, fields, SEARCH_LIMIT);
        let mut samples = Vec::with_capacity(200);
        for _ in 0..200 {
            let started = Instant::now();
            let hits = search_count(&database, query, fields, SEARCH_LIMIT);
            let elapsed = started.elapsed();
            assert_eq!(
                hits, expected_hits,
                "search result changed during measurement"
            );
            samples.push(elapsed);
        }
        let stats = stats(&mut samples);
        slowest_p95 = slowest_p95.max(us(stats.p95));
        println!(
            "PERF search query=\"{query}\" fields={} hits={} n={} mean_us={:.1} min_us={:.1} p50_us={:.1} p95_us={:.1} max_us={:.1}",
            fields.join("+"),
            expected_hits,
            stats.n,
            us(stats.total) / stats.n as f64,
            us(stats.min),
            us(stats.p50),
            us(stats.p95),
            us(stats.max)
        );
    }
    println!(
        "PERF search_target_100ms slowest_p95_us={slowest_p95:.1} under_target={}",
        slowest_p95 < 100_000.0
    );

    if !cfg!(debug_assertions) {
        assert!(
            slowest_p95 < 100_000.0,
            "v1.0 search target is <100 ms per query on 500 entries; measured p95 = {slowest_p95} us"
        );
    }
}
