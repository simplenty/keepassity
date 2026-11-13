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

/// A KDBX 3.1 header can name an arbitrary AES-KDF round count; a hostile file
/// with ~2e18 rounds would keep the reader busy for minutes (the daily fuzzing
/// job produced exactly this input: `timeout-87bf88e5...`, minimized bytes are
/// committed as `data/extreme-kdf-rounds.bin`). The reader must reject it
/// immediately with a normal, explainable error instead of blocking the UI.
#[test]
fn extreme_kdf_parameters_are_rejected_quickly() {
    const EXTREME: &[u8] = include_bytes!("data/extreme-kdf-rounds.bin");
    let started = std::time::Instant::now();
    let result = keepassity_core::kdbx::reader::parse(EXTREME, "demopass");
    let elapsed = started.elapsed();
    match result {
        Err(keepassity_core::kdbx::reader::KdbxReadError::Unsupported(message)) => {
            assert!(
                message.contains("extreme") && message.contains("AES-KDF"),
                "unexpected safety-limit reason: {message}"
            );
        }
        Err(other) => panic!("expected the KDF safety-limit error, got {other:?}"),
        Ok(_) => panic!("an extreme-KDF file must never parse"),
    }
    assert!(
        elapsed < std::time::Duration::from_secs(2),
        "rejecting the file must not spend the KDF time (took {elapsed:?})"
    );
}

fn variant_entry(value_type: u8, key: &[u8], value: &[u8]) -> Vec<u8> {
    let mut entry = vec![value_type];
    entry.extend_from_slice(&(key.len() as u32).to_le_bytes());
    entry.extend_from_slice(key);
    entry.extend_from_slice(&(value.len() as u32).to_le_bytes());
    entry.extend_from_slice(value);
    entry
}

fn aes_kdf_dictionary(rounds: u64) -> Vec<u8> {
    const UUID_AES: [u8; 16] = [
        0xC9, 0xD9, 0xF3, 0x9A, 0x62, 0x8A, 0x44, 0x60, 0xBF, 0x74, 0x0D, 0x08, 0xC1, 0x8A, 0x4F,
        0xEA,
    ];
    let mut dictionary = vec![0x00, 0x01];
    // KDBX 4 stores `$UUID` as VariantDictionary ByteArray (0x42).
    dictionary.extend_from_slice(&variant_entry(0x42, b"$UUID", &UUID_AES));
    dictionary.extend_from_slice(&variant_entry(0x05, b"R", &rounds.to_le_bytes()));
    dictionary.push(0);
    dictionary
}

fn kdbx4_header(dictionaries: &[Vec<u8>]) -> Vec<u8> {
    let mut bytes = vec![
        0x03, 0xD9, 0xA2, 0x9A, 0x67, 0xFB, 0x4B, 0xB5, 0x00, 0x00, 0x04, 0x00,
    ];
    for dictionary in dictionaries {
        bytes.push(0x0B);
        bytes.extend_from_slice(&(dictionary.len() as u32).to_le_bytes());
        bytes.extend_from_slice(dictionary);
    }
    bytes.push(0x00);
    bytes.extend_from_slice(&4u32.to_le_bytes());
    bytes.extend_from_slice(&[0x0D, 0x0A, 0x0D, 0x0A]);
    bytes
}

/// Legal KDBX 4 uses a ByteArray `$UUID`. A file that asks for 30M AES-KDF
/// rounds through that shape must be rejected before the key derivation runs.
#[test]
fn kdbx4_byte_array_kdf_with_extreme_rounds_is_rejected_quickly() {
    let bytes = kdbx4_header(&[aes_kdf_dictionary(30_000_000)]);
    let started = std::time::Instant::now();
    let result = keepassity_core::kdbx::reader::parse(&bytes, "demopass");
    let elapsed = started.elapsed();
    match result {
        Err(keepassity_core::kdbx::reader::KdbxReadError::Unsupported(message)) => {
            assert!(
                message.contains("extreme") && message.contains("AES-KDF"),
                "unexpected safety-limit reason: {message}"
            );
        }
        Err(other) => panic!("expected the KDF safety-limit error, got {other:?}"),
        Ok(_) => panic!("an extreme-KDF file must never parse"),
    }
    assert!(
        elapsed < std::time::Duration::from_secs(2),
        "rejecting the file must not spend the KDF time (took {elapsed:?})"
    );
}

/// Two KDF-parameters fields would let keepass use the last one while a
/// one-pass validator inspected the first. Reject the ambiguous header.
#[test]
fn duplicate_kdbx4_kdf_header_fields_are_rejected() {
    let bytes = kdbx4_header(&[aes_kdf_dictionary(600_000), aes_kdf_dictionary(30_000_000)]);
    let result = keepassity_core::kdbx::reader::parse(&bytes, "demopass");
    assert!(
        matches!(
            result,
            Err(keepassity_core::kdbx::reader::KdbxReadError::Header(_))
        ),
        "duplicate KDF fields must be a header error, got {result:?}"
    );
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
