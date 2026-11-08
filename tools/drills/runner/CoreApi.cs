// SPDX-License-Identifier: GPL-3.0-or-later
// Thin P/Invoke wrapper over the keepassity FFI v1 C ABI (crates/ffi). The
// application's CoreClient uses the same calls, so a drill that edits, saves,
// decides or merges through this wrapper exercises the real product seam.

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Keepassity.Drills.Runner;

internal sealed class CoreApi : IDisposable
{
    private const string Library = "keepassity_ffi";
    private IntPtr _handle;

    private CoreApi(IntPtr handle)
    {
        _handle = handle;
    }

    public static void CheckAbi()
    {
        uint version = kp_api_version();
        if (version != 1)
        {
            throw new InvalidOperationException(
                $"FFI major version {version} is not supported (expected 1).");
        }
    }

    public static CoreApi Open(byte[] kdbx, string password)
    {
        byte[] options = Utf8Z(JsonSerializer.Serialize(new { password }));
        IntPtr handle = kp_open(kdbx, (UIntPtr)kdbx.LongLength, options);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("kp_open failed: " + LastError());
        }
        return new CoreApi(handle);
    }

    public string Call(string commandJson)
    {
        EnsureOpen();
        if (kp_call(_handle, Utf8Z(commandJson), out IntPtr output) != 0 ||
            output == IntPtr.Zero)
        {
            throw new InvalidOperationException("kp_call failed: " + LastError());
        }
        try
        {
            return Marshal.PtrToStringUTF8(output) ?? string.Empty;
        }
        finally
        {
            kp_string_free(output);
        }
    }

    public string Call(object command)
        => Call(JsonSerializer.Serialize(command));

    public byte[] Save()
    {
        EnsureOpen();
        if (kp_save(_handle, out IntPtr pointer, out UIntPtr length) != 0 ||
            pointer == IntPtr.Zero)
        {
            throw new InvalidOperationException("kp_save failed: " + LastError());
        }
        try
        {
            byte[] bytes = new byte[checked((int)length.ToUInt64())];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            kp_buffer_free(pointer, length);
        }
    }

    public string SyncDecide(string remoteMetaJson, bool localDirty)
    {
        EnsureOpen();
        if (kp_sync_decide(_handle, Utf8Z(remoteMetaJson), localDirty ? 1 : 0,
                out IntPtr output) != 0 || output == IntPtr.Zero)
        {
            throw new InvalidOperationException("kp_sync_decide failed: " + LastError());
        }
        try
        {
            return Marshal.PtrToStringUTF8(output) ?? string.Empty;
        }
        finally
        {
            kp_string_free(output);
        }
    }

    public (byte[] Bytes, string ConflictsJson) SyncMerge(byte[] remote)
    {
        EnsureOpen();
        if (kp_sync_merge(_handle, remote, (UIntPtr)remote.LongLength,
                out IntPtr bytesPointer, out UIntPtr bytesLength,
                out IntPtr conflictsPointer) != 0 ||
            bytesPointer == IntPtr.Zero || conflictsPointer == IntPtr.Zero)
        {
            throw new InvalidOperationException("kp_sync_merge failed: " + LastError());
        }
        try
        {
            byte[] bytes = new byte[checked((int)bytesLength.ToUInt64())];
            Marshal.Copy(bytesPointer, bytes, 0, bytes.Length);
            string conflicts = Marshal.PtrToStringUTF8(conflictsPointer) ?? "[]";
            return (bytes, conflicts);
        }
        finally
        {
            kp_buffer_free(bytesPointer, bytesLength);
            kp_string_free(conflictsPointer);
        }
    }

    public void SyncCommit(string? etag, string? sha256)
    {
        EnsureOpen();
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
                throw new InvalidOperationException("kp_sync_commit failed: " + LastError());
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
    }

    public void MarkDirty()
    {
        EnsureOpen();
        if (kp_sync_mark_dirty(_handle) != 0)
        {
            throw new InvalidOperationException("kp_sync_mark_dirty failed: " + LastError());
        }
    }

    /// <summary>Ids and titles of every entry, in list_entries order.</summary>
    public List<(string Uuid, string Title)> ListEntries()
    {
        using JsonDocument document = JsonDocument.Parse(
            Call("{\"command\":\"list_entries\",\"include_subgroups\":true}"));
        var entries = new List<(string Uuid, string Title)>();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            entries.Add((
                item.GetProperty("uuid").GetString() ?? string.Empty,
                item.GetProperty("title").GetString() ?? string.Empty));
        }
        return entries;
    }

    public string UpdateTitle(string uuid, string title)
        => Call(new
        {
            command = "update_entry",
            uuid,
            patch = new Dictionary<string, string> { ["Title"] = title },
        });

    public bool SearchFinds(string query)
    {
        using JsonDocument document = JsonDocument.Parse(Call(new
        {
            command = "search",
            query,
            limit = 10,
        }));
        return document.RootElement.GetProperty("matches").GetArrayLength() > 0;
    }

    public int EntryCount()
    {
        using JsonDocument document = JsonDocument.Parse(
            Call("{\"command\":\"diagnose\"}"));
        return document.RootElement.GetProperty("entries").GetInt32();
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            _ = kp_close(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private void EnsureOpen()
    {
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("core handle is not open");
        }
    }

    private static byte[] Utf8Z(string text)
        => Encoding.UTF8.GetBytes(text + "\0");

    private static string LastError()
    {
        IntPtr pointer = kp_last_error_json();
        return pointer == IntPtr.Zero
            ? "unknown FFI error"
            : Marshal.PtrToStringUTF8(pointer) ?? "unknown FFI error";
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint kp_api_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr kp_last_error_json();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr kp_open(byte[] kdbx, UIntPtr len, byte[] optsJson);

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
    private static extern int kp_sync_mark_dirty(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void kp_string_free(IntPtr value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void kp_buffer_free(IntPtr value, UIntPtr len);
}
