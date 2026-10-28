// SPDX-License-Identifier: GPL-3.0-or-later
//! TOTP / HOTP generation (RFC 6238 / RFC 4226).
//!
//! Supports SHA-1/SHA-256/SHA-512, 6/8-digit codes, custom periods, Steam's
//! five-character alphabet, N-step prefetch and clock-skew detection.
//!
//! Secrets are kept in the encrypted database; this module never logs them and
//! error messages never include secret bytes. A TOTP code is not a password:
//! callers should not persist generated codes.

use hmac::{Hmac, Mac};
use std::fmt;

pub mod fields;

/// TOTP hash algorithm.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TotpAlgorithm {
    /// HMAC-SHA1 (RFC 6238 default).
    Sha1,
    /// HMAC-SHA256.
    Sha256,
    /// HMAC-SHA512.
    Sha512,
}

impl TotpAlgorithm {
    /// Display name used by the DTO / otpauth URI.
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Sha1 => "SHA1",
            Self::Sha256 => "SHA256",
            Self::Sha512 => "SHA512",
        }
    }
}

/// TOTP encoder flavour.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TotpEncoder {
    /// Standard TOTP digits.
    Totp,
    /// Steam's 5-character alphabet.
    Steam,
}

/// TOTP configuration (secret already decoded from Base32).
#[derive(Clone, PartialEq, Eq)]
pub struct TotpConfig {
    /// Decoded secret bytes. Never log or dump this field.
    pub secret: Vec<u8>,
    /// Hash algorithm.
    pub algorithm: TotpAlgorithm,
    /// Code digits for standard TOTP (6 or 8); Steam always uses 5.
    pub digits: u32,
    /// Time step in seconds.
    pub period: u32,
    /// Encoder flavour.
    pub encoder: TotpEncoder,
}

impl fmt::Debug for TotpConfig {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("TotpConfig")
            .field("secret", &"<redacted>")
            .field("algorithm", &self.algorithm)
            .field("digits", &self.digits)
            .field("period", &self.period)
            .field("encoder", &self.encoder)
            .finish()
    }
}

/// One generated code and its validity window.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TotpCode {
    /// Code string (zero-padded for standard TOTP).
    pub code: String,
    /// Seconds remaining in the current step (1..=period).
    pub remaining: u32,
    /// Time step used.
    pub step: u64,
    /// Period used.
    pub period: u32,
    /// Digits shown.
    pub digits: u32,
    /// Algorithm used.
    pub algorithm: TotpAlgorithm,
}

/// TOTP/HOTP errors.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum TotpError {
    /// Empty secret.
    EmptySecret,
    /// Unsupported digit count (standard TOTP only supports 6 or 8).
    InvalidDigits(u32),
    /// Zero period.
    InvalidPeriod,
    /// Secret is not valid Base32.
    InvalidBase32,
    /// otpauth/steam URI or field settings are malformed.
    InvalidUri,
    /// HMAC could not be initialised.
    Hmac,
}

impl fmt::Display for TotpError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::EmptySecret => write!(f, "TOTP secret is empty"),
            Self::InvalidDigits(digits) => write!(f, "unsupported TOTP digits: {digits}"),
            Self::InvalidPeriod => write!(f, "TOTP period must be positive"),
            Self::InvalidBase32 => write!(f, "TOTP secret is not valid Base32"),
            Self::InvalidUri => write!(f, "TOTP URI or settings are malformed"),
            Self::Hmac => write!(f, "cannot initialise TOTP HMAC"),
        }
    }
}

impl std::error::Error for TotpError {}

const STEAM_ALPHABET: &[u8] = b"23456789BCDFGHJKMNPQRTVWXY";

impl TotpConfig {
    /// Total number of digits the current encoder produces.
    pub fn output_digits(&self) -> u32 {
        match self.encoder {
            TotpEncoder::Totp => self.digits,
            TotpEncoder::Steam => 5,
        }
    }

    fn validate(&self) -> Result<(), TotpError> {
        if self.secret.is_empty() {
            return Err(TotpError::EmptySecret);
        }
        if self.period == 0 {
            return Err(TotpError::InvalidPeriod);
        }
        if self.encoder == TotpEncoder::Totp && !matches!(self.digits, 6 | 8) {
            return Err(TotpError::InvalidDigits(self.digits));
        }
        Ok(())
    }
}

/// Generate the TOTP code for `unix_time` (seconds since the Unix epoch).
pub fn generate_at(config: &TotpConfig, unix_time: u64) -> Result<TotpCode, TotpError> {
    config.validate()?;
    let period = u64::from(config.period);
    let step = unix_time / period;
    let remaining = (period - (unix_time % period)) as u32;
    let code = code_for_step(config, step)?;
    Ok(TotpCode {
        code,
        remaining,
        step,
        period: config.period,
        digits: config.output_digits(),
        algorithm: config.algorithm,
    })
}

/// Generate `steps` consecutive codes starting at `unix_time` (inclusive).
///
/// Useful for showing the next codes while the current one is about to expire.
pub fn prefetch(
    config: &TotpConfig,
    unix_time: u64,
    steps: usize,
) -> Result<Vec<TotpCode>, TotpError> {
    config.validate()?;
    let mut out = Vec::with_capacity(steps);
    for offset in 0..steps {
        out.push(generate_at(
            config,
            unix_time + offset as u64 * u64::from(config.period),
        )?);
    }
    Ok(out)
}

fn code_for_step(config: &TotpConfig, step: u64) -> Result<String, TotpError> {
    let digest = hmac_digest(config.algorithm, &config.secret, &step.to_be_bytes())?;
    let offset = (digest[digest.len() - 1] & 0x0f) as usize;
    let binary = u32::from_be_bytes([
        digest[offset],
        digest[offset + 1],
        digest[offset + 2],
        digest[offset + 3],
    ]) & 0x7fff_ffff;

    match config.encoder {
        TotpEncoder::Totp => {
            let modulus = 10u32.pow(config.digits);
            Ok(format!(
                "{:0width$}",
                binary % modulus,
                width = config.digits as usize
            ))
        }
        TotpEncoder::Steam => {
            let mut value = binary;
            let mut code = String::with_capacity(5);
            for _ in 0..5 {
                let index = (value % STEAM_ALPHABET.len() as u32) as usize;
                code.push(STEAM_ALPHABET[index] as char);
                value /= STEAM_ALPHABET.len() as u32;
            }
            Ok(code)
        }
    }
}

/// HOTP (RFC 4226) code for an explicit counter, for tests and Steam counters.
pub fn hotp(
    secret: &[u8],
    algorithm: TotpAlgorithm,
    counter: u64,
    digits: u32,
) -> Result<String, TotpError> {
    if secret.is_empty() {
        return Err(TotpError::EmptySecret);
    }
    if !matches!(digits, 6 | 8) {
        return Err(TotpError::InvalidDigits(digits));
    }
    let digest = hmac_digest(algorithm, secret, &counter.to_be_bytes())?;
    let offset = (digest[digest.len() - 1] & 0x0f) as usize;
    let binary = u32::from_be_bytes([
        digest[offset],
        digest[offset + 1],
        digest[offset + 2],
        digest[offset + 3],
    ]) & 0x7fff_ffff;
    let modulus = 10u32.pow(digits);
    Ok(format!(
        "{:0width$}",
        binary % modulus,
        width = digits as usize
    ))
}

fn hmac_digest(algorithm: TotpAlgorithm, key: &[u8], message: &[u8]) -> Result<Vec<u8>, TotpError> {
    match algorithm {
        TotpAlgorithm::Sha1 => {
            let mut mac = Hmac::<sha1::Sha1>::new_from_slice(key).map_err(|_| TotpError::Hmac)?;
            mac.update(message);
            Ok(mac.finalize().into_bytes().to_vec())
        }
        TotpAlgorithm::Sha256 => {
            let mut mac = Hmac::<sha2::Sha256>::new_from_slice(key).map_err(|_| TotpError::Hmac)?;
            mac.update(message);
            Ok(mac.finalize().into_bytes().to_vec())
        }
        TotpAlgorithm::Sha512 => {
            let mut mac = Hmac::<sha2::Sha512>::new_from_slice(key).map_err(|_| TotpError::Hmac)?;
            mac.update(message);
            Ok(mac.finalize().into_bytes().to_vec())
        }
    }
}

/// Whether the local clock is more than 30 seconds away from a trusted
/// reference (NTP/server time). A warning should be surfaced to the user but
/// must not silently block code generation.
pub fn clock_skew_warning(now_unix: u64, reference_unix: u64) -> bool {
    now_unix.abs_diff(reference_unix) > 30
}

/// Encode bytes as RFC 4648 Base32 without padding.
pub fn base32_encode(bytes: &[u8]) -> String {
    const ALPHABET: &[u8; 32] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    let mut out = String::with_capacity((bytes.len() * 8).div_ceil(5));
    let mut buffer: u32 = 0;
    let mut bits: u32 = 0;
    for byte in bytes {
        buffer = (buffer << 8) | u32::from(*byte);
        bits += 8;
        while bits >= 5 {
            bits -= 5;
            out.push(ALPHABET[((buffer >> bits) & 0x1f) as usize] as char);
        }
    }
    if bits > 0 {
        out.push(ALPHABET[((buffer << (5 - bits)) & 0x1f) as usize] as char);
    }
    out
}

/// Decode RFC 4648 Base32 (case-insensitive, optional `=` padding, spaces and
/// hyphens ignored) into secret bytes.
pub fn base32_decode(secret: &str) -> Result<Vec<u8>, TotpError> {
    let mut out = Vec::new();
    let mut buffer: u32 = 0;
    let mut bits: u32 = 0;
    let mut padding_seen = false;
    for byte in secret.bytes() {
        let value = match byte {
            b'A'..=b'Z' => byte - b'A',
            b'a'..=b'z' => byte - b'a',
            b'2'..=b'7' => byte - b'2' + 26,
            b'=' => {
                padding_seen = true;
                continue;
            }
            b' ' | b'\t' | b'\r' | b'\n' | b'-' => continue,
            _ => return Err(TotpError::InvalidBase32),
        };
        if padding_seen {
            return Err(TotpError::InvalidBase32);
        }
        buffer = (buffer << 5) | u32::from(value);
        bits += 5;
        if bits >= 8 {
            bits -= 8;
            out.push((buffer >> bits) as u8);
            buffer &= (1 << bits) - 1;
        }
    }
    if out.is_empty() {
        return Err(TotpError::InvalidBase32);
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn config(secret: &[u8], algorithm: TotpAlgorithm) -> TotpConfig {
        TotpConfig {
            secret: secret.to_vec(),
            algorithm,
            digits: 8,
            period: 30,
            encoder: TotpEncoder::Totp,
        }
    }

    #[test]
    fn rfc6238_sha1_vectors() {
        let cfg = config(b"12345678901234567890", TotpAlgorithm::Sha1);
        let cases = [
            (59, "94287082"),
            (1_111_111_109, "07081804"),
            (1_111_111_111, "14050471"),
            (1_234_567_890, "89005924"),
            (2_000_000_000, "69279037"),
            (20_000_000_000, "65353130"),
        ];
        for (time, expected) in cases {
            assert_eq!(generate_at(&cfg, time).unwrap().code, expected, "t={time}");
        }
    }

    #[test]
    fn rfc6238_sha256_vectors() {
        let cfg = config(b"12345678901234567890123456789012", TotpAlgorithm::Sha256);
        let cases = [
            (59, "46119246"),
            (1_111_111_109, "68084774"),
            (1_111_111_111, "67062674"),
            (1_234_567_890, "91819424"),
            (2_000_000_000, "90698825"),
            (20_000_000_000, "77737706"),
        ];
        for (time, expected) in cases {
            assert_eq!(generate_at(&cfg, time).unwrap().code, expected, "t={time}");
        }
    }

    #[test]
    fn rfc6238_sha512_vectors() {
        let cfg = config(
            b"1234567890123456789012345678901234567890123456789012345678901234",
            TotpAlgorithm::Sha512,
        );
        let cases = [
            (59, "90693936"),
            (1_111_111_109, "25091201"),
            (1_111_111_111, "99943326"),
            (1_234_567_890, "93441116"),
            (2_000_000_000, "38618901"),
            (20_000_000_000, "47863826"),
        ];
        for (time, expected) in cases {
            assert_eq!(generate_at(&cfg, time).unwrap().code, expected, "t={time}");
        }
    }

    #[test]
    fn rfc4226_hotp_vectors() {
        let secret = b"12345678901234567890";
        let expected = [
            "755224", "287082", "359152", "969429", "338314", "254676", "287922", "162583",
            "399871", "520489",
        ];
        for (counter, expected) in expected.iter().enumerate() {
            assert_eq!(
                hotp(secret, TotpAlgorithm::Sha1, counter as u64, 6).unwrap(),
                *expected,
                "counter={counter}"
            );
        }
    }

    #[test]
    fn six_digit_totp_is_zero_padded() {
        let mut cfg = config(b"12345678901234567890", TotpAlgorithm::Sha1);
        cfg.digits = 6;
        let code = generate_at(&cfg, 59).unwrap();
        assert_eq!(code.code.len(), 6);
        assert_eq!(code.code, "287082");
    }

    #[test]
    fn custom_period_and_remaining() {
        let mut cfg = config(b"12345678901234567890", TotpAlgorithm::Sha1);
        cfg.period = 60;
        let code = generate_at(&cfg, 121).unwrap();
        assert_eq!(code.step, 2);
        assert_eq!(code.remaining, 59);
        assert_eq!(code.period, 60);
    }

    #[test]
    fn steam_uses_five_chars_from_alphabet() {
        let mut cfg = config(b"12345678901234567890", TotpAlgorithm::Sha1);
        cfg.encoder = TotpEncoder::Steam;
        cfg.digits = 6; // ignored by Steam
        let code = generate_at(&cfg, 59).unwrap();
        assert_eq!(code.code.len(), 5);
        assert!(code.code.bytes().all(|byte| STEAM_ALPHABET.contains(&byte)));
        assert_eq!(code.digits, 5);
    }

    #[test]
    fn prefetch_returns_consecutive_steps() {
        let cfg = config(b"12345678901234567890", TotpAlgorithm::Sha1);
        let codes = prefetch(&cfg, 59, 3).unwrap();
        assert_eq!(codes.len(), 3);
        assert_eq!(codes[0].step, 1);
        assert_eq!(codes[1].step, 2);
        assert_eq!(codes[2].step, 3);
        assert_eq!(codes[0].code, generate_at(&cfg, 59).unwrap().code);
    }

    #[test]
    fn clock_skew_threshold_is_30_seconds() {
        assert!(!clock_skew_warning(1000, 1030));
        assert!(clock_skew_warning(1000, 1031));
        assert!(clock_skew_warning(1031, 1000));
    }

    #[test]
    fn base32_decode_handles_rfc_secret() {
        let secret = base32_decode("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ").unwrap();
        assert_eq!(secret, b"12345678901234567890");
        assert_eq!(
            base32_decode("gezd gnbv gy3t qojq gezd gnbv gy3t qojq").unwrap(),
            secret
        );
        assert_eq!(base32_decode("MZXW6===").unwrap(), b"foo");
    }

    #[test]
    fn base32_decode_rejects_invalid() {
        assert_eq!(base32_decode(""), Err(TotpError::InvalidBase32));
        assert_eq!(base32_decode("!!!!"), Err(TotpError::InvalidBase32));
        assert_eq!(base32_decode("MZXW6=AAA"), Err(TotpError::InvalidBase32));
    }

    #[test]
    fn debug_does_not_leak_secret() {
        let cfg = config(b"super-secret", TotpAlgorithm::Sha1);
        let debug = format!("{cfg:?}");
        assert!(debug.contains("<redacted>"));
        assert!(!debug.contains("super-secret"));
    }

    #[test]
    fn invalid_config_is_rejected() {
        let mut cfg = config(b"secret", TotpAlgorithm::Sha1);
        cfg.digits = 7;
        assert_eq!(generate_at(&cfg, 0), Err(TotpError::InvalidDigits(7)));
        let mut cfg = config(b"secret", TotpAlgorithm::Sha1);
        cfg.period = 0;
        assert_eq!(generate_at(&cfg, 0), Err(TotpError::InvalidPeriod));
        let mut cfg = config(&[], TotpAlgorithm::Sha1);
        cfg.secret.clear();
        assert_eq!(generate_at(&cfg, 0), Err(TotpError::EmptySecret));
    }
}
