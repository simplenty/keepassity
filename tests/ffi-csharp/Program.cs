// SPDX-License-Identifier: GPL-3.0-or-later
// C# P/Invoke smoke: open + list + TOTP + save through the C ABI.
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class FfiSmoke
{
    private const string Library = "keepassity_ffi";

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
    private static extern int kp_verify(byte[] bytes, UIntPtr len, byte[] optsJson);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void kp_string_free(IntPtr value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void kp_buffer_free(IntPtr value, UIntPtr len);

    private static byte[] Utf8Z(string value) => Encoding.UTF8.GetBytes(value + "\0");

    private static string LastError()
    {
        IntPtr pointer = kp_last_error_json();
        return pointer == IntPtr.Zero ? "(no error)" : Marshal.PtrToStringUTF8(pointer) ?? "(null)";
    }

    private static string Call(IntPtr handle, string command)
    {
        if (kp_call(handle, Utf8Z(command), out IntPtr output) != 0 || output == IntPtr.Zero)
        {
            throw new InvalidOperationException(LastError());
        }
        try
        {
            return Marshal.PtrToStringUTF8(output) ?? throw new InvalidOperationException("null JSON");
        }
        finally
        {
            kp_string_free(output);
        }
    }

    private static int Main()
    {
        string fixture = Environment.GetEnvironmentVariable("KEEPASSITY_FFI_FIXTURE")
            ?? throw new InvalidOperationException("KEEPASSITY_FFI_FIXTURE is required");
        string password = Environment.GetEnvironmentVariable("KEEPASSITY_FFI_PASSWORD") ?? "demopass";
        byte[] bytes = File.ReadAllBytes(fixture);
        byte[] opts = Utf8Z($"{{\"password\":\"{password}\"}}");

        if (kp_api_version() != 1)
        {
            Console.Error.WriteLine("FFI major version mismatch");
            return 1;
        }
        IntPtr handle = kp_open(bytes, (UIntPtr)bytes.LongLength, opts);
        if (handle == IntPtr.Zero)
        {
            Console.Error.WriteLine($"kp_open failed: {LastError()}");
            return 1;
        }

        try
        {
            using JsonDocument version = JsonDocument.Parse(Call(handle, "{\"command\":\"version\"}"));
            _ = version.RootElement.GetProperty("api").GetInt64();

            using JsonDocument entries = JsonDocument.Parse(Call(handle, "{\"command\":\"list_entries\"}"));
            JsonElement first = entries.RootElement.EnumerateArray().First();
            string entryUuid = first.GetProperty("uuid").GetString()!;

            using JsonDocument groups = JsonDocument.Parse(Call(handle, "{\"command\":\"list_groups\"}"));
            string groupUuid = groups.RootElement.EnumerateArray().First()
                .GetProperty("uuid").GetString()!;

            string create = $"{{\"command\":\"create_entry\",\"group_uuid\":\"{groupUuid}\"," +
                            "\"fields\":{\"Title\":\"csharp-smoke\",\"UserName\":\"u\",\"Password\":\"p\"}}";
            using JsonDocument created = JsonDocument.Parse(Call(handle, create));
            string createdUuid = created.RootElement.GetProperty("uuid").GetString()!;

            string setTotp = $"{{\"command\":\"totp_set\",\"uuid\":\"{createdUuid}\",\"config\":{{" +
                             "\"secret_base32\":\"JBSWY3DPEHPK3PXP\",\"algorithm\":\"SHA1\"," +
                             "\"digits\":6,\"period\":30,\"encoder\":\"totp\"}}";
            _ = Call(handle, setTotp);

            string codeCommand = $"{{\"command\":\"totp_code\",\"uuid\":\"{createdUuid}\",\"at_unix\":0}}";
            using JsonDocument code = JsonDocument.Parse(Call(handle, codeCommand));
            string codeValue = code.RootElement.GetProperty("code").GetString()!;
            if (codeValue.Length != 6)
            {
                Console.Error.WriteLine("unexpected TOTP code length");
                return 1;
            }

            if (kp_save(handle, out IntPtr saved, out UIntPtr savedLen) != 0 || saved == IntPtr.Zero)
            {
                Console.Error.WriteLine($"kp_save failed: {LastError()}");
                return 1;
            }
            try
            {
                int savedLength = checked((int)savedLen.ToUInt64());
                byte[] verified = new byte[savedLength];
                Marshal.Copy(saved, verified, 0, savedLength);
                if (kp_verify(verified, (UIntPtr)verified.LongLength, opts) != 0)
                {
                    Console.Error.WriteLine($"kp_verify failed: {LastError()}");
                    return 1;
                }
            }
            finally
            {
                kp_buffer_free(saved, savedLen);
            }

            // Read the pre-existing entry too (open+list+get path).
            _ = Call(handle, $"{{\"command\":\"get_entry\",\"uuid\":\"{entryUuid}\"}}");
            _ = kp_lock(handle);
            Console.WriteLine("C# P/Invoke smoke: open/list/totp/save passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FFI smoke failed: {error.Message}");
            return 1;
        }
        finally
        {
            _ = kp_close(handle);
        }
    }
}
