// SPDX-License-Identifier: GPL-3.0-or-later
//! Model coverage test for M1-01: every entity in requirements §6.3 is present
//! in the canonical dump, and unknown XML survives a JSON round trip.

use keepassity_core::model::*;

fn uuid(byte: u8) -> Uuid {
    Uuid::from_bytes([byte; 16])
}

fn full_database() -> Database {
    let mut meta = Meta {
        generator: "keepassity-test".into(),
        database_name: "Test".into(),
        database_description: "description".into(),
        default_username: "default".into(),
        recycle_bin_enabled: true,
        recycle_bin_uuid: Some(uuid(9)),
        recycle_bin_changed: Some("2026-10-25T10:00:00Z".into()),
        history_max_items: 10,
        history_max_size: 1024,
        ..Meta::default()
    };
    meta.memory_protection.protect_password = true;
    meta.custom_data.items.insert(
        "plugin".into(),
        CustomDataItem {
            value: Some(CustomDataValue::String("value".into())),
            last_modification: Some("2026-10-25T10:00:00Z".into()),
            unknown: vec![RawXml::new("Nested")],
        },
    );
    meta.custom_data.items.insert(
        "binary".into(),
        CustomDataItem {
            value: Some(CustomDataValue::Binary(vec![1, 2, 3, 4])),
            last_modification: None,
            unknown: Vec::new(),
        },
    );
    meta.custom_icons.push(CustomIcon {
        uuid: uuid(8),
        data: vec![0x89, 0x50, 0x4e, 0x47],
        name: Some("icon".into()),
        last_modification: Some("2026-10-25T10:00:00Z".into()),
        unknown: Vec::new(),
    });
    meta.unknown.push(RawXml::new("MaintenanceHistory"));

    let mut inner_entry = Entry {
        uuid: uuid(3),
        icon_id: 2,
        tags: vec!["inner".into()],
        times: Times {
            creation: "2026-10-25T10:00:00Z".into(),
            last_modification: "2026-10-25T10:00:00Z".into(),
            last_access: "2026-10-25T10:00:00Z".into(),
            ..Times::default()
        },
        ..Entry::default()
    };
    inner_entry.strings.push(StringField {
        key: "Title".into(),
        value: "history".into(),
        protected: false,
        unknown: Vec::new(),
    });

    let mut entry = Entry {
        uuid: uuid(2),
        icon_id: 1,
        foreground_color: "#FFFFFF".into(),
        background_color: "#000000".into(),
        override_url: "https://example.invalid".into(),
        tags: vec!["tag-b".into(), "tag-a".into()],
        previous_parent_group: Some(uuid(7)),
        times: Times {
            creation: "2026-10-25T10:00:00Z".into(),
            last_modification: "2026-10-25T10:00:00Z".into(),
            last_access: "2026-10-25T10:00:00Z".into(),
            ..Times::default()
        },
        auto_type: Some(AutoType {
            enabled: true,
            data_transfer_obfuscation: true,
            default_sequence: "{USERNAME}{TAB}{PASSWORD}{ENTER}".into(),
            associations: vec![Association {
                window: "*.example".into(),
                keystroke_sequence: "{USERNAME}{TAB}{PASSWORD}{ENTER}".into(),
                unknown: Vec::new(),
            }],
            unknown: Vec::new(),
        }),
        history: vec![inner_entry],
        ..Entry::default()
    };
    entry.strings.push(StringField {
        key: "Title".into(),
        value: "Example".into(),
        protected: false,
        unknown: Vec::new(),
    });
    entry.strings.push(StringField {
        key: "UserName".into(),
        value: "user".into(),
        protected: false,
        unknown: Vec::new(),
    });
    entry.strings.push(StringField {
        key: "Password".into(),
        value: "secret".into(),
        protected: true,
        unknown: Vec::new(),
    });
    entry.strings.push(StringField {
        key: "otp".into(),
        value: "otpauth://totp/x?secret=ABC".into(),
        protected: true,
        unknown: Vec::new(),
    });
    entry.binaries.push(Binary {
        key: "file.txt".into(),
        value: b"attachment".to_vec(),
        protected: false,
        unknown: Vec::new(),
    });
    entry.unknown.push(RawXml::new("EntryPluginData"));

    let mut group = Group {
        uuid: uuid(1),
        name: "Root".into(),
        notes: "notes".into(),
        icon_id: 0,
        tags: vec!["root".into()],
        previous_parent_group: Some(uuid(6)),
        ..Group::default()
    };
    group.custom_data.items.insert(
        "group-data".into(),
        CustomDataItem {
            value: Some(CustomDataValue::String("g".into())),
            last_modification: None,
            unknown: Vec::new(),
        },
    );
    group.entries.push(entry);
    group.groups.push(Group {
        uuid: uuid(4),
        name: "Child".into(),
        ..Group::default()
    });
    group.unknown.push(RawXml::new("GroupPluginData"));

    Database {
        format_version: "4.1".into(),
        meta,
        root: group,
        deleted_objects: vec![DeletedObject {
            uuid: uuid(5),
            deletion_time: "2026-10-25T10:00:00Z".into(),
        }],
        unknown: vec![RawXml::new("DatabasePluginData")],
    }
}

#[test]
fn canonical_dump_covers_all_requirement_entities() {
    let db = full_database();
    let json = db.canonical_json().expect("serialize model");
    for expected in [
        "\"format_version\"",
        "\"meta\"",
        "\"custom_icons\"",
        "\"custom_data\"",
        "\"root\"",
        "\"groups\"",
        "\"entries\"",
        "\"strings\"",
        "\"binaries\"",
        "\"auto_type\"",
        "\"history\"",
        "\"deleted_objects\"",
        "\"unknown\"",
        "DatabasePluginData",
        "GroupPluginData",
        "EntryPluginData",
    ] {
        assert!(json.contains(expected), "canonical dump missing {expected}");
    }

    let back: Database = serde_json::from_str(&json).expect("deserialize model");
    assert_eq!(back, db);
}

#[test]
fn unknown_xml_roundtrip_is_lossless() {
    let mut raw = RawXml::new("CustomPlugin");
    raw.attributes
        .insert("Plugin".into(), "keepass2android".into());
    raw.text = Some("payload".into());
    raw.children.push(RawXml::new("Nested"));
    let db = Database {
        root: Group {
            unknown: vec![raw.clone()],
            ..Group::default()
        },
        ..Database::default()
    };
    let json = serde_json::to_string(&db).expect("serialize");
    let back: Database = serde_json::from_str(&json).expect("deserialize");
    assert_eq!(back.root.unknown, vec![raw]);
}
