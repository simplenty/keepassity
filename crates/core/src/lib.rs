// SPDX-License-Identifier: GPL-3.0-or-later
//! keepassity core.
//!
//! Boundary rules:
//! - no file IO, no network, no environment variables, no platform APIs;
//! - the only cross-language surface is `crates/ffi`;
//! - model is the single source of truth; UI never parses KDBX.

/// FFI main version. Bump only with a breaking ABI change.
pub const API_MAJOR: u32 = 1;

/// Core crate version (shown in the app's About page).
pub fn version() -> &'static str {
    env!("CARGO_PKG_VERSION")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn version_is_not_empty() {
        assert!(!version().is_empty());
    }

    #[test]
    fn api_major_is_one() {
        assert_eq!(API_MAJOR, 1);
    }
}
