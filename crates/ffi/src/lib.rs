// SPDX-License-Identifier: GPL-3.0-or-later
//! C ABI surface (v1).
//!
//! Rules: no panic across FFI, all returned pointers freed by the caller via the
//! matching `kp_*_free`, ABI is additive-only.

use keepassity_core::API_MAJOR;

/// Returns the FFI main version. UI must refuse to start on mismatch.
#[no_mangle]
pub extern "C" fn kp_api_version() -> u32 {
    API_MAJOR
}

#[cfg(test)]
mod tests {
    #[test]
    fn api_version_matches_core() {
        assert_eq!(super::kp_api_version(), super::API_MAJOR);
    }
}
