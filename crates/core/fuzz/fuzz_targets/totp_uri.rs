// SPDX-License-Identifier: GPL-3.0-or-later
//! Fuzz target: TOTP / otpauth / Base32 text input.
//!
//! Covers the three text surfaces:
//! 1. `otpauth://` / `steam://` URI parsing (`totp::fields::parse_uri`) and its
//!    canonical round-trip, followed by code generation with the parsed config;
//! 2. Base32 secret decoding (`totp::base32_decode`), the fallback the UI uses
//!    when a pasted secret is not a URI;
//! 3. entry-field reading (`totp::fields::read_fields`): attacker-controlled
//!    database content (`otp`, legacy `TOTP Seed`/`TOTP Settings`, KeePass
//!    `TimeOtp-*` fields).
//!
//! It also builds `TotpConfig` values straight from bytes (secret / algorithm /
//! digits / period / encoder) to check the HMAC/code arithmetic never panics on
//! edge values (`period = 0`, huge `unix_time`, empty secret, Steam encoder).
//!
//! Note: the core accepts `otpauth://` and `steam://`. `otp://` is not a
//! supported core scheme (see README); the URI parser is still fuzzed with it
//! because it is rejected through the same code path.
#![no_main]

use keepassity_core::totp::{
    base32_decode, fields, generate_at, hotp, prefetch, TotpAlgorithm, TotpConfig, TotpEncoder,
};
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    let text = String::from_utf8_lossy(data);
    let text: &str = &text;

    // 1) URI parser + canonical round-trip + generation.
    if let Ok(spec) = fields::parse_uri(text) {
        if let Ok(uri) = fields::to_uri(&spec) {
            let _ = fields::parse_uri(&uri);
            let _ = generate_at(&spec.config, u64::MAX);
            let _ = generate_at(&spec.config, 0);
            let _ = prefetch(&spec.config, u64::MAX - 1, 3);
        }
    }

    // 2) Base32 decoder.
    let _ = base32_decode(text);

    // 3) Entry field readers (all sources at once, then single sources so the
    //    priority order is exercised in every branch).
    let all = [
        ("otp", text),
        ("TOTP Seed", text),
        ("TOTP Settings", text),
        ("TimeOtp-Secret-Base32", text),
        ("TimeOtp-Period", text),
        ("TimeOtp-Length", text),
        ("TimeOtp-Algorithm", text),
    ];
    let _ = fields::read_fields(&all);
    for key in ["otp", "TOTP Seed", "TimeOtp-Secret-Base32"] {
        let _ = fields::read_fields(&[(key, text)]);
    }
    let _ = fields::read_fields(&[("TOTP Seed", text), ("TOTP Settings", text)]);
    let _ = fields::read_fields(&[
        ("TimeOtp-Secret-Base32", text),
        ("TimeOtp-Period", text),
        ("TimeOtp-Length", text),
        ("TimeOtp-Algorithm", text),
    ]);

    // 4) Direct generator configurations built from bytes.
    if let Some((first, secret)) = data.split_first() {
        let algorithm = match first & 0b11 {
            0 => TotpAlgorithm::Sha1,
            1 => TotpAlgorithm::Sha256,
            2 => TotpAlgorithm::Sha512,
            _ => TotpAlgorithm::Sha1,
        };
        let encoder = if first & 0b100 != 0 {
            TotpEncoder::Steam
        } else {
            TotpEncoder::Totp
        };
        // Exercise both valid digit counts, zero period and a large period.
        let period = match first & 0b11 {
            0 => 0,
            1 => 1,
            2 => 30,
            _ => u32::from(data.get(1).copied().unwrap_or(30)) + 1,
        };
        let config = TotpConfig {
            secret: secret.to_vec(),
            algorithm,
            digits: if first & 0b1000 != 0 { 8 } else { 6 },
            period,
            encoder,
        };
        let at = if data.len() >= 8 {
            u64::from_le_bytes([
                data[0], data[1], data[2], data[3], data[4], data[5], data[6], data[7],
            ])
        } else {
            u64::MAX
        };
        let _ = generate_at(&config, at);
        let _ = prefetch(&config, at, 2);
        // HOTP shares the dynamic-truncation code; invalid digit counts must
        // be rejected, not panic.
        let _ = hotp(secret, algorithm, at, if first & 0b1000 != 0 { 8 } else { 6 });
        let _ = hotp(secret, algorithm, at, u32::from(*first));
    }
});
