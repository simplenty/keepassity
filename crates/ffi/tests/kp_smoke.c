/* SPDX-License-Identifier: GPL-3.0-or-later */
/* FFI v1 smoke harness: exercises every exported C function and every kp_call
 * command against a fixture. Build: cc kp_smoke.c -Iinclude libkeepassity_ffi.a -lpthread -ldl -lm
 * Usage: kp_smoke <fixture.kdbx> <password>
 */
#include "keepassity.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int failures = 0;

static void check(int condition, const char* what) {
    if (!condition) {
        const char* error = kp_last_error_json();
        fprintf(stderr, "FAIL: %s%s%s\n", what, error ? " — " : "",
                error ? error : "");
        failures++;
    }
}

static char* read_file(const char* path, size_t* out_len) {
    FILE* file = fopen(path, "rb");
    if (!file) {
        return NULL;
    }
    if (fseek(file, 0, SEEK_END) != 0) {
        fclose(file);
        return NULL;
    }
    long size = ftell(file);
    if (size < 0 || fseek(file, 0, SEEK_SET) != 0) {
        fclose(file);
        return NULL;
    }
    char* data = (char*)malloc((size_t)size);
    if (!data) {
        fclose(file);
        return NULL;
    }
    if (fread(data, 1, (size_t)size, file) != (size_t)size) {
        free(data);
        fclose(file);
        return NULL;
    }
    fclose(file);
    *out_len = (size_t)size;
    return data;
}

static int call_ok(KpHandle* handle, const char* command, char** out) {
    int32_t result = kp_call(handle, command, out);
    check(result == 0, command);
    return result == 0 && out && *out;
}

/* First "uuid":"..." value in a JSON document; sufficient for smoke tests. */
static int first_uuid(const char* json, char* out, size_t out_len) {
    const char* key = "\"uuid\":\"";
    const char* found = json ? strstr(json, key) : NULL;
    if (!found) {
        return 0;
    }
    found += strlen(key);
    if (strlen(found) < 32 || out_len < 33) {
        return 0;
    }
    memcpy(out, found, 32);
    out[32] = '\0';
    return 1;
}

static void event_cb(void* user, const char* event_json) {
    (void)user;
    (void)event_json;
}

int main(int argc, char** argv) {
    if (argc < 3) {
        fprintf(stderr, "usage: %s <fixture.kdbx> <password>\n", argv[0]);
        return 2;
    }
    size_t len = 0;
    char* bytes = read_file(argv[1], &len);
    if (!bytes) {
        fprintf(stderr, "cannot read fixture\n");
        return 2;
    }

    char opts[512];
    snprintf(opts, sizeof(opts), "{\"password\":\"%s\"}", argv[2]);
    void* handle = kp_open((const uint8_t*)bytes, len, opts);
    check(handle != NULL, "kp_open");
    if (!handle) {
        free(bytes);
        return 1;
    }
    check(kp_api_version() == 1, "kp_api_version");
    check(kp_subscribe(handle, event_cb, NULL) == 0, "kp_subscribe");

    char* json = NULL;
    char* groups = NULL;
    char* entries = NULL;
    char* saved = NULL;
    size_t saved_len = 0;
    char entry_uuid[33] = {0};
    char group_uuid[33] = {0};

    check(call_ok(handle, "{\"command\":\"version\"}", &json), "version");
    kp_string_free(json);
    json = NULL;
    check(call_ok(handle, "{\"command\":\"list_groups\"}", &groups), "list_groups");
    check(first_uuid(groups, group_uuid, sizeof(group_uuid)), "group uuid");
    check(call_ok(handle, "{\"command\":\"list_entries\"}", &entries), "list_entries");
    check(first_uuid(entries, entry_uuid, sizeof(entry_uuid)), "entry uuid");

    char command[2048];
    snprintf(command, sizeof(command), "{\"command\":\"get_entry\",\"uuid\":\"%s\"}", entry_uuid);
    check(call_ok(handle, command, &json), "get_entry");
    kp_string_free(json);
    json = NULL;
    check(call_ok(handle, "{\"command\":\"search\",\"query\":\"a\"}", &json), "search");
    kp_string_free(json);
    json = NULL;

    snprintf(command, sizeof(command),
             "{\"command\":\"create_entry\",\"group_uuid\":\"%s\","
             "\"fields\":{\"Title\":\"c-harness-entry\",\"UserName\":\"u\","
             "\"Password\":\"p\"}}",
             group_uuid);
    check(call_ok(handle, command, &json), "create_entry");
    check(first_uuid(json, entry_uuid, sizeof(entry_uuid)), "created uuid");
    kp_string_free(json);
    json = NULL;

    snprintf(command, sizeof(command),
             "{\"command\":\"update_entry\",\"uuid\":\"%s\",\"patch\":{\"Title\":\"c-updated\"}}",
             entry_uuid);
    check(call_ok(handle, command, &json), "update_entry");
    kp_string_free(json);
    json = NULL;

    snprintf(command, sizeof(command),
             "{\"command\":\"totp_set\",\"uuid\":\"%s\",\"config\":{"
             "\"secret_base32\":\"JBSWY3DPEHPK3PXP\",\"algorithm\":\"SHA1\","
             "\"digits\":6,\"period\":30,\"encoder\":\"totp\"}}",
             entry_uuid);
    check(call_ok(handle, command, &json), "totp_set");
    kp_string_free(json);
    json = NULL;
    snprintf(command, sizeof(command),
             "{\"command\":\"totp_code\",\"uuid\":\"%s\",\"at_unix\":0}", entry_uuid);
    check(call_ok(handle, command, &json), "totp_code");
    kp_string_free(json);
    json = NULL;
    check(call_ok(handle,
                  "{\"command\":\"totp_parse_uri\",\"uri\":"
                  "\"otpauth://totp/Test:user?secret=JBSWY3DPEHPK3PXP&period=30&digits=6\"}",
                  &json),
          "totp_parse_uri");
    kp_string_free(json);
    json = NULL;
    snprintf(command, sizeof(command), "{\"command\":\"totp_delete\",\"uuid\":\"%s\"}", entry_uuid);
    check(call_ok(handle, command, &json), "totp_delete");
    kp_string_free(json);
    json = NULL;

    check(call_ok(handle,
                  "{\"command\":\"password_generate\",\"length\":24,"
                  "\"classes\":{\"lowercase\":true,\"uppercase\":true,"
                  "\"digits\":true,\"symbols\":true},\"exclude_ambiguous\":true}",
                  &json),
          "password_generate");
    kp_string_free(json);
    json = NULL;
    check(call_ok(handle, "{\"command\":\"sync_state\"}", &json), "sync_state");
    kp_string_free(json);
    json = NULL;
    check(call_ok(handle, "{\"command\":\"export_model_json\"}", &json), "export_model_json");
    kp_string_free(json);
    json = NULL;
    check(call_ok(handle, "{\"command\":\"diagnose\"}", &json), "diagnose");
    kp_string_free(json);
    json = NULL;

    snprintf(command, sizeof(command),
             "{\"command\":\"move_entry\",\"uuid\":\"%s\",\"group_uuid\":\"%s\"}",
             entry_uuid, group_uuid);
    check(call_ok(handle, command, &json), "move_entry");
    kp_string_free(json);
    json = NULL;
    snprintf(command, sizeof(command),
             "{\"command\":\"delete_entry\",\"uuid\":\"%s\",\"permanent\":true}",
             entry_uuid);
    check(call_ok(handle, command, &json), "delete_entry");
    kp_string_free(json);
    json = NULL;

    check(kp_save(handle, (uint8_t**)&saved, &saved_len) == 0, "kp_save");
    check(saved != NULL && saved_len > 0, "kp_save buffer");
    check(kp_verify((const uint8_t*)saved, saved_len, opts) == 0, "kp_verify");

    char* decision = NULL;
    check(kp_sync_decide(handle,
                         "{\"etag\":\"v1\",\"size\":10,\"modified_unix\":1,"
                         "\"reliability\":\"full\"}",
                         1, &decision) == 0,
          "kp_sync_decide");
    kp_string_free(decision);
    check(kp_sync_mark_dirty(handle) == 0, "kp_sync_mark_dirty");
    check(kp_sync_commit(handle, "v2", "sha256") == 0, "kp_sync_commit");

    char* merged = NULL;
    size_t merged_len = 0;
    char* conflicts = NULL;
    check(kp_sync_merge(handle, (const uint8_t*)saved, saved_len,
                        (uint8_t**)&merged, &merged_len, &conflicts) == 0,
          "kp_sync_merge");
    kp_string_free(conflicts);
    kp_buffer_free((uint8_t*)merged, merged_len);

    check(kp_unsubscribe(handle) == 0, "kp_unsubscribe");
    check(kp_lock(handle) == 0, "kp_lock");
    check(kp_close(handle) == 0, "kp_close");

    kp_buffer_free((uint8_t*)saved, saved_len);
    kp_string_free(groups);
    kp_string_free(entries);
    free(bytes);

    if (failures != 0) {
        fprintf(stderr, "%d FFI smoke failure(s)\n", failures);
        return 1;
    }
    printf("FFI smoke: all commands passed\n");
    return 0;
}
