// SPDX-License-Identifier: GPL-3.0-or-later
//! TOTP field compatibility and dual-write (M1-06).
//!
//! Read priority (plan §5.2): `otp` (otpauth URI) → legacy KeePassXC
//! `TOTP Seed` + `TOTP Settings` → KeePass 2.x `TimeOtp-*`.
//!
//! Write: canonical `otp` URI (protected) **and** the KeePass 2.x
//! `TimeOtp-*` fields, while removing the legacy pair so readers do not see
//! conflicting configurations.

use super::{
    base32_decode, base32_encode, generate_at, TotpAlgorithm, TotpConfig, TotpEncoder, TotpError,
};
use crate::model::Entry;
use std::fmt;

/// A TOTP configuration plus the account/issuer metadata carried by otpauth.
#[derive(Clone, PartialEq, Eq)]
pub struct TotpSpec {
    /// Generator configuration (secret is in `config.secret`).
    pub config: TotpConfig,
    /// Issuer from the URI/query.
    pub issuer: Option<String>,
    /// Account label.
    pub account: Option<String>,
}

impl fmt::Debug for TotpSpec {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("TotpSpec")
            .field("config", &self.config)
            .field("issuer", &self.issuer)
            .field("account", &self.account)
            .finish()
    }
}

/// Where a parsed TOTP configuration came from.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TotpSource {
    /// Canonical `otp` field (otpauth URI).
    OtpUri,
    /// Legacy KeePassXC `TOTP Seed` + `TOTP Settings`.
    KeepassXcLegacy,
    /// KeePass 2.x `TimeOtp-*`.
    TimeOtp,
}

/// Parsed TOTP and source diagnostics.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TotpRead {
    /// Parsed configuration.
    pub spec: TotpSpec,
    /// Source that won the priority order.
    pub source: TotpSource,
    /// Other sources also present (details page should warn about conflicts).
    pub multiple_sources: bool,
}

/// A field update produced by [`dual_write`].
#[derive(Clone, PartialEq, Eq)]
pub struct TotpFieldUpdate {
    /// Fields to set (key/value/protected).
    pub set: Vec<TotpFieldValue>,
    /// Field keys to remove.
    pub remove: Vec<String>,
}

/// One field to write.
#[derive(Clone, PartialEq, Eq)]
pub struct TotpFieldValue {
    /// Field key.
    pub key: String,
    /// Field value.
    pub value: String,
    /// Whether the field is protected.
    pub protected: bool,
}

/// Parse an otpauth/steam URI into a [`TotpSpec`].
pub fn parse_uri(uri: &str) -> Result<TotpSpec, TotpError> {
    let uri = uri.trim();
    let (scheme, rest) = if let Some(rest) = uri.strip_prefix("otpauth://") {
        ("otpauth", rest)
    } else if let Some(rest) = uri.strip_prefix("steam://") {
        ("steam", rest)
    } else {
        return Err(TotpError::InvalidUri);
    };
    let rest = rest.split('#').next().unwrap_or(rest);
    let (kind, rest) = match scheme {
        "otpauth" => rest.split_once('/').ok_or(TotpError::InvalidUri)?,
        _ => ("totp", rest),
    };
    if !matches!(kind, "totp" | "steam") {
        return Err(TotpError::InvalidUri);
    }
    let (label, query) = match rest.split_once('?') {
        Some((label, query)) => (label, query),
        None => (rest, ""),
    };
    let label = percent_decode(label);
    let (issuer, account) = match label.split_once(':') {
        Some((issuer, account)) => (
            Some(percent_decode(issuer.trim())).filter(|value| !value.is_empty()),
            Some(percent_decode(account.trim())).filter(|value| !value.is_empty()),
        ),
        None => (None, Some(label).filter(|value| !value.is_empty())),
    };
    let mut secret = None;
    let mut algorithm = TotpAlgorithm::Sha1;
    let steam = scheme == "steam" || kind == "steam";
    let mut digits = if steam { 5 } else { 6 };
    let mut period = 30u32;
    let mut encoder = if steam {
        TotpEncoder::Steam
    } else {
        TotpEncoder::Totp
    };
    let mut issuer_query = None;
    for pair in query.split('&').filter(|pair| !pair.is_empty()) {
        let (key, value) = pair.split_once('=').unwrap_or((pair, ""));
        let key = percent_decode(key).to_ascii_lowercase();
        let value = percent_decode(value);
        match key.as_str() {
            "secret" => {
                secret = Some(value);
            }
            "algorithm" => {
                algorithm = match value.to_ascii_uppercase().as_str() {
                    "SHA1" | "SHA-1" => TotpAlgorithm::Sha1,
                    "SHA256" | "SHA-256" => TotpAlgorithm::Sha256,
                    "SHA512" | "SHA-512" => TotpAlgorithm::Sha512,
                    _ => return Err(TotpError::InvalidUri),
                };
            }
            "digits" => {
                digits = value.parse().map_err(|_| TotpError::InvalidUri)?;
            }
            "period" => {
                period = value.parse().map_err(|_| TotpError::InvalidUri)?;
            }
            "issuer" => issuer_query = Some(value),
            "encoder" if value.eq_ignore_ascii_case("steam") => {
                encoder = TotpEncoder::Steam;
                digits = 5;
            }
            "encoder" => {}
            _ => {}
        }
    }
    let secret = secret.ok_or(TotpError::EmptySecret)?;
    let config = TotpConfig {
        secret: base32_decode(&secret)?,
        algorithm,
        digits,
        period,
        encoder,
    };
    // Force validation before returning a spec.
    generate_at(&config, 0)?;
    Ok(TotpSpec {
        issuer: issuer_query.or(issuer),
        account,
        config,
    })
}

/// Serialise a [`TotpSpec`] to its canonical otpauth URI.
pub fn to_uri(spec: &TotpSpec) -> Result<String, TotpError> {
    let label = match (&spec.issuer, &spec.account) {
        (Some(issuer), Some(account)) => {
            format!("{}:{}", percent_encode(issuer), percent_encode(account))
        }
        (_, Some(account)) => percent_encode(account),
        (Some(issuer), None) => percent_encode(issuer),
        (None, None) => "".to_string(),
    };
    let secret = base32_encode(&spec.config.secret);
    let mut query = format!(
        "secret={}&algorithm={}&digits={}&period={}",
        secret,
        spec.config.algorithm.as_str(),
        spec.config.output_digits(),
        spec.config.period
    );
    if let Some(issuer) = &spec.issuer {
        query.push_str(&format!("&issuer={}", percent_encode(issuer)));
    }
    if spec.config.encoder == TotpEncoder::Steam {
        query.push_str("&encoder=steam");
    }
    Ok(format!("otpauth://totp/{label}?{query}"))
}

/// Read TOTP from a model entry using the plan's priority order.
pub fn read_entry(entry: &Entry) -> Result<Option<TotpRead>, TotpError> {
    let fields: Vec<(&str, &str)> = entry
        .strings
        .iter()
        .map(|field| (field.key.as_str(), field.value.as_str()))
        .collect();
    read_fields(&fields)
}

/// Read TOTP from key/value fields using the plan's priority order.
pub fn read_fields(fields: &[(&str, &str)]) -> Result<Option<TotpRead>, TotpError> {
    let get = |key: &str| {
        fields
            .iter()
            .find(|(candidate, _)| *candidate == key)
            .map(|(_, value)| *value)
            .filter(|value| !value.trim().is_empty())
    };
    let otp_present = get("otp").is_some();
    let legacy_present = get("TOTP Seed").is_some();
    let timeotp_present = get("TimeOtp-Secret-Base32").is_some();
    let sources = [otp_present, legacy_present, timeotp_present]
        .into_iter()
        .filter(|present| *present)
        .count();

    if let Some(otp) = get("otp") {
        return Ok(Some(TotpRead {
            spec: parse_uri(otp)?,
            source: TotpSource::OtpUri,
            multiple_sources: sources > 1,
        }));
    }
    if let Some(seed) = get("TOTP Seed") {
        let (period, digits, algorithm) = parse_legacy_settings(get("TOTP Settings"))?;
        let config = TotpConfig {
            secret: base32_decode(seed)?,
            algorithm,
            digits,
            period,
            encoder: TotpEncoder::Totp,
        };
        generate_at(&config, 0)?;
        return Ok(Some(TotpRead {
            spec: TotpSpec {
                config,
                issuer: None,
                account: None,
            },
            source: TotpSource::KeepassXcLegacy,
            multiple_sources: sources > 1,
        }));
    }
    if let Some(seed) = get("TimeOtp-Secret-Base32") {
        let mut config = TotpConfig {
            secret: base32_decode(seed)?,
            algorithm: parse_timeotp_algorithm(get("TimeOtp-Algorithm")),
            digits: get("TimeOtp-Length")
                .map(|value| value.parse().map_err(|_| TotpError::InvalidUri))
                .transpose()?
                .unwrap_or(6),
            period: get("TimeOtp-Period")
                .map(|value| value.parse().map_err(|_| TotpError::InvalidPeriod))
                .transpose()?
                .unwrap_or(30),
            encoder: TotpEncoder::Totp,
        };
        generate_at(&config, 0)?;
        config.secret.shrink_to_fit();
        return Ok(Some(TotpRead {
            spec: TotpSpec {
                config,
                issuer: None,
                account: None,
            },
            source: TotpSource::TimeOtp,
            multiple_sources: sources > 1,
        }));
    }
    Ok(None)
}

/// Fields to set/remove when saving a TOTP configuration.
pub fn dual_write(spec: &TotpSpec) -> Result<TotpFieldUpdate, TotpError> {
    let uri = to_uri(spec)?;
    let algorithm = match spec.config.algorithm {
        TotpAlgorithm::Sha1 => "HMAC-SHA-1",
        TotpAlgorithm::Sha256 => "HMAC-SHA-256",
        TotpAlgorithm::Sha512 => "HMAC-SHA-512",
    };
    let mut set = vec![
        TotpFieldValue {
            key: "otp".to_string(),
            value: uri,
            protected: true,
        },
        TotpFieldValue {
            key: "TimeOtp-Secret-Base32".to_string(),
            value: base32_encode(&spec.config.secret),
            protected: false,
        },
        TotpFieldValue {
            key: "TimeOtp-Length".to_string(),
            value: spec.config.output_digits().to_string(),
            protected: false,
        },
        TotpFieldValue {
            key: "TimeOtp-Period".to_string(),
            value: spec.config.period.to_string(),
            protected: false,
        },
        TotpFieldValue {
            key: "TimeOtp-Algorithm".to_string(),
            value: algorithm.to_string(),
            protected: false,
        },
    ];
    set.sort_by(|a, b| a.key.cmp(&b.key));
    Ok(TotpFieldUpdate {
        set,
        remove: vec!["TOTP Seed".to_string(), "TOTP Settings".to_string()],
    })
}

fn parse_legacy_settings(settings: Option<&str>) -> Result<(u32, u32, TotpAlgorithm), TotpError> {
    let Some(settings) = settings else {
        return Ok((30, 6, TotpAlgorithm::Sha1));
    };
    let mut parts = settings.split(';');
    let period = parts
        .next()
        .unwrap_or("30")
        .trim()
        .parse()
        .map_err(|_| TotpError::InvalidPeriod)?;
    let digits = parts
        .next()
        .unwrap_or("6")
        .trim()
        .parse()
        .map_err(|_| TotpError::InvalidUri)?;
    let algorithm = match parts.next().map(str::trim) {
        Some(value) if value.eq_ignore_ascii_case("SHA256") => TotpAlgorithm::Sha256,
        Some(value) if value.eq_ignore_ascii_case("SHA512") => TotpAlgorithm::Sha512,
        _ => TotpAlgorithm::Sha1,
    };
    Ok((period, digits, algorithm))
}

fn parse_timeotp_algorithm(value: Option<&str>) -> TotpAlgorithm {
    match value.unwrap_or("").to_ascii_uppercase() {
        value if value.contains("256") => TotpAlgorithm::Sha256,
        value if value.contains("512") => TotpAlgorithm::Sha512,
        _ => TotpAlgorithm::Sha1,
    }
}

fn percent_decode(value: &str) -> String {
    let bytes = value.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut index = 0;
    while index < bytes.len() {
        if bytes[index] == b'%' && index + 2 < bytes.len() {
            let high = (bytes[index + 1] as char).to_digit(16);
            let low = (bytes[index + 2] as char).to_digit(16);
            if let (Some(high), Some(low)) = (high, low) {
                out.push((high as u8) << 4 | low as u8);
                index += 3;
                continue;
            }
        }
        out.push(bytes[index]);
        index += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

fn percent_encode(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    for byte in value.bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'.' | b'_' | b'~') {
            out.push(byte as char);
        } else {
            out.push_str(&format!("%{byte:02X}"));
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn config(secret: &str) -> TotpConfig {
        TotpConfig {
            secret: base32_decode(secret).unwrap(),
            algorithm: TotpAlgorithm::Sha1,
            digits: 6,
            period: 30,
            encoder: TotpEncoder::Totp,
        }
    }

    #[test]
    fn otpauth_parse_and_roundtrip() {
        let spec = parse_uri("otpauth://totp/KeePassXC:alice?secret=JBSWY3DPEHPK3PXP&period=30&digits=6&issuer=KeePassXC").unwrap();
        assert_eq!(spec.issuer.as_deref(), Some("KeePassXC"));
        assert_eq!(spec.account.as_deref(), Some("alice"));
        assert_eq!(
            spec.config.secret,
            base32_decode("JBSWY3DPEHPK3PXP").unwrap()
        );
        let uri = to_uri(&spec).unwrap();
        let reparsed = parse_uri(&uri).unwrap();
        assert_eq!(reparsed, spec);
    }

    #[test]
    fn reads_otp_before_legacy_and_timeotp() {
        let fields = [
            (
                "otp",
                "otpauth://totp/Otp:one?secret=JBSWY3DPEHPK3PXP&period=30&digits=6",
            ),
            ("TOTP Seed", "JBSWY3DPEHPK3PXP"),
            ("TOTP Settings", "60;8"),
            ("TimeOtp-Secret-Base32", "JBSWY3DPEHPK3PXP"),
            ("TimeOtp-Period", "90"),
        ];
        let read = read_fields(&fields).unwrap().unwrap();
        assert_eq!(read.source, TotpSource::OtpUri);
        assert!(read.multiple_sources);
        assert_eq!(read.spec.account.as_deref(), Some("one"));
    }

    #[test]
    fn reads_legacy_keeppassxc_fields() {
        let fields = [
            ("TOTP Seed", "JBSWY3DPEHPK3PXP"),
            ("TOTP Settings", "60;8;SHA256"),
        ];
        let read = read_fields(&fields).unwrap().unwrap();
        assert_eq!(read.source, TotpSource::KeepassXcLegacy);
        assert_eq!(read.spec.config.period, 60);
        assert_eq!(read.spec.config.digits, 8);
        assert_eq!(read.spec.config.algorithm, TotpAlgorithm::Sha256);
    }

    #[test]
    fn reads_keepass_timeotp_fields() {
        let fields = [
            ("TimeOtp-Secret-Base32", "JBSWY3DPEHPK3PXP"),
            ("TimeOtp-Length", "8"),
            ("TimeOtp-Period", "45"),
            ("TimeOtp-Algorithm", "HMAC-SHA-512"),
        ];
        let read = read_fields(&fields).unwrap().unwrap();
        assert_eq!(read.source, TotpSource::TimeOtp);
        assert_eq!(read.spec.config.digits, 8);
        assert_eq!(read.spec.config.period, 45);
        assert_eq!(read.spec.config.algorithm, TotpAlgorithm::Sha512);
    }

    #[test]
    fn missing_fields_yield_none() {
        assert_eq!(read_fields(&[]).unwrap(), None);
    }

    #[test]
    fn dual_write_sets_both_formats_and_removes_legacy() {
        let spec = TotpSpec {
            config: config("JBSWY3DPEHPK3PXP"),
            issuer: Some("KeePassXC".into()),
            account: Some("alice".into()),
        };
        let update = dual_write(&spec).unwrap();
        let keys: Vec<&str> = update.set.iter().map(|field| field.key.as_str()).collect();
        assert!(keys.contains(&"otp"));
        assert!(keys.contains(&"TimeOtp-Secret-Base32"));
        assert!(keys.contains(&"TimeOtp-Length"));
        assert!(keys.contains(&"TimeOtp-Period"));
        assert!(keys.contains(&"TimeOtp-Algorithm"));
        assert_eq!(update.remove, vec!["TOTP Seed", "TOTP Settings"]);
        assert!(
            update
                .set
                .iter()
                .find(|field| field.key == "otp")
                .unwrap()
                .protected
        );
    }

    #[test]
    fn steam_uri_roundtrip() {
        let spec = parse_uri("steam://Steam:alice?secret=JBSWY3DPEHPK3PXP").unwrap();
        assert_eq!(spec.config.encoder, TotpEncoder::Steam);
        assert_eq!(spec.config.output_digits(), 5);
        let uri = to_uri(&spec).unwrap();
        assert!(uri.contains("encoder=steam"));
        assert_eq!(parse_uri(&uri).unwrap(), spec);
    }

    #[test]
    fn rejects_bad_uri_and_settings() {
        assert_eq!(
            parse_uri("https://example.com").unwrap_err(),
            TotpError::InvalidUri
        );
        assert!(parse_uri("otpauth://totp/X").is_err());
        assert!(
            read_fields(&[("TOTP Seed", "JBSWY3DPEHPK3PXP"), ("TOTP Settings", "x;y")]).is_err()
        );
    }

    #[test]
    fn read_entry_uses_model_strings() {
        let mut entry = Entry::default();
        entry.strings.push(crate::model::StringField {
            key: "TimeOtp-Secret-Base32".into(),
            value: "JBSWY3DPEHPK3PXP".into(),
            protected: false,
            unknown: Vec::new(),
        });
        let read = read_entry(&entry).unwrap().unwrap();
        assert_eq!(read.source, TotpSource::TimeOtp);
    }
}
