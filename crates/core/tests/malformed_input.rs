// SPDX-License-Identifier: GPL-3.0-or-later
//! Regression coverage for malformed KDBX input (M5-02 fuzz finding).
//!
//! The fuzzer found that the upstream `keepass` 0.17 reader panics instead of
//! returning an error on some malformed headers (a variant dictionary whose
//! length field points past the end of the buffer indexes out of bounds inside
//! `byteorder::read_u64`). A database can arrive from an untrusted place - a
//! WebDAV share, a shared folder, a file manager hand-off - so the reader must
//! reject it with a normal error and never abort the process.
//!
//! `data/malformed-kdbx4-variant-dictionary.bin` is the artifact libFuzzer
//! produced (`crash-752a6ab1e51be0e005297e36804aa02e35e3d4fd`, 2319 bytes) and
//! was minimized by hand around the malformed header only; the password is the
//! public fixture password `demopass`, the bytes are synthetic.

const CRASH: &[u8] = include_bytes!("data/malformed-kdbx4-variant-dictionary.bin");

#[test]
fn malformed_variant_dictionary_is_a_read_error_not_a_panic() {
    let result = keepassity_core::kdbx::reader::parse(CRASH, "demopass");
    match result {
        Err(keepassity_core::kdbx::reader::KdbxReadError::Corrupt(message)) => {
            assert!(
                message.contains("malformed") || message.contains("corrupt"),
                "unexpected corrupt reason: {message}"
            );
        }
        Err(other) => panic!("expected a corrupt-file error, got {other:?}"),
        Ok(_) => panic!("a malformed file must never parse successfully"),
    }
}

/// A truncated file (shorter than the 12-byte header) is also a read error.
#[test]
fn short_input_is_a_header_error() {
    for length in 0..12 {
        let result = keepassity_core::kdbx::reader::parse(&CRASH[..length], "demopass");
        assert!(result.is_err(), "a {length}-byte input must not parse");
    }
}

/// Garbage of every length up to the header size must not panic either. This
/// is deliberately tiny (no fuzzing in unit tests) and only covers the cheapest
/// path the fuzzer exercises first.
#[test]
fn random_short_inputs_never_panic() {
    let mut state: u64 = 0x9E37_79B9_7F4A_7C15;
    for length in 0..64usize {
        let mut bytes = Vec::with_capacity(length);
        for _ in 0..length {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            bytes.push((state & 0xFF) as u8);
        }
        let _ = keepassity_core::kdbx::reader::parse(&bytes, "demopass");
    }
}
