// SPDX-License-Identifier: GPL-3.0-or-later
//! M1-08: property tests for model round-trip, Base32, and TOTP generation.

use keepassity_core::kdbx::reader;
use keepassity_core::model::{Database, Entry, Group, RawXml, StringField};
use keepassity_core::totp::{
    base32_decode, base32_encode, generate_at, TotpAlgorithm, TotpConfig, TotpEncoder,
};
use proptest::prelude::*;

proptest! {
    #![proptest_config(ProptestConfig::with_cases(100))]

    #[test]
    fn model_json_roundtrip_preserves_fields(
        title in "\\PC*",
        password in "\\PC*",
        protected in any::<bool>(),
        tags in prop::collection::vec("\\PC*", 0..4),
        unknown_name in "\\PC+",
    ) {
        let mut entry = Entry::default();
        entry.strings.push(StringField {
            key: "Title".into(),
            value: title,
            protected,
            unknown: Vec::new(),
        });
        entry.strings.push(StringField {
            key: "Password".into(),
            value: password,
            protected: true,
            unknown: Vec::new(),
        });
        entry.tags = tags;
        entry.unknown.push(RawXml::new(unknown_name));
        let db = Database {
            format_version: "4.1".into(),
            root: Group {
                entries: vec![entry],
                ..Group::default()
            },
            ..Database::default()
        };
        let json = serde_json::to_string(&db).expect("serialize");
        let back: Database = serde_json::from_str(&json).expect("deserialize");
        prop_assert_eq!(back, db);
    }

    #[test]
    fn base32_roundtrip(bytes in prop::collection::vec(any::<u8>(), 1..64)) {
        let encoded = base32_encode(&bytes);
        let decoded = base32_decode(&encoded).expect("decode");
        prop_assert_eq!(decoded, bytes);
    }

    #[test]
    fn totp_is_deterministic_and_well_formed(
        secret in prop::collection::vec(any::<u8>(), 1..64),
        algorithm_index in 0usize..3,
        digits in prop_oneof![Just(6u32), Just(8u32)],
        period in 1u32..=300,
        time in any::<u64>(),
    ) {
        let algorithm = [TotpAlgorithm::Sha1, TotpAlgorithm::Sha256, TotpAlgorithm::Sha512]
            [algorithm_index];
        let config = TotpConfig {
            secret,
            algorithm,
            digits,
            period,
            encoder: TotpEncoder::Totp,
        };
        let first = generate_at(&config, time).expect("generate");
        let second = generate_at(&config, time).expect("generate again");
        prop_assert_eq!(&first.code, &second.code);
        prop_assert_eq!(first.code.len(), digits as usize);
        prop_assert!(first.remaining >= 1 && first.remaining <= period);
    }
}

#[test]
fn reader_rejects_non_kdbx_bytes() {
    assert!(matches!(
        reader::parse(b"definitely not a kdbx file", "x"),
        Err(keepassity_core::kdbx::reader::KdbxReadError::Corrupt(_))
    ));
}
