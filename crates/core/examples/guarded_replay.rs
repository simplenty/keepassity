// SPDX-License-Identifier: GPL-3.0-or-later
//! Replay one KDBX byte buffer through the product reader and classify the
//! outcome. CI uses it to decide whether a libFuzzer crash artifact is a
//! *known, guarded* upstream panic or a real problem in our code.
//!
//! Background: `libfuzzer-sys` installs a panic hook that aborts the process
//! before unwinding, so any panic inside a fuzz target is reported as a crash -
//! including panics raised by the third-party `keepass` reader. The product
//! guards those with `catch_unwind` and returns `KdbxReadError`, so a fuzz
//! "crash" that this tool replays as a normal error is a hardened upstream
//! panic, not a product crash. Anything this tool reports as `PANICKED` or
//! `PARSED` (when it should not) is not covered by the guard.
//!
//! Exit codes:
//!   0  the guard converted the input into a read error (expected for crash
//!      artifacts of upstream panics);
//!   2  the file parsed successfully (the artifact is not a crash input);
//!   3  the process panicked/aborted despite the guard (real coverage gap);
//!   4  usage/IO error (CI problem, not a fuzz finding).

use std::io::Read;

fn main() {
    let Some(path) = std::env::args().nth(1) else {
        eprintln!("usage: guarded_replay <file.kdbx> [password]");
        std::process::exit(4);
    };
    let password = std::env::args().nth(2).unwrap_or_else(|| "demopass".to_string());
    let mut bytes = Vec::new();
    match std::fs::File::open(&path).and_then(|mut file| file.read_to_end(&mut bytes)) {
        Ok(_) => {}
        Err(error) => {
            eprintln!("cannot read {path}: {error}");
            std::process::exit(4);
        }
    }
    match keepassity_core::kdbx::reader::parse(&bytes, &password) {
        Ok(_) => {
            println!("PARSED {path}");
            std::process::exit(2);
        }
        Err(error) => {
            println!("GUARDED {path}: {error}");
            std::process::exit(0);
        }
    }
}
