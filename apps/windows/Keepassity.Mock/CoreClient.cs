// SPDX-License-Identifier: GPL-3.0-or-later
// Thin P/Invoke wrapper around the keepassity FFI v1 contract.
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keepassity.Mock;

public sealed class CoreException : Exception
{
    public CoreException(string code, string message) : base($"{code}: {message}")
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class CoreClient : IDisposable
{
    private const string Library = "keepassity_ffi";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IntPtr _handle;

    public bool IsOpen => _handle != IntPtr.Zero;
    public string? DatabasePath { get; private set; }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint kp_api_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr kp_last_error_json();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr kp_open(byte[] kdbx, UIntPtr len, byte[] optsJson);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int kp_lock(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int kp_close(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int kp_call(IntPtr handle, byte[] commandJson, out IntPtr outJson);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int kp_save(IntPtr handle, out IntPtr outBytes, out UIntPtr outLen);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int kp_sync_decide(IntPtr handle, byte[] remoteMetaJson,
        int localDirty, out IntPtr outJson);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int kp_sync_merge(IntPtr handle, byte[] remote, UIntPtr len,
        out IntPtr outBytes, out UIntPtr outLen, out IntPtr conflictsJson);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int kp_sync_commit(IntPtr handle, IntPtr etag, IntPtr sha256);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void kp_string_free(IntPtr value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void kp_buffer_free(IntPtr value, UIntPtr len);

    public async Task OpenAsync(string path, string password)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CloseHandleLocked();
            await Task.Run(() =>
            {
                string error = LastError();
                if (kp_api_version() != 1)
                {
                    throw new CoreException("VersionMismatch",
                        $"FFI major version {kp_api_version()} is not supported (expected 1).");
                }
                byte[] options = Utf8Z(JsonSerializer.Serialize(new { password }));
                IntPtr handle = kp_open(bytes, (UIntPtr)bytes.LongLength, options);
                if (handle == IntPtr.Zero)
                {
                    error = LastError();
                    throw ParseError(error, "open database");
                }
                _handle = handle;
            }).ConfigureAwait(false);
            DatabasePath = path;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<GroupDto>> ListGroupsAsync()
        => await CallAsync<List<GroupDto>>("{\"command\":\"list_groups\"}")
            .ConfigureAwait(false);

    public async Task<List<EntrySummaryDto>> ListEntriesAsync(string? groupUuid = null)
    {
        string command = groupUuid is null
            ? "{\"command\":\"list_entries\",\"include_subgroups\":true}"
            : JsonSerializer.Serialize(new
            {
                command = "list_entries",
                group_uuid = groupUuid,
                include_subgroups = true,
            });
        return await CallAsync<List<EntrySummaryDto>>(command).ConfigureAwait(false);
    }

    public async Task<EntryFullDto> GetEntryAsync(string uuid)
        => await CallAsync<EntryFullDto>(JsonSerializer.Serialize(new
        {
            command = "get_entry",
            uuid,
        })).ConfigureAwait(false);

    public async Task<List<SearchMatchDto>> SearchAsync(string query)
    {
        string command = JsonSerializer.Serialize(new
        {
            command = "search",
            query,
            limit = 200,
        });
        SearchResponseDto response = await CallAsync<SearchResponseDto>(command).ConfigureAwait(false);
        return response.Matches;
    }

    public async Task<string> CreateEntryAsync(string groupUuid, string title, string username,
        string password, string url, string notes)
    {
        string command = JsonSerializer.Serialize(new
        {
            command = "create_entry",
            group_uuid = groupUuid,
            fields = new Dictionary<string, string>
            {
                ["Title"] = title,
                ["UserName"] = username,
                ["Password"] = password,
                ["URL"] = url,
                ["Notes"] = notes,
            },
        });
        CreateEntryResponse response = await CallAsync<CreateEntryResponse>(command)
            .ConfigureAwait(false);
        return response.Uuid;
    }

    public async Task UpdateEntryAsync(string uuid, string title, string username,
        string password, string url, string notes, IReadOnlyList<string> tags)
    {
        string command = JsonSerializer.Serialize(new
        {
            command = "update_entry",
            uuid,
            patch = new Dictionary<string, object>
            {
                ["Title"] = title,
                ["UserName"] = username,
                ["Password"] = password,
                ["URL"] = url,
                ["Notes"] = notes,
                ["tags"] = tags,
            },
        });
        _ = await CallAsync<JsonElement>(command).ConfigureAwait(false);
    }

    public async Task DeleteEntryAsync(string uuid)
        => _ = await CallAsync<JsonElement>(JsonSerializer.Serialize(new
        {
            command = "delete_entry",
            uuid,
            permanent = false,
        })).ConfigureAwait(false);

    public async Task MoveEntryAsync(string uuid, string groupUuid)
        => _ = await CallAsync<JsonElement>(JsonSerializer.Serialize(new
        {
            command = "move_entry",
            uuid,
            group_uuid = groupUuid,
        })).ConfigureAwait(false);

    public async Task<TotpCodeDto?> TotpCodeAsync(string uuid)
    {
        try
        {
            return await CallAsync<TotpCodeDto>(JsonSerializer.Serialize(new
            {
                command = "totp_code",
                uuid,
            })).ConfigureAwait(false);
        }
        catch (CoreException ex) when (ex.Code == "NotFound")
        {
            return null;
        }
    }

    public async Task TotpSetAsync(string uuid, TotpConfigDto config)
        => _ = await CallAsync<JsonElement>(JsonSerializer.Serialize(new
        {
            command = "totp_set",
            uuid,
            config,
        })).ConfigureAwait(false);

    public async Task TotpDeleteAsync(string uuid)
        => _ = await CallAsync<JsonElement>(JsonSerializer.Serialize(new
        {
            command = "totp_delete",
            uuid,
        })).ConfigureAwait(false);

    public async Task<PasswordGenerateDto> GeneratePasswordAsync(int length = 20,
        bool excludeAmbiguous = true)
        => await CallAsync<PasswordGenerateDto>(JsonSerializer.Serialize(new
        {
            command = "password_generate",
            length,
            exclude_ambiguous = excludeAmbiguous,
        })).ConfigureAwait(false);

    public async Task<byte[]> SaveBytesAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureOpen();
            return await Task.Run(() =>
            {
                if (kp_save(_handle, out IntPtr pointer, out UIntPtr length) != 0 ||
                    pointer == IntPtr.Zero)
                {
                    throw ParseError(LastError(), "save database");
                }
                try
                {
                    var result = new byte[checked((int)length.ToUInt64())];
                    Marshal.Copy(pointer, result, 0, result.Length);
                    return result;
                }
                finally
                {
                    kp_buffer_free(pointer, length);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(string path)
    {
        byte[] bytes = await SaveBytesAsync().ConfigureAwait(false);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory,
            $".{Path.GetFileName(path)}.keepassity-tmp-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
        if (File.Exists(path))
        {
            File.Replace(temp, path, path + ".keepassity-bak", true);
        }
        else
        {
            File.Move(temp, path);
        }
        DatabasePath = path;
    }

    public async Task<SyncDecisionDto> SyncDecideAsync(string remoteMetaJson, bool localDirty)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureOpen();
            return await Task.Run(() =>
            {
                if (kp_sync_decide(_handle, Utf8Z(remoteMetaJson), localDirty ? 1 : 0,
                        out IntPtr output) != 0 || output == IntPtr.Zero)
                {
                    throw ParseError(LastError(), "sync decide");
                }
                try
                {
                    string json = Marshal.PtrToStringUTF8(output)
                        ?? throw new CoreException("Internal", "sync decide returned null.");
                    return JsonSerializer.Deserialize<SyncDecisionDto>(json, JsonOptions)
                        ?? throw new CoreException("Internal", "sync decide returned empty JSON.");
                }
                finally
                {
                    kp_string_free(output);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SyncMergeResult> SyncMergeAsync(byte[] remote)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureOpen();
            return await Task.Run(() =>
            {
                if (kp_sync_merge(_handle, remote, (UIntPtr)remote.LongLength,
                        out IntPtr bytesPointer, out UIntPtr bytesLength,
                        out IntPtr conflictsPointer) != 0 ||
                    bytesPointer == IntPtr.Zero || conflictsPointer == IntPtr.Zero)
                {
                    throw ParseError(LastError(), "sync merge");
                }
                try
                {
                    var bytes = new byte[checked((int)bytesLength.ToUInt64())];
                    Marshal.Copy(bytesPointer, bytes, 0, bytes.Length);
                    string conflictsJson = Marshal.PtrToStringUTF8(conflictsPointer)
                        ?? "[]";
                    List<SyncConflictDto> conflicts = JsonSerializer.Deserialize<List<SyncConflictDto>>(
                        conflictsJson, JsonOptions) ?? new List<SyncConflictDto>();
                    return new SyncMergeResult(bytes, conflicts);
                }
                finally
                {
                    kp_buffer_free(bytesPointer, bytesLength);
                    kp_string_free(conflictsPointer);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SyncCommitAsync(string? etag, string? sha256)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureOpen();
            await Task.Run(() =>
            {
                IntPtr etagPointer = IntPtr.Zero;
                IntPtr shaPointer = IntPtr.Zero;
                try
                {
                    if (etag is not null)
                    {
                        etagPointer = Marshal.StringToCoTaskMemUTF8(etag);
                    }
                    if (sha256 is not null)
                    {
                        shaPointer = Marshal.StringToCoTaskMemUTF8(sha256);
                    }
                    if (kp_sync_commit(_handle, etagPointer, shaPointer) != 0)
                    {
                        throw ParseError(LastError(), "sync commit");
                    }
                }
                finally
                {
                    if (etagPointer != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(etagPointer);
                    }
                    if (shaPointer != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(shaPointer);
                    }
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task LockAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_handle != IntPtr.Zero)
            {
                _ = kp_lock(_handle);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            CloseHandleLocked();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void CloseHandleLocked()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }
        _ = kp_close(_handle);
        _handle = IntPtr.Zero;
    }

    private async Task<T> CallAsync<T>(string command)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureOpen();
            return await Task.Run(() =>
            {
                byte[] input = Utf8Z(command);
                if (kp_call(_handle, input, out IntPtr output) != 0 || output == IntPtr.Zero)
                {
                    throw ParseError(LastError(), command);
                }
                try
                {
                    string json = Marshal.PtrToStringUTF8(output)
                        ?? throw new CoreException("Internal", "FFI returned null JSON.");
                    return JsonSerializer.Deserialize<T>(json, JsonOptions)
                        ?? throw new CoreException("Internal", "FFI returned empty JSON.");
                }
                finally
                {
                    kp_string_free(output);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureOpen()
    {
        if (_handle == IntPtr.Zero)
        {
            throw new CoreException("Locked", "database is locked");
        }
    }

    private static byte[] Utf8Z(string value) => Encoding.UTF8.GetBytes(value + "\0");

    private static CoreException ParseError(string json, string operation)
    {
        try
        {
            ErrorDto? error = JsonSerializer.Deserialize<ErrorDto>(json, JsonOptions);
            return new CoreException(error?.Code ?? "Internal",
                error?.Message ?? $"{operation} failed.");
        }
        catch (JsonException)
        {
            return new CoreException("Internal", $"{operation} failed: {json}");
        }
    }

    private static string LastError()
    {
        IntPtr pointer = kp_last_error_json();
        return pointer == IntPtr.Zero
            ? "{\"code\":\"Internal\",\"message\":\"no error details\"}"
            : Marshal.PtrToStringUTF8(pointer) ?? "{\"code\":\"Internal\",\"message\":\"null error\"}";
    }
}

public sealed class GroupDto
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("parent_uuid")] public string? ParentUuid { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("icon")] public int Icon { get; set; }
    [JsonPropertyName("order")] public int Order { get; set; }
}

public sealed class EntrySummaryDto
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("group_uuid")] public string GroupUuid { get; set; } = "";
    [JsonPropertyName("has_totp")] public bool HasTotp { get; set; }
    [JsonPropertyName("modified_unix")] public long ModifiedUnix { get; set; }
}

public sealed class EntryFullDto
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("group_uuid")] public string GroupUuid { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("password_secret")] public string PasswordSecret { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("custom_fields")] public List<CustomFieldDto> CustomFields { get; set; } = new();
    [JsonPropertyName("times")] public TimesDto Times { get; set; } = new();
    [JsonPropertyName("history_count")] public int HistoryCount { get; set; }
    [JsonPropertyName("totp")] public TotpConfigDto? Totp { get; set; }
}

public sealed class CustomFieldDto
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("protected")] public bool Protected { get; set; }
}

public sealed class TimesDto
{
    [JsonPropertyName("created_unix")] public long? CreatedUnix { get; set; }
    [JsonPropertyName("modified_unix")] public long? ModifiedUnix { get; set; }
}

public sealed class TotpConfigDto
{
    [JsonPropertyName("secret_base32")] public string SecretBase32 { get; set; } = "";
    [JsonPropertyName("issuer")] public string? Issuer { get; set; }
    [JsonPropertyName("account")] public string? Account { get; set; }
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "SHA1";
    [JsonPropertyName("digits")] public int Digits { get; set; } = 6;
    [JsonPropertyName("period")] public int Period { get; set; } = 30;
    [JsonPropertyName("encoder")] public string Encoder { get; set; } = "totp";
}

public sealed class TotpCodeDto
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("remaining")] public int Remaining { get; set; }
    [JsonPropertyName("period")] public int Period { get; set; } = 30;
    [JsonPropertyName("digits")] public int Digits { get; set; } = 6;
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "SHA1";
    [JsonPropertyName("issuer")] public string? Issuer { get; set; }
    [JsonPropertyName("account")] public string? Account { get; set; }
}

public sealed class PasswordGenerateDto
{
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    [JsonPropertyName("entropy_bits")] public double EntropyBits { get; set; }
}

public sealed class SearchResponseDto
{
    [JsonPropertyName("matches")] public List<SearchMatchDto> Matches { get; set; } = new();
}

public sealed class SearchMatchDto
{
    [JsonPropertyName("entry")] public EntrySummaryDto Entry { get; set; } = new();
    [JsonPropertyName("matched_fields")] public List<string> MatchedFields { get; set; } = new();
}

public sealed class CreateEntryResponse
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
}

public sealed class ErrorDto
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class SyncDecisionDto
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("degraded")] public bool Degraded { get; set; }
    [JsonPropertyName("retry_after_ms")] public long? RetryAfterMs { get; set; }
}

public sealed class SyncConflictDto
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("local_modified_unix")] public long? LocalModifiedUnix { get; set; }
    [JsonPropertyName("remote_modified_unix")] public long? RemoteModifiedUnix { get; set; }
    [JsonPropertyName("fields")] public List<string> Fields { get; set; } = new();
}

public sealed record SyncMergeResult(byte[] Bytes, List<SyncConflictDto> Conflicts);
