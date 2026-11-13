// SPDX-License-Identifier: GPL-3.0-or-later
//! C ABI surface (v1) for the WinUI shell and C harness.
//!
//! Rules: no panic across FFI, all returned pointers are freed by the caller
//! via `kp_string_free` / `kp_buffer_free`, ABI is additive-only.

// The C ABI intentionally exposes safe `extern "C"` functions taking raw
// pointers; validity is the caller's contract (nulls are checked defensively).
#![allow(clippy::not_unsafe_ptr_arg_deref)]

use keepassity_core::dto::{
    ConflictItem, EntryFull, EntrySummary, SyncRemoteMeta, TotpAlgorithm as DtoTotpAlgorithm,
    TotpConfig as DtoTotpConfig, TotpEncoder as DtoTotpEncoder,
};
use keepassity_core::kdbx::writer;
use keepassity_core::model::{
    xml_datetime_from_unix, Database, DeletedObject, Entry, Group, HistoryLimits, StringField,
    Times, Uuid,
};
use keepassity_core::sync::{local_hash, merge_for_sync, SyncActionKind, SyncBaseline, SyncState};
use keepassity_core::totp::fields as totp_fields;
use keepassity_core::totp::{
    base32_decode, base32_encode, generate_at, TotpAlgorithm as CoreTotpAlgorithm,
    TotpConfig as CoreTotpConfig, TotpEncoder as CoreTotpEncoder,
};
use serde_json::{json, Value};
use std::cell::RefCell;
use std::ffi::{c_char, c_void, CStr, CString};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

/// FFI main version (must match core API_MAJOR).
pub const API_MAJOR: u32 = keepassity_core::API_MAJOR;

/// Event callback type (v1).
pub type KpEventCb = Option<extern "C" fn(user: *mut c_void, event_json: *const c_char)>;

struct Subscriber {
    _callback: KpEventCb,
    _user: *mut c_void,
}

struct HandleState {
    database: Option<Database>,
    password: Option<String>,
    dirty: bool,
    baseline: SyncBaseline,
    last_sync_unix: Option<i64>,
    subscriber: Option<Subscriber>,
}

/// Opaque handle passed to C callers.
pub struct KpHandle {
    state: Mutex<HandleState>,
}

struct FfiError {
    code: &'static str,
    message: String,
    retryable: bool,
}

impl FfiError {
    fn new(code: &'static str, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
            retryable: false,
        }
    }

    fn invalid(message: impl Into<String>) -> Self {
        Self::new("InvalidArgument", message)
    }

    fn not_found() -> Self {
        Self::new("NotFound", "item not found")
    }

    fn locked() -> Self {
        Self::new("Locked", "database is locked")
    }

    fn internal(message: impl Into<String>) -> Self {
        Self::new("Internal", message)
    }

    fn to_json(&self) -> String {
        json!({
            "code": self.code,
            "message": self.message,
            "retryable": self.retryable,
            "detail": Value::Null,
        })
        .to_string()
    }
}

thread_local! {
    static LAST_ERROR: RefCell<Option<CString>> = const { RefCell::new(None) };
}

fn set_last_error(error: &FfiError) {
    let text = error.to_json().replace('\0', "");
    LAST_ERROR.with(|slot| {
        *slot.borrow_mut() = CString::new(text).ok();
    });
}

fn clear_last_error() {
    LAST_ERROR.with(|slot| {
        *slot.borrow_mut() = None;
    });
}

fn fail(error: FfiError) -> i32 {
    set_last_error(&error);
    1
}

fn success() -> i32 {
    clear_last_error();
    0
}

fn guarded(function: impl FnOnce() -> i32) -> i32 {
    match catch_unwind(AssertUnwindSafe(function)) {
        Ok(code) => code,
        Err(_) => fail(FfiError::internal("panic caught at FFI boundary")),
    }
}

/// Returns the FFI main version.
#[no_mangle]
pub extern "C" fn kp_api_version() -> u32 {
    API_MAJOR
}

/// Borrowed last-error JSON (valid until the next call on this thread).
#[no_mangle]
pub extern "C" fn kp_last_error_json() -> *const c_char {
    LAST_ERROR.with(|slot| {
        slot.borrow()
            .as_ref()
            .map_or(std::ptr::null(), |error| error.as_ptr())
    })
}

/// Free a `char*` returned by this library.
#[no_mangle]
pub extern "C" fn kp_string_free(pointer: *mut c_char) {
    if pointer.is_null() {
        return;
    }
    unsafe {
        drop(CString::from_raw(pointer));
    }
}

/// Free a byte buffer returned by this library.
#[no_mangle]
pub extern "C" fn kp_buffer_free(pointer: *mut u8, len: usize) {
    if pointer.is_null() {
        return;
    }
    unsafe {
        let slice = std::ptr::slice_from_raw_parts_mut(pointer, len);
        drop(Box::from_raw(slice));
    }
}

fn options_password(opts: *const c_char) -> Result<String, FfiError> {
    if opts.is_null() {
        return Err(FfiError::invalid("missing options JSON"));
    }
    let text = unsafe { CStr::from_ptr(opts) }
        .to_str()
        .map_err(|_| FfiError::invalid("options JSON is not UTF-8"))?;
    let value: Value =
        serde_json::from_str(text).map_err(|_| FfiError::invalid("options JSON is invalid"))?;
    value
        .get("password")
        .and_then(Value::as_str)
        .map(str::to_string)
        .ok_or_else(|| FfiError::invalid("options.password is required"))
}

fn open_database(bytes: &[u8], password: &str) -> Result<Database, FfiError> {
    keepassity_core::kdbx::reader::parse(bytes, password).map_err(|error| match error {
        keepassity_core::kdbx::reader::KdbxReadError::BadPassword => {
            FfiError::new("BadPassword", "incorrect master password")
        }
        keepassity_core::kdbx::reader::KdbxReadError::Corrupt(_) => {
            FfiError::new("CorruptDatabase", "the database file is damaged")
        }
        keepassity_core::kdbx::reader::KdbxReadError::Unsupported(_) => {
            FfiError::new("UnsupportedFormat", "unsupported database format")
        }
        keepassity_core::kdbx::reader::KdbxReadError::Header(_) => {
            FfiError::new("CorruptDatabase", "invalid database header")
        }
    })
}

/// Open a KDBX byte buffer; `opts_json` must contain `{"password":"..."}`.
#[no_mangle]
pub extern "C" fn kp_open(kdbx: *const u8, len: usize, opts_json: *const c_char) -> *mut KpHandle {
    match catch_unwind(AssertUnwindSafe(|| {
        if kdbx.is_null() {
            return Err(FfiError::invalid("KDBX buffer is null"));
        }
        let bytes = unsafe { std::slice::from_raw_parts(kdbx, len) };
        let password = options_password(opts_json)?;
        let database = open_database(bytes, &password)?;
        clear_last_error();
        Ok(Box::into_raw(Box::new(KpHandle {
            state: Mutex::new(HandleState {
                database: Some(database),
                password: Some(password),
                dirty: false,
                baseline: SyncBaseline::default(),
                last_sync_unix: None,
                subscriber: None,
            }),
        })))
    })) {
        Ok(Ok(handle)) => handle,
        Ok(Err(error)) => {
            fail(error);
            std::ptr::null_mut()
        }
        Err(_) => {
            fail(FfiError::internal("panic in kp_open"));
            std::ptr::null_mut()
        }
    }
}

fn with_state<R>(
    handle: *mut KpHandle,
    function: impl FnOnce(&mut HandleState) -> Result<R, FfiError>,
) -> Result<R, FfiError> {
    if handle.is_null() {
        return Err(FfiError::invalid("handle is null"));
    }
    let handle = unsafe { &*handle };
    let mut state = handle
        .state
        .lock()
        .map_err(|_| FfiError::internal("handle lock poisoned"))?;
    function(&mut state)
}

fn with_database<R>(
    state: &mut HandleState,
    function: impl FnOnce(&mut Database) -> Result<R, FfiError>,
) -> Result<R, FfiError> {
    state
        .database
        .as_mut()
        .ok_or_else(FfiError::locked)
        .and_then(function)
}

/// Run a mutating database command and mark the handle dirty on success.
fn with_database_dirty<R>(
    state: &mut HandleState,
    function: impl FnOnce(&mut Database) -> Result<R, FfiError>,
) -> Result<R, FfiError> {
    let result = {
        let database = state.database.as_mut().ok_or_else(FfiError::locked)?;
        function(database)?
    };
    state.dirty = true;
    Ok(result)
}

/// Lock and clear the decrypted database.
#[no_mangle]
pub extern "C" fn kp_lock(handle: *mut KpHandle) -> i32 {
    guarded(|| {
        match with_state(handle, |state| {
            state.database = None;
            state.password = None;
            Ok(())
        }) {
            Ok(()) => success(),
            Err(error) => fail(error),
        }
    })
}

/// Close a handle and free its resources.
#[no_mangle]
pub extern "C" fn kp_close(handle: *mut KpHandle) -> i32 {
    guarded(|| {
        if handle.is_null() {
            return fail(FfiError::invalid("handle is null"));
        }
        unsafe {
            drop(Box::from_raw(handle));
        }
        success()
    })
}

/// Invoke one JSON command (`{"command":"...", ...}`).
#[no_mangle]
pub extern "C" fn kp_call(
    handle: *mut KpHandle,
    cmd_json: *const c_char,
    out_json: *mut *mut c_char,
) -> i32 {
    guarded(|| {
        if cmd_json.is_null() || out_json.is_null() {
            return fail(FfiError::invalid("command or output pointer is null"));
        }
        let text = match unsafe { CStr::from_ptr(cmd_json) }.to_str() {
            Ok(text) => text,
            Err(_) => return fail(FfiError::invalid("command JSON is not UTF-8")),
        };
        let command: Value = match serde_json::from_str(text) {
            Ok(command) => command,
            Err(_) => return fail(FfiError::invalid("command JSON is invalid")),
        };
        match with_state(handle, |state| dispatch(state, &command)) {
            Ok(json_text) => match CString::new(json_text) {
                Ok(value) => {
                    unsafe {
                        *out_json = value.into_raw();
                    }
                    success()
                }
                Err(_) => fail(FfiError::internal("result contains NUL")),
            },
            Err(error) => fail(error),
        }
    })
}

fn dispatch(state: &mut HandleState, command: &Value) -> Result<String, FfiError> {
    let name = command
        .get("command")
        .or_else(|| command.get("cmd"))
        .and_then(Value::as_str)
        .ok_or_else(|| FfiError::invalid("command name is required"))?;
    match name {
        "version" => Ok(json!({
            "api": API_MAJOR,
            "core_version": keepassity_core::version(),
            "write_kdbx_version": writer::DEFAULT_WRITE_VERSION,
        })
        .to_string()),
        "list_groups" => with_database(state, |database| {
            Ok(json!(flatten_groups(database)).to_string())
        }),
        "create_group" => with_database_dirty(state, |database| {
            let parent_uuid = parse_uuid_arg(command, "parent_uuid")?;
            let name = parse_group_name(command)?;
            let parent = find_group_mut(database, parent_uuid).ok_or_else(FfiError::not_found)?;
            let uuid = new_uuid();
            let now = now_unix();
            let stamp = xml_datetime_from_unix(now);
            let group = Group {
                uuid,
                name,
                icon_id: 48,
                times: Times {
                    creation: stamp.clone(),
                    last_modification: stamp.clone(),
                    last_access: stamp.clone(),
                    expiry: stamp.clone(),
                    location_changed: stamp,
                    ..Times::default()
                },
                is_expanded: true,
                ..Group::default()
            };
            parent.groups.push(group);
            Ok(json!({"uuid": uuid.to_hex()}).to_string())
        }),
        "rename_group" => with_database_dirty(state, |database| {
            let uuid = parse_uuid_arg(command, "uuid")?;
            let name = parse_group_name(command)?;
            let group = find_group_mut(database, uuid).ok_or_else(FfiError::not_found)?;
            group.name = name;
            group.times.last_modification = xml_datetime_from_unix(now_unix());
            Ok(json!({"uuid": uuid.to_hex()}).to_string())
        }),
        "delete_group" => with_database_dirty(state, |database| {
            let uuid = parse_uuid_arg(command, "uuid")?;
            if uuid == database.root.uuid {
                return Err(FfiError::invalid("the root group cannot be deleted"));
            }
            if database.meta.recycle_bin_uuid == Some(uuid) {
                return Err(FfiError::invalid("the recycle bin group cannot be deleted"));
            }
            let group = find_group(database, uuid).ok_or_else(FfiError::not_found)?;
            let mut tombstones = Vec::new();
            collect_group_tombstones(group, &mut tombstones);
            remove_group(&mut database.root, uuid).ok_or_else(FfiError::not_found)?;

            // Record the group and every removed descendant/entry. Without
            // tombstones a later remote merge would resurrect the subtree.
            let deletion_time = xml_datetime_from_unix(now_unix());
            for deleted_uuid in tombstones {
                database.deleted_objects.push(DeletedObject {
                    uuid: deleted_uuid,
                    deletion_time: deletion_time.clone(),
                });
            }
            Ok(json!({"deleted": true}).to_string())
        }),
        "list_entries" => with_database(state, |database| {
            let entries = select_entries(
                database,
                command.get("group_uuid").and_then(Value::as_str),
                command
                    .get("include_subgroups")
                    .and_then(Value::as_bool)
                    .unwrap_or(true),
            );
            let summaries: Vec<EntrySummary> = entries
                .into_iter()
                .map(|(entry, group)| EntrySummary::from_model(entry, group))
                .collect();
            Ok(json!(summaries).to_string())
        }),
        "get_entry" => with_database(state, |database| {
            let uuid = parse_uuid_arg(command, "uuid")?;
            let (entry, parent) = find_entry(database, uuid).ok_or_else(FfiError::not_found)?;
            let mut full = EntryFull::from_model(entry, parent);
            if let Ok(Some(read)) = totp_fields::read_entry(entry) {
                full.totp = Some(core_totp_to_dto(&read.spec));
            }
            Ok(json!(full).to_string())
        }),
        "search" => with_database(state, |database| {
            let query = command
                .get("query")
                .and_then(Value::as_str)
                .ok_or_else(|| FfiError::invalid("query is required"))?
                .to_lowercase();
            let limit = command
                .get("limit")
                .and_then(Value::as_u64)
                .unwrap_or(100)
                .min(1_000) as usize;
            let fields = command
                .get("fields")
                .and_then(Value::as_array)
                .map(|values| values.iter().filter_map(Value::as_str).collect::<Vec<_>>())
                .unwrap_or_else(|| vec!["Title", "UserName", "URL", "Notes"]);
            let mut matches = Vec::new();
            for (entry, parent) in all_entries(database) {
                let mut matched = Vec::new();
                for field in &fields {
                    if entry
                        .field(field)
                        .is_some_and(|value| value.to_lowercase().contains(&query))
                    {
                        matched.push((*field).to_string());
                    }
                }
                if !matched.is_empty() {
                    matches.push(json!({
                        "entry": EntrySummary::from_model(entry, parent),
                        "matched_fields": matched,
                    }));
                    if matches.len() >= limit {
                        break;
                    }
                }
            }
            Ok(json!({"matches": matches}).to_string())
        }),
        "create_entry" => with_database_dirty(state, |database| {
            let group_uuid = parse_uuid_arg(command, "group_uuid")?;
            let fields = command
                .get("fields")
                .and_then(Value::as_object)
                .ok_or_else(|| FfiError::invalid("fields object is required"))?;
            let group = find_group_mut(database, group_uuid).ok_or_else(FfiError::not_found)?;
            let uuid = new_uuid();
            let entry = entry_from_fields(uuid, fields);
            group.entries.push(entry);
            Ok(json!({"uuid": uuid.to_hex()}).to_string())
        }),
        "update_entry" => with_database_dirty(state, |database| {
            let uuid = parse_uuid_arg(command, "uuid")?;
            let patch = command
                .get("patch")
                .and_then(Value::as_object)
                .ok_or_else(|| FfiError::invalid("patch object is required"))?;
            let limits = HistoryLimits::from_meta(&database.meta);
            let (entry, _) = find_entry_mut(database, uuid).ok_or_else(FfiError::not_found)?;
            // Keep the previous version recoverable before changing anything;
            // KeePass expects history to grow on every edit, bounded by the
            // database's own retention settings.
            entry.push_history_snapshot(limits);
            apply_patch(entry, patch);
            let now = now_unix();
            touch(entry, now);
            Ok(json!({"uuid": uuid.to_hex(), "modified_unix": now}).to_string())
        }),
        "delete_entry" => with_database_dirty(state, |database| {
            let uuid = parse_uuid_arg(command, "uuid")?;
            let permanent = command
                .get("permanent")
                .and_then(Value::as_bool)
                .unwrap_or(false);
            let (mut entry, _) =
                remove_entry(&mut database.root, uuid).ok_or_else(FfiError::not_found)?;
            let mut recycled = false;
            if !permanent {
                if let Some(target) = ensure_recycle_bin(database) {
                    if let Some(group) = find_group_mut(database, target) {
                        entry.times.location_changed = xml_datetime_from_unix(now_unix());
                        group.entries.push(entry);
                        recycled = true;
                    }
                }
            }
            if !recycled {
                database.deleted_objects.push(DeletedObject {
                    uuid,
                    deletion_time: xml_datetime_from_unix(now_unix()),
                });
            }
            Ok(json!({"deleted": true, "recycled": recycled}).to_string())
        }),
        "move_entry" => with_database_dirty(state, |database| {
            let uuid = parse_uuid_arg(command, "uuid")?;
            let target = parse_uuid_arg(command, "group_uuid")?;
            // Validate the destination before removing the entry: a failed
            // move must leave the in-memory model unchanged.
            if find_group(database, target).is_none() {
                return Err(FfiError::not_found());
            }
            let (mut entry, old_parent) =
                remove_entry(&mut database.root, uuid).ok_or_else(FfiError::not_found)?;
            let target_group = find_group_mut(database, target).ok_or_else(FfiError::not_found)?;
            entry.previous_parent_group = Some(old_parent);
            entry.times.location_changed = xml_datetime_from_unix(now_unix());
            target_group.entries.push(entry);
            Ok(json!({"moved": true}).to_string())
        }),
        "totp_code" => with_database(state, |database| {
            let uuid = parse_uuid_arg(command, "uuid")?;
            let at = command
                .get("at_unix")
                .and_then(Value::as_u64)
                .unwrap_or_else(|| now_unix() as u64);
            let (entry, _) = find_entry(database, uuid).ok_or_else(FfiError::not_found)?;
            let read = totp_fields::read_entry(entry)
                .map_err(|error| FfiError::invalid(error.to_string()))?
                .ok_or_else(|| FfiError::new("NotFound", "entry has no TOTP configuration"))?;
            let code = generate_at(&read.spec.config, at)
                .map_err(|error| FfiError::invalid(error.to_string()))?;
            let spec = read.spec;
            Ok(json!({
                "code": code.code,
                "remaining": code.remaining,
                "period": code.period,
                "digits": code.digits,
                "algorithm": algorithm_name(code.algorithm),
                "issuer": spec.issuer,
                "account": spec.account,
                "skew_warning": false,
            })
            .to_string())
        }),
        "totp_parse_uri" => {
            let uri = command
                .get("uri")
                .and_then(Value::as_str)
                .ok_or_else(|| FfiError::invalid("uri is required"))?;
            let spec = totp_fields::parse_uri(uri)
                .map_err(|error| FfiError::invalid(error.to_string()))?;
            Ok(json!(core_totp_to_dto(&spec)).to_string())
        }
        "totp_set" => with_database_dirty(state, |database| {
            let uuid = parse_uuid_arg(command, "uuid")?;
            let config = command
                .get("config")
                .ok_or_else(|| FfiError::invalid("config is required"))?;
            let spec = dto_to_totp_spec(config)?;
            let (entry, _) = find_entry_mut(database, uuid).ok_or_else(FfiError::not_found)?;
            let update = totp_fields::dual_write(&spec)
                .map_err(|error| FfiError::invalid(error.to_string()))?;
            for key in &update.remove {
                entry.strings.retain(|field| &field.key != key);
            }
            for field in update.set {
                set_field(entry, &field.key, &field.value, field.protected);
            }
            Ok(json!({"ok": true}).to_string())
        }),
        "totp_delete" => with_database_dirty(state, |database| {
            let uuid = parse_uuid_arg(command, "uuid")?;
            let (entry, _) = find_entry_mut(database, uuid).ok_or_else(FfiError::not_found)?;
            entry.strings.retain(|field| {
                field.key != "otp"
                    && field.key != "TOTP Seed"
                    && field.key != "TOTP Settings"
                    && !field.key.starts_with("TimeOtp-")
            });
            Ok(json!({"ok": true}).to_string())
        }),
        "password_generate" => {
            let length = command
                .get("length")
                .and_then(Value::as_u64)
                .unwrap_or(20)
                .clamp(1, 256) as usize;
            let classes = command.get("classes");
            let exclude_ambiguous = command
                .get("exclude_ambiguous")
                .and_then(Value::as_bool)
                .unwrap_or(true);
            let (password, entropy_bits) = generate_password(length, classes, exclude_ambiguous)?;
            Ok(json!({"password": password, "entropy_bits": entropy_bits}).to_string())
        }
        "sync_state" => Ok(json!({
            "dirty": state.dirty,
            "last_sync_unix": state.last_sync_unix,
            "baseline_present": state.baseline.etag.is_some() || state.baseline.sha256.is_some(),
        })
        .to_string()),
        "export_model_json" => with_database(state, |database| {
            let dump = database
                .canonical_json()
                .map_err(|error| FfiError::internal(error.to_string()))?;
            let sha = local_hash(dump.as_bytes());
            Ok(json!({"model": dump, "sha256": sha}).to_string())
        }),
        "diagnose" => with_database(state, |database| {
            let (groups, entries, totp_entries, history) = database_stats(database);
            Ok(json!({
                "format_version": database.format_version.clone(),
                "groups": groups,
                "entries": entries,
                "totp_entries": totp_entries,
                "history_entries": history,
                "write_backend": writer::WRITE_BACKEND,
            })
            .to_string())
        }),
        other => Err(FfiError::invalid(format!("unknown command: {other}"))),
    }
}

/// Save the current model as KDBX bytes.
#[no_mangle]
pub extern "C" fn kp_save(
    handle: *mut KpHandle,
    out_bytes: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    guarded(|| {
        if out_bytes.is_null() || out_len.is_null() {
            return fail(FfiError::invalid("output pointers are null"));
        }
        match with_state(handle, |state| {
            let password = state.password.clone().ok_or_else(FfiError::locked)?;
            let database = state.database.as_ref().ok_or_else(FfiError::locked)?;
            let bytes = writer::write_model(database, &password)
                .map_err(|_| FfiError::new("IoError", "cannot serialize the database"))?;
            set_buffer(out_bytes, out_len, bytes);
            Ok(())
        }) {
            Ok(()) => success(),
            Err(error) => fail(error),
        }
    })
}

fn set_buffer(out_bytes: *mut *mut u8, out_len: *mut usize, bytes: Vec<u8>) {
    let len = bytes.len();
    let boxed: Box<[u8]> = bytes.into_boxed_slice();
    let pointer = Box::into_raw(boxed) as *mut u8;
    unsafe {
        *out_bytes = pointer;
        *out_len = len;
    }
}

/// Validate that bytes decrypt to a KDBX database (no handle retained).
#[no_mangle]
pub extern "C" fn kp_verify(bytes: *const u8, len: usize, opts_json: *const c_char) -> i32 {
    guarded(|| {
        if bytes.is_null() {
            return fail(FfiError::invalid("KDBX buffer is null"));
        }
        let slice = unsafe { std::slice::from_raw_parts(bytes, len) };
        match options_password(opts_json).and_then(|password| open_database(slice, &password)) {
            Ok(_) => success(),
            Err(error) => fail(error),
        }
    })
}

/// Create a new empty KDBX 4.0 database. `opts_json` is
/// `{"password":"...","name":"..."}`; output is a caller-owned byte buffer.
#[no_mangle]
pub extern "C" fn kp_create_database(
    opts_json: *const c_char,
    out_bytes: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    guarded(|| {
        if opts_json.is_null() || out_bytes.is_null() || out_len.is_null() {
            return fail(FfiError::invalid("options or output pointer is null"));
        }
        let text = match unsafe { CStr::from_ptr(opts_json) }.to_str() {
            Ok(text) => text,
            Err(_) => return fail(FfiError::invalid("options JSON is not UTF-8")),
        };
        let options: Value = match serde_json::from_str(text) {
            Ok(value) => value,
            Err(_) => return fail(FfiError::invalid("options JSON is invalid")),
        };
        let password = match options.get("password").and_then(Value::as_str) {
            Some(value) if !value.is_empty() => value,
            _ => return fail(FfiError::invalid("password is required")),
        };
        let name = options
            .get("name")
            .and_then(Value::as_str)
            .unwrap_or("keepassity");
        match keepassity_core::kdbx::writer::create_empty(name, password) {
            Ok(bytes) => {
                set_buffer(out_bytes, out_len, bytes);
                success()
            }
            Err(_) => fail(FfiError::new("IoError", "cannot create database")),
        }
    })
}

/// Decide the next sync action from remote metadata.
#[no_mangle]
pub extern "C" fn kp_sync_decide(
    handle: *mut KpHandle,
    remote_meta_json: *const c_char,
    local_dirty: i32,
    out_json: *mut *mut c_char,
) -> i32 {
    guarded(|| {
        if remote_meta_json.is_null() || out_json.is_null() {
            return fail(FfiError::invalid("metadata or output pointer is null"));
        }
        let text = match unsafe { CStr::from_ptr(remote_meta_json) }.to_str() {
            Ok(text) => text,
            Err(_) => return fail(FfiError::invalid("metadata JSON is not UTF-8")),
        };
        let remote: SyncRemoteMeta = match serde_json::from_str(text) {
            Ok(remote) => remote,
            Err(_) => return fail(FfiError::invalid("metadata JSON is invalid")),
        };
        match with_state(handle, |state| {
            state.dirty = local_dirty != 0;
            let mut sync_state = SyncState::new(state.baseline.clone());
            sync_state.local_dirty = state.dirty;
            let decision = sync_state.decide(&keepassity_core::sync::RemoteMeta {
                etag: remote.etag,
                size: remote.size,
                modified_unix: remote.modified_unix,
                sha256: None,
            });
            Ok(json!({
                "action": action_name(decision.action),
                "reason": decision.reason,
                "degraded": decision.degraded,
                "retry_after_ms": decision.retry_after_ms,
            })
            .to_string())
        }) {
            Ok(text) => match CString::new(text) {
                Ok(value) => {
                    unsafe {
                        *out_json = value.into_raw();
                    }
                    success()
                }
                Err(_) => fail(FfiError::internal("NUL in JSON")),
            },
            Err(error) => fail(error),
        }
    })
}

/// Download remote bytes, merge, write the merged result and return conflicts.
#[no_mangle]
pub extern "C" fn kp_sync_merge(
    handle: *mut KpHandle,
    remote: *const u8,
    len: usize,
    out_bytes: *mut *mut u8,
    out_len: *mut usize,
    conflicts_json: *mut *mut c_char,
) -> i32 {
    guarded(|| {
        if remote.is_null() || out_bytes.is_null() || out_len.is_null() || conflicts_json.is_null()
        {
            return fail(FfiError::invalid("argument pointer is null"));
        }
        let remote_bytes = unsafe { std::slice::from_raw_parts(remote, len) };
        match with_state(handle, |state| {
            let password = state.password.clone().ok_or_else(FfiError::locked)?;
            let remote_db = open_database(remote_bytes, &password)?;
            let local = state.database.as_ref().ok_or_else(FfiError::locked)?;
            let merged = merge_for_sync(local, &remote_db);
            let bytes = writer::write_model(&merged.database, &password)
                .map_err(|_| FfiError::new("IoError", "cannot serialize merged database"))?;
            let conflicts: Vec<ConflictItem> =
                merged.conflicts.iter().map(ConflictItem::from).collect();
            let conflicts_text = serde_json::to_string(&conflicts)
                .map_err(|_| FfiError::internal("cannot serialize conflicts"))?;
            state.database = Some(merged.database);
            state.dirty = true;
            set_buffer(out_bytes, out_len, bytes);
            unsafe {
                *conflicts_json = CString::new(conflicts_text)
                    .map_err(|_| FfiError::internal("NUL in conflicts JSON"))?
                    .into_raw();
            }
            Ok(())
        }) {
            Ok(()) => success(),
            Err(error) => fail(error),
        }
    })
}

/// Record a successful sync baseline.
#[no_mangle]
pub extern "C" fn kp_sync_commit(
    handle: *mut KpHandle,
    etag: *const c_char,
    content_sha256: *const c_char,
) -> i32 {
    guarded(|| {
        let result = (|| {
            let etag = c_string_opt(etag)?.filter(|value| !value.trim().is_empty());
            // A baseline without the content hash is not a baseline: the next
            // sync could not tell whether the remote or local copy changed and
            // would be free to overwrite the local database. Fail closed.
            let sha = c_string_opt(content_sha256)?
                .filter(|value| !value.trim().is_empty())
                .ok_or_else(|| {
                    FfiError::invalid("content SHA-256 is required for a sync baseline")
                })?;
            if sha.len() != 64 || !sha.bytes().all(|byte| byte.is_ascii_hexdigit()) {
                return Err(FfiError::invalid(
                    "content SHA-256 must be 64 hexadecimal characters",
                ));
            }
            with_state(handle, |state| {
                state.baseline = SyncBaseline {
                    etag,
                    sha256: Some(sha),
                    size: None,
                    modified_unix: None,
                };
                state.dirty = false;
                state.last_sync_unix = Some(now_unix());
                Ok(())
            })
        })();
        match result {
            Ok(()) => success(),
            Err(error) => fail(error),
        }
    })
}

/// Mark local state dirty (unsaved/unuploaded edits).
#[no_mangle]
pub extern "C" fn kp_sync_mark_dirty(handle: *mut KpHandle) -> i32 {
    guarded(|| {
        match with_state(handle, |state| {
            state.dirty = true;
            Ok(())
        }) {
            Ok(()) => success(),
            Err(error) => fail(error),
        }
    })
}

/// Subscribe to events (v1 stores the callback; close/unsubscribe first).
#[no_mangle]
pub extern "C" fn kp_subscribe(
    handle: *mut KpHandle,
    callback: KpEventCb,
    user: *mut c_void,
) -> i32 {
    guarded(|| {
        match with_state(handle, |state| {
            state.subscriber = Some(Subscriber {
                _callback: callback,
                _user: user,
            });
            Ok(())
        }) {
            Ok(()) => success(),
            Err(error) => fail(error),
        }
    })
}

/// Remove the event subscription.
#[no_mangle]
pub extern "C" fn kp_unsubscribe(handle: *mut KpHandle) -> i32 {
    guarded(|| {
        match with_state(handle, |state| {
            state.subscriber = None;
            Ok(())
        }) {
            Ok(()) => success(),
            Err(error) => fail(error),
        }
    })
}

fn c_string_opt(pointer: *const c_char) -> Result<Option<String>, FfiError> {
    if pointer.is_null() {
        return Ok(None);
    }
    unsafe { CStr::from_ptr(pointer) }
        .to_str()
        .map(|text| Some(text.to_string()))
        .map_err(|_| FfiError::invalid("argument is not UTF-8"))
}

fn action_name(action: SyncActionKind) -> &'static str {
    match action {
        SyncActionKind::UpToDate => "up_to_date",
        SyncActionKind::Upload => "upload",
        SyncActionKind::Download => "download",
        SyncActionKind::DownloadAndMerge => "download_and_merge",
        SyncActionKind::Retry => "retry",
        SyncActionKind::Offline => "offline",
        SyncActionKind::Conflict => "conflict",
    }
}

fn parse_uuid_arg(command: &Value, key: &str) -> Result<Uuid, FfiError> {
    let text = command
        .get(key)
        .and_then(Value::as_str)
        .ok_or_else(|| FfiError::invalid(format!("{key} is required")))?;
    parse_uuid(text).ok_or_else(|| FfiError::invalid(format!("{key} is invalid")))
}

fn parse_uuid(text: &str) -> Option<Uuid> {
    let bytes = text.as_bytes();
    if bytes.len() != 32 {
        return None;
    }
    let mut out = [0u8; 16];
    for index in 0..16 {
        let high = (bytes[index * 2] as char).to_digit(16)?;
        let low = (bytes[index * 2 + 1] as char).to_digit(16)?;
        out[index] = (high * 16 + low) as u8;
    }
    Some(Uuid::from_bytes(out))
}

fn new_uuid() -> Uuid {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or(0);
    let mut bytes = [0u8; 16];
    bytes[..8].copy_from_slice(&(nanos as u64).to_le_bytes());
    bytes[8..12].copy_from_slice(&std::process::id().to_le_bytes());
    bytes[12..].copy_from_slice(&(nanos >> 64).to_le_bytes()[..4]);
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    Uuid::from_bytes(bytes)
}

fn now_unix() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_secs() as i64)
        .unwrap_or(0)
}

fn touch(entry: &mut Entry, now: i64) {
    let text = xml_datetime_from_unix(now);
    entry.times.last_modification = text.clone();
    entry.times.last_access = text;
}

fn set_field(entry: &mut Entry, key: &str, value: &str, protected: bool) {
    if let Some(field) = entry.strings.iter_mut().find(|field| field.key == key) {
        field.value = value.to_string();
        // An existing protection flag is a property of the file, not of this
        // call: a generic update path must never turn a protected secret into
        // plaintext by passing `false`. New fields take the caller's default.
        return;
    }
    entry.strings.push(StringField {
        key: key.to_string(),
        value: value.to_string(),
        protected,
        unknown: Vec::new(),
    });
}

fn entry_from_fields(uuid: Uuid, fields: &serde_json::Map<String, Value>) -> Entry {
    let now = xml_datetime_from_unix(now_unix());
    let mut entry = Entry {
        uuid,
        times: Times {
            creation: now.clone(),
            last_modification: now.clone(),
            last_access: now.clone(),
            location_changed: now,
            ..Times::default()
        },
        ..Entry::default()
    };
    for (key, value) in fields {
        if let Some(text) = value.as_str() {
            let protected = key == "Password" || key == "otp" || key.starts_with("TimeOtp-");
            set_field(&mut entry, key, text, protected);
        }
    }
    entry
}

fn apply_patch(entry: &mut Entry, patch: &serde_json::Map<String, Value>) {
    for (key, value) in patch {
        if key == "tags" {
            entry.tags = value
                .as_array()
                .map(|values| {
                    values
                        .iter()
                        .filter_map(Value::as_str)
                        .map(str::to_string)
                        .collect()
                })
                .unwrap_or_default();
            continue;
        }
        match value {
            Value::Null => entry.strings.retain(|field| &field.key != key),
            // Existing fields keep their flag (see `set_field`); a field added
            // by a patch is unprotected by default. The create path is what
            // marks Password/OTP fields protected.
            Value::String(text) => set_field(entry, key, text, false),
            _ => {}
        }
    }
}

fn flatten_groups(database: &Database) -> Vec<Value> {
    fn walk(group: &Group, parent: Option<Uuid>, order: &mut usize, out: &mut Vec<Value>) {
        out.push(json!({
            "uuid": group.uuid.to_hex(),
            "parent_uuid": parent.map(|uuid| uuid.to_hex()),
            "name": group.name,
            "icon": group.icon_id,
            "order": *order,
        }));
        *order += 1;
        for child in &group.groups {
            walk(child, Some(group.uuid), order, out);
        }
    }
    let mut out = Vec::new();
    let mut order = 0;
    walk(&database.root, None, &mut order, &mut out);
    out
}

fn all_entries(database: &Database) -> Vec<(&Entry, Uuid)> {
    fn walk<'a>(group: &'a Group, out: &mut Vec<(&'a Entry, Uuid)>) {
        for entry in &group.entries {
            out.push((entry, group.uuid));
        }
        for child in &group.groups {
            walk(child, out);
        }
    }
    let mut out = Vec::new();
    walk(&database.root, &mut out);
    out
}

fn select_entries<'a>(
    database: &'a Database,
    group_uuid: Option<&str>,
    include_subgroups: bool,
) -> Vec<(&'a Entry, Uuid)> {
    let Some(text) = group_uuid else {
        return all_entries(database);
    };
    let Some(uuid) = parse_uuid(text) else {
        return Vec::new();
    };
    let Some(group) = find_group(database, uuid) else {
        return Vec::new();
    };
    let mut out = Vec::new();
    if include_subgroups {
        fn walk<'a>(group: &'a Group, out: &mut Vec<(&'a Entry, Uuid)>) {
            for entry in &group.entries {
                out.push((entry, group.uuid));
            }
            for child in &group.groups {
                walk(child, out);
            }
        }
        walk(group, &mut out);
    } else {
        for entry in &group.entries {
            out.push((entry, group.uuid));
        }
    }
    out
}

fn find_group(database: &Database, uuid: Uuid) -> Option<&Group> {
    fn walk(group: &Group, uuid: Uuid) -> Option<&Group> {
        if group.uuid == uuid {
            return Some(group);
        }
        group.groups.iter().find_map(|child| walk(child, uuid))
    }
    walk(&database.root, uuid)
}

fn find_group_mut(database: &mut Database, uuid: Uuid) -> Option<&mut Group> {
    fn walk(group: &mut Group, uuid: Uuid) -> Option<&mut Group> {
        if group.uuid == uuid {
            return Some(group);
        }
        group.groups.iter_mut().find_map(|child| walk(child, uuid))
    }
    walk(&mut database.root, uuid)
}

fn find_entry(database: &Database, uuid: Uuid) -> Option<(&Entry, Uuid)> {
    fn walk(group: &Group, uuid: Uuid) -> Option<(&Entry, Uuid)> {
        if let Some(entry) = group.entries.iter().find(|entry| entry.uuid == uuid) {
            return Some((entry, group.uuid));
        }
        group.groups.iter().find_map(|child| walk(child, uuid))
    }
    walk(&database.root, uuid)
}

fn find_entry_mut(database: &mut Database, uuid: Uuid) -> Option<(&mut Entry, Uuid)> {
    fn walk(group: &mut Group, uuid: Uuid) -> Option<(&mut Entry, Uuid)> {
        let parent = group.uuid;
        if let Some(index) = group.entries.iter().position(|entry| entry.uuid == uuid) {
            return Some((&mut group.entries[index], parent));
        }
        group.groups.iter_mut().find_map(|child| walk(child, uuid))
    }
    walk(&mut database.root, uuid)
}

fn remove_entry(group: &mut Group, uuid: Uuid) -> Option<(Entry, Uuid)> {
    let parent = group.uuid;
    if let Some(index) = group.entries.iter().position(|entry| entry.uuid == uuid) {
        return Some((group.entries.remove(index), parent));
    }
    for child in &mut group.groups {
        if let Some(found) = remove_entry(child, uuid) {
            return Some(found);
        }
    }
    None
}

fn remove_group(group: &mut Group, uuid: Uuid) -> Option<Group> {
    if let Some(index) = group.groups.iter().position(|child| child.uuid == uuid) {
        return Some(group.groups.remove(index));
    }
    for child in &mut group.groups {
        if let Some(found) = remove_group(child, uuid) {
            return Some(found);
        }
    }
    None
}

fn collect_group_tombstones(group: &Group, out: &mut Vec<Uuid>) {
    out.push(group.uuid);
    for entry in &group.entries {
        out.push(entry.uuid);
    }
    for child in &group.groups {
        collect_group_tombstones(child, out);
    }
}

/// Return the recycle-bin group, creating it when the database enables the
/// recycle bin but has no usable group UUID. A new database used to have a nil
/// UUID, so deleting an entry silently produced a permanent tombstone while the
/// UI promised a recycle bin.
fn ensure_recycle_bin(database: &mut Database) -> Option<Uuid> {
    if !database.meta.recycle_bin_enabled {
        return None;
    }
    if let Some(uuid) = database.meta.recycle_bin_uuid {
        if uuid != database.root.uuid && find_group(database, uuid).is_some() {
            return Some(uuid);
        }
    }
    // Adopt an existing group with KeePass's standard name instead of creating
    // a second one next to it.
    if let Some(uuid) = database
        .root
        .groups
        .iter()
        .find(|group| group.name == "Recycle Bin")
        .map(|group| group.uuid)
    {
        database.meta.recycle_bin_uuid = Some(uuid);
        return Some(uuid);
    }
    let uuid = new_uuid();
    let now = xml_datetime_from_unix(now_unix());
    database.meta.recycle_bin_uuid = Some(uuid);
    database.meta.recycle_bin_changed = Some(now.clone());
    database.root.groups.push(Group {
        uuid,
        name: "Recycle Bin".to_string(),
        icon_id: 43,
        times: Times {
            creation: now.clone(),
            last_modification: now.clone(),
            last_access: now.clone(),
            expiry: now.clone(),
            location_changed: now,
            ..Times::default()
        },
        is_expanded: false,
        ..Group::default()
    });
    Some(uuid)
}

fn parse_group_name(command: &Value) -> Result<String, FfiError> {
    let name = command
        .get("name")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|name| !name.is_empty())
        .ok_or_else(|| FfiError::invalid("group name is required"))?;
    Ok(name.to_string())
}

fn database_stats(database: &Database) -> (usize, usize, usize, usize) {
    fn walk(group: &Group, counts: &mut (usize, usize, usize, usize)) {
        counts.0 += 1;
        for entry in &group.entries {
            counts.1 += 1;
            counts.3 += entry.history.len();
            if entry.has_totp() {
                counts.2 += 1;
            }
        }
        for child in &group.groups {
            walk(child, counts);
        }
    }
    let mut counts = (0, 0, 0, 0);
    walk(&database.root, &mut counts);
    counts
}

fn algorithm_name(algorithm: CoreTotpAlgorithm) -> &'static str {
    match algorithm {
        CoreTotpAlgorithm::Sha1 => "SHA1",
        CoreTotpAlgorithm::Sha256 => "SHA256",
        CoreTotpAlgorithm::Sha512 => "SHA512",
    }
}

fn core_totp_to_dto(spec: &totp_fields::TotpSpec) -> DtoTotpConfig {
    let config = &spec.config;
    DtoTotpConfig {
        secret_base32: base32_encode(&config.secret),
        issuer: spec.issuer.clone(),
        account: spec.account.clone(),
        algorithm: match config.algorithm {
            CoreTotpAlgorithm::Sha1 => DtoTotpAlgorithm::Sha1,
            CoreTotpAlgorithm::Sha256 => DtoTotpAlgorithm::Sha256,
            CoreTotpAlgorithm::Sha512 => DtoTotpAlgorithm::Sha512,
        },
        digits: config.digits,
        period: config.period,
        encoder: match config.encoder {
            CoreTotpEncoder::Totp => DtoTotpEncoder::Totp,
            CoreTotpEncoder::Steam => DtoTotpEncoder::Steam,
        },
        counter: None,
    }
}

fn dto_to_totp_spec(value: &Value) -> Result<totp_fields::TotpSpec, FfiError> {
    let secret = value
        .get("secret_base32")
        .and_then(Value::as_str)
        .ok_or_else(|| FfiError::invalid("secret_base32 is required"))?;
    let secret_bytes =
        base32_decode(secret).map_err(|error| FfiError::invalid(error.to_string()))?;
    let algorithm = match value
        .get("algorithm")
        .and_then(Value::as_str)
        .unwrap_or("SHA1")
    {
        "SHA256" => CoreTotpAlgorithm::Sha256,
        "SHA512" => CoreTotpAlgorithm::Sha512,
        _ => CoreTotpAlgorithm::Sha1,
    };
    let encoder = match value
        .get("encoder")
        .and_then(Value::as_str)
        .unwrap_or("totp")
    {
        "steam" => CoreTotpEncoder::Steam,
        _ => CoreTotpEncoder::Totp,
    };
    let config = CoreTotpConfig {
        secret: secret_bytes,
        algorithm,
        digits: value
            .get("digits")
            .and_then(Value::as_u64)
            .unwrap_or(6)
            .clamp(1, 10) as u32,
        period: value
            .get("period")
            .and_then(Value::as_u64)
            .unwrap_or(30)
            .clamp(1, 3_600) as u32,
        encoder,
    };
    Ok(totp_fields::TotpSpec {
        config,
        issuer: value
            .get("issuer")
            .and_then(Value::as_str)
            .map(str::to_string),
        account: value
            .get("account")
            .and_then(Value::as_str)
            .map(str::to_string),
    })
}

fn generate_password(
    length: usize,
    classes: Option<&Value>,
    exclude_ambiguous: bool,
) -> Result<(String, f64), FfiError> {
    let mut alphabet = String::new();
    let lower = classes
        .and_then(|classes| classes.get("lowercase"))
        .and_then(Value::as_bool)
        .unwrap_or(true);
    let upper = classes
        .and_then(|classes| classes.get("uppercase"))
        .and_then(Value::as_bool)
        .unwrap_or(true);
    let digits = classes
        .and_then(|classes| classes.get("digits"))
        .and_then(Value::as_bool)
        .unwrap_or(true);
    let symbols = classes
        .and_then(|classes| classes.get("symbols"))
        .and_then(Value::as_bool)
        .unwrap_or(true);
    if lower {
        alphabet.push_str("abcdefghijklmnopqrstuvwxyz");
    }
    if upper {
        alphabet.push_str("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
    }
    if digits {
        alphabet.push_str("0123456789");
    }
    if symbols {
        alphabet.push_str("!@#$%^&*()-_=+[]{};:,.?/");
    }
    if exclude_ambiguous {
        alphabet.retain(|character| !"Il1O0o".contains(character));
    }
    if alphabet.is_empty() {
        return Err(FfiError::invalid("password charset is empty"));
    }
    let alphabet: Vec<char> = alphabet.chars().collect();
    let mut bytes = vec![0u8; length * 4];
    fill_random(&mut bytes).map_err(|_| FfiError::new("Internal", "OS randomness unavailable"))?;
    let mut password = String::with_capacity(length);
    let mut index = 0;
    while password.chars().count() < length {
        let threshold = 256 - (256 % alphabet.len());
        let byte = bytes[index % bytes.len()];
        index += 1;
        if usize::from(byte) < threshold {
            password.push(alphabet[usize::from(byte) % alphabet.len()]);
        }
    }
    let entropy = length as f64 * (alphabet.len() as f64).log2();
    Ok((password, entropy))
}

#[cfg(unix)]
fn fill_random(buffer: &mut [u8]) -> Result<(), ()> {
    use std::fs::File;
    use std::io::Read;
    let mut file = File::open("/dev/urandom").map_err(|_| ())?;
    file.read_exact(buffer).map_err(|_| ())
}

#[cfg(windows)]
#[link(name = "advapi32")]
extern "system" {
    fn SystemFunction036(buffer: *mut u8, len: u32) -> u8;
}

#[cfg(windows)]
fn fill_random(buffer: &mut [u8]) -> Result<(), ()> {
    let len = u32::try_from(buffer.len()).map_err(|_| ())?;
    let status = unsafe { SystemFunction036(buffer.as_mut_ptr(), len) };
    if status == 0 {
        Err(())
    } else {
        Ok(())
    }
}

#[cfg(not(any(unix, windows)))]
fn fill_random(buffer: &mut [u8]) -> Result<(), ()> {
    for (index, byte) in buffer.iter_mut().enumerate() {
        *byte = (index.wrapping_mul(31) ^ (now_unix() as usize)) as u8;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture() -> Vec<u8> {
        std::fs::read(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../tests/fixtures/keepass-rs/test_db_kdbx4_with_password_aes.kdbx"
        ))
        .expect("fixture")
    }

    fn open_fixture() -> *mut KpHandle {
        let bytes = fixture();
        let opts = CString::new(r#"{"password":"demopass"}"#).unwrap();
        let handle = kp_open(bytes.as_ptr(), bytes.len(), opts.as_ptr());
        assert!(!handle.is_null());
        handle
    }

    fn fixture_bytes(name: &str) -> Vec<u8> {
        std::fs::read(format!(
            "{}/../../tests/fixtures/keepass-rs/{name}",
            env!("CARGO_MANIFEST_DIR")
        ))
        .expect("fixture")
    }

    fn open_bytes(bytes: &[u8], password: &str) -> *mut KpHandle {
        let opts = CString::new(format!(r#"{{"password":"{password}"}}"#)).unwrap();
        let handle = kp_open(bytes.as_ptr(), bytes.len(), opts.as_ptr());
        assert!(!handle.is_null());
        handle
    }

    fn parse(json_text: &str) -> Value {
        serde_json::from_str(json_text).expect("valid command JSON")
    }

    /// Assert a command fails and return its error JSON.
    fn call_error(handle: *mut KpHandle, command: &str) -> String {
        let command = CString::new(command).unwrap();
        let mut output = std::ptr::null_mut();
        assert_ne!(
            kp_call(handle, command.as_ptr(), &mut output),
            0,
            "command should fail: {command:?}"
        );
        assert!(output.is_null());
        let error = unsafe { CStr::from_ptr(kp_last_error_json()) }
            .to_str()
            .expect("error JSON")
            .to_string();
        assert!(!error.contains("demopass"), "error leaks the password");
        error
    }

    fn call(handle: *mut KpHandle, command: &str) -> String {
        let command = CString::new(command).unwrap();
        let mut output = std::ptr::null_mut();
        assert_eq!(kp_call(handle, command.as_ptr(), &mut output), 0);
        assert!(!output.is_null());
        let text = unsafe { CStr::from_ptr(output) }
            .to_str()
            .unwrap()
            .to_string();
        kp_string_free(output);
        text
    }

    #[test]
    fn error_json_is_sanitized_and_cleared() {
        let opts = CString::new(r#"{"password":"wrong"}"#).unwrap();
        let bytes = fixture();
        let handle = kp_open(bytes.as_ptr(), bytes.len(), opts.as_ptr());
        assert!(handle.is_null());
        let error = unsafe { CStr::from_ptr(kp_last_error_json()) }
            .to_str()
            .unwrap();
        assert!(error.contains("BadPassword"));
        assert!(!error.contains("wrong"));
        assert!(!error.contains("demopass"));
    }

    #[test]
    fn open_version_list_save_and_diagnose() {
        let handle = open_fixture();
        let version: Value =
            serde_json::from_str(&call(handle, r#"{"command":"version"}"#)).unwrap();
        assert_eq!(version["api"], 1);
        let groups: Value =
            serde_json::from_str(&call(handle, r#"{"command":"list_groups"}"#)).unwrap();
        assert!(groups.as_array().is_some_and(|groups| !groups.is_empty()));
        let entries: Value =
            serde_json::from_str(&call(handle, r#"{"command":"list_entries"}"#)).unwrap();
        assert!(entries
            .as_array()
            .is_some_and(|entries| !entries.is_empty()));
        let diagnose: Value =
            serde_json::from_str(&call(handle, r#"{"command":"diagnose"}"#)).unwrap();
        assert!(diagnose["entries"].as_u64().unwrap() > 0);

        let mut saved = std::ptr::null_mut();
        let mut saved_len = 0usize;
        assert_eq!(kp_save(handle, &mut saved, &mut saved_len), 0);
        assert!(!saved.is_null() && saved_len > 0);
        let verify_opts = CString::new(r#"{"password":"demopass"}"#).unwrap();
        assert_eq!(kp_verify(saved, saved_len, verify_opts.as_ptr()), 0);
        kp_buffer_free(saved, saved_len);
        assert_eq!(kp_close(handle), 0);
    }

    #[test]
    fn null_and_locked_paths_do_not_panic() {
        assert_ne!(kp_close(std::ptr::null_mut()), 0);
        assert_ne!(kp_lock(std::ptr::null_mut()), 0);
        let handle = open_fixture();
        assert_eq!(kp_lock(handle), 0);
        let command = CString::new(r#"{"command":"list_entries"}"#).unwrap();
        let mut output = std::ptr::null_mut();
        assert_ne!(kp_call(handle, command.as_ptr(), &mut output), 0);
        assert_eq!(kp_close(handle), 0);
    }

    #[test]
    fn crud_and_totp_commands_smoke() {
        let handle = open_fixture();
        let groups: Value =
            serde_json::from_str(&call(handle, r#"{"command":"list_groups"}"#)).unwrap();
        let group_uuid = groups[0]["uuid"].as_str().unwrap().to_string();
        let create = format!(
            r#"{{"command":"create_entry","group_uuid":"{}","fields":{{"Title":"ffi-created","UserName":"u","Password":"p"}}}}"#,
            group_uuid
        );
        let created: Value = serde_json::from_str(&call(handle, &create)).unwrap();
        let entry_uuid = created["uuid"].as_str().unwrap().to_string();
        let get = format!(r#"{{"command":"get_entry","uuid":"{}"}}"#, entry_uuid);
        let full: Value = serde_json::from_str(&call(handle, &get)).unwrap();
        assert_eq!(full["title"], "ffi-created");
        let update = format!(
            r#"{{"command":"update_entry","uuid":"{}","patch":{{"Title":"ffi-updated"}}}}"#,
            entry_uuid
        );
        let _: Value = serde_json::from_str(&call(handle, &update)).unwrap();
        let set_totp = format!(
            r#"{{"command":"totp_set","uuid":"{}","config":{{"secret_base32":"JBSWY3DPEHPK3PXP","algorithm":"SHA1","digits":6,"period":30,"encoder":"totp"}}}}"#,
            entry_uuid
        );
        let _: Value = serde_json::from_str(&call(handle, &set_totp)).unwrap();
        let code = format!(
            r#"{{"command":"totp_code","uuid":"{}","at_unix":0}}"#,
            entry_uuid
        );
        let code: Value = serde_json::from_str(&call(handle, &code)).unwrap();
        assert_eq!(code["code"].as_str().unwrap().len(), 6);
        let delete_totp = format!(r#"{{"command":"totp_delete","uuid":"{}"}}"#, entry_uuid);
        let _: Value = serde_json::from_str(&call(handle, &delete_totp)).unwrap();
        let delete = format!(
            r#"{{"command":"delete_entry","uuid":"{}","permanent":true}}"#,
            entry_uuid
        );
        let _: Value = serde_json::from_str(&call(handle, &delete)).unwrap();
        assert_eq!(kp_close(handle), 0);
    }

    fn first_group_uuid(handle: *mut KpHandle) -> String {
        let groups: Value =
            serde_json::from_str(&call(handle, r#"{"command":"list_groups"}"#)).unwrap();
        groups[0]["uuid"].as_str().expect("group uuid").to_string()
    }

    fn create_entry_in(handle: *mut KpHandle, group_uuid: &str, fields: &str) -> String {
        let command = format!(
            r#"{{"command":"create_entry","group_uuid":"{group_uuid}","fields":{fields}}}"#
        );
        let created: Value = serde_json::from_str(&call(handle, &command)).unwrap();
        created["uuid"].as_str().expect("entry uuid").to_string()
    }

    /// Clone one entry out of the open handle (test-only access to the model).
    fn state_entry(handle: *mut KpHandle, uuid: Uuid) -> Entry {
        let handle = unsafe { &*handle };
        let state = handle.state.lock().expect("handle lock");
        let database = state.database.as_ref().expect("open database");
        find_entry(database, uuid)
            .expect("entry in model")
            .0
            .clone()
    }

    fn set_history_limits(handle: *mut KpHandle, max_items: i64, max_size: i64) {
        let handle = unsafe { &*handle };
        let mut state = handle.state.lock().expect("handle lock");
        let database = state.database.as_mut().expect("open database");
        database.meta.history_max_items = max_items;
        database.meta.history_max_size = max_size;
    }

    fn update_field(handle: *mut KpHandle, uuid: &str, key: &str, value: &str) {
        let command = format!(
            r#"{{"command":"update_entry","uuid":"{uuid}","patch":{{"{key}":"{value}"}}}}"#
        );
        let _: Value = serde_json::from_str(&call(handle, &command)).unwrap();
    }

    #[test]
    fn update_entry_records_old_password_in_history() {
        let handle = open_fixture();
        let group = first_group_uuid(handle);
        let uuid_text = create_entry_in(
            handle,
            &group,
            r#"{"Title":"history-test","UserName":"u","Password":"old-password"}"#,
        );
        let uuid = parse_uuid(&uuid_text).expect("uuid");

        update_field(handle, &uuid_text, "Password", "new-password");

        let full: Value = serde_json::from_str(&call(
            handle,
            &format!(r#"{{"command":"get_entry","uuid":"{uuid_text}"}}"#),
        ))
        .unwrap();
        assert_eq!(full["history_count"], 1);

        let entry = state_entry(handle, uuid);
        assert_eq!(entry.field("Password"), Some("new-password"));
        assert_eq!(entry.history.len(), 1);
        assert_eq!(entry.history[0].field("Password"), Some("old-password"));
        assert!(
            entry.history[0].history.is_empty(),
            "history snapshots must not nest history"
        );
        assert_eq!(entry.history[0].previous_parent_group, None);
        assert!(
            entry
                .strings
                .iter()
                .find(|field| field.key == "Password")
                .is_some_and(|field| field.protected),
            "an existing protected password must stay protected"
        );
        assert_eq!(kp_close(handle), 0);
    }

    #[test]
    fn update_entry_patch_keeps_existing_protected_flag() {
        let handle = open_fixture();
        let group = first_group_uuid(handle);
        let uuid_text = create_entry_in(
            handle,
            &group,
            r#"{"Title":"otp-protected","otp":"JBSWY3DPEHPK3PXP"}"#,
        );
        let uuid = parse_uuid(&uuid_text).expect("uuid");
        let before = state_entry(handle, uuid);
        assert!(
            before
                .strings
                .iter()
                .find(|field| field.key == "otp")
                .is_some_and(|field| field.protected),
            "create must store otp as protected"
        );

        update_field(handle, &uuid_text, "otp", "JBSWY3DPEHPK3PXQ");

        let entry = state_entry(handle, uuid);
        let otp = entry
            .strings
            .iter()
            .find(|field| field.key == "otp")
            .expect("otp field");
        assert_eq!(otp.value, "JBSWY3DPEHPK3PXQ");
        assert!(
            otp.protected,
            "a generic patch must not turn a protected field into plaintext"
        );
        assert_eq!(kp_close(handle), 0);
    }

    #[test]
    fn update_entry_respects_history_item_limit() {
        let handle = open_fixture();
        set_history_limits(handle, 2, -1);
        let group = first_group_uuid(handle);
        let uuid_text = create_entry_in(
            handle,
            &group,
            r#"{"Title":"history-limit","Password":"p0"}"#,
        );
        let uuid = parse_uuid(&uuid_text).expect("uuid");
        for password in ["p1", "p2", "p3"] {
            update_field(handle, &uuid_text, "Password", password);
        }

        let entry = state_entry(handle, uuid);
        assert_eq!(entry.field("Password"), Some("p3"));
        assert_eq!(entry.history.len(), 2);
        assert_eq!(entry.history[0].field("Password"), Some("p1"));
        assert_eq!(entry.history[1].field("Password"), Some("p2"));
        assert_eq!(kp_close(handle), 0);
    }

    #[test]
    fn update_entry_respects_history_size_limit() {
        let handle = open_fixture();
        set_history_limits(handle, -1, 0);
        let group = first_group_uuid(handle);
        let uuid_text = create_entry_in(
            handle,
            &group,
            r#"{"Title":"history-size","Password":"p0"}"#,
        );
        let uuid = parse_uuid(&uuid_text).expect("uuid");

        update_field(handle, &uuid_text, "Password", "p1");

        let entry = state_entry(handle, uuid);
        assert_eq!(entry.field("Password"), Some("p1"));
        assert!(
            entry.history.is_empty(),
            "a zero size limit must keep no history"
        );
        assert_eq!(kp_close(handle), 0);
    }

    #[test]
    fn move_entry_to_missing_group_keeps_the_entry() {
        let handle = open_fixture();
        let group = first_group_uuid(handle);
        let uuid_text = create_entry_in(
            handle,
            &group,
            r#"{"Title":"move-me","UserName":"u","Password":"p"}"#,
        );
        let missing = "ffffffffffffffffffffffffffffffff";
        let error = call_error(
            handle,
            &format!(r#"{{"command":"move_entry","uuid":"{uuid_text}","group_uuid":"{missing}"}}"#),
        );
        assert!(error.contains("NotFound"), "unexpected error: {error}");

        // The failed move must not have removed the entry from the model.
        let full: Value = serde_json::from_str(&call(
            handle,
            &format!(r#"{{"command":"get_entry","uuid":"{uuid_text}"}}"#),
        ))
        .unwrap();
        assert_eq!(full["title"], "move-me");
        assert_eq!(full["group_uuid"], group);
        assert_eq!(kp_close(handle), 0);
    }

    #[test]
    fn password_generate_honours_options_and_reports_consistent_entropy() {
        let handle = open_fixture();

        // Lowercase + digits without ambiguous look-alikes: 24 + 8 = 32
        // symbols, i.e. exactly 5 bits per character -> 160 bits for 32 chars.
        let generated = parse(&call(
            handle,
            r#"{"command":"password_generate","length":32,"classes":{"lowercase":true,"uppercase":false,"digits":true,"symbols":false},"exclude_ambiguous":true}"#,
        ));
        let password = generated["password"].as_str().expect("password string");
        assert_eq!(password.chars().count(), 32);
        assert!(password
            .chars()
            .all(|character| character.is_ascii_lowercase() || character.is_ascii_digit()));
        assert!(
            password
                .chars()
                .all(|character| !"Il1O0o".contains(character)),
            "ambiguous characters must be excluded: {password}"
        );
        assert_eq!(generated["entropy_bits"].as_f64(), Some(160.0));

        // Digits only, ambiguous excluded: "23456789" -> 3 bits per char.
        let digits_only = parse(&call(
            handle,
            r#"{"command":"password_generate","length":16,"classes":{"lowercase":false,"uppercase":false,"digits":true,"symbols":false},"exclude_ambiguous":true}"#,
        ));
        let password = digits_only["password"].as_str().expect("password string");
        assert_eq!(password.len(), 16);
        assert!(password
            .chars()
            .all(|character| ('2'..='9').contains(&character)));
        assert_eq!(digits_only["entropy_bits"].as_f64(), Some(48.0));

        // The same digits-only charset without the exclusion has 10 symbols;
        // entropy must grow accordingly (10 * log2(10) for 10 characters).
        let digits_without_exclusion = parse(&call(
            handle,
            r#"{"command":"password_generate","length":10,"classes":{"lowercase":false,"uppercase":false,"digits":true,"symbols":false},"exclude_ambiguous":false}"#,
        ));
        let password = digits_without_exclusion["password"]
            .as_str()
            .expect("password string");
        assert_eq!(password.len(), 10);
        assert!(password.chars().all(|character| character.is_ascii_digit()));
        let expected = 10.0 * 10.0_f64.log2();
        assert_eq!(
            digits_without_exclusion["entropy_bits"].as_f64(),
            Some(expected)
        );
        assert_ne!(
            digits_only["entropy_bits"].as_f64(),
            digits_without_exclusion["entropy_bits"].as_f64()
        );

        // All classes enabled: 26 lower + 26 upper + 10 digits + 24 symbols = 86.
        let everything = parse(&call(
            handle,
            r#"{"command":"password_generate","length":12,"classes":{"lowercase":true,"uppercase":true,"digits":true,"symbols":true},"exclude_ambiguous":false}"#,
        ));
        assert_eq!(everything["password"].as_str().unwrap().chars().count(), 12);
        let expected = 12.0 * 86.0_f64.log2();
        assert_eq!(everything["entropy_bits"].as_f64(), Some(expected));

        // No character class selected must be rejected, not silently generated.
        let error = call_error(
            handle,
            r#"{"command":"password_generate","length":8,"classes":{"lowercase":false,"uppercase":false,"digits":false,"symbols":false}}"#,
        );
        assert!(
            error.contains("InvalidArgument"),
            "unexpected error: {error}"
        );
        assert!(
            error.contains("charset is empty"),
            "unexpected error: {error}"
        );

        assert_eq!(kp_close(handle), 0);
    }

    #[test]
    fn totp_code_at_unix_uses_the_requested_window() {
        let bytes = fixture_bytes("test_db_kdbx4_with_totp_entry.kdbx");
        let handle = open_bytes(&bytes, "test");

        let entries = parse(&call(handle, r#"{"command":"list_entries"}"#));
        let uuid = entries
            .as_array()
            .expect("entry list")
            .iter()
            .find(|entry| entry["has_totp"].as_bool() == Some(true))
            .and_then(|entry| entry["uuid"].as_str())
            .expect("fixture has a TOTP entry")
            .to_string();
        let full = parse(&call(
            handle,
            &format!(r#"{{"command":"get_entry","uuid":"{uuid}"}}"#),
        ));
        assert_eq!(full["title"].as_str(), Some("this entry has totp"));
        let config = &full["totp"];
        // Public fixture configuration: otpauth://totp/KeePassXC:none
        // ?secret=JBSWY3DPEHPK3PXP&period=30&digits=6 (SHA-1).
        assert_eq!(config["secret_base32"].as_str(), Some("JBSWY3DPEHPK3PXP"));
        assert_eq!(config["algorithm"].as_str(), Some("SHA1"));
        assert_eq!(config["digits"].as_u64(), Some(6));
        assert_eq!(config["period"].as_u64(), Some(30));

        // Reference codes for RFC 6238 TOTP-SHA1 / 6 digits / 30 s with the
        // fixture secret, computed by an independent implementation:
        // (at_unix, code, seconds remaining in the window).
        let cases: [(u64, &str, u64); 8] = [
            (0, "282760", 30),
            (29, "282760", 1),
            (30, "996554", 30),
            (59, "996554", 1),
            (1_700_000_000, "324550", 10),
            (1_700_000_009, "324550", 1),
            (1_700_000_010, "367665", 30),
            (1_700_000_040, "870960", 30),
        ];
        for (at_unix, expected_code, expected_remaining) in cases {
            let code = parse(&call(
                handle,
                &format!(r#"{{"command":"totp_code","uuid":"{uuid}","at_unix":{at_unix}}}"#),
            ));
            assert_eq!(
                code["code"].as_str(),
                Some(expected_code),
                "code at at_unix={at_unix}"
            );
            assert_eq!(
                code["remaining"].as_u64(),
                Some(expected_remaining),
                "remaining at at_unix={at_unix}"
            );
            assert_eq!(code["period"].as_u64(), Some(30));
            assert_eq!(code["digits"].as_u64(), Some(6));
            assert_eq!(code["algorithm"].as_str(), Some("SHA1"));
            assert_eq!(code["skew_warning"].as_bool(), Some(false));
        }

        // Same window keeps the code stable, a step boundary rotates it.
        let window_start = parse(&call(
            handle,
            &format!(r#"{{"command":"totp_code","uuid":"{uuid}","at_unix":0}}"#),
        ));
        let window_end = parse(&call(
            handle,
            &format!(r#"{{"command":"totp_code","uuid":"{uuid}","at_unix":29}}"#),
        ));
        let next_window = parse(&call(
            handle,
            &format!(r#"{{"command":"totp_code","uuid":"{uuid}","at_unix":30}}"#),
        ));
        assert_eq!(window_start["code"], window_end["code"]);
        assert_ne!(window_start["code"], next_window["code"]);
        assert_eq!(window_start["remaining"].as_u64(), Some(30));
        assert_eq!(window_end["remaining"].as_u64(), Some(1));
        assert_eq!(next_window["remaining"].as_u64(), Some(30));

        assert_eq!(kp_close(handle), 0);
    }

    #[test]
    fn deleting_an_entry_creates_and_uses_the_recycle_bin() {
        let options = CString::new(r#"{"password":"recycle-pass","name":"recycle"}"#).unwrap();
        let mut saved = std::ptr::null_mut();
        let mut saved_len = 0usize;
        assert_eq!(
            kp_create_database(options.as_ptr(), &mut saved, &mut saved_len),
            0
        );
        let bytes = unsafe { std::slice::from_raw_parts(saved, saved_len) }.to_vec();
        kp_buffer_free(saved, saved_len);
        let handle = open_bytes(&bytes, "recycle-pass");

        let groups: Value =
            serde_json::from_str(&call(handle, r#"{"command":"list_groups"}"#)).unwrap();
        let root = groups[0]["uuid"].as_str().unwrap();
        let uuid = create_entry_in(handle, root, r#"{"Title":"delete-me","Password":"p"}"#);
        let deleted: Value = serde_json::from_str(&call(
            handle,
            &format!(r#"{{"command":"delete_entry","uuid":"{uuid}"}}"#),
        ))
        .unwrap();
        assert_eq!(deleted["recycled"], true);

        let groups: Value =
            serde_json::from_str(&call(handle, r#"{"command":"list_groups"}"#)).unwrap();
        let recycle = groups
            .as_array()
            .unwrap()
            .iter()
            .find(|group| group["name"] == "Recycle Bin")
            .expect("a recycle-bin group must be created");
        let recycle_uuid = recycle["uuid"].as_str().unwrap();
        let entries: Value = serde_json::from_str(&call(
            handle,
            &format!(
                r#"{{"command":"list_entries","group_uuid":"{recycle_uuid}","include_subgroups":true}}"#
            ),
        ))
        .unwrap();
        assert!(
            entries
                .as_array()
                .unwrap()
                .iter()
                .any(|entry| entry["uuid"] == uuid.as_str()),
            "the deleted entry must live in the recycle bin"
        );
        assert_eq!(kp_close(handle), 0);
    }

    #[test]
    fn permanent_entry_deletion_still_uses_a_tombstone() {
        let handle = open_fixture();
        let groups: Value =
            serde_json::from_str(&call(handle, r#"{"command":"list_groups"}"#)).unwrap();
        let root = groups[0]["uuid"].as_str().unwrap();
        let uuid = create_entry_in(handle, root, r#"{"Title":"gone","Password":"p"}"#);
        let deleted: Value = serde_json::from_str(&call(
            handle,
            &format!(r#"{{"command":"delete_entry","uuid":"{uuid}","permanent":true}}"#),
        ))
        .unwrap();
        assert_eq!(deleted["recycled"], false);
        let full = call_error(
            handle,
            &format!(r#"{{"command":"get_entry","uuid":"{uuid}"}}"#),
        );
        assert!(full.contains("NotFound"), "unexpected error: {full}");
        assert_eq!(kp_close(handle), 0);
    }

    #[test]
    fn save_keeps_the_source_kdbx_header() {
        let direct = keepassity_core::kdbx::reader::parse(&fixture(), "demopass").unwrap();
        let source_version = direct.format_version.clone();
        let handle = open_fixture();
        let mut bytes = std::ptr::null_mut();
        let mut len = 0usize;
        assert_eq!(kp_save(handle, &mut bytes, &mut len), 0);
        let saved = unsafe { std::slice::from_raw_parts(bytes, len) };
        let database =
            keepassity_core::kdbx::reader::parse(saved, "demopass").expect("saved file reads");
        let header = database.outer.expect("saved file keeps its header");
        let version = header.version.clone();
        let kdf = header.kdf.clone();
        assert_eq!(version, source_version, "kdf={kdf:?}");
        assert!(
            matches!(
                header.kdf,
                Some(keepassity_core::model::KdfSettings::Aes { .. })
            ),
            "the AES-KDF fixture must not be rewritten as Argon2id"
        );
        kp_buffer_free(bytes, len);
        assert_eq!(kp_close(handle), 0);
    }

    #[test]
    fn api_version_matches_core() {
        assert_eq!(kp_api_version(), keepassity_core::API_MAJOR);
    }

    #[test]
    fn sync_commit_rejects_an_incomplete_baseline() {
        let handle = open_fixture();
        // A null/null commit used to succeed and clear the dirty flag, which let
        // the next sync overwrite local edits without a baseline.
        assert_ne!(
            kp_sync_commit(handle, std::ptr::null(), std::ptr::null()),
            0
        );
        let empty_etag = CString::new("").unwrap();
        assert_ne!(
            kp_sync_commit(handle, empty_etag.as_ptr(), std::ptr::null()),
            0
        );
        let short_sha = CString::new("abc").unwrap();
        assert_ne!(
            kp_sync_commit(handle, std::ptr::null(), short_sha.as_ptr()),
            0
        );
        let valid_sha = CString::new("a".repeat(64)).unwrap();
        assert_eq!(
            kp_sync_commit(handle, std::ptr::null(), valid_sha.as_ptr()),
            0
        );
        assert_eq!(kp_close(handle), 0);
    }
}
