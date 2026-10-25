// SPDX-License-Identifier: GPL-3.0-or-later
//! KDBX format helpers shared by the IO layer (CLI) and future core modules.
//!
//! This module is **IO-free**: callers pass bytes or do file access themselves.
//! It exists so version detection and the write-backend contract live in one
//! place instead of being duplicated by every harness.

pub mod reader;

/// KDBX file format version detected from the file header.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum KdbxVersion {
    /// KDBX 3.1 (legacy KeePass 1.x-compatible format).
    V3_1,
    /// KDBX 4.0.
    V4_0,
    /// KDBX 4.1.
    V4_1,
}

impl KdbxVersion {
    /// Canonical dotted version string (`"3.1"`, `"4.0"`, `"4.1"`).
    pub fn as_str(self) -> &'static str {
        match self {
            KdbxVersion::V3_1 => "3.1",
            KdbxVersion::V4_0 => "4.0",
            KdbxVersion::V4_1 => "4.1",
        }
    }
}

impl std::fmt::Display for KdbxVersion {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
}

/// Why a KDBX header could not be parsed.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum KdbxHeaderError {
    /// Fewer than 12 bytes were supplied.
    TooShort,
    /// The 8-byte KDBX signature is not present.
    BadSignature,
    /// Signature is valid but the version word is unknown.
    UnknownVersion(u32),
}

impl std::fmt::Display for KdbxHeaderError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            KdbxHeaderError::TooShort => f.write_str("KDBX header is shorter than 12 bytes"),
            KdbxHeaderError::BadSignature => f.write_str("not a KDBX file (bad signature)"),
            KdbxHeaderError::UnknownVersion(v) => write!(f, "unknown KDBX version word 0x{v:08X}"),
        }
    }
}

impl std::error::Error for KdbxHeaderError {}

/// First half of the KDBX file signature.
pub const KDBX_SIG_1: u32 = 0x9AA2_D903;
/// Second half of the KDBX file signature.
pub const KDBX_SIG_2: u32 = 0xB54B_FB67;

/// Write backend selected by ADR-0002. Kept here so harnesses and tools do not
/// hard-code the version independently.
pub const WRITE_BACKEND: &str = "keepassxcr 0.3.1";

/// Read the KDBX version word from the first 12 bytes of a file.
///
/// Layout: `u32 sig1 | u32 sig2 | u32 version` (little-endian).
pub fn detect_version(header: &[u8]) -> Result<KdbxVersion, KdbxHeaderError> {
    if header.len() < 12 {
        return Err(KdbxHeaderError::TooShort);
    }
    let sig1 = u32::from_le_bytes(header[0..4].try_into().expect("checked length"));
    let sig2 = u32::from_le_bytes(header[4..8].try_into().expect("checked length"));
    if sig1 != KDBX_SIG_1 || sig2 != KDBX_SIG_2 {
        return Err(KdbxHeaderError::BadSignature);
    }
    let version = u32::from_le_bytes(header[8..12].try_into().expect("checked length"));
    match version {
        0x0003_0001 => Ok(KdbxVersion::V3_1),
        0x0004_0000 => Ok(KdbxVersion::V4_0),
        0x0004_0001 => Ok(KdbxVersion::V4_1),
        other => Err(KdbxHeaderError::UnknownVersion(other)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn header(version_word: u32) -> Vec<u8> {
        let mut out = Vec::with_capacity(12);
        out.extend_from_slice(&KDBX_SIG_1.to_le_bytes());
        out.extend_from_slice(&KDBX_SIG_2.to_le_bytes());
        out.extend_from_slice(&version_word.to_le_bytes());
        out
    }

    #[test]
    fn detects_all_supported_versions() {
        assert_eq!(detect_version(&header(0x0003_0001)), Ok(KdbxVersion::V3_1));
        assert_eq!(detect_version(&header(0x0004_0000)), Ok(KdbxVersion::V4_0));
        assert_eq!(detect_version(&header(0x0004_0001)), Ok(KdbxVersion::V4_1));
    }

    #[test]
    fn rejects_short_header() {
        assert_eq!(detect_version(&[0u8; 11]), Err(KdbxHeaderError::TooShort));
    }

    #[test]
    fn rejects_bad_signature() {
        assert_eq!(
            detect_version(&[0u8; 12]),
            Err(KdbxHeaderError::BadSignature)
        );
    }

    #[test]
    fn rejects_unknown_version() {
        assert_eq!(
            detect_version(&header(0xDEAD_BEEF)),
            Err(KdbxHeaderError::UnknownVersion(0xDEAD_BEEF))
        );
    }

    #[test]
    fn version_display_is_dotted() {
        assert_eq!(KdbxVersion::V4_1.to_string(), "4.1");
    }
}
