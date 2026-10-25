/* SPDX-License-Identifier: GPL-3.0-or-later */
/* keepassity FFI v1 — hand-maintained from .agents/contracts/ffi.md.
 * ABI is additive-only; this header is the C source of truth. */
#ifndef KEEPASSITY_H
#define KEEPASSITY_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct KpHandle KpHandle;
typedef void (*KpEventCb)(void* user, const char* event_json);

/* 0 = success, non-zero = failure; details via kp_last_error_json(). */
uint32_t kp_api_version(void);

/* Borrowed pointer valid until the next call on this thread. Do not free. */
const char* kp_last_error_json(void);

void kp_string_free(char* string);
void kp_buffer_free(uint8_t* buffer, size_t len);

KpHandle* kp_open(const uint8_t* kdbx, size_t len, const char* opts_json);
int32_t kp_lock(KpHandle* handle);
int32_t kp_close(KpHandle* handle);

int32_t kp_call(KpHandle* handle, const char* cmd_json, char** out_json);
int32_t kp_save(KpHandle* handle, uint8_t** out_bytes, size_t* out_len);
int32_t kp_verify(const uint8_t* bytes, size_t len, const char* opts_json);

int32_t kp_sync_decide(KpHandle* handle, const char* remote_meta_json,
                       int local_dirty, char** out_json);
int32_t kp_sync_merge(KpHandle* handle, const uint8_t* remote, size_t len,
                      uint8_t** out_bytes, size_t* out_len,
                      char** conflicts_json);
int32_t kp_sync_commit(KpHandle* handle, const char* etag,
                       const char* content_sha256);
int32_t kp_sync_mark_dirty(KpHandle* handle);

int32_t kp_subscribe(KpHandle* handle, KpEventCb cb, void* user);
int32_t kp_unsubscribe(KpHandle* handle);

#ifdef __cplusplus
}
#endif

#endif /* KEEPASSITY_H */
